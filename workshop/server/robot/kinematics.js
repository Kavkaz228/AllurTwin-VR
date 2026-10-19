import { add, array, axisAngle, clamp, cross, finiteVector, multiply, norm, normalizeQuaternion, point, rotate, rotationError, scale, solveLinear, sub } from './math.js';

export function validPose(pose) {
  return pose && finiteVector(array(pose.position), 3) && finiteVector(pose.orientation, 4)
    && norm(pose.orientation) >= 0.99 && norm(pose.orientation) <= 1.01;
}

export function forwardKinematics(config, joints) {
  if (!finiteVector(joints, config.joints.length)) throw new TypeError('Joint vector must contain finite radians.');
  let position = array(config.basePosition);
  let orientation = [0, 0, 0, 1];
  const links = [point(position)];
  const origins = [], axes = [];
  for (let i = 0; i < joints.length; i++) {
    const joint = config.joints[i];
    position = add(position, rotate(orientation, joint.offset));
    origins.push([...position]);
    axes.push(rotate(orientation, joint.axis));
    links.push(point(position));
    orientation = normalizeQuaternion(multiply(orientation, axisAngle(joint.axis, joints[i])));
  }
  position = add(position, rotate(orientation, array(config.toolOffset)));
  links.push(point(position));
  return { position: point(position), orientation, links, origins, axes };
}

function errorAt(fk, target, weight) {
  const linear = sub(array(target.position), array(fk.position));
  const angular = rotationError(target.orientation, fk.orientation);
  return { vector: [...linear, ...scale(angular, weight)], position: norm(linear), orientation: norm(angular) };
}

function solveFromSeed(config, target, seed) {
  const options = config.ik;
  let joints = seed.map((q, i) => clamp(q, config.joints[i].min, config.joints[i].max));
  let lastError;
  for (let iteration = 0; iteration <= options.iterations; iteration++) {
    const fk = forwardKinematics(config, joints);
    const error = errorAt(fk, target, options.orientationWeight);
    lastError = error;
    if (error.position <= options.positionTolerance && error.orientation <= options.orientationTolerance) {
      return { ok: true, joints, iterations: iteration, error: { position: error.position, orientation: error.orientation } };
    }
    if (iteration === options.iterations) break;
    const tcp = array(fk.position);
    const columns = fk.axes.map((axis, i) => [...cross(axis, sub(tcp, fk.origins[i])), ...scale(axis, options.orientationWeight)]);
    const matrix = Array.from({ length: 6 }, (_, row) => Array.from({ length: 6 }, (_, col) =>
      columns.reduce((sum, column) => sum + column[row] * column[col], 0) + (row === col ? options.damping ** 2 : 0)));
    const solved = solveLinear(matrix, error.vector);
    if (!solved) break;
    let step = columns.map(column => column.reduce((sum, v, i) => sum + v * solved[i], 0));
    const largest = Math.max(...step.map(Math.abs));
    if (largest > options.maxStep) step = scale(step, options.maxStep / largest);
    let improved = false;
    for (const fraction of [1, 0.5, 0.25, 0.125]) {
      const candidate = joints.map((q, i) => clamp(q + step[i] * fraction, config.joints[i].min, config.joints[i].max));
      const candidateError = errorAt(forwardKinematics(config, candidate), target, options.orientationWeight);
      if (norm(candidateError.vector) < norm(error.vector) - 1e-12) {
        joints = candidate;
        improved = true;
        break;
      }
    }
    if (!improved) break;
  }
  return { ok: false, code: 'IK_UNREACHABLE', error: 'Target pose cannot be reached within the configured joint limits and IK tolerance.', residual: { position: lastError.position, orientation: lastError.orientation } };
}

/** Bounded damped least-squares IK with a geometric Jacobian and full 6DoF error. */
export function solveIK(config, pose, seed = config.home, { allowHomeFallback = true } = {}) {
  if (!validPose(pose) || !finiteVector(seed, config.joints.length)) return { ok: false, code: 'INVALID_POSE', error: 'Pose must contain a finite position and a unit quaternion.' };
  const target = { position: pose.position, orientation: normalizeQuaternion(pose.orientation) };
  const reach = config.joints.reduce((sum, joint) => sum + norm(joint.offset), norm(array(config.toolOffset)));
  if (norm(sub(array(pose.position), array(config.basePosition))) > reach + config.ik.positionTolerance) {
    return { ok: false, code: 'IK_UNREACHABLE', error: 'Target lies beyond the arm reach.' };
  }
  // Seed with the current state for continuous teleoperation. The home seed offers
  // one deterministic fallback without unbounded random searches or branch jumps.
  const attempts = [seed];
  if (allowHomeFallback && seed.some((q, i) => Math.abs(q - config.home[i]) > 0.01)) attempts.push(config.home);
  let result;
  for (const attempt of attempts) {
    result = solveFromSeed(config, target, attempt);
    if (result.ok) return result;
  }
  return result;
}
