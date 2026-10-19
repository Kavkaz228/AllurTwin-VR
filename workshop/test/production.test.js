import test from 'node:test';
import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { mkdtemp, rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { WorkshopController } from '../server/controller.js';
import { loadConfig } from '../server/config.js';
import { forwardKinematics, checkConfiguration } from '../server/robot/index.js';
import { rulesAdvice } from '../server/integrations/rules.js';
import { productionLayout, transferLayout, finishingLayout } from '../server/production.js';

class Socket extends EventEmitter {
  readyState = 1; bufferedAmount = 0; messages = [];
  send(data) { this.messages.push(JSON.parse(data)); if (this.messages.length > 128) this.messages.shift(); }
  close() { this.readyState = 3; this.emit('close'); }
}

async function fixture(t, modify = () => {}) {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'vr-production-test-'));
  const config = await loadConfig({ DATA_DIR: directory, VR_ACCESS_TOKEN: 'production-test-token' });
  modify(config);
  let now = 1000, sequence = 0;
  const controller = new WorkshopController(config, { now: () => now });
  t.after(async () => { await controller.close(); assert.equal(path.dirname(directory), path.resolve(os.tmpdir())); await rm(directory, { recursive: true, force: true, maxRetries: 5, retryDelay: 50 }); });
  const send = async (client, type, fields = {}) => {
    const id = `request-${++sequence}`;
    await controller.handle(client, { type, id, robotId: 'training-arm', ...fields });
    return client.socket.messages.find(message => message.requestId === id);
  };
  const connect = async name => {
    const client = controller.attach(new Socket());
    await send(client, 'auth', { protocolVersion: 1, accessToken: config.accessToken, clientName: name });
    await send(client, 'subscribe', { watchAll: true });
    return client;
  };
  const owner = await connect('AUTO operator');
  const advance = milliseconds => { now += milliseconds; controller.tick(); };
  return { config, controller, send, connect, owner, advance, now: () => now };
}

const paintedBody = id => ({ id, position: productionLayout.length, processed: 2, status: 'finished', robotId: '', processProgress: 1, weldProgress: 1, coating: 1 });

test('finishing retains four complete cars, reserves slots and applies backpressure without losing the fifth body', async t => {
  const f = await fixture(t), finishing = f.controller.finishing, painting = f.controller.productionLines.get('painting');
  const begun = await f.send(f.owner, 'finishing.start');
  const paintStart = await f.send(f.owner, 'production.start', { lineId: 'painting' });
  assert.equal(begun.ok, true); assert.equal(paintStart.ok, true);
  const tick = async index => {
    if (index % 10 === 0) {
      assert.equal((await f.send(f.owner, 'finishing.heartbeat', { finishingId: begun.finishingId })).ok, true);
      assert.equal((await f.send(f.owner, 'production.heartbeat', { lineId: 'painting', productionId: paintStart.productionId })).ok, true);
    }
    f.advance(20);
  };
  // Unit-stage inputs are fully processed painting outputs; the full pipeline test above creates its own from welding.
  for (let index = 0; index < 4; index++) {
    const id = `parking-fixture-${index}`;
    painting.parts.push(paintedBody(id));
    await tick(0);
    assert.equal(painting.parts.length, 0);
    assert.equal(finishing.active.id, id);
    assert.equal(finishing.active.slotIndex, index);
    assert.equal(finishing.snapshot().slots[index].reserved, true);
    assert.equal(finishing.snapshot().slots[index].occupied, false);
    assert.equal(finishing.accept(paintedBody('another')), false, 'The work bay has capacity one.');
    let previousPosition = { ...finishing.active.positionWorld };
    for (let step = 1; step < 5000 && finishing.completed <= index; step++) {
      const active = finishing.active;
      if (active?.status === 'lifting') { assert.equal(active.assemblyProgress, 0); assert.equal(active.positionWorld.z, -7); }
      if (active?.status === 'lowering') { assert.equal(active.positionWorld.x, -8); assert.equal(active.positionWorld.z, -14.3); }
      if (active && active.assemblyProgress > 0 && !['lowering', 'parking'].includes(active.status)) assert.equal(active.positionWorld.y, 1.13);
      await tick(step);
      const part = finishing.parts.find(item => item.id === id);
      assert.ok(Math.hypot(part.positionWorld.x - previousPosition.x, part.positionWorld.y - previousPosition.y, part.positionWorld.z - previousPosition.z) <= finishingLayout.speed * .02 + 1e-7, 'Server motion must remain speed bounded, including stage transitions.');
      previousPosition = { ...part.positionWorld };
      assert.equal(new Set(finishing.parts.map(item => item.id)).size, finishing.parts.length);
      for (const parked of finishing.parts.filter(item => item.status === 'parked')) assert.deepEqual(parked.positionWorld, finishingLayout.slots[parked.slotIndex]);
    }
    assert.equal(finishing.completed, index + 1);
  }
  const parked = structuredClone(finishing.parts);
  painting.parts.push(paintedBody('fifth-waits-on-paint'));
  for (let step = 0; step < 200; step++) await tick(step);
  assert.equal(finishing.parts.length, 4);
  assert.equal(finishing.phase, 'parking_full');
  assert.equal(finishing.snapshot().parkedCount, 4);
  assert.equal(finishing.snapshot().slots.filter(slot => slot.occupied).length, 4);
  assert.equal(painting.phase, 'waiting_finishing');
  assert.equal(painting.parts[0].id, 'fifth-waits-on-paint');
  assert.equal(painting.parts[0].position, productionLayout.length);
  assert.equal(painting.completed, 4);
  assert.equal(painting.completedIds.length, 4);
  assert.deepEqual(finishing.parts, parked);
  assert.equal((await f.send(f.owner, 'finishing.stop')).ok, true);
  assert.equal(painting.running, true, 'Finishing stop must not stop the painting session.');
  assert.equal((await f.send(f.owner, 'finishing.reset')).ok, true);
  assert.deepEqual(finishing.parts, parked, 'Reset cannot delete parked cars to free capacity.');
});

test('finishing rejects incomplete admission and preserves failed QC instead of issuing a ready vehicle', async t => {
  const f = await fixture(t), finishing = f.controller.finishing;
  const start = await f.send(f.owner, 'finishing.start');
  assert.equal(start.ok, true);
  for (const invalid of [{ weldProgress: .5 }, { coating: .9 }, { coating: NaN }, { processed: 1 }])
    assert.equal(finishing.accept({ ...paintedBody('bad'), ...invalid }), false);
  assert.equal(finishing.parts.length, 0);
  assert.equal(finishing.accept(paintedBody('qc-fixture')), true);
  for (let step = 0; step < 4000; step++) {
    if (step % 10 === 0) await f.send(f.owner, 'finishing.heartbeat', { finishingId: start.finishingId });
    // Simulate damaged coating evidence after admission, before the server's independent inspection.
    if (finishing.active.status === 'inspecting') finishing.active.coating = .85;
    f.advance(20);
    if (finishing.active.status === 'rejected') break;
  }
  const vehicle = finishing.active;
  assert.equal(vehicle.status, 'rejected');
  assert.equal(vehicle.quality, 'rejected');
  assert.equal(vehicle.ready, false);
  assert.equal(vehicle.weldPassed, true); assert.equal(vehicle.paintPassed, false); assert.equal(vehicle.assemblyPassed, true);
  assert.equal(vehicle.qualityChecks.find(check => check.name === 'paint').measured, .85);
  assert.equal(finishing.accept(paintedBody('cannot-pass-rejected')), false);
  assert.equal(finishing.completed, 0);
  const preserved = structuredClone(finishing.parts);
  await f.send(f.owner, 'finishing.stop');
  await f.send(f.owner, 'finishing.reset');
  const restart = await f.send(f.owner, 'finishing.start');
  assert.equal(restart.ok, true);
  for (let step = 0; step < 25; step++) f.advance(20);
  assert.equal(finishing.waiting, true);
  assert.deepEqual(finishing.parts, preserved, 'Stop/reset/start cannot erase a failed inspection or bypass it.');
});

test('finishing heartbeat, scheduler and disconnect stop independently, preserve progress and retain the server epoch on reconnect', async t => {
  const f = await fixture(t), finishing = f.controller.finishing;
  const lineOwner = await f.connect('independent painting operator');
  const paintStart = await f.send(lineOwner, 'production.start', { lineId: 'painting' });
  assert.equal(paintStart.ok, true);
  let start = await f.send(f.owner, 'finishing.start');
  finishing.accept(paintedBody('persistent-finish'));
  for (let step = 0; step < 12; step++) f.advance(20);
  const moved = structuredClone(finishing.parts);
  assert.notDeepEqual(moved[0].positionWorld, finishingLayout.entry);
  assert.equal((await f.send(lineOwner, 'finishing.heartbeat', { finishingId: start.finishingId })).ok, false);
  assert.equal(finishing.running, true, 'An outsider heartbeat cannot revoke the valid owner.');
  await f.send(f.owner, 'finishing.stop');
  assert.equal(f.controller.productionLines.get('painting').running, true);
  assert.equal((await f.send(f.owner, 'finishing.start')).ok, false);
  await f.send(f.owner, 'finishing.reset');
  assert.deepEqual(finishing.parts, moved);
  start = await f.send(f.owner, 'finishing.start');
  for (let step = 0; step < 101; step++) {
    if (step % 10 === 0) await f.send(lineOwner, 'production.heartbeat', { lineId: 'painting', productionId: paintStart.productionId });
    f.advance(20);
  }
  assert.equal(finishing.running, false);
  assert.match(finishing.stopReason, /heartbeat/);
  assert.equal(f.controller.productionLines.get('painting').running, true);
  assert.equal((await f.send(f.owner, 'finishing.heartbeat', { finishingId: start.finishingId })).ok, false);
  const timedOut = structuredClone(finishing.parts);
  f.advance(20); assert.deepEqual(finishing.parts, timedOut);
  await f.send(f.owner, 'finishing.reset');
  start = await f.send(f.owner, 'finishing.start');
  f.advance(301);
  assert.equal(finishing.running, false); assert.match(finishing.stopReason, /цикла/);
  assert.deepEqual(finishing.parts, timedOut);
  await f.send(f.owner, 'finishing.reset');
  await f.send(f.owner, 'finishing.start');
  const epoch = f.controller.serverSessionId;
  f.owner.socket.close();
  assert.equal(finishing.running, false);
  const reconnected = await f.connect('reconnected operator');
  const welcome = reconnected.socket.messages.find(message => message.type === 'welcome');
  assert.equal(welcome.serverSessionId, epoch);
  assert.deepEqual(welcome.finishing.parts, timedOut);
  assert.equal(f.controller.status().serverSessionId, epoch);
  assert.equal((await f.send(reconnected, 'finishing.start')).ok, false, 'Reconnect cannot automatically restart movement.');
  const other = await fixture(t);
  assert.notEqual(other.controller.serverSessionId, epoch, 'A new simulation server must have a new epoch.');
});

test('finishing rejects physical-line configuration without issuing hardware operations', async t => {
  const f = await fixture(t);
  let hardwareCalls = 0;
  const robot = f.controller.robots.get('paint-arm');
  robot.definition.mode = 'rosbridge';
  robot.driver.stop = () => { hardwareCalls++; };
  robot.driver.reset = () => { hardwareCalls++; };
  assert.equal((await f.send(f.owner, 'finishing.start')).ok, false);
  assert.equal((await f.send(f.owner, 'finishing.reset')).ok, false);
  assert.equal(f.controller.finishing.accept(paintedBody('physical-excluded')), false);
  assert.equal(hardwareCalls, 0);
});

test('conveyor panels follow the authoritative distance exactly and freeze after stop', async t => {
  const f = await fixture(t);
  const initial = f.controller.production.snapshot();
  assert.equal(initial.running, false);
  assert.equal(initial.parts.length, 1);
  assert.equal(initial.parts[0].position, 0);
  const started = await f.send(f.owner, 'production.start');
  assert.equal(started.ok, true);
  assert.ok(started.productionId);
  for (let i = 0; i < 25; i++) f.advance(20);
  const moved = f.controller.production.snapshot();
  assert.ok(Math.abs(moved.conveyorPosition - 0.35 * 0.5) < 1e-10);
  assert.ok(Math.abs(moved.parts[0].position - moved.conveyorPosition) < 1e-10);
  assert.equal(moved.parts[0].id, initial.parts[0].id);
  assert.equal(moved.phase, 'transport');
  assert.equal((await f.send(f.owner, 'production.stop')).ok, true);
  const stopped = f.controller.production.snapshot();
  f.advance(100);
  assert.deepEqual(f.controller.production.snapshot().parts, stopped.parts);
  assert.equal(stopped.speed, 0);
  assert.equal((await f.send(f.owner, 'production.start')).ok, false, 'Stop must require explicit reset.');
  assert.equal((await f.send(f.owner, 'production.reset')).ok, true);
  assert.deepEqual(f.controller.production.snapshot().parts, stopped.parts, 'Reset does not teleport panels or reset progress.');
});

test('both welding stations complete bounded FK/IK spot welds and a processed panel exits', async t => {
  const f = await fixture(t);
  const start = await f.send(f.owner, 'production.start');
  assert.equal(start.ok, true);
  const transferStart = await f.send(f.owner, 'transfer.start');
  assert.equal(transferStart.ok, true);
  const phases = [new Set(), new Set()];
  const changed = [false, false];
  const initial = f.controller.production.lineRobots().map(robot => robot.driver.snapshot().joints);
  let sawBothProcessed = false, previous = f.controller.production.lineRobots().map(robot => robot.driver.snapshot());
  for (let step = 0; step < 10000; step++) {
    if (step % 10 === 0) {
      assert.equal((await f.send(f.owner, 'production.heartbeat', { productionId: start.productionId })).ok, true);
      assert.equal((await f.send(f.owner, 'transfer.heartbeat', { transferId: transferStart.transferId })).ok, true);
    }
    f.advance(20);
    const state = f.controller.production.snapshot();
    for (let i = 1; i < state.parts.length; i++)
      assert.ok(state.parts[i-1].position - state.parts[i].position >= productionLayout.bodyLength + .25,
        'Full-size bodies and their carriers must not overlap on the conveyor.');
    assert.equal(state.running, true, state.stopReason);
    state.stations.forEach((station, i) => phases[i].add(station.phase));
    if (state.parts.some(part => part.processed === 2)) sawBothProcessed = true;
    if (state.phase === 'processing') assert.equal(state.speed, 0);
    f.controller.production.lineRobots().forEach((robot, i) => {
      const actual = robot.driver.snapshot();
      if (actual.joints.some((q, j) => Math.abs(q - initial[i][j]) > 0.001)) changed[i] = true;
      actual.joints.forEach((q, j) => assert.ok(Math.abs(q - previous[i].joints[j]) <= robot.driver.config.joints[j].maxSpeed * 0.02 + 1e-10));
      assert.deepEqual(actual.tool.position, forwardKinematics(robot.driver.config, actual.joints).position);
      assert.equal(checkConfiguration(robot.driver.config, actual.joints).ok, true);
      previous[i] = actual;
    });
    if (state.completed >= 1) break;
  }
  const finished = f.controller.production.snapshot();
  assert.equal(finished.completed >= 1, true, 'A panel must travel through both stations to the output.');
  assert.equal(sawBothProcessed, true);
  assert.deepEqual(changed, [true, true]);
  for (const phasesForRobot of phases) for (const phase of ['approach', 'lower', 'process', 'retract', 'return']) assert.ok(phasesForRobot.has(phase), phase);
  assert.ok(finished.stations.every(station => station.cycles >= 1));
  assert.ok(finished.parts.length <= 5);
  assert.equal(new Set(finished.parts.map(part => part.id)).size, finished.parts.length);
});

test('welding and painting independently complete with distinct active processes and authoritative coverage', async t => {
  const f = await fixture(t);
  const firstId = f.controller.production.parts[0].id;
  assert.equal(f.controller.productionLines.get('painting').parts.length, 0, 'Painting must receive welded bodies, never generate its own.');
  const transferStart = await f.send(f.owner, 'transfer.start');
  assert.equal(transferStart.ok, true);
  const finishingStart = await f.send(f.owner, 'finishing.start');
  assert.equal(finishingStart.ok, true);
  const starts = [];
  for (const lineId of ['welding', 'painting']) {
    const response = await f.send(f.owner, 'production.start', { lineId });
    assert.equal(response.ok, true, response.error);
    assert.equal(response.lineId, lineId);
    starts.push(response);
  }
  assert.notEqual(starts[0].productionId, starts[1].productionId);
  const active = new Set(), partialCoverage = new Set();
  const transferIds = new Set(), paintingIds = new Set(), finishingPhases = new Set();
  const previous = new Map([...f.controller.robots.values()].map(robot => [robot.definition.id, robot.driver.snapshot().joints]));
  for (let tick = 0; tick < 10000; tick++) {
    if (tick % 10 === 0) {
      for (const start of starts) assert.equal((await f.send(f.owner, 'production.heartbeat', { lineId: start.lineId, productionId: start.productionId })).ok, true);
      assert.equal((await f.send(f.owner, 'transfer.heartbeat', { transferId: transferStart.transferId })).ok, true);
      assert.equal((await f.send(f.owner, 'finishing.heartbeat', { finishingId: finishingStart.finishingId })).ok, true);
    }
    f.advance(20);
    const welding = f.controller.production, painting = f.controller.productionLines.get('painting'), transfer = f.controller.transfer;
    const liveIds = [...welding.parts, ...transfer.parts, ...painting.parts, ...f.controller.finishing.parts].map(part => part.id);
    assert.equal(new Set(liveIds).size, liveIds.length, 'A body must have exactly one owning conveyor.');
    assert.equal(liveIds.length, welding.nextPart - 1, 'Every generated body must remain present, including finished vehicles.');
    assert.equal(painting.completed, f.controller.finishing.accepted);
    for (const part of f.controller.finishing.parts) { finishingPhases.add(part.status); assert.equal(part.weldProgress, 1); assert.equal(part.coating, 1); }
    assert.equal(welding.completed, transfer.completed + transfer.parts.length);
    assert.equal(transfer.completed, painting.completed + painting.parts.length);
    for (const part of transfer.parts) { transferIds.add(part.id); assert.equal(part.weldProgress, 1); }
    for (const part of painting.parts) { paintingIds.add(part.id); assert.equal(part.weldProgress, 1); }
    for (const [lineId, line] of f.controller.productionLines) {
      const state = line.snapshot();
      assert.equal(state.running, true, `${lineId}: ${state.stopReason}`);
      assert.equal(state.process, lineId);
      for (const station of state.stations) {
        if (station.active) {
          active.add(lineId);
          assert.equal(station.phase, 'process');
          assert.equal(station.operation, lineId === 'painting' ? 'spray-pass' : 'spot-weld');
          if (lineId === 'welding') assert.equal(f.controller.robots.get(station.robotId).driver.snapshot().moving, false);
        }
        assert.ok(station.stepProgress >= 0 && station.stepProgress <= 1);
      }
      for (const part of state.parts) {
        const coverage = lineId === 'painting' ? part.coating : part.weldProgress;
        assert.equal(coverage, part.processProgress);
        assert.ok(coverage >= 0 && coverage <= 1);
        if (coverage > 0 && coverage < 1) partialCoverage.add(lineId);
        if (part.processed === 2) assert.equal(coverage, 1);
      }
      for (const robot of line.lineRobots()) {
        const snapshot = robot.driver.snapshot();
        const joints = previous.get(robot.definition.id);
        snapshot.joints.forEach((value, i) => assert.ok(Math.abs(value - joints[i]) <= robot.driver.config.joints[i].maxSpeed * .02 + 1e-10));
        assert.equal(checkConfiguration(robot.driver.config, snapshot.joints).ok, true);
        previous.set(robot.definition.id, snapshot.joints);
      }
    }
    if (f.controller.finishing.completed >= 1) break;
  }
  assert.deepEqual([...active].sort(), ['painting', 'welding']);
  assert.deepEqual([...partialCoverage].sort(), ['painting', 'welding']);
  assert.ok([...f.controller.productionLines.values()].every(line => line.completed >= 1));
  assert.ok(transferIds.has(firstId) && paintingIds.has(firstId));
  assert.ok(f.controller.productionLines.get('painting').completedIds.includes(firstId));
  const vehicle = f.controller.finishing.parts.find(part => part.id === firstId);
  assert.equal(vehicle.status, 'parked');
  assert.equal(vehicle.ready, true);
  assert.equal(vehicle.quality, 'passed');
  assert.equal(vehicle.assemblyProgress, 1);
  assert.equal(vehicle.inspectionProgress, 1);
  assert.deepEqual(vehicle.qualityChecks.map(check => [check.name, check.measured, check.passed]), [['weld', 1, true], ['paint', 1, true], ['assembly', 1, true]]);
  assert.deepEqual(vehicle.positionWorld, finishingLayout.slots[0]);
  for (const phase of ['to_assembly', 'lifting', 'assembling', 'to_inspection', 'inspecting', 'to_release', 'lowering', 'parking', 'parked']) assert.ok(finishingPhases.has(phase), phase);
  assert.deepEqual(f.controller.productionLines.get('painting').stations.map(station => [station.robotId, station.position]), [['paint-arm-2', 5], ['paint-arm', 9]]);
  const status = f.controller.status();
  assert.equal(status.production.lineId, 'welding');
  assert.equal(status.productionLines.length, 2);
  assert.equal(f.controller.snapshot(f.controller.robots.get('paint-arm')).production.lineId, 'painting');
});

test('manual operation and reset are isolated to the selected production line', async t => {
  const f = await fixture(t);
  const welding = await f.send(f.owner, 'production.start');
  const painting = await f.send(f.owner, 'production.start', { lineId: 'painting' });
  assert.equal(welding.ok && painting.ok, true);
  const lease = await f.send(f.owner, 'acquire', { robotId: 'paint-arm' });
  assert.equal(lease.ok, true);
  assert.equal(f.controller.production.running, true);
  assert.equal(f.controller.productionLines.get('painting').running, false);
  assert.ok(f.controller.production.lineRobots().every(robot => !robot.stopLatched));
  assert.ok(f.controller.productionLines.get('painting').lineRobots().every(robot => robot.stopLatched));
  const before = f.controller.production.position;
  f.advance(20);
  assert.ok(f.controller.production.position > before);
  assert.equal((await f.send(f.owner, 'production.reset', { lineId: 'painting' })).ok, false);
  assert.equal((await f.send(f.owner, 'reset', { robotId: 'paint-arm', leaseId: lease.leaseId })).ok, true);
  assert.equal((await f.send(f.owner, 'stop', { robotId: 'paint-arm' })).ok, true);
  assert.equal(f.controller.production.running, true, 'Stopping the manual paint arm must not stop welding AUTO.');
  assert.equal((await f.send(f.owner, 'release', { robotId: 'paint-arm', leaseId: lease.leaseId })).ok, true);
  assert.equal((await f.send(f.owner, 'production.reset', { lineId: 'painting' })).ok, true);
  assert.equal((await f.send(f.owner, 'production.start', { lineId: 'painting' })).ok, true);
  assert.equal(f.controller.production.running, true);
});

test('production rights and heartbeat timeouts are separate for each line', async t => {
  const f = await fixture(t);
  const welding = await f.send(f.owner, 'production.start');
  const painting = await f.send(f.owner, 'production.start', { lineId: 'painting' });
  const wrong = await f.send(f.owner, 'production.heartbeat', { lineId: 'painting', productionId: welding.productionId });
  assert.equal(wrong.ok, false);
  assert.equal(wrong.lineId, 'painting');
  assert.equal((await f.send(f.owner, 'production.start', { lineId: 'missing' })).ok, false);
  for (let tick = 0; tick < 101; tick++) {
    if (tick % 10 === 0) assert.equal((await f.send(f.owner, 'production.heartbeat', { lineId: 'welding', productionId: welding.productionId })).ok, true);
    f.advance(20);
  }
  assert.equal(f.controller.production.running, true);
  assert.equal(f.controller.productionLines.get('painting').running, false);
  assert.match(f.controller.productionLines.get('painting').stopReason, /heartbeat/);
  const expired = await f.send(f.owner, 'production.heartbeat', { lineId: 'painting', productionId: painting.productionId });
  assert.equal(expired.ok, false);
  assert.equal(expired.lineId, 'painting');
});

test('transfer capacity blocks welding and a stopped painting inlet without discarding or duplicating bodies', async t => {
  const f = await fixture(t);
  const welding = f.controller.production, painting = f.controller.productionLines.get('painting'), transfer = f.controller.transfer;
  const lineStart = await f.send(f.owner, 'production.start');
  const firstId = welding.parts[0].id;
  async function ticksUntil(condition, transferId, budget = 8000) {
    for (let tick = 0; tick < budget && !condition(); tick++) {
      if (tick % 10 === 0) {
        assert.equal((await f.send(f.owner, 'production.heartbeat', { productionId: lineStart.productionId })).ok, true);
        if (transferId) assert.equal((await f.send(f.owner, 'transfer.heartbeat', { transferId })).ok, true);
      }
      f.advance(20);
      assert.equal(welding.running, true, welding.stopReason);
      assert.equal(new Set([...welding.parts, ...transfer.parts, ...painting.parts].map(part => part.id)).size,
        welding.parts.length + transfer.parts.length + painting.parts.length);
    }
    assert.ok(condition(), 'Expected bounded production state was not reached.');
  }
  await ticksUntil(() => welding.phase === 'waiting_transfer');
  assert.equal(welding.parts[0].id, firstId);
  assert.equal(welding.parts[0].position, productionLayout.length);
  assert.equal(welding.completed, 0);
  const blockedPosition = welding.position;
  for (let tick = 0; tick < 20; tick++) f.advance(20);
  assert.equal(welding.position, blockedPosition);
  assert.equal(painting.parts.length, 0);
  const start = await f.send(f.owner, 'transfer.start');
  await ticksUntil(() => transfer.phase === 'waiting_output' && transfer.snapshot().queued === 1, start.transferId);
  assert.equal(transfer.parts.length, transferLayout.capacity);
  assert.equal(transfer.parts[0].id, firstId);
  assert.equal(transfer.parts[0].position, transferLayout.length);
  assert.equal(transfer.snapshot().occupied, true);
  assert.equal(transfer.snapshot().waiting, true);
  assert.equal(transfer.completed, 0);
  assert.equal(welding.completed, 1);
  const positions = [...welding.parts, ...transfer.parts].map(part => part.position);
  assert.equal((await f.send(f.owner, 'transfer.stop')).ok, true);
  assert.equal((await f.send(f.owner, 'transfer.start')).ok, false, 'Transfer stop requires explicit reset.');
  assert.equal((await f.send(f.owner, 'transfer.reset')).ok, true);
  assert.deepEqual([...welding.parts, ...transfer.parts].map(part => part.position), positions, 'Reset must not move or clear a body.');
  const paintStart = await f.send(f.owner, 'production.start', { lineId: 'painting' });
  assert.equal(paintStart.ok, true);
  const restart = await f.send(f.owner, 'transfer.start');
  assert.equal(restart.ok, true);
  f.advance(20);
  assert.equal(painting.parts[0].id, firstId);
  assert.equal(painting.parts[0].weldProgress, 1);
  assert.equal(painting.parts[0].coating, 0);
  assert.equal(painting.parts[0].processed, 0);
  assert.equal(transfer.completed, 1);
  assert.equal(transfer.parts.length, 0);
  const queuedId = welding.parts[0].id;
  f.advance(20);
  assert.equal(transfer.parts[0].id, queuedId);
  assert.notEqual(queuedId, firstId);
});

test('manual transfer jog uses real received welded body, releases immediately, reverses, and times out without affecting the line', async t => {
  const f = await fixture(t);
  const welding = f.controller.production, transfer = f.controller.transfer;
  assert.equal((await f.send(f.owner, 'transfer.mode', { mode: 'manual' })).ok, true);
  const start = await f.send(f.owner, 'transfer.start');
  const line = await f.send(f.owner, 'production.start');
  assert.equal(start.ok && line.ok, true);
  assert.equal((await f.send(f.owner, 'transfer.mode', { mode: 'auto' })).ok, false);
  for (let tick = 0; tick < 8000 && welding.phase !== 'waiting_transfer'; tick++) {
    if (tick % 10 === 0) {
      await f.send(f.owner, 'production.heartbeat', { productionId: line.productionId });
      await f.send(f.owner, 'transfer.heartbeat', { transferId: start.transferId });
    }
    f.advance(20);
  }
  assert.equal(welding.phase, 'waiting_transfer');
  assert.equal(transfer.parts.length, 0, 'MANUAL start must not load or move a body without a held jog command.');
  let sequence = 0;
  const jog = (direction, deadman = true, observedSequence = transfer.sequence) => f.send(f.owner, 'transfer.jog',
    { transferId: start.transferId, sequence: sequence++, observedSequence, direction, deadman });
  for (let tick = 0; tick < 40; tick++) { assert.equal((await jog(1)).ok, true); f.advance(20); }
  const part = transfer.parts[0];
  assert.ok(part.position > .2);
  const forward = part.position;
  assert.equal((await jog(0, false)).ok, true);
  for (let tick = 0; tick < 10; tick++) f.advance(20);
  assert.equal(part.position, forward);
  assert.equal(transfer.snapshot().moving, false);
  assert.equal((await f.send(f.owner, 'transfer.heartbeat', { transferId: start.transferId })).ok, true);
  for (let tick = 0; tick < 20; tick++) { assert.equal((await jog(-1)).ok, true); f.advance(20); }
  assert.ok(part.position < forward - .1 && part.position >= 0);
  assert.equal((await jog(0, false)).ok, true);
  const released = part.position;
  for (let tick = 0; tick < 10; tick++) f.advance(20);
  assert.equal(part.position, released, 'Heartbeat alone must not replay a released jog.');
  await f.send(f.owner, 'production.heartbeat', { productionId: line.productionId });
  await f.send(f.owner, 'transfer.heartbeat', { transferId: start.transferId });
  assert.equal((await jog(1)).ok, true);
  for (let tick = 0; tick < 16; tick++) f.advance(20);
  assert.equal(transfer.running, false);
  assert.match(transfer.stopReason, /ручных команд/);
  assert.equal(welding.running, true, 'A transfer command timeout must not stop independent welding control.');
  const timedOut = part.position;
  f.advance(20);
  assert.equal(part.position, timedOut);
  assert.equal((await jog(1)).ok, false);
  assert.equal((await f.send(f.owner, 'transfer.reset')).ok, true);
  assert.equal(part.position, timedOut);
  const restarted = await f.send(f.owner, 'transfer.start');
  assert.equal(restarted.ok, true);
  assert.notEqual(restarted.transferId, start.transferId);
  f.advance(20);
  assert.equal(part.position, timedOut, 'New manual session also requires a new held command.');
});

test('transfer manual input rejects stale telemetry, duplicate commands, foreign ownership, and lost heartbeat', async t => {
  const f = await fixture(t);
  const transfer = f.controller.transfer;
  const other = await f.connect('Other transfer operator');
  await f.send(f.owner, 'transfer.mode', { mode: 'manual' });
  let start = await f.send(f.owner, 'transfer.start');
  assert.equal((await f.send(other, 'transfer.start')).ok, false);
  assert.equal((await f.send(other, 'transfer.heartbeat', { transferId: start.transferId })).ok, false);
  assert.equal((await f.send(other, 'transfer.jog', { transferId: start.transferId, sequence: 0, direction: 1, deadman: true, observedSequence: transfer.sequence })).ok, false);
  assert.equal(transfer.running, true);
  const stale = transfer.sequence;
  for (let tick = 0; tick < 13; tick++) f.advance(20);
  assert.equal((await f.send(f.owner, 'transfer.jog', { transferId: start.transferId, sequence: 0, direction: 1, deadman: true, observedSequence: stale })).ok, false);
  assert.equal(transfer.running, false);
  assert.match(transfer.stopReason, /телеметрия/);
  await f.send(f.owner, 'transfer.reset'); start = await f.send(f.owner, 'transfer.start');
  const command = { transferId: start.transferId, sequence: 0, direction: 1, deadman: true, observedSequence: transfer.sequence };
  assert.equal((await f.send(f.owner, 'transfer.jog', command)).ok, true);
  assert.equal((await f.send(f.owner, 'transfer.jog', command)).ok, false);
  assert.equal(transfer.running, false);
  await f.send(f.owner, 'transfer.reset'); await f.send(f.owner, 'transfer.start');
  for (let tick = 0; tick < 101; tick++) f.advance(20);
  assert.equal(transfer.running, false);
  assert.match(transfer.stopReason, /heartbeat/);
  await f.send(f.owner, 'transfer.reset'); await f.send(f.owner, 'transfer.start');
  f.advance(301);
  assert.equal(transfer.running, false);
  assert.match(transfer.stopReason, /серверного цикла/);
});

test('transfer disconnect stops only its owned session and never issues physical commands', async t => {
  const f = await fixture(t);
  const other = await f.connect('Independent painting owner');
  assert.equal((await f.send(other, 'production.start', { lineId: 'painting' })).ok, true);
  assert.equal((await f.send(f.owner, 'transfer.start')).ok, true);
  f.owner.socket.close();
  assert.equal(f.controller.transfer.running, false);
  assert.equal(f.controller.productionLines.get('painting').running, true);
  const configured = await fixture(t, config => { config.robots[3].mode = 'rosbridge'; config.robots[3].connection = {}; });
  const physical = configured.controller.robots.get('paint-arm-2').driver;
  physical.command = physical.reset = () => { throw new Error('Transfer must never command/reset hardware.'); };
  assert.equal((await configured.send(configured.owner, 'transfer.start')).ok, false);
  assert.equal((await configured.send(configured.owner, 'transfer.reset')).ok, false);
  assert.equal((await configured.send(configured.owner, 'transfer.stop')).ok, true);
});

test('physical equipment on either line is excluded from automatic commands and resets', async t => {
  const f = await fixture(t, config => { config.robots[2].mode = 'rosbridge'; config.robots[2].connection = {}; });
  const physical = f.controller.robots.get('paint-arm').driver;
  physical.command = () => { throw new Error('Physical equipment must never be commanded by production.'); };
  physical.reset = () => { throw new Error('Physical equipment must never be reset by production.'); };
  physical.stop = () => { throw new Error('Line stop must not call a physical driver.'); };
  assert.equal((await f.send(f.owner, 'production.start', { lineId: 'painting' })).ok, false);
  assert.equal((await f.send(f.owner, 'production.reset', { lineId: 'painting' })).ok, false);
  assert.equal((await f.send(f.owner, 'production.stop', { lineId: 'painting' })).ok, true);
  assert.equal((await f.send(f.owner, 'production.start')).ok, true, 'Independent simulation line remains usable.');
  physical.stop = () => ({ ok: true }); // Global shutdown may explicitly stop every configured driver.
});

test('manual lease acquisition stops AUTO and belt before granting exclusive control', async t => {
  const f = await fixture(t);
  assert.equal((await f.send(f.owner, 'production.start')).ok, true);
  f.advance(20);
  const manual = await f.connect('Manual operator');
  const lease = await f.send(manual, 'acquire');
  assert.equal(lease.ok, true);
  assert.equal(f.controller.production.snapshot().running, false);
  const position = f.controller.production.snapshot().conveyorPosition;
  f.advance(20);
  assert.equal(f.controller.production.snapshot().conveyorPosition, position);
  assert.ok(f.controller.production.lineRobots().every(robot => robot.driver.snapshot().fault));
  assert.equal((await f.send(f.owner, 'production.reset')).ok, false);
  assert.equal((await f.send(f.owner, 'production.start')).ok, false);
  assert.equal((await f.send(manual, 'reset', { leaseId: lease.leaseId })).ok, true);
  assert.equal((await f.send(f.owner, 'acquire')).ok, false);
});

test('AUTO disconnect, wrong-owner heartbeat, expired heartbeat and delayed scheduler are guarded', async t => {
  const f = await fixture(t);
  const other = await f.connect('Observer');
  let start = await f.send(f.owner, 'production.start');
  assert.equal((await f.send(other, 'production.heartbeat', { productionId: start.productionId })).ok, false);
  assert.equal((await f.send(f.owner, 'production.heartbeat', { productionId: 'wrong-id' })).ok, false);
  f.advance(301);
  assert.equal(f.controller.production.snapshot().phase, 'fault');
  assert.match(f.controller.production.snapshot().stopReason, /сервер/i);
  await f.send(f.owner, 'production.reset');
  start = await f.send(f.owner, 'production.start');
  for (let i = 0; i < 101; i++) f.advance(20);
  assert.equal(f.controller.production.snapshot().running, false);
  assert.match(f.controller.production.snapshot().stopReason, /heartbeat/i);
  assert.equal((await f.send(f.owner, 'production.heartbeat', { productionId: start.productionId })).ok, false);
  await f.send(f.owner, 'production.reset');
  assert.equal((await f.send(f.owner, 'production.start')).ok, true);
  f.owner.socket.close();
  assert.equal(f.controller.production.snapshot().running, false);
  assert.ok(f.controller.production.lineRobots().every(robot => robot.driver.snapshot().fault));
});

test('AUTO and line reset are unavailable for a configuration with physical equipment', async t => {
  const f = await fixture(t, config => { config.robots[1].mode = 'rosbridge'; config.robots[1].connection = {}; });
  const physical = f.controller.robots.get('training-arm-2').driver;
  physical.command = () => { throw new Error('A physical command must never be sent by AUTO.'); };
  physical.reset = () => { throw new Error('Physical reset must never be called.'); };
  assert.equal((await f.send(f.owner, 'production.start')).ok, false);
  assert.equal((await f.send(f.owner, 'production.reset')).ok, false);
  assert.equal(f.controller.production.snapshot().available, false);
  assert.equal((await f.send(f.owner, 'production.stop')).ok, true);
});

test('local help identifies rule-based provenance and explains current line state without issuing commands', () => {
  const snapshot = { robotId: 'training-arm', mode: 'simulation', connected: true, moving: false, fault: 'Потеря heartbeat', production: { available: true, running: false, phase: 'fault', completed: 3, cycles: 8, parts: [{ id: 'panel-9' }], stopReason: 'Нет оператора' } };
  const advice = rulesAdvice(snapshot, 'Как запустить производственную линию?', 'LLM unavailable');
  assert.equal(advice.source, 'rules');
  assert.equal(advice.executable, false);
  assert.match(advice.summary, /готовых панелей: 3/);
  assert.ok(advice.notes.some(note => note.includes('«Сброс», затем «Пуск»')));
  assert.ok(advice.notes.some(note => note.includes('языковая модель не использована')));
  assert.equal(rulesAdvice(snapshot, 'Почему робот остановлен?').summary, snapshot.fault);
  assert.match(rulesAdvice(snapshot, 'Проверь камеру').summary, /проверить камеру невозможно/);
  assert.equal(rulesAdvice(snapshot, '').ok, false);
});
