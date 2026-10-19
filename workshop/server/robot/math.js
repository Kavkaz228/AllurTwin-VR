// Scalar, vector and quaternion math in a right-handed, Y-up coordinate frame.
// Quaternions use [x, y, z, w]. Distances are metres, angles radians.
export const clamp = (x, low, high) => Math.max(low, Math.min(high, x));
export const add = (a, b) => a.map((v, i) => v + b[i]);
export const sub = (a, b) => a.map((v, i) => v - b[i]);
export const scale = (a, n) => a.map(v => v * n);
export const dot = (a, b) => a.reduce((s, v, i) => s + v * b[i], 0);
export const norm = a => Math.hypot(...a);
export const cross = (a, b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
export const array = p => Array.isArray(p) ? [...p] : [p?.x, p?.y, p?.z];
export const point = p => ({ x: p[0], y: p[1], z: p[2] });
export const finiteVector = (p, size) => Array.isArray(p) && p.length === size && p.every(Number.isFinite);
export const normalizeQuaternion = q => scale(q, 1 / norm(q));
export const conjugate = q => [-q[0], -q[1], -q[2], q[3]];
export function multiply(a, b) {
  return [
    a[3] * b[0] + a[0] * b[3] + a[1] * b[2] - a[2] * b[1],
    a[3] * b[1] - a[0] * b[2] + a[1] * b[3] + a[2] * b[0],
    a[3] * b[2] + a[0] * b[1] - a[1] * b[0] + a[2] * b[3],
    a[3] * b[3] - a[0] * b[0] - a[1] * b[1] - a[2] * b[2]
  ];
}
export function rotate(q, v) {
  const u = q.slice(0, 3);
  const t = scale(cross(u, v), 2);
  return add(v, add(scale(t, q[3]), cross(u, t)));
}
export function axisAngle(axis, radians) {
  const half = radians / 2;
  return [...scale(axis, Math.sin(half)), Math.cos(half)];
}
export function rotationError(target, current) {
  let d = normalizeQuaternion(multiply(target, conjugate(current)));
  if (d[3] < 0) d = scale(d, -1);
  const s = norm(d.slice(0, 3));
  return s < 1e-10 ? scale(d.slice(0, 3), 2) : scale(d.slice(0, 3), 2 * Math.atan2(s, clamp(d[3], -1, 1)) / s);
}
// Partial-pivot Gaussian elimination for the fixed-size damped IK system.
export function solveLinear(matrix, rhs) {
  const n = rhs.length;
  const a = matrix.map((row, i) => [...row, rhs[i]]);
  for (let k = 0; k < n; k++) {
    let pivot = k;
    for (let i = k + 1; i < n; i++) if (Math.abs(a[i][k]) > Math.abs(a[pivot][k])) pivot = i;
    if (!Number.isFinite(a[pivot][k]) || Math.abs(a[pivot][k]) < 1e-14) return null;
    [a[k], a[pivot]] = [a[pivot], a[k]];
    for (let i = k + 1; i < n; i++) {
      const factor = a[i][k] / a[k][k];
      for (let j = k; j <= n; j++) a[i][j] -= factor * a[k][j];
    }
  }
  const x = new Array(n).fill(0);
  for (let i = n - 1; i >= 0; i--) {
    x[i] = (a[i][n] - a[i].slice(i + 1, n).reduce((s, v, k) => s + v * x[i + 1 + k], 0)) / a[i][i];
  }
  return x.every(Number.isFinite) ? x : null;
}
