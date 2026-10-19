import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, writeFile, rename, rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { EventEmitter } from 'node:events';
import { AllurBrief, validateBrief } from '../server/allur-brief.js';
import { WorkshopController } from '../server/controller.js';
import { loadConfig } from '../server/config.js';

const sample = time => ({ available: true, generatedAtUnixMs: time, source: 'Учебный VR-цех', summary: 'Сварка, окраска, выпуск',
  activeVehicles: 2, readyVehicles: 1, registeredVehicles: 3, qualityPassed: 1, qualityRejected: 0,
  alerts: ['Учебные данные'], robotInsights: [{ robotId: 'training-arm', status: 'В работе', cycles: 4, condition: 'Автоматическая сварка' }] });

async function fixture(t) {
  const dir = await mkdtemp(path.join(os.tmpdir(), 'allurtwin-brief-'));
  t.after(async () => {
    assert.equal(path.dirname(dir), path.resolve(os.tmpdir()));
    assert.ok(path.basename(dir).startsWith('allurtwin-brief-'));
    await rm(dir, { recursive: true, force: true, maxRetries: 3, retryDelay: 50 });
  });
  return { dir, file: path.join(dir, 'brief.json') };
}

test('atomic Allur brief is relayed, cached, expires and recovers', async t => {
  const { file } = await fixture(t);
  let now = 1700000000000;
  const relay = new AllurBrief(file, { now: () => now, start: false });
  t.after(() => relay.close());
  await writeFile(file, JSON.stringify(sample(now)));
  await relay.poll();
  assert.deepEqual(relay.snapshot(), sample(now));
  const updated = { ...sample(now), readyVehicles: 2 };
  await writeFile(file + '.tmp', JSON.stringify(updated));
  await rename(file + '.tmp', file);
  await relay.poll();
  assert.equal(relay.snapshot().readyVehicles, 1, 'Reads must be limited to one per second');
  now += 1000; await relay.poll();
  assert.equal(relay.snapshot().readyVehicles, 2);
  now += 4001;
  assert.equal(relay.snapshot().available, false, 'Frozen file must not stay live');
  assert.equal(relay.snapshot().readyVehicles, 2, 'Last observation is retained as stale');
  await writeFile(file, JSON.stringify(sample(now))); await relay.poll();
  assert.equal(relay.snapshot().available, true);
});

test('missing, malformed, oversized and future brief cannot claim current analytics', async t => {
  const { file } = await fixture(t);
  let now = 1700000000000;
  const relay = new AllurBrief(file, { now: () => now, start: false });
  t.after(() => relay.close());
  await relay.poll(); assert.equal(relay.snapshot().available, false);
  for (const content of ['{', 'x'.repeat(32769), JSON.stringify({ ...sample(now), qualityPassed: -1 }), JSON.stringify(sample(now + 20000))]) {
    now += 1000; await writeFile(file, content); await relay.poll();
    assert.equal(relay.snapshot().available, false);
  }
  assert.throws(() => validateBrief({ ...sample(now), alerts: Array(9).fill('x') }));
  assert.throws(() => validateBrief({ ...sample(now), robotInsights: [sample(now).robotInsights[0], sample(now).robotInsights[0]] }));
});

test('Allur relay is read-only in control path and emitted at one hertz', async t => {
  const { dir } = await fixture(t);
  const config = await loadConfig({ DATA_DIR: dir, PORT: '0' });
  let clock = 0, snapshots = 0;
  const brief = sample(Date.now());
  const allur = { snapshot: () => { snapshots++; return brief; }, close: async () => {} };
  const controller = new WorkshopController(config, { now: () => clock, allur });
  t.after(() => controller.close());
  class Socket extends EventEmitter {
    readyState = 1; bufferedAmount = 0; messages = [];
    send(message) { this.messages.push(JSON.parse(message)); }
    close() { this.readyState = 3; this.emit('close'); }
  }
  const socket = new Socket(), client = controller.attach(socket);
  await controller.handle(client, { type: 'auth', id: 'auth-brief', protocolVersion: 1, accessToken: config.accessToken });
  assert.deepEqual(socket.messages.find(message => message.type === 'welcome').allur, brief);
  await controller.handle(client, { type: 'subscribe', id: 'subscribe-brief', robotId: 'training-arm', watchAll: true });
  socket.messages = []; snapshots = 0;
  for (let i = 0; i < 50; i++) { clock += 20; controller.tick(); }
  const messages = socket.messages.filter(message => message.type === 'telemetry');
  assert.equal(messages.length, 200, 'Motion telemetry keeps 50 Hz for every robot');
  assert.equal(messages.filter(message => message.allur).length, 4, 'Brief is sent once per second per subscribed robot');
  assert.equal(snapshots, 4);
  assert.deepEqual(controller.status().allur, brief);
  assert.ok([...controller.robots.values()].every(robot => !robot.lease && !robot.deadman), 'Analytics must not acquire control');
});
