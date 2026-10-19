import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { once } from 'node:events';
import { setTimeout as delay } from 'node:timers/promises';
import { WebSocketServer } from 'ws';
import { RosbridgeRobot } from '../server/integrations/rosbridge.js';
import { SiemensValidationGateway } from '../server/integrations/siemens.js';
import { OllamaAdvisor } from '../server/integrations/ollama.js';
import { rulesAdvice } from '../server/integrations/rules.js';

async function httpFixture(t, handler) {
  const server = http.createServer(handler);
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  t.after(() => { server.closeAllConnections(); server.close(); });
  return `http://127.0.0.1:${server.address().port}`;
}

async function jsonBody(req) {
  const chunks = [];
  for await (const chunk of req) chunks.push(chunk);
  return JSON.parse(Buffer.concat(chunks).toString('utf8'));
}

function reply(res, data) {
  res.writeHead(200, { 'content-type': 'application/json' });
  res.end(JSON.stringify(data));
}

async function until(predicate) {
  const deadline = Date.now() + 1500;
  while (!predicate()) {
    if (Date.now() > deadline) throw new Error('Fixture condition timed out');
    await delay(5);
  }
}

const pose = { position: { x: 0.4, y: 0.8, z: 0.1 }, orientation: [0, 0, 0, 1] };
const trajectory = [{ t: 0, ...pose, gripper: 0.4 }];

test('Ollama is disabled without a model, and never returns rules as AI', async () => {
  const advisor = new OllamaAdvisor({}, { fetchImpl: () => { throw new Error('Must not fetch'); } });
  assert.equal(advisor.snapshot().status, 'DISABLED');
  const result = await advisor.advise({}, 'Проверить положение');
  assert.equal(result.ok, false);
  assert.equal(result.source, 'disabled');
});

test('Ollama sends a real structured generation request and bounds the output', async t => {
  let request;
  const url = await httpFixture(t, async (req, res) => {
    assert.equal(req.url, '/api/generate');
    request = await jsonBody(req);
    reply(res, { done: true, response: JSON.stringify({ title: 'Положение', summary: 'Проверьте инструмент.', notes: ['Сравните телеметрию с моделью.'] }) });
  });
  const advisor = new OllamaAdvisor({ url, model: 'fixture-model' });
  const result = await advisor.advise({ robot: pose }, 'Проверить положение');
  assert.equal(result.ok, true);
  assert.equal(result.executable, false);
  assert.equal(request.model, 'fixture-model');
  assert.equal(request.stream, false);
  assert.equal(request.think, false);
  assert.equal(request.format.additionalProperties, false);
  assert.equal(JSON.parse(request.prompt).telemetry.robot.position.y, 0.8);
  assert.equal(advisor.snapshot().status, 'READY');
});

test('Ollama rejects an invented cause from missing stop information without mislabelling rules as AI', async t => {
  let summary = '';
  const url = await httpFixture(t, (req, res) => reply(res, { done: true, response: JSON.stringify({ title: 'Состояние линии', summary, notes: [] }) }));
  const advisor = new OllamaAdvisor({ url, model: 'fixture-model' });
  const snapshot = { production: { running: false, stopReason: '', completed: 0, stations: [{}, {}], parts: [{}] } };
  const question = 'Сколько роботов на линии, сколько готовых панелей выпущено и почему сейчас конвейер стоит?';
  for (const wrong of [
    'На линии 2 робота. Конвейер остановлен из-за отсутствия причины в данных.',
    'Конвейер стоит, потому что нет сведений о причине остановки.',
    'Причиной остановки является отсутствие данных.',
  ]) {
    summary = wrong;
    const result = await advisor.advise(snapshot, question);
    assert.equal(result.ok, false);
    assert.match(result.error, /отсутствие сведений/);
    const fallback = rulesAdvice(snapshot, question, result.error);
    assert.equal(fallback.source, 'rules');
    assert.equal(fallback.executable, false);
    assert.match(fallback.summary, /Причина остановки не указана/);
  }
  summary = 'На линии 2 робота, готовых панелей выпущено 0. Конвейер стоит. Причина остановки не указана.';
  assert.equal((await advisor.advise(snapshot, question)).ok, true);
});

test('Ollama rejects executable fields and incomplete or oversized replies', async t => {
  let data = { done: true, response: JSON.stringify({ title: 'x', summary: 'y', notes: [], command: { move: true } }) };
  const url = await httpFixture(t, (req, res) => reply(res, data));
  const advisor = new OllamaAdvisor({ url, model: 'fixture-model' });
  assert.equal((await advisor.advise({}, 'task')).ok, false);
  data = { done: false, response: '{}' };
  assert.equal((await advisor.advise({}, 'task')).ok, false);
  data = { done: true, response: 'x'.repeat(40000) };
  const oversized = await advisor.advise({}, 'task');
  assert.equal(oversized.ok, false);
  assert.match(oversized.error, /large/);
});

test('Ollama deadline covers response body stalls after successful headers', async t => {
  const url = await httpFixture(t, (req, res) => {
    res.writeHead(200, { 'content-type': 'application/json' });
    res.write('{"done":');
  });
  const advisor = new OllamaAdvisor({ url, model: 'fixture-model', timeoutMs: 80 });
  const start = Date.now();
  const result = await advisor.advise({}, 'task');
  assert.equal(result.ok, false);
  assert.match(result.error, /deadline/);
  assert.ok(Date.now() - start < 1500);
  assert.equal(advisor.snapshot().busy, false);
});

function validationResponse(input, overrides = {}) {
  return {
    schema: input.schema, requestId: input.requestId, robotId: input.robotId,
    modelRevision: input.modelRevision, trajectoryHash: input.trajectoryHash,
    coordinateFrame: input.coordinateFrame, engine: 'Siemens Process Simulate', allowed: true, issues: [], ...overrides
  };
}

test('Siemens gateway is disabled by default and denies without configured revision', async () => {
  const client = new SiemensValidationGateway();
  const result = await client.validate({ robotId: 'r1', trajectory });
  assert.equal(result.allowed, false);
  assert.equal(client.snapshot().status, 'DISABLED');
});

test('Siemens gateway correlates request, model, frame, robot and exact path', async t => {
  let input;
  const url = await httpFixture(t, async (req, res) => {
    assert.equal(req.headers.authorization, 'Bearer fixture-token');
    input = await jsonBody(req);
    reply(res, validationResponse(input));
  });
  const gateway = new SiemensValidationGateway({ url, modelRevision: 'cell-v4', bearerToken: 'fixture-token' });
  const result = await gateway.validate({ robotId: 'r1', trajectory });
  assert.equal(result.ok, true);
  assert.equal(result.allowed, true);
  assert.equal(result.modelRevision, 'cell-v4');
  assert.equal(input.trajectory[0].position.x, 0.4);
  assert.match(input.trajectoryHash, /^[a-f0-9]{64}$/);
  assert.equal(result.trajectoryHash, input.trajectoryHash);
});

test('Siemens gateway denies mismatched, replayed and contradictory answers', async t => {
  let overrides;
  const url = await httpFixture(t, async (req, res) => reply(res, validationResponse(await jsonBody(req), overrides)));
  const gateway = new SiemensValidationGateway({ url, modelRevision: 'cell-v4' });
  for (const override of [
    { requestId: 'replayed-request' }, { modelRevision: 'cell-v3' }, { trajectoryHash: 'wrong-path' },
    { robotId: 'other-robot' }, { coordinateFrame: 'wrong-frame' }, { engine: 'local fixture' },
    { allowed: true, issues: ['Collision'] }, { allowed: 'true' }
  ]) {
    overrides = override;
    const result = await gateway.validate({ robotId: 'r1', trajectory });
    assert.equal(result.allowed, false, JSON.stringify(override));
  }
});

test('Siemens gateway denies a response arriving after its deadline', async t => {
  const url = await httpFixture(t, (req, res) => {
    res.writeHead(200, { 'content-type': 'application/json' });
    res.write('{');
  });
  const gateway = new SiemensValidationGateway({ url, modelRevision: 'cell-v4', timeoutMs: 60 });
  const result = await gateway.validate({ robotId: 'r1', trajectory });
  assert.equal(result.allowed, false);
  assert.match(result.error, /deadline/);
});

async function rosFixture(t, commissioned = true, now = Date.now) {
  const messages = [];
  const wss = new WebSocketServer({ host: '127.0.0.1', port: 0 });
  await once(wss, 'listening');
  t.after(() => { for (const client of wss.clients) client.terminate(); wss.close(); });
  let socket;
  let authorization;
  wss.on('connection', (client, request) => {
    socket = client;
    authorization = request.headers.authorization;
    client.on('message', raw => {
      const message = JSON.parse(raw.toString());
      messages.push(message);
      if (message.op === 'subscribe') client.send(JSON.stringify({ op: 'publish', topic: '/fixture/state', msg: { pose, joints: [0, 0, 0, 0, 0, 0], gripper: 0.4 } }));
    });
  });
  const config = {
    url: `ws://127.0.0.1:${wss.address().port}`, commissioned, bearerToken: 'fixture-token',
    telemetry: { topic: '/fixture/state', type: 'fixture/State', positionPath: 'pose.position', orientationPath: 'pose.orientation', jointsPath: 'joints', gripperPath: 'gripper' },
    command: { topic: '/fixture/target', type: 'fixture/Target', template: { x: '$pose.position.x', y: '$pose.position.y', z: '$pose.position.z', qw: '$pose.orientation.3', grip: '$gripper', sequence: '$sequence' } },
    stop: { topic: '/fixture/stop', type: 'fixture/Stop', template: { stop: true, reason: '$reason' } }
  };
  const robot = new RosbridgeRobot(config, { now });
  t.after(() => robot.disconnect());
  assert.equal((await robot.connect()).ok, true);
  await until(() => robot.snapshot().status === 'READY');
  return { robot, messages, config, socket, authorization };
}

test('ROS adapter authenticates, subscribes and publishes mapped messages to fixture', async t => {
  const { robot, messages, authorization } = await rosFixture(t);
  assert.equal(authorization, 'Bearer fixture-token');
  const result = robot.command(pose, 0.6);
  assert.equal(result.ok, true);
  assert.equal(result.acknowledged, false);
  await until(() => messages.some(message => message.topic === '/fixture/target' && message.op === 'publish'));
  const command = messages.find(message => message.topic === '/fixture/target' && message.op === 'publish');
  assert.deepEqual(command.msg, { x: 0.4, y: 0.8, z: 0.1, qw: 1, grip: 0.6, sequence: 1 });
  assert.deepEqual(robot.snapshot().telemetry.joints, [0, 0, 0, 0, 0, 0]);
  robot.stop('Released trigger');
  await until(() => messages.some(message => message.topic === '/fixture/stop' && message.op === 'publish'));
  assert.equal(robot.snapshot().motionRequested, false);
});

test('ROS monitor mode never advertises or publishes robot controls', async t => {
  const { robot, messages } = await rosFixture(t, false);
  assert.equal(robot.command(pose, 0.5).ok, false);
  robot.stop('ignored in monitor mode');
  await delay(20);
  assert.equal(messages.filter(message => ['advertise', 'publish'].includes(message.op)).length, 0);
});

test('ROS command watchdog stops motion and stale telemetry blocks new targets', async t => {
  let time = 1000;
  const { robot, messages } = await rosFixture(t, true, () => time);
  assert.equal(robot.command(pose, 0.5).ok, true);
  time += 300;
  robot.tick();
  assert.equal(robot.snapshot().motionRequested, false);
  assert.match(robot.snapshot().stopReason, /watchdog/);
  await until(() => messages.some(message => message.topic === '/fixture/stop' && message.op === 'publish'));
  time += 300;
  assert.equal(robot.command(pose, 0.5).ok, false);
  assert.equal(robot.snapshot().telemetryFresh, false);
});

test('ROS malformed telemetry closes the transport and inhibits motion', async t => {
  const { robot, socket } = await rosFixture(t);
  socket.send(JSON.stringify({ op: 'publish', topic: '/fixture/state', msg: { pose: { ...pose, orientation: [0, 0, 0, 0] } } }));
  await until(() => robot.snapshot().status !== 'READY');
  assert.equal(robot.command(pose, 0.5).ok, false);
  assert.equal(robot.snapshot().motionRequested, false);
});

test('ROS null JSON envelope cannot crash the server', async t => {
  const { robot, socket } = await rosFixture(t);
  socket.send('null');
  await until(() => robot.snapshot().status !== 'READY');
  assert.equal(robot.command(pose, 0.5).ok, false);
  assert.match(robot.snapshot().lastError, /envelope/);
});

test('ROS adapter refuses guessed mappings, unsafe templates and absent stop routes', async () => {
  const telemetry = { topic: '/state', type: 'fixture/State', positionPath: 'p', orientationPath: 'q' };
  for (const config of [
    { url: 'ws://127.0.0.1:1', commissioned: true },
    { url: 'http://127.0.0.1:1' },
    { url: 'ws://127.0.0.1:1', telemetry: { ...telemetry, positionPath: '__proto__.x' } },
    { url: 'ws://127.0.0.1:1', telemetry, commissioned: true, command: { topic: '/move', type: 'fixture/Move', template: { x: '$pose.position.x' } } },
    { url: 'ws://127.0.0.1:1', telemetry, command: { topic: '/move', type: 'fixture/Move', template: { x: '$arbitraryCode' } } },
    { url: 'ws://127.0.0.1:1', telemetry, stop: { topic: '/stop', type: 'fixture/Stop', template: { x: '$pose.position.x' } } }
  ]) {
    const robot = new RosbridgeRobot(config);
    const result = await robot.connect();
    assert.equal(result.ok, false);
    assert.equal(robot.snapshot().status, 'MISCONFIGURED');
  }
});
