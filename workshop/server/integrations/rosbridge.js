import WebSocket from 'ws';
import { performance } from 'node:perf_hooks';
import { boundedNumber, checkedUrl } from './http.js';

const forbiddenKeys = new Set(['__proto__', 'prototype', 'constructor']);
const substitutions = new Set([
  '$pose.position.x', '$pose.position.y', '$pose.position.z',
  '$pose.orientation.0', '$pose.orientation.1', '$pose.orientation.2', '$pose.orientation.3',
  '$gripper', '$reason', '$timestamp', '$sequence'
]);

function validPath(path) {
  return typeof path === 'string' && /^[A-Za-z0-9_]+(?:\.[A-Za-z0-9_]+)*$/.test(path) && !path.split('.').some(key => forbiddenKeys.has(key));
}

function atPath(object, path) {
  if (!validPath(path)) return undefined;
  return path.split('.').reduce((value, key) => value && Object.hasOwn(value, key) ? value[key] : undefined, object);
}

function inspectTemplate(template, depth = 0) {
  if (depth > 12) throw new Error('ROS template nesting is too deep');
  if (typeof template === 'string' && template.startsWith('$') && !substitutions.has(template)) throw new Error(`Unknown ROS template placeholder: ${template}`);
  if (typeof template === 'number' && !Number.isFinite(template)) throw new Error('Non-finite ROS template number');
  if (template && typeof template === 'object') {
    for (const [key, value] of Object.entries(template)) {
      if (forbiddenKeys.has(key)) throw new Error('Invalid ROS template key');
      inspectTemplate(value, depth + 1);
    }
  } else if (!['string', 'number', 'boolean'].includes(typeof template) && template !== null) throw new Error('ROS templates must be JSON values');
}

function expand(template, context) {
  if (typeof template === 'string' && substitutions.has(template)) return atPath(context, template.slice(1));
  if (Array.isArray(template)) return template.map(value => expand(value, context));
  if (template && typeof template === 'object') return Object.fromEntries(Object.entries(template).map(([key, value]) => [key, expand(value, context)]));
  return template;
}

function checkTopic(mapping, name, needsTemplate = false) {
  if (!mapping || typeof mapping.topic !== 'string' || !/^\/[A-Za-z_][A-Za-z0-9_/]*$/.test(mapping.topic) ||
      typeof mapping.type !== 'string' || !/^[A-Za-z_][A-Za-z0-9_]*\/[A-Za-z_][A-Za-z0-9_]*$/.test(mapping.type)) {
    throw new Error(`Explicit ROS1 topic and package/Message type required: ${name}`);
  }
  if (needsTemplate) {
    if (!mapping.template || Array.isArray(mapping.template) || typeof mapping.template !== 'object') throw new Error(`${name}.template must be a JSON object`);
    if (JSON.stringify(mapping.template).length > 16384) throw new Error('ROS template is too large');
    inspectTemplate(mapping.template);
  }
}

export function validPose(pose) {
  return Boolean(pose && pose.position && ['x', 'y', 'z'].every(axis => Number.isFinite(pose.position[axis]) && Math.abs(pose.position[axis]) <= 10000) &&
    Array.isArray(pose.orientation) && pose.orientation.length === 4 && pose.orientation.every(Number.isFinite) &&
    Math.abs(Math.hypot(...pose.orientation) - 1) < 0.02);
}

/** ROS1 transport, with installation-owned mappings. No assumed vendor topic names. */
export class RosbridgeRobot {
  constructor(config = {}, dependencies = {}) {
    this.config = structuredClone(config);
    this.WebSocket = dependencies.WebSocketImpl || WebSocket;
    this.now = dependencies.now || (() => performance.now());
    this.wallNow = dependencies.wallNow || Date.now;
    this.socket = null;
    this.status = 'DISCONNECTED';
    this.lastError = '';
    this.lastTelemetryAt = null;
    this.lastCommandAt = null;
    this.telemetry = null;
    this.motionRequested = false;
    this.stopReason = '';
    this.stopDelivery = 'NOT_REQUESTED';
    this.sequence = 0;
    this.connecting = null;
    this.maxTelemetryAgeMs = boundedNumber(config.maxTelemetryAgeMs, 500, 20, 5000);
    this.commandTimeoutMs = boundedNumber(config.commandTimeoutMs, 250, 20, 2000);
    this.connectTimeoutMs = boundedNumber(config.connectTimeoutMs, 5000, 10, 30000);
    this.maxBufferedBytes = boundedNumber(config.maxBufferedBytes, 65536, 1024, 1048576);
  }

  validateConfiguration() {
    checkedUrl(this.config.url, ['ws:', 'wss:']);
    if (this.config.bearerToken !== undefined && (typeof this.config.bearerToken !== 'string' || /[\r\n]/.test(this.config.bearerToken))) throw new Error('Invalid bearer token');
    checkTopic(this.config.telemetry, 'telemetry');
    for (const key of ['positionPath', 'orientationPath']) {
      if (!validPath(this.config.telemetry[key])) throw new Error(`Explicit telemetry.${key} required`);
    }
    for (const key of ['jointsPath', 'gripperPath']) {
      if (this.config.telemetry[key] !== undefined && !validPath(this.config.telemetry[key])) throw new Error(`Invalid telemetry.${key}`);
    }
    if (this.config.command) checkTopic(this.config.command, 'command', true);
    if (this.config.stop) {
      checkTopic(this.config.stop, 'stop', true);
      // A stop template must never depend on a potentially stale target pose.
      const stopJson = JSON.stringify(this.config.stop.template);
      if (stopJson.includes('$pose.') || stopJson.includes('$gripper')) throw new Error('Stop template cannot contain pose or gripper placeholders');
    }
    if (this.config.commissioned === true && (!this.config.command || !this.config.stop)) throw new Error('Commissioned robot requires command and stop mappings');
    if (this.config.rosAuth) {
      const auth = this.config.rosAuth;
      if (!['mac', 'client', 'dest', 'rand', 'level'].every(key => typeof auth[key] === 'string' && auth[key]) ||
          !Number.isInteger(auth.t) || !Number.isInteger(auth.end) || auth.end <= Math.floor(this.wallNow() / 1000)) throw new Error('Invalid or expired ROS authentication credentials');
    }
  }

  async connect() {
    if (this.socket?.readyState === WebSocket.OPEN) return { ok: true, status: this.status };
    if (this.connecting) return this.connecting;
    try { this.validateConfiguration(); } catch (error) {
      this.status = 'MISCONFIGURED';
      this.lastError = error.message;
      return { ok: false, error: error.message };
    }
    this.status = 'CONNECTING';
    this.lastError = '';
    this.telemetry = null;
    this.lastTelemetryAt = null;
    this.motionRequested = false;
    this.connecting = new Promise(resolve => {
      let settled = false;
      let timer;
      const finish = result => {
        if (settled) return;
        settled = true;
        clearTimeout(timer);
        resolve(result);
      };
      const headers = this.config.bearerToken ? { authorization: `Bearer ${this.config.bearerToken}` } : {};
      const socket = new this.WebSocket(this.config.url, { headers, maxPayload: 65536, perMessageDeflate: false, handshakeTimeout: this.connectTimeoutMs });
      this.socket = socket;
      timer = setTimeout(() => {
        if (this.socket !== socket) return;
        this.status = 'DISCONNECTED';
        this.lastError = 'ROS connection deadline exceeded';
        socket.terminate();
        finish({ ok: false, error: this.lastError });
      }, this.connectTimeoutMs);
      socket.on('open', () => {
        if (this.socket !== socket) return;
        try {
          this.status = 'AWAITING_TELEMETRY';
          if (this.config.rosAuth) this.send({ ...Object.fromEntries(['mac', 'client', 'dest', 'rand', 't', 'level', 'end'].map(key => [key, this.config.rosAuth[key]])), op: 'auth' });
          this.send({ op: 'subscribe', id: 'vr-telemetry', topic: this.config.telemetry.topic, type: this.config.telemetry.type, queue_length: 1, throttle_rate: 0 });
          if (this.config.commissioned === true) {
            for (const mapping of [this.config.command, this.config.stop]) this.send({ op: 'advertise', topic: mapping.topic, type: mapping.type, queue_size: 1, latch: false });
          }
          finish({ ok: true, status: this.status });
        } catch (error) {
          this.fail(error.message);
          finish({ ok: false, error: error.message });
        }
      });
      socket.on('message', (data, isBinary) => {
        if (this.socket !== socket) return;
        if (isBinary) return this.fail('Only JSON ROS telemetry is supported');
        this.receive(data);
      });
      socket.on('error', () => {
        if (this.socket !== socket) return;
        this.lastError = 'ROS WebSocket connection failed';
        this.status = 'DISCONNECTED';
        this.motionRequested = false;
        finish({ ok: false, error: this.lastError });
      });
      socket.on('close', () => {
        if (this.socket !== socket) return;
        this.status = 'DISCONNECTED';
        this.motionRequested = false;
        this.stopDelivery = 'UNCONFIRMED';
        finish({ ok: false, error: this.lastError || 'ROS connection closed' });
      });
    });
    try { return await this.connecting; }
    catch (error) {
      this.status = 'DISCONNECTED';
      this.lastError = 'ROS transport could not be initialized';
      return { ok: false, error: this.lastError };
    } finally { this.connecting = null; }
  }

  send(message) {
    if (this.socket?.readyState !== WebSocket.OPEN) throw new Error('ROS transport is not connected');
    if (this.socket.bufferedAmount > this.maxBufferedBytes) throw new Error('ROS transport is congested');
    const socket = this.socket;
    socket.send(JSON.stringify(message), error => {
      if (error && this.socket === socket) this.fail('ROS message delivery failed');
    });
  }

  receive(raw) {
    let message;
    try { message = JSON.parse(raw.toString()); } catch { return this.fail('Invalid ROS JSON'); }
    if (!message || typeof message !== 'object' || Array.isArray(message)) return this.fail('Invalid ROS message envelope');
    if (message.op === 'status' && message.level === 'error') return this.fail('ROS bridge rejected an operation');
    if (message.op !== 'publish' || message.topic !== this.config.telemetry.topic) return;
    const map = this.config.telemetry;
    let orientation = atPath(message.msg, map.orientationPath);
    if (orientation && !Array.isArray(orientation)) orientation = ['x', 'y', 'z', 'w'].map(key => orientation[key]);
    const pose = { position: atPath(message.msg, map.positionPath), orientation };
    const joints = map.jointsPath ? atPath(message.msg, map.jointsPath) : null;
    const gripper = map.gripperPath ? atPath(message.msg, map.gripperPath) : null;
    if (!validPose(pose) || (map.jointsPath && (!Array.isArray(joints) || !joints.length || joints.length > 32 || !joints.every(Number.isFinite))) ||
        (map.gripperPath && (!Number.isFinite(gripper) || gripper < 0 || gripper > 1))) return this.fail('Invalid mapped robot telemetry');
    this.telemetry = structuredClone({ ...pose, joints, gripper });
    this.lastTelemetryAt = this.now();
    this.status = 'READY';
    this.lastError = '';
  }

  command(pose, gripper) {
    if (this.config.commissioned !== true) return { ok: false, error: 'Physical command mappings are not commissioned' };
    if (!validPose(pose) || !Number.isFinite(gripper) || gripper < 0 || gripper > 1) {
      this.stop('Invalid command');
      return { ok: false, error: 'Invalid robot pose or gripper value' };
    }
    if (this.status !== 'READY' || this.lastTelemetryAt === null || this.now() - this.lastTelemetryAt > this.maxTelemetryAgeMs) {
      this.stop('Robot telemetry is unavailable or stale');
      return { ok: false, error: 'Fresh robot telemetry is required' };
    }
    try {
      this.send({ op: 'publish', topic: this.config.command.topic,
        msg: expand(this.config.command.template, { pose, gripper, reason: '', timestamp: this.wallNow(), sequence: ++this.sequence }) });
      this.lastCommandAt = this.now();
      this.motionRequested = true;
      this.stopReason = '';
      return { ok: true, dispatched: true, acknowledged: false, sequence: this.sequence };
    } catch (error) {
      this.fail(error.message);
      return { ok: false, error: error.message };
    }
  }

  stop(reason = 'Operator stop') {
    this.motionRequested = false;
    this.stopReason = String(reason).slice(0, 250);
    this.stopDelivery = 'UNCONFIRMED';
    if (this.config.commissioned !== true || !this.config.stop) return { ok: false, dispatched: false, error: 'No commissioned stop mapping' };
    try {
      this.send({ op: 'publish', topic: this.config.stop.topic,
        msg: expand(this.config.stop.template, { reason: this.stopReason, timestamp: this.wallNow(), sequence: ++this.sequence }) });
      this.stopDelivery = 'DISPATCHED_UNCONFIRMED';
      return { ok: true, dispatched: true, acknowledged: false };
    } catch (error) { return { ok: false, dispatched: false, error: error.message }; }
  }

  fail(reason) {
    this.stop(reason);
    this.status = 'FAULT';
    this.lastError = reason;
    this.socket?.terminate();
  }

  tick() {
    if (this.lastTelemetryAt !== null && this.now() - this.lastTelemetryAt > this.maxTelemetryAgeMs) {
      if (this.motionRequested) this.stop('Robot telemetry timed out');
      if (this.status === 'READY') this.status = 'STALE';
    }
    if (this.motionRequested && this.now() - this.lastCommandAt > this.commandTimeoutMs) this.stop('Robot command watchdog expired');
    return this.snapshot();
  }

  reset() {
    return { ok: false, error: 'Remote physical controller reset is not implemented; use the commissioned local controller procedure' };
  }

  disconnect() {
    this.stop('Adapter disconnected');
    if (this.socket?.readyState === WebSocket.OPEN) {
      try { this.send({ op: 'unsubscribe', id: 'vr-telemetry', topic: this.config.telemetry.topic }); } catch {}
      const socket = this.socket;
      socket.close(1000, 'Adapter disconnect');
      const timer = setTimeout(() => socket.terminate(), 500);
      timer.unref?.();
      socket.once('close', () => clearTimeout(timer));
    } else this.socket?.terminate();
    this.status = 'DISCONNECTED';
    this.telemetry = null;
    this.lastTelemetryAt = null;
  }

  snapshot() {
    return {
      source: 'rosbridge-cortex', status: this.status, connected: Boolean(this.socket?.readyState === WebSocket.OPEN),
      commissioned: this.config.commissioned === true, telemetry: this.telemetry ? structuredClone(this.telemetry) : null,
      telemetryAgeMs: this.lastTelemetryAt === null ? null : this.now() - this.lastTelemetryAt,
      telemetryFresh: this.lastTelemetryAt !== null && this.now() - this.lastTelemetryAt <= this.maxTelemetryAgeMs,
      motionRequested: this.motionRequested, lastError: this.lastError, stopReason: this.stopReason,
      stopDelivery: this.stopDelivery, sequence: this.sequence
    };
  }
}
