import { createHash, randomUUID } from 'node:crypto';
import { boundedNumber, checkedUrl, postJson } from './http.js';

/** Client of THIS PROJECT's gateway contract. This is not a Siemens REST API. */
export class SiemensValidationGateway {
  constructor(config = {}, dependencies = {}) {
    this.url = config.url ? checkedUrl(config.url).toString() : null;
    this.modelRevision = String(config.modelRevision || '');
    this.timeoutMs = boundedNumber(config.timeoutMs, 3000, 10, 30000);
    this.bearerToken = config.bearerToken;
    this.fetchImpl = dependencies.fetchImpl || globalThis.fetch;
    this.now = dependencies.now || Date.now;
    this.status = this.url && this.modelRevision ? 'CONFIGURED' : 'DISABLED';
    this.lastResult = null;
  }

  snapshot() {
    return { source: 'process-simulate-gateway', status: this.status, modelRevision: this.modelRevision || null, lastResult: this.lastResult };
  }

  async validate({ robotId, trajectory, modelRevision = this.modelRevision, coordinateFrame = 'cell-base' } = {}) {
    const requestId = randomUUID();
    const deny = (error, status = 'UNAVAILABLE') => {
      this.status = status;
      this.lastResult = { ok: false, allowed: false, source: 'process-simulate-gateway', requestId, modelRevision, issues: [], error };
      return this.lastResult;
    };
    if (!this.url || !this.modelRevision) return deny('Process Simulate gateway URL and model revision are required', 'DISABLED');
    if (modelRevision !== this.modelRevision) return deny('Model revision does not match configured revision', 'REJECTED');
    if (typeof robotId !== 'string' || !robotId || robotId.length > 100 || typeof coordinateFrame !== 'string' || !coordinateFrame || coordinateFrame.length > 100 || !Array.isArray(trajectory) || !trajectory.length || trajectory.length > 20000) return deny('Invalid trajectory request', 'REJECTED');
    for (const sample of trajectory) {
      if (!sample || !sample.position || !['x', 'y', 'z'].every(axis => Number.isFinite(sample.position[axis])) ||
          !Array.isArray(sample.orientation) || sample.orientation.length !== 4 || !sample.orientation.every(Number.isFinite) ||
          Math.abs(Math.hypot(...sample.orientation) - 1) > 0.02 ||
          (sample.gripper !== undefined && (!Number.isFinite(sample.gripper) || sample.gripper < 0 || sample.gripper > 1)) ||
          (sample.joints !== undefined && (!Array.isArray(sample.joints) || !sample.joints.length || sample.joints.length > 32 || !sample.joints.every(Number.isFinite)))) {
        return deny('Trajectory contains an invalid pose, joint angle or gripper value', 'REJECTED');
      }
    }
    let serialized;
    try { serialized = JSON.stringify(trajectory); } catch { return deny('Trajectory cannot be serialized', 'REJECTED'); }
    if (serialized.length > 4 * 1024 * 1024) return deny('Trajectory is too large', 'REJECTED');
    // The gateway echoes the digest so an answer cannot accidentally approve another path.
    const trajectoryHash = createHash('sha256').update(serialized).digest('hex');
    const expiresAt = this.now() + this.timeoutMs;
    const payload = {
      schema: 'vr-factory-validation/1', requestId, robotId, modelRevision, coordinateFrame,
      trajectoryHash, expiresAt, units: { position: 'm', orientation: 'quaternion-xyzw', joints: 'rad' }, trajectory
    };
    this.status = 'VALIDATING';
    try {
      const data = await postJson(this.url, payload, {
        timeoutMs: this.timeoutMs, maxBytes: 65536, bearerToken: this.bearerToken, fetchImpl: this.fetchImpl
      });
      if (this.now() >= expiresAt) return deny('Validation arrived after deadline');
      if (data.schema !== payload.schema || data.requestId !== requestId || data.robotId !== robotId ||
          data.modelRevision !== modelRevision || data.trajectoryHash !== trajectoryHash || data.coordinateFrame !== coordinateFrame ||
          data.engine !== 'Siemens Process Simulate' || typeof data.allowed !== 'boolean' ||
          !Array.isArray(data.issues) || data.issues.length > 100 || data.issues.some(issue => typeof issue !== 'string' || issue.length > 500)) {
        return deny('Validation response does not match the request or schema', 'REJECTED');
      }
      if (data.allowed && data.issues.length) return deny('Validation approved a path with unresolved issues', 'REJECTED');
      this.status = data.allowed ? 'READY' : 'REJECTED';
      this.lastResult = {
        ok: true, allowed: data.allowed, source: 'process-simulate-gateway', requestId, robotId,
        modelRevision, trajectoryHash, coordinateFrame, issues: data.issues, validatedAt: this.now(), expiresAt
      };
      return this.lastResult;
    } catch (error) { return deny(error.message); }
  }
}
