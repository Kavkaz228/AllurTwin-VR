import { readFileSync } from 'node:fs';
import { array, clamp, finiteVector, norm, rotationError, axisAngle, multiply, normalizeQuaternion } from './math.js';
import { forwardKinematics, solveIK, validPose } from './kinematics.js';
import { checkConfiguration, checkJointPath } from './collision.js';

const template = JSON.parse(readFileSync(new URL('./training-arm.json', import.meta.url), 'utf8'));
function freezeTree(value) {
  if (value && typeof value === 'object') { Object.values(value).forEach(freezeTree); Object.freeze(value); }
  return value;
}
export const defaultRobotConfig = freezeTree(template);
export { forwardKinematics, solveIK } from './kinematics.js';
export { checkConfiguration, checkJointPath, segmentIntersectsBox } from './collision.js';

export function validateRobotConfig(input) {
  const config = structuredClone({ ...defaultRobotConfig, ...input, ik: { ...defaultRobotConfig.ik, ...input?.ik } });
  const requireValue = (ok, message) => { if (!ok) throw new TypeError(`Invalid robot configuration: ${message}`); };
  const positive = value => Number.isFinite(value) && value > 0;
  requireValue(typeof config.id === 'string' && config.id.length > 0, 'id is required');
  requireValue(config.units === 'metres-radians', 'units must be metres-radians');
  requireValue(finiteVector(array(config.basePosition), 3) && finiteVector(array(config.toolOffset), 3) && norm(array(config.basePosition)) <= 100 && norm(array(config.toolOffset)) <= 100, 'base/tool positions must be finite and within 100 metres');
  requireValue(Array.isArray(config.joints) && config.joints.length === 6, 'exactly six revolute joints are required');
  for (const joint of config.joints) {
    requireValue(finiteVector(joint.axis, 3) && Math.abs(norm(joint.axis) - 1) < 1e-6, 'joint axes must be unit vectors');
    requireValue(finiteVector(joint.offset, 3) && norm(joint.offset) <= 100, 'joint offsets must be finite and within 100 metres');
    requireValue(Number.isFinite(joint.min) && Number.isFinite(joint.max) && joint.min < joint.max && joint.max - joint.min <= 4 * Math.PI, 'joint limits must be finite and bounded');
    requireValue(positive(joint.maxSpeed) && joint.maxSpeed <= 10 && positive(joint.radius) && joint.radius <= 10, 'joint speed and link radius must be positive and bounded');
  }
  requireValue(finiteVector(config.home, 6) && config.home.every((value, i) => value >= config.joints[i].min && value <= config.joints[i].max), 'home must be inside joint limits');
  requireValue(positive(config.toolRadius) && config.toolRadius <= 10 && positive(config.gripperSpeed) && config.gripperSpeed <= 10, 'tool radius and gripper speed must be positive and bounded');
  const validBox = box => box && finiteVector(array(box.min), 3) && finiteVector(array(box.max), 3) && array(box.min).every((value, i) => value < array(box.max)[i]) && [...array(box.min), ...array(box.max)].every(value => Math.abs(value) <= 1000);
  requireValue(validBox(config.workspace), 'workspace must be a nonempty finite box');
  requireValue(Array.isArray(config.obstacles) && config.obstacles.length <= 128 && config.obstacles.every(box => validBox(box) && typeof box.id === 'string'), 'obstacles must be named finite boxes (maximum 128)');
  requireValue(Number.isInteger(config.ik.iterations) && config.ik.iterations > 0 && config.ik.iterations <= 200, 'IK iteration budget must be 1..200');
  for (const key of ['positionTolerance', 'orientationTolerance', 'orientationWeight', 'damping', 'maxStep']) requireValue(positive(config.ik[key]) && config.ik[key] <= 10, `IK ${key} must be positive and bounded`);
  requireValue(config.ik.maxStep <= 0.5, 'IK angular step must not exceed 0.5 radians');
  requireValue(positive(config.pathStepRadians) && config.pathStepRadians <= 0.1, 'collision path step must be at most 0.1 radians');
  requireValue(Number.isInteger(config.maxPathSamples) && config.maxPathSamples > 0 && config.maxPathSamples <= 4096, 'collision budget must be 1..4096');
  requireValue(positive(config.maxTickSeconds) && config.maxTickSeconds <= 0.25, 'tick maximum must be at most 0.25 seconds');
  return freezeTree(config);
}

/**
 * Server-owned kinematic simulation. The enclosing session runtime owns leases,
 * command freshness and telemetry freshness and must call stop() on timeout.
 * This class never talks to physical equipment. Deadman is required on every
 * motion stream; setting it true alone does not replay a previous target.
 */
export class KinematicRobot {
  constructor(config = defaultRobotConfig) {
    this.config = validateRobotConfig(config);
    this.joints = [...this.config.home];
    this.targetJoints = [...this.joints];
    this.gripper = 0.6;
    this.targetGripper = this.gripper;
    this.deadman = false;
    this.fault = null;
    this.stopReason = 'Awaiting operator enable';
    this.ikDiagnostics = null;
    this.motionLimit = '';
    this.payloadKg=0; this.speedRamp=0;
    const initial = checkConfiguration(this.config, this.joints);
    if (!initial.ok) throw new Error(`Unsafe initial robot configuration: ${initial.error}`);
  }

  setDeadman(held) {
    this.deadman = held === true;
    if (!this.deadman) this.stop('Enable button released', { latch: false });
  }

  stop(reason = 'Stopped by operator', { latch = true } = {}) {
    this.targetJoints = [...this.joints];
    this.targetGripper = this.gripper;
    this.deadman = false;
    if (latch && !this.fault) this.fault = reason;
    this.stopReason = this.fault || reason;
    this.motionLimit = '';
    return { ok: true, stopped: true };
  }

  reset() {
    if (this.deadman) return { ok: false, code: 'RELEASE_DEADMAN', error: 'Release the enable button before reset.' };
    const check = checkConfiguration(this.config, this.joints);
    if (!check.ok) return check;
    this.fault = null;
    this.targetJoints = [...this.joints];
    this.targetGripper = this.gripper;
    this.stopReason = 'Reset complete; awaiting a new enabled command';
    this.motionLimit = '';
    return { ok: true };
  }

  reject(result) {
    this.stop(result.error);
    return result;
  }

  command(pose, gripper = this.gripper) {
    if (this.fault) return { ok: false, code: 'FAULT_LATCHED', error: this.fault };
    if (!this.deadman) return { ok: false, code: 'DEADMAN_REQUIRED', error: 'Hold the enable button to send a motion command.' };
    if (!validPose(pose) || !Number.isFinite(gripper) || gripper < 0 || gripper > 1) {
      return this.reject({ ok: false, code: 'INVALID_COMMAND', error: 'A finite pose, unit quaternion and gripper value from 0 to 1 are required.' });
    }
    const result = solveIK(this.config, pose, this.joints);
    if (!result.ok) return this.reject(result);
    const path = checkJointPath(this.config, this.joints, result.joints);
    if (!path.ok) return this.reject(path);
    this.targetJoints = result.joints;
    this.targetGripper = gripper;
    this.stopReason = '';
    this.motionLimit = '';
    this.ikDiagnostics = { iterations: result.iterations, residual: result.error, pathSamples: path.samples };
    return { ok: true, ik: this.ikDiagnostics };
  }

  // Explicit simulation-only hand following. Approach the requested pose in a
  // bounded 6DoF step from the measured joints, preserving the current IK branch.
  // A reach limit holds the last valid pose; collision/invalid input still latch.
  commandTeleop(pose, gripper = this.gripper) {
    if (this.fault) return { ok: false, code: 'FAULT_LATCHED', error: this.fault };
    if (!this.deadman) return { ok: false, code: 'DEADMAN_REQUIRED', error: 'Hold the enable button to send a motion command.' };
    if (!validPose(pose) || !Number.isFinite(gripper) || gripper < 0 || gripper > 1)
      return this.reject({ ok: false, code: 'INVALID_COMMAND', error: 'A finite pose, unit quaternion and gripper value from 0 to 1 are required.' });
    const current = forwardKinematics(this.config, this.joints);
    const delta = array(pose.position).map((v, i) => v - array(current.position)[i]);
    const rotation = rotationError(pose.orientation, current.orientation);
    const distance = norm(delta), angle = norm(rotation);
    if (!Number.isFinite(distance) || !Number.isFinite(angle))
      return this.reject({ ok: false, code: 'INVALID_COMMAND', error: 'Requested pose delta is not finite.' });
    const step = Math.min(1, distance > 0 ? 0.012 / distance : 1, angle > 0 ? 0.035 / angle : 1);
    const axis = angle > 1e-10 ? rotation.map(v => v / angle) : [1, 0, 0];
    let result;
    for (const fraction of [1, 0.5, 0.25, 0.125]) {
      const scale = step * fraction;
      const candidate = {
        position: Object.fromEntries(['x', 'y', 'z'].map((key, i) => [key, current.position[key] + delta[i] * scale])),
        orientation: normalizeQuaternion(multiply(axisAngle(axis, angle * scale), current.orientation))
      };
      result = solveIK(this.config, candidate, this.joints, { allowHomeFallback: false });
      if (!result.ok) {
        if (result.code === 'IK_UNREACHABLE') continue;
        return this.reject(result);
      }
      const path = checkJointPath(this.config, this.joints, result.joints);
      if (!path.ok) return this.reject(path);
      this.targetJoints = result.joints;
      this.targetGripper = gripper;
      this.stopReason = '';
      this.motionLimit = fraction < 1 ? 'IK_LIMIT' : '';
      this.ikDiagnostics = { iterations: result.iterations, residual: result.error, pathSamples: path.samples, teleop: true };
      return { ok: true, motionLimit: this.motionLimit, ik: this.ikDiagnostics };
    }
    // Do not continue chasing an old target when this direction is unreachable.
    this.targetJoints = [...this.joints];
    this.targetGripper = gripper;
    this.stopReason = '';
    this.motionLimit = 'IK_LIMIT';
    this.ikDiagnostics = { residual: result?.residual, teleop: true };
    return { ok: true, motionLimit: this.motionLimit, ik: this.ikDiagnostics };
  }

  tick(dt) {
    if (!Number.isFinite(dt) || dt <= 0 || dt > this.config.maxTickSeconds) {
      return this.reject({ ok: false, code: 'TIMING_FAULT', error: 'Server simulation timestep is invalid or stale.' });
    }
    if (this.fault || !this.deadman) {this.speedRamp=0;return { ok: true, moving: false };}
    this.speedRamp=Math.min(1,this.speedRamp+dt*4);
    const deltas = this.targetJoints.map((value, i) => value - this.joints[i]);
    const duration = Math.max(...deltas.map((value, i) => Math.abs(value) / this.config.joints[i].maxSpeed));
    const fraction = duration > 0 ? Math.min(1, dt / duration*this.speedRamp/(1+this.payloadKg*.15)) : 1;
    // One shared fraction keeps the executed motion on the checked joint path.
    const next = this.joints.map((value, i) => value + deltas[i] * fraction);
    const check = checkJointPath(this.config, this.joints, next);
    if (!check.ok) return this.reject(check);
    this.joints = next;
    this.gripper += clamp(this.targetGripper - this.gripper, -this.config.gripperSpeed * dt, this.config.gripperSpeed * dt);
    return { ok: true, moving: this.isMoving() };
  }

  isMoving() {
    return this.deadman && !this.fault && (this.joints.some((value, i) => Math.abs(value - this.targetJoints[i]) > 1e-7) || Math.abs(this.gripper - this.targetGripper) > 1e-7);
  }
  setPayload(kg){if(!Number.isFinite(kg)||kg<0||kg>5)return {ok:false,error:'Simulation payload must be 0..5 kg.'};this.payloadKg=kg;return {ok:true};}

  snapshot() {
    const state = forwardKinematics(this.config, this.joints);
    return {
      model: this.config.id,
      tool: { position: state.position, orientation: state.orientation, gripper: this.gripper },
      joints: [...this.joints],
      targetJoints: [...this.targetJoints],
      links: state.links,
      moving: this.isMoving(),
      stopReason: this.stopReason,
      fault: this.fault,
      deadman: this.deadman,
      ik: this.ikDiagnostics,
      motionLimit: this.motionLimit
      ,payloadKg:this.payloadKg,maxPayloadKg:5,gripperForceN:80,collisionModel:'separated-link-capsules'
    };
  }
}
