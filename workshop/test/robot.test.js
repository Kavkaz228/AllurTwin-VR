import test from 'node:test';
import assert from 'node:assert/strict';
import { KinematicRobot, defaultRobotConfig, forwardKinematics, solveIK, checkConfiguration, checkJointPath, segmentIntersectsBox, validateRobotConfig } from '../server/robot/index.js';
import { rotationError, norm } from '../server/robot/math.js';

const copy = () => structuredClone(defaultRobotConfig);
const poseFor = (config, joints) => {
  const state = forwardKinematics(config, joints);
  return { position: state.position, orientation: state.orientation };
};
const distance = (a, b) => Math.hypot(a.x - b.x, a.y - b.y, a.z - b.z);
const goalJoints = [0.16, 0.98, -1.3, 0.2, 0.4, -0.1];

test('continuous teleop follows a 6DoF target with bounded joint speeds and stops on release', () => {
  const robot = new KinematicRobot();
  const target = poseFor(robot.config, goalJoints);
  assert.equal(robot.commandTeleop(target).code, 'DEADMAN_REQUIRED');
  robot.setDeadman(true);
  for (let i = 0; i < 150; ++i) {
    const before = robot.snapshot();
    assert.equal(robot.commandTeleop(target, 0.1).ok, true);
    assert.equal(robot.tick(.02).ok, true);
    const after = robot.snapshot();
    after.joints.forEach((q, j) => assert.ok(Math.abs(q - before.joints[j]) <= robot.config.joints[j].maxSpeed * .02 + 1e-12));
    assert.equal(checkConfiguration(robot.config, after.joints).ok, true);
  }
  const actual = robot.snapshot();
  assert.ok(distance(actual.tool.position, target.position) <= robot.config.ik.positionTolerance);
  assert.ok(norm(rotationError(target.orientation, actual.tool.orientation)) <= robot.config.ik.orientationTolerance);
  assert.equal(actual.fault, null);
  assert.equal(actual.ik.teleop, true);
  robot.setDeadman(false);
  const stopped = robot.snapshot();
  robot.setDeadman(true);
  robot.tick(.02);
  assert.deepEqual(robot.snapshot().joints, stopped.joints);
  assert.equal(robot.snapshot().tool.gripper, stopped.tool.gripper);
});

test('unreachable teleop direction holds at joint limits and recovers without reset', () => {
  const config = copy();
  config.joints.forEach((joint, i) => { joint.min = config.home[i] - .08; joint.max = config.home[i] + .08; });
  const robot = new KinematicRobot(config);
  const home = poseFor(config, config.home);
  const far = { position: { ...home.position, y: home.position.y + 2 }, orientation: home.orientation };
  robot.setDeadman(true);
  let limited = false;
  for (let i = 0; i < 200; ++i) {
    assert.equal(robot.commandTeleop(far).ok, true);
    robot.tick(.02);
    limited ||= robot.snapshot().motionLimit === 'IK_LIMIT';
    assert.equal(robot.snapshot().fault, null);
    assert.equal(checkConfiguration(config, robot.snapshot().joints).ok, true);
  }
  assert.equal(limited, true);
  const limitedPose = robot.snapshot().tool.position;
  assert.ok(distance(limitedPose, home.position) > .005, 'Approach a valid intermediate pose instead of freezing immediately.');
  for (let i = 0; i < 150; ++i) { assert.equal(robot.commandTeleop(home).ok, true); robot.tick(.02); }
  assert.ok(distance(robot.snapshot().tool.position, home.position) <= config.ik.positionTolerance);
  assert.equal(robot.snapshot().motionLimit, '');
  assert.equal(robot.snapshot().fault, null);
});

test('teleop still latches collision and malformed pose faults', () => {
  const config = copy();
  config.obstacles = [{ id: 'thin-wall', min: { x: .56, y: .57, z: -.005 }, max: { x: .58, y: .59, z: .005 } }];
  config.home[0] = -.4;
  const robot = new KinematicRobot(config);
  const goal = [...config.home]; goal[0] = .4;
  robot.setDeadman(true);
  let result;
  for (let i = 0; i < 200; ++i) {
    result = robot.commandTeleop(poseFor(config, goal));
    if (!result.ok) break;
    assert.equal(robot.tick(.02).ok, true);
  }
  assert.equal(result.code, 'OBSTACLE_COLLISION');
  assert.equal(robot.snapshot().moving, false);
  assert.ok(robot.snapshot().fault);
  const invalid = new KinematicRobot();
  invalid.setDeadman(true);
  assert.equal(invalid.commandTeleop({ position: { x: NaN, y: 0, z: 0 }, orientation: [0, 0, 0, 1] }).code, 'INVALID_COMMAND');
  assert.ok(invalid.snapshot().fault);
});

test('six-axis forward kinematics has expected zero-pose geometry and unit orientation', () => {
  const state = forwardKinematics(defaultRobotConfig, [0, 0, 0, 0, 0, 0]);
  assert.ok(distance(state.position, { x: 1.1, y: 0.35, z: 0 }) < 1e-12);
  assert.deepEqual(state.orientation, [0, 0, 0, 1]);
  assert.equal(state.links.length, 8);
  const yaw = forwardKinematics(defaultRobotConfig, [Math.PI / 2, 0, 0, 0, 0, 0]);
  assert.ok(distance(yaw.position, { x: 0, y: 0.35, z: -1.1 }) < 1e-12);
});

test('bounded IK solves a reachable position and orientation simultaneously', () => {
  const target = poseFor(defaultRobotConfig, goalJoints);
  const solution = solveIK(defaultRobotConfig, target);
  assert.equal(solution.ok, true, JSON.stringify(solution));
  const actual = poseFor(defaultRobotConfig, solution.joints);
  assert.ok(distance(target.position, actual.position) <= defaultRobotConfig.ik.positionTolerance);
  assert.ok(norm(rotationError(target.orientation, actual.orientation)) <= defaultRobotConfig.ik.orientationTolerance);
  assert.ok(solution.joints.every((q, i) => q >= defaultRobotConfig.joints[i].min && q <= defaultRobotConfig.joints[i].max));
  assert.ok(solution.iterations <= defaultRobotConfig.ik.iterations);
});

test('IK rejects impossible reach, impossible joint limits and malformed rotations', () => {
  assert.equal(solveIK(defaultRobotConfig, { position: { x: 12, y: 0.4, z: 0 }, orientation: [0, 0, 0, 1] }).code, 'IK_UNREACHABLE');
  const constrained = copy();
  constrained.joints.forEach((joint, i) => { joint.min = constrained.home[i] - 0.005; joint.max = constrained.home[i] + 0.005; });
  const target = poseFor(defaultRobotConfig, goalJoints);
  assert.equal(solveIK(constrained, target).ok, false);
  assert.equal(solveIK(defaultRobotConfig, { ...target, orientation: [0, 0, 0, 0] }).code, 'INVALID_POSE');
  assert.equal(solveIK(defaultRobotConfig, { ...target, position: { x: NaN, y: 0, z: 0 } }).code, 'INVALID_POSE');
});

test('server joint speeds and gripper speed are limited and settled pose is accurate', () => {
  const robot = new KinematicRobot();
  robot.setDeadman(true);
  const target = poseFor(robot.config, goalJoints);
  assert.equal(robot.command(target, 0.1).ok, true);
  for (let step = 0; step < 200; step++) {
    const before = robot.snapshot();
    assert.equal(robot.tick(0.02).ok, true);
    const after = robot.snapshot();
    after.joints.forEach((q, i) => assert.ok(Math.abs(q - before.joints[i]) <= robot.config.joints[i].maxSpeed * 0.02 + 1e-12));
    assert.ok(Math.abs(after.tool.gripper - before.tool.gripper) <= robot.config.gripperSpeed * 0.02 + 1e-12);
  }
  const actual = robot.snapshot();
  assert.equal(actual.moving, false);
  assert.ok(distance(actual.tool.position, target.position) <= robot.config.ik.positionTolerance);
  assert.ok(norm(rotationError(target.orientation, actual.tool.orientation)) <= robot.config.ik.orientationTolerance);
  assert.ok(Math.abs(actual.tool.gripper - 0.1) < 1e-12);
});

test('all robot links are checked and a crossing path is rejected even when both endpoints are clear', () => {
  const config = copy();
  config.obstacles = [{ id: 'thin-wall', min: { x: 0.56, y: 0.57, z: -0.005 }, max: { x: 0.58, y: 0.59, z: 0.005 } }];
  const from = [...config.home], to = [...config.home];
  from[0] = -0.4;
  to[0] = 0.4;
  config.home = from;
  assert.equal(checkConfiguration(config, from).ok, true);
  assert.equal(checkConfiguration(config, to).ok, true);
  const crossing = checkJointPath(config, from, to);
  assert.equal(crossing.code, 'OBSTACLE_COLLISION');
  assert.ok(crossing.link < 6, 'The intermediate arm link, rather than only TCP, must cause rejection.');
  const robot = new KinematicRobot(config);
  robot.setDeadman(true);
  assert.equal(robot.command(poseFor(config, to)).code, 'OBSTACLE_COLLISION');
  assert.deepEqual(robot.snapshot().joints, from);
  assert.ok(robot.snapshot().fault);
});

test('continuous path padding catches thin obstacles between pose samples', () => {
  const config = copy();
  config.pathStepRadians = 0.1;
  config.joints.forEach(joint => { joint.radius = 0.0001; });
  config.toolRadius = 0.0001;
  const from = [...config.home], to = [...config.home];
  from[0] = -0.04;
  to[0] = 0.04;
  const between = [...config.home];
  between[0] = 0.024;
  const location = forwardKinematics(config, between).position;
  config.obstacles = [{ id: 'needle', min: { x: location.x - 0.0002, y: location.y - 0.0002, z: location.z - 0.0002 }, max: { x: location.x + 0.0002, y: location.y + 0.0002, z: location.z + 0.0002 } }];
  assert.equal(checkConfiguration(config, from).ok, true);
  assert.equal(checkConfiguration(config, to).ok, true);
  assert.equal(checkConfiguration(config, config.home).ok, true, 'The midpoint sample alone misses this obstacle.');
  assert.equal(checkJointPath(config, from, to).code, 'OBSTACLE_COLLISION');
});

test('workspace constraints include the link radius and not only tool position', () => {
  const config = copy();
  config.workspace.min.y = 0.09;
  assert.equal(checkConfiguration(config, config.home).code, 'WORKSPACE_COLLISION');
  assert.throws(() => new KinematicRobot(config), /Unsafe initial robot configuration/);
});

test('slab intersection handles thin boxes, parallel segments and rounded-link clearance conservatively', () => {
  const box = { min: { x: 0.49999, y: -0.01, z: -0.01 }, max: { x: 0.50001, y: 0.01, z: 0.01 } };
  assert.equal(segmentIntersectsBox({ x: 0, y: 0, z: 0 }, { x: 1, y: 0, z: 0 }, box), true);
  assert.equal(segmentIntersectsBox({ x: 0, y: 0.03, z: 0 }, { x: 1, y: 0.03, z: 0 }, box), false);
  assert.equal(segmentIntersectsBox({ x: 0, y: 0.03, z: 0 }, { x: 1, y: 0.03, z: 0 }, box, 0.025), true);
});

test('deadman release stops arm and gripper, and repress alone never resumes the old command', () => {
  const robot = new KinematicRobot();
  const target = poseFor(robot.config, goalJoints);
  assert.equal(robot.command(target).code, 'DEADMAN_REQUIRED');
  robot.setDeadman(true);
  assert.equal(robot.command(target, 0).ok, true);
  robot.tick(0.02);
  robot.setDeadman(false);
  const stopped = robot.snapshot();
  robot.setDeadman(true);
  robot.tick(0.02);
  assert.deepEqual(robot.snapshot().joints, stopped.joints);
  assert.equal(robot.snapshot().tool.gripper, stopped.tool.gripper);
  assert.equal(robot.snapshot().moving, false);
});

test('faults latch, require released deadman before reset and require a fresh command', () => {
  const robot = new KinematicRobot();
  const target = poseFor(robot.config, goalJoints);
  robot.setDeadman(true);
  assert.equal(robot.command(target).ok, true);
  robot.stop('Command watchdog expired');
  const stopped = robot.snapshot().joints;
  robot.setDeadman(true);
  assert.equal(robot.command(target).code, 'FAULT_LATCHED');
  assert.equal(robot.reset().code, 'RELEASE_DEADMAN');
  robot.setDeadman(false);
  assert.equal(robot.reset().ok, true);
  robot.setDeadman(true);
  robot.tick(0.02);
  assert.deepEqual(robot.snapshot().joints, stopped);
  assert.equal(robot.command(target).ok, true);
});

test('invalid command and stale server tick stop motion without applying a target', () => {
  const robot = new KinematicRobot();
  robot.setDeadman(true);
  const joints = robot.snapshot().joints;
  assert.equal(robot.command({ position: { x: Infinity, y: 0, z: 0 }, orientation: [0, 0, 0, 1] }).code, 'INVALID_COMMAND');
  assert.deepEqual(robot.snapshot().joints, joints);
  assert.ok(robot.snapshot().fault);
  robot.reset();
  robot.setDeadman(true);
  assert.equal(robot.command(poseFor(robot.config, goalJoints)).ok, true);
  assert.equal(robot.tick(2).code, 'TIMING_FAULT');
  assert.deepEqual(robot.snapshot().joints, joints);
});

test('configuration rejects unbounded loops, malformed geometry and nonfinite state', () => {
  assert.throws(() => validateRobotConfig({ ik: { iterations: 100000 } }), /iteration budget/);
  assert.throws(() => validateRobotConfig({ pathStepRadians: 0 }), /collision path step/);
  const malformed = copy();
  malformed.joints[0].axis = [NaN, 0, 1];
  assert.throws(() => new KinematicRobot(malformed), /unit vectors/);
  assert.throws(() => forwardKinematics(defaultRobotConfig, [0, 0, 0, NaN, 0, 0]), /finite radians/);
});
