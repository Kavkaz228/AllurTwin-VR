import test from 'node:test';
import assert from 'node:assert/strict';
import { EventEmitter, once } from 'node:events';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import WebSocket from 'ws';
import { createApplication } from '../server/index.js';
import { WorkshopController } from '../server/controller.js';
import { loadConfig } from '../server/config.js';
import { forwardKinematics, defaultRobotConfig } from '../server/robot/index.js';

const ROBOT = 'training-arm';
const SECOND_ROBOT = 'training-arm-2';
const TOKEN = 'server-test-access-token-123456789';
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));

async function makeConfig(t) {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'vr-workshop-server-test-'));
  t.after(async () => {
    assert.equal(path.dirname(directory), path.resolve(os.tmpdir()));
    assert.ok(path.basename(directory).startsWith('vr-workshop-server-test-'));
    await rm(directory, { recursive: true, force: true, maxRetries: 5, retryDelay: 50 });
  });
  return loadConfig({ HOST: '127.0.0.1', PORT: '0', VR_ACCESS_TOKEN: TOKEN, DATA_DIR: directory });
}

class MemorySocket extends EventEmitter {
  readyState = 1;
  bufferedAmount = 0;
  messages = [];
  send(text) { this.messages.push(JSON.parse(text)); }
  close(code = 1000, reason = '') { this.readyState = 3; this.closeCode = code; this.closeReason = reason; this.emit('close'); }
}

async function controllerFixture(t) {
  const config = await makeConfig(t);
  let clock = 1000;
  const controller = new WorkshopController(config, { now: () => clock });
  t.after(() => controller.close());
  const advance = ms => { clock += ms; controller.tick(); };
  let request = 0;
  const connect = async name => {
    const socket = new MemorySocket();
    const client = controller.attach(socket);
    await controller.handle(client, { type: 'auth', id: `auth-${++request}`, protocolVersion: 1, accessToken: TOKEN, clientName: name });
    return { client, socket };
  };
  const send = async (peer, type, fields = {}) => {
    const id = `message-${++request}`;
    await controller.handle(peer.client, { type, id, robotId: ROBOT, ...fields });
    return peer.socket.messages.find(message => message.requestId === id);
  };
  advance(20);
  const owner = await connect('Operator');
  await send(owner, 'subscribe');
  const lease = await send(owner, 'acquire');
  const robot = controller.robots.get(ROBOT);
  const target = () => {
    const tool = robot.driver.snapshot().tool;
    return { position: { ...tool.position, y: tool.position.y + 0.025 }, orientation: [...tool.orientation] };
  };
  const command = (sequence, fields = {}) => send(owner, 'command', { leaseId: lease.leaseId, sequence, observedSequence: robot.sequence, deadman: true, pose: target(), gripper: 0.4, ...fields });
  return { config, controller, advance, connect, send, owner, lease, robot, target, command };
}

function wireClient(url, options) {
  const ws = new WebSocket(url, options);
  const messages = [], waiters = new Set();
  let lastError;
  ws.on('error', error => { lastError = error; });
  ws.on('message', bytes => {
    const value = JSON.parse(bytes.toString());
    messages.push(value);
    if (messages.length > 500) messages.shift();
    for (const waiter of [...waiters]) if (waiter.predicate(value)) { waiters.delete(waiter); clearTimeout(waiter.timer); waiter.resolve(value); }
  });
  const next = (predicate, { fresh = false, timeout = 3000 } = {}) => {
    if (!fresh) {
      const existing = messages.findLast(predicate);
      if (existing) return Promise.resolve(existing);
    }
    return new Promise((resolve, reject) => {
      const waiter = { predicate, resolve };
      waiter.timer = setTimeout(() => { waiters.delete(waiter); reject(new Error(`Timed out waiting for WebSocket response${lastError ? ': ' + lastError.message : ''}`)); }, timeout);
      waiters.add(waiter);
    });
  };
  let count = 0;
  return {
    ws, messages, next,
    open: () => once(ws, 'open'),
    send: (type, fields = {}) => { const id = `wire-${++count}`; ws.send(JSON.stringify({ type, id, robotId: ROBOT, ...fields })); return id; },
    async request(type, fields = {}) { const id = this.send(type, fields); return next(value => value.requestId === id); },
    async auth(name) { this.send('auth', { protocolVersion: 1, accessToken: TOKEN, clientName: name }); return next(value => value.type === 'welcome'); },
    close() { for (const waiter of waiters) clearTimeout(waiter.timer); waiters.clear(); ws.terminate(); }
  };
}

async function serverFixture(t) {
  const config = await makeConfig(t);
  const app = await createApplication(config);
  const address = await app.listen();
  t.after(() => app.close());
  assert.notEqual(address.port, 4173);
  assert.notEqual(address.port, 4180);
  const base = `http://127.0.0.1:${address.port}`;
  const peers = [];
  t.after(() => peers.forEach(peer => peer.close()));
  const connect = async name => {
    const peer = wireClient(`ws://127.0.0.1:${address.port}/ws`);
    peers.push(peer);
    await peer.open();
    if (name) await peer.auth(name);
    return peer;
  };
  return { config, app, base, connect };
}

test('simulation teleop is opt-in and preserves command watchdog and ordering gates', async t => {
  const f = await controllerFixture(t);
  const far = { position: { x: 4, y: .55, z: 0 }, orientation: [0, 0, 0, 1] };
  assert.equal((await f.command(1, { teleop: true, pose: far })).ok, true);
  f.advance(20);
  assert.equal(f.robot.driver.snapshot().ik.teleop, true);
  assert.equal(f.robot.driver.snapshot().fault, null);
  assert.equal((await f.command(1, { teleop: true, pose: far })).ok, false);
  assert.ok(f.robot.driver.snapshot().fault);
  assert.equal((await f.send(f.owner, 'reset', { leaseId: f.lease.leaseId })).ok, true);
  assert.equal((await f.command(2, { teleop: true, pose: far })).ok, true);
  f.advance(20);
  f.advance(f.config.commandTimeoutMs + 1);
  assert.ok(f.robot.driver.snapshot().fault);
  assert.equal((await f.send(f.owner, 'reset', { leaseId: f.lease.leaseId })).ok, true);
  assert.equal((await f.command(3, { pose: far })).ok, false, 'Ordinary absolute targets still reject impossible poses.');
});

test('controller executes a server-solved pose and duplicate commands stop with a latched fault', async t => {
  const f = await controllerFixture(t);
  const before = f.robot.driver.snapshot();
  assert.equal((await f.command(1)).ok, true);
  f.advance(20);
  const moved = f.robot.driver.snapshot();
  assert.notDeepEqual(moved.joints, before.joints);
  const fk = forwardKinematics(defaultRobotConfig, moved.joints);
  assert.deepEqual(moved.tool.position, fk.position);
  assert.equal((await f.command(1)).ok, false);
  const stopped = f.robot.driver.snapshot();
  assert.ok(stopped.fault);
  assert.equal(stopped.moving, false);
  f.advance(20);
  assert.deepEqual(f.robot.driver.snapshot().joints, stopped.joints);
  assert.equal((await f.command(2)).ok, false, 'A new sequence must not clear the fault.');
});

test('controller checks observed telemetry age and requires a subscription', async t => {
  const f = await controllerFixture(t);
  const observedSequence = f.robot.sequence;
  f.advance(f.config.telemetryMaxAgeMs + 1);
  assert.equal((await f.command(1, { observedSequence })).ok, false);
  assert.match(f.robot.driver.snapshot().fault, /телеметр/i);
  assert.equal((await f.send(f.owner, 'reset', { leaseId: f.lease.leaseId })).ok, true);
  f.owner.client.subscriptions.clear();
  assert.equal((await f.command(2)).ok, false);
  assert.equal(f.robot.driver.snapshot().moving, false);
});

test('command and lease watchdogs stop motion and expire ownership deterministically', async t => {
  const f = await controllerFixture(t);
  assert.equal((await f.command(1)).ok, true);
  f.advance(20);
  const before = f.robot.driver.snapshot().joints;
  f.advance(f.config.commandTimeoutMs + 1);
  assert.deepEqual(f.robot.driver.snapshot().joints, before);
  assert.ok(f.robot.driver.snapshot().fault);
  assert.ok(f.robot.lease, 'Command timeout retains ownership for an explicit reset.');
  f.advance(f.config.leaseTimeoutMs + 1);
  assert.equal(f.robot.lease, null);
  assert.equal((await f.send(f.owner, 'heartbeat', { leaseId: f.lease.leaseId })).ok, false);
});

test('deadman release, observer stop, exclusive ownership and disconnect cannot replay a target', async t => {
  const f = await controllerFixture(t);
  const observer = await f.connect('Observer');
  assert.equal((await f.send(observer, 'acquire')).ok, false);
  assert.equal((await f.send(observer, 'command', { leaseId: f.lease.leaseId, sequence: 1, deadman: true, pose: f.target() })).ok, false);
  assert.equal((await f.command(1)).ok, true);
  f.advance(20);
  assert.equal((await f.command(2, { deadman: false })).ok, true);
  const released = f.robot.driver.snapshot();
  f.advance(20);
  assert.deepEqual(f.robot.driver.snapshot().joints, released.joints);
  assert.equal(released.fault, null);
  assert.equal((await f.command(3)).ok, true);
  assert.equal((await f.send(observer, 'stop')).ok, true);
  assert.equal(f.robot.driver.snapshot().moving, false);
  assert.equal((await f.send(observer, 'reset', { leaseId: f.lease.leaseId })).ok, false);
  assert.equal((await f.send(f.owner, 'reset', { leaseId: f.lease.leaseId })).ok, true);
  f.owner.socket.close();
  assert.equal(f.robot.lease, null);
  assert.ok(f.robot.driver.snapshot().fault);
});

test('initial telemetry received before the first server tick can authorize the first enabled command', async t => {
  const config = await makeConfig(t);
  const controller = new WorkshopController(config, { now: () => 1000 });
  t.after(() => controller.close());
  const socket = new MemorySocket();
  const client = controller.attach(socket);
  await controller.handle(client, { type: 'auth', id: 'auth', protocolVersion: 1, accessToken: TOKEN });
  await controller.handle(client, { type: 'subscribe', id: 'subscribe', robotId: ROBOT });
  const telemetry = socket.messages.findLast(message => message.type === 'telemetry');
  assert.equal(telemetry.sequence, 0);
  await controller.handle(client, { type: 'acquire', id: 'acquire', robotId: ROBOT });
  const lease = socket.messages.find(message => message.requestId === 'acquire');
  await controller.handle(client, {
    type: 'command', id: 'first-command', robotId: ROBOT, leaseId: lease.leaseId,
    sequence: 1, observedSequence: telemetry.sequence, deadman: true,
    pose: { position: { ...telemetry.tool.position, y: telemetry.tool.position.y + 0.02 }, orientation: telemetry.tool.orientation }, gripper: 0.5
  });
  assert.equal(socket.messages.find(message => message.requestId === 'first-command').ok, true);
});

test('controller shutdown awaits active recording footer persistence', async t => {
  const f = await controllerFixture(t);
  const started = await f.send(f.owner, 'recording.start', { leaseId: f.lease.leaseId });
  f.advance(100);
  await f.controller.close();
  const file = path.join(f.config.dataDir, 'recordings', `${started.recording.id}.jsonl`);
  const rows = (await readFile(file, 'utf8')).trim().split('\n').map(line => JSON.parse(line));
  assert.equal(rows.at(-1).type, 'footer');
  assert.ok(rows.at(-1).samples >= 1);
});

test('physical-mode application stop latch blocks a permissive driver and reset never resets hardware', async t => {
  const config = await makeConfig(t);
  config.robots = [{ id: ROBOT, name: 'Injected physical transport', mode: 'rosbridge' }];
  const feedback = { connected: true, telemetryFresh: true };
  const calls = { command: 0, stop: 0, reset: 0 };
  const pose = { position: { x: 0.5, y: 0.6, z: 0 }, orientation: [0, 0, 0, 1] };
  const driver = {
    command() { calls.command++; return { ok: true }; },
    stop() { calls.stop++; },
    reset() { calls.reset++; return { ok: true }; },
    tick() {},
    snapshot() { return { ...feedback, moving: true, telemetry: { ...pose, gripper: 0.5, joints: [0, 0, 0, 0, 0, 0] } }; }
  };
  const controller = new WorkshopController(config, { now: () => 1000, drivers: { [ROBOT]: driver } });
  t.after(() => controller.close());
  const socket = new MemorySocket();
  const client = controller.attach(socket);
  let messageId = 0;
  const send = async (type, fields = {}) => {
    const id = `physical-${++messageId}`;
    await controller.handle(client, { type, id, robotId: ROBOT, ...fields });
    return socket.messages.find(message => message.requestId === id);
  };
  await send('auth', { protocolVersion: 1, accessToken: TOKEN });
  await send('subscribe');
  const lease = await send('acquire');
  const robot = controller.robots.get(ROBOT);
  // Even a client-supplied teleop flag must not activate simulation stepping for hardware.
  const command = sequence => send('command', { leaseId: lease.leaseId, sequence, observedSequence: 0, deadman: true, teleop: true, pose, gripper: 0.5 });
  assert.equal((await command(1)).ok, true);
  assert.equal(calls.command, 1);
  assert.equal((await send('stop')).ok, true);
  assert.equal((await command(2)).ok, false);
  assert.equal(calls.command, 1, 'The application must block motion even if the physical driver would accept it.');
  assert.ok(calls.stop >= 1);
  const stopped = controller.snapshot(robot);
  assert.equal(stopped.motionKnown, false, 'No physical stationary-state claim may be inferred from requested motion.');
  assert.ok(stopped.fault);
  feedback.connected = false;
  assert.equal((await send('reset', { leaseId: lease.leaseId })).ok, false);
  feedback.connected = true;
  feedback.telemetryFresh = false;
  assert.equal((await send('reset', { leaseId: lease.leaseId })).ok, false);
  feedback.telemetryFresh = true;
  const reset = await send('reset', { leaseId: lease.leaseId });
  assert.equal(reset.ok, true);
  assert.equal(reset.scope, 'application');
  assert.equal(reset.hardwareReset, false);
  assert.equal(calls.reset, 0, 'An app reset must never call the physical hardware reset.');
  assert.equal(calls.command, 1, 'Reset itself must not replay a command.');
  assert.equal((await command(3)).ok, true);
  assert.equal(calls.command, 2);
});

test('remote server binding requires TLS certificate, key and a nondefault strong token', async () => {
  await assert.rejects(loadConfig({ HOST: '0.0.0.0' }), /TLS/);
  await assert.rejects(loadConfig({ HOST: '0.0.0.0', VR_ACCESS_TOKEN: TOKEN }), /TLS/);
  await assert.rejects(loadConfig({ HOST: '0.0.0.0', VR_ACCESS_TOKEN: 'short', TLS_CERT_FILE: 'cert.pem', TLS_KEY_FILE: 'key.pem' }), /TLS/);
  const secure = await loadConfig({ HOST: '0.0.0.0', VR_ACCESS_TOKEN: TOKEN, TLS_CERT_FILE: 'cert.pem', TLS_KEY_FILE: 'key.pem' });
  assert.equal(secure.accessToken, TOKEN);
  const local = await loadConfig({ HOST: '127.0.0.1' });
  assert.equal(local.accessToken, 'local-training-only');
});

test('real HTTP and WebSocket enforce authorization and disabled LLM returns explicitly labelled local rules', async t => {
  const f = await serverFixture(t);
  assert.equal((await fetch(`${f.base}/health`)).status, 200);
  assert.equal((await fetch(`${f.base}/api/status`)).status, 401);
  const response = await fetch(`${f.base}/api/status`, { headers: { authorization: `Bearer ${TOKEN}` } });
  assert.equal(response.status, 200);
  assert.equal((await response.json()).integrations.ollama.status, 'DISABLED');
  const rejected = await f.connect();
  const closed = once(rejected.ws, 'close');
  const auth = await rejected.request('auth', { protocolVersion: 1, accessToken: 'wrong-token' });
  assert.equal(auth.ok, false);
  assert.equal((await closed)[0], 1008);
  const peer = await f.connect('Advisor client');
  const advice = await peer.request('ai.request', { prompt: 'Объясни текущее положение робота' });
  assert.equal(advice.type, 'ai.result');
  assert.equal(advice.ok, true);
  assert.equal(advice.executable, false);
  assert.equal(advice.source, 'rules');
  assert.ok(advice.notes.some(note => note.includes('языковая модель не использована')));
  assert.match(advice.fallbackReason, /OLLAMA_MODEL/);
  const foreign = wireClient(f.base.replace('http:', 'ws:') + '/ws', { origin: 'https://untrusted.example' });
  try { await assert.rejects(foreign.open()); } finally { foreign.close(); }
});

test('real WebSocket motion, exclusive lease, recordings, stop, release and disconnect work end to end', async t => {
  const f = await serverFixture(t);
  const owner = await f.connect('Headset operator');
  const observer = await f.connect('Control room');
  assert.equal((await owner.request('subscribe')).ok, true);
  assert.equal((await observer.request('subscribe')).ok, true);
  const lease = await owner.request('acquire');
  assert.equal(lease.ok, true);
  assert.equal((await observer.request('acquire')).ok, false);
  const telemetry = await owner.next(value => value.type === 'telemetry' && value.sequence > 0, { fresh: true });
  const recording = await owner.request('recording.start', { leaseId: lease.leaseId });
  assert.equal(recording.ok, true);
  const goal = { position: { ...telemetry.tool.position, y: telemetry.tool.position.y + 0.03 }, orientation: telemetry.tool.orientation };
  assert.equal((await owner.request('command', { leaseId: lease.leaseId, sequence: 1, observedSequence: telemetry.sequence, deadman: true, pose: goal, gripper: 0.2 })).ok, true);
  const moved = await owner.next(value => value.type === 'telemetry' && value.joints.some((joint, i) => Math.abs(joint - telemetry.joints[i]) > 1e-7));
  assert.equal(moved.mode, 'simulation');
  assert.ok(moved.links.length >= 7);
  assert.deepEqual(moved.tool.position, forwardKinematics(defaultRobotConfig, moved.joints).position);
  const sampled = await owner.next(value => value.type === 'telemetry' && value.recording.samples >= 2, { fresh: true });
  assert.ok(sampled.recording.active);
  const ended = await owner.request('recording.stop', { leaseId: lease.leaseId });
  assert.equal(ended.ok, true);
  assert.ok(ended.recording.samples >= 2);
  const headers = { authorization: `Bearer ${TOKEN}` };
  const records = await (await fetch(`${f.base}/api/recordings`, { headers })).json();
  assert.ok(records.some(record => record.id === ended.recording.id && record.active === false));
  const exported = await (await fetch(`${f.base}/api/recordings/${ended.recording.id}`, { headers })).text();
  const persisted = await readFile(path.join(f.config.dataDir, 'recordings', `${ended.recording.id}.jsonl`), 'utf8');
  assert.equal(exported, persisted);
  const rows = persisted.trim().split('\n').map(line => JSON.parse(line));
  assert.equal(rows[0].type, 'header');
  assert.equal(rows[0].robotId, ROBOT);
  assert.equal(rows.at(-1).type, 'footer');
  assert.equal(rows.filter(row => row.type === 'sample').length, ended.recording.samples);
  assert.ok(rows.filter(row => row.type === 'sample').every(row => row.joints.length === 6));
  assert.equal((await observer.request('stop')).ok, true);
  assert.equal((await owner.request('reset', { leaseId: lease.leaseId })).ok, true);
  assert.equal((await owner.request('release', { leaseId: lease.leaseId })).ok, true);
  const otherLease = await observer.request('acquire');
  assert.equal(otherLease.ok, true);
  assert.equal((await observer.request('reset', { leaseId: otherLease.leaseId })).ok, true);
  const disconnected = owner.next(value => value.type === 'telemetry' && value.leaseOwner === '' && !!value.fault, { fresh: true });
  observer.close();
  const final = await disconnected;
  assert.equal(final.moving, false);
  assert.ok(final.fault);
});

test('real socket closure stops active motion and finishes its active recording', async t => {
  const f = await serverFixture(t);
  const owner = await f.connect('Disconnect test');
  const observer = await f.connect('Observer');
  await owner.request('subscribe');
  await observer.request('subscribe');
  const lease = await owner.request('acquire');
  const telemetry = await owner.next(value => value.type === 'telemetry' && value.sequence > 0, { fresh: true });
  const recording = await owner.request('recording.start', { leaseId: lease.leaseId });
  const pose = { position: { ...telemetry.tool.position, y: telemetry.tool.position.y + 0.04 }, orientation: telemetry.tool.orientation };
  assert.equal((await owner.request('command', { leaseId: lease.leaseId, sequence: 1, observedSequence: telemetry.sequence, deadman: true, pose, gripper: 0.3 })).ok, true);
  await observer.next(value => value.type === 'telemetry' && value.moving, { fresh: true });
  const stopped = observer.next(value => value.type === 'telemetry' && value.leaseOwner === '' && !!value.fault, { fresh: true });
  owner.close();
  assert.equal((await stopped).moving, false);
  const file = path.join(f.config.dataDir, 'recordings', `${recording.recording.id}.jsonl`);
  let rows;
  for (let attempt = 0; attempt < 20; attempt++) {
    rows = (await readFile(file, 'utf8')).trim().split('\n').map(line => JSON.parse(line));
    if (rows.at(-1).type === 'footer') break;
    await pause(10);
  }
  assert.equal(rows.at(-1).type, 'footer');
  assert.match(rows.at(-1).reason, /отключ/i);
});

test('watchAll streams four independent robots while leases and commands remain robot-specific', async t => {
  const f = await serverFixture(t);
  assert.deepEqual(f.config.robots.map(robot => robot.id), [ROBOT, SECOND_ROBOT, 'paint-arm', 'paint-arm-2']);
  assert.equal(new Set(f.config.robots.map(robot => robot.name)).size, 4);
  const owner = await f.connect('Multiple workstation operator');
  const observer = await f.connect('All workstations observer');
  const welcome = await owner.next(message => message.type === 'welcome');
  assert.equal(welcome.robots.length, 4);
  assert.ok(welcome.robots.every(robot => robot.mode === 'simulation'));
  assert.equal((await owner.request('subscribe', { watchAll: true })).ok, true);
  assert.equal((await observer.request('subscribe', { watchAll: true })).ok, true);
  const initialA = await owner.next(message => message.type === 'telemetry' && message.robotId === ROBOT);
  const initialB = await owner.next(message => message.type === 'telemetry' && message.robotId === SECOND_ROBOT);
  const leaseA = await owner.request('acquire');
  assert.equal(leaseA.ok, true);
  assert.equal((await observer.request('acquire')).ok, false);
  const freshA = await owner.next(message => message.type === 'telemetry' && message.robotId === ROBOT, { fresh: true });
  const poseA = { position: { ...freshA.tool.position, y: freshA.tool.position.y + 0.03 }, orientation: freshA.tool.orientation };
  const commandA = { leaseId: leaseA.leaseId, sequence: 1, observedSequence: freshA.sequence, deadman: true, pose: poseA, gripper: 0.3 };
  assert.equal((await observer.request('command', commandA)).ok, false, 'Observing all robots cannot use another client\'s lease.');
  assert.equal((await owner.request('command', { ...commandA, robotId: SECOND_ROBOT })).ok, false, 'A lease for A cannot control B.');
  assert.equal((await owner.request('command', commandA)).ok, true);
  const movedA = await owner.next(message => message.type === 'telemetry' && message.robotId === ROBOT && message.joints.some((q, i) => Math.abs(q - initialA.joints[i]) > 1e-6), { fresh: true });
  const stillB = await owner.next(message => message.type === 'telemetry' && message.robotId === SECOND_ROBOT && message.sequence >= movedA.sequence);
  assert.deepEqual(stillB.joints, initialB.joints);
  assert.deepEqual(stillB.tool, initialB.tool);
  assert.equal(stillB.leaseOwner, '');

  assert.equal((await owner.request('release', { leaseId: leaseA.leaseId })).ok, true);
  const stoppedA = await owner.next(message => message.type === 'telemetry' && message.robotId === ROBOT && message.leaseOwner === '' && !message.moving, { fresh: true });
  assert.equal((await owner.request('subscribe', { robotId: SECOND_ROBOT, watchAll: true })).ok, true);
  const leaseB = await owner.request('acquire', { robotId: SECOND_ROBOT });
  assert.equal(leaseB.ok, true);
  assert.notEqual(leaseB.leaseId, leaseA.leaseId);
  assert.equal((await owner.request('reset', { robotId: SECOND_ROBOT, leaseId: leaseB.leaseId })).ok, true);
  const freshB = await owner.next(message => message.type === 'telemetry' && message.robotId === SECOND_ROBOT, { fresh: true });
  const poseB = { position: { ...freshB.tool.position, y: freshB.tool.position.y + 0.025 }, orientation: freshB.tool.orientation };
  assert.equal((await owner.request('command', { ...commandA, sequence: 2 })).ok, false, 'A released lease stays unusable after switching.');
  assert.equal((await owner.request('command', { robotId: SECOND_ROBOT, leaseId: leaseB.leaseId, sequence: 1, observedSequence: freshB.sequence, deadman: true, pose: poseB, gripper: 0.4 })).ok, true);
  const movedB = await owner.next(message => message.type === 'telemetry' && message.robotId === SECOND_ROBOT && message.joints.some((q, i) => Math.abs(q - initialB.joints[i]) > 1e-6), { fresh: true });
  const stillA = await owner.next(message => message.type === 'telemetry' && message.robotId === ROBOT && message.sequence >= movedB.sequence);
  assert.deepEqual(stillA.joints, stoppedA.joints);
  assert.equal(stillA.moving, false);
  assert.equal((await owner.request('command', { robotId: SECOND_ROBOT, leaseId: leaseB.leaseId, sequence: 2, observedSequence: -1, deadman: true, pose: poseB, gripper: 0.4 })).ok, false, 'Watching all robots still requires fresh observed telemetry.');
  assert.equal((await owner.request('release', { robotId: SECOND_ROBOT, leaseId: leaseB.leaseId })).ok, true);
});

test('ordinary subscribe keeps only the selected robot after watchAll was enabled', async t => {
  const f = await serverFixture(t);
  const peer = await f.connect('Single workstation observer');
  assert.equal((await peer.request('subscribe', { watchAll: true })).ok, true);
  await peer.next(message => message.type === 'telemetry' && message.robotId === SECOND_ROBOT);
  assert.equal((await peer.request('subscribe')).ok, true);
  peer.messages.length = 0;
  const first = await peer.next(message => message.type === 'telemetry' && message.robotId === ROBOT, { fresh: true });
  await peer.next(message => message.type === 'telemetry' && message.robotId === ROBOT && message.sequence >= first.sequence + 3, { fresh: true });
  assert.equal(peer.messages.some(message => message.type === 'telemetry' && message.robotId === SECOND_ROBOT), false);
});
