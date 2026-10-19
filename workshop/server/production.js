import { randomUUID } from 'node:crypto';
import { forwardKinematics } from './robot/index.js';

export const productionLayout = Object.freeze({ length: 11, beltWidth: 1.05, surfaceHeight: 0.89, partCenterHeight: 0.95, spacing: 5, bodyLength: 4.24, stationPositions: [2, 6] });
export const productionDefinitions = Object.freeze([
  Object.freeze({ lineId: 'welding', name: 'Сварочная линия', process: 'welding', robotIds: Object.freeze(['training-arm', 'training-arm-2']), stationPositions: Object.freeze([2, 6]) }),
  Object.freeze({ lineId: 'painting', name: 'Покрасочная линия', process: 'painting', robotIds: Object.freeze(['paint-arm-2', 'paint-arm']), stationPositions: Object.freeze([5, 9]) }),
]);
const pose = (x, y, z) => ({ position: { x, y, z }, orientation: [0, 0, 0, 1] });
// Server-frame TCP targets. Unity bases: (-1.85,.45,.2)/yaw0 and (1.85,.45,3.8)/yaw180.
// The second line uses the same relative frames translated in the Unity scene.
// Simulated spot welds and paint passes, with no claimed thermal/chemical model.
const weldingSteps = [
  { phase: 'approach', pose: pose(.90, 0.74, 0.2) },
  { phase: 'lower', pose: pose(.92, 0.70, 0.04) },
  { phase: 'process', pose: pose(.92, 0.70, 0.04), dwell: 0.6, operation: 'spot-weld' },
  { phase: 'process', pose: pose(.92, 0.70, 0.20), dwell: 0.6, operation: 'spot-weld' },
  { phase: 'process', pose: pose(.92, 0.70, 0.36), dwell: 0.6, operation: 'spot-weld' },
  { phase: 'retract', pose: pose(.90, 0.74, 0.2) },
  { phase: 'return' },
];
const paintingSteps = [
  { phase: 'approach', pose: pose(.90, 0.74, 0.2) },
  { phase: 'lower', pose: pose(.92, 0.70, 0.04) },
  { phase: 'process', pose: pose(.92, 0.70, 0.36), dwell: 0.15, operation: 'spray-pass' },
  { phase: 'process', pose: pose(.90, 0.74, 0.04), dwell: 0.15, operation: 'spray-pass' },
  { phase: 'process', pose: pose(.90, 0.74, 0.36), dwell: 0.15, operation: 'spray-pass' },
  { phase: 'retract', pose: pose(.90, 0.74, 0.2) },
  { phase: 'return' },
];
const distance = (a, b) => Math.hypot(a.x - b.x, a.y - b.y, a.z - b.z);

/** Server-owned simulation line. Never commands a ROS/physical driver. */
export class ProductionLine {
  constructor(controller, options = {}) {
    this.controller = controller;
    const definition = productionDefinitions.find(line => line.lineId === (options.lineId || 'welding'));
    if (!definition) throw new Error('Unknown production line.');
    Object.assign(this, definition);
    this.steps = this.process === 'painting' ? paintingSteps : weldingSteps;
    this.processSteps = this.steps.filter(step => step.phase === 'process').length;
    this.enabled = options.enabled !== false;
    this.beltSpeed = options.beltSpeed ?? 0.35;
    if (!Number.isFinite(this.beltSpeed) || this.beltSpeed < 0.05 || this.beltSpeed > 0.75) throw new Error('Production beltSpeed must be 0.05..0.75 m/s.');
    this.running = false;
    this.phase = 'stopped';
    this.stopReason = '';
    this.owner = null;
    this.position = 0;
    this.completed = 0;
    this.lastCompletedId = '';
    this.completedIds = [];
    this.currentSpeed = 0;
    this.nextPart = 1;
    this.parts = [];
    this.stations = this.robotIds.map((robotId, index) => ({ robotId, position: this.stationPositions[index], phase: 'idle', cycles: 0, job: null, active: false, stepProgress: 0, operation: '' }));
    this.spawn();
  }
  unavailable() {
    if (!this.enabled) return 'Автоматическая линия отключена в настройках.';
    if (this.robotIds.some(id => !this.controller.robots.has(id))) return `Для AUTO линии нужны симуляторы ${this.robotIds.join(' и ')}.`;
    if (this.lineRobots().some(robot => robot.definition.mode !== 'simulation')) return 'AUTO доступен только для симуляторов этой линии; физическое оборудование исключено.';
    return '';
  }
  lineRobots() { return this.robotIds.map(id => this.controller.robots.get(id)).filter(Boolean); }
  start(client) {
    const unavailable = this.unavailable();
    if (unavailable) return { ok: false, error: unavailable };
    if (this.running) return { ok: false, error: 'Линия уже работает в AUTO.' };
    for (const robot of this.lineRobots()) {
      if (robot.lease) return { ok: false, error: 'Освободите ручное управление роботами этой линии перед AUTO.' };
      if (robot.stopLatched || robot.driver.snapshot().fault) return { ok: false, error: 'Сначала выполните явный «Сброс линии».' };
    }
    if (this.robotIds.some(id => !client.subscriptions.has(id))) return { ok: false, error: 'Для AUTO подпишитесь на телеметрию линии (watchAll).' };
    this.owner = { id: randomUUID(), clientId: client.id, name: client.name, lastHeartbeat: this.controller.now() };
    this.running = true;
    this.phase = 'transport';
    this.stopReason = '';
    if (!this.parts.length) this.spawn();
    this.controller.log(this.lineId, `${client.name}: AUTO запущен · ${this.name}`);
    return { ok: true, productionId: this.owner.id };
  }
  heartbeat(client, productionId) {
    if (!this.running || this.owner?.clientId !== client.id || this.owner.id !== productionId)
      return { ok: false, error: 'Нет действующего права управления AUTO.' };
    if (this.controller.now() - this.owner.lastHeartbeat > this.controller.config.leaseTimeoutMs) {
      this.stop('Потеря heartbeat AUTO', true);
      return { ok: false, error: 'Сеанс AUTO истёк; нужен явный сброс и запуск.' };
    }
    this.owner.lastHeartbeat = this.controller.now();
    return { ok: true };
  }
  reset() {
    const unavailable = this.unavailable();
    if (unavailable) return { ok: false, error: unavailable };
    if (this.running || this.lineRobots().some(robot => robot.lease))
      return { ok: false, error: 'Остановите AUTO и освободите ручные посты перед сбросом линии.' };
    for (const robot of this.lineRobots()) {
      robot.driver.setDeadman(false);
      const result = robot.driver.reset();
      if (!result.ok) return result;
    }
    for (const robot of this.lineRobots()) { robot.stopLatched = false; robot.appStopReason = ''; robot.deadman = false; }
    this.phase = 'stopped';
    this.currentSpeed = 0;
    this.stopReason = '';
    this.clearJobs();
    // Reset clears faults only. Belt position, panels and completed counts persist.
    this.controller.log(this.lineId, `${this.name}: сброс симуляции линии`);
    return { ok: true, scope: 'simulation', hardwareReset: false };
  }
  clearJobs() {
    for (const station of this.stations) { station.job = null; station.phase = 'idle'; station.active = false; station.operation = ''; station.stepProgress = 0; }
    for (const part of this.parts) { part.robotId = ''; part.status = part.processed === 2 ? 'finished' : part.processed === 1 ? 'processed-1' : 'raw'; }
  }
  stop(reason = 'Линия остановлена оператором', fault = false) {
    this.running = false;
    this.currentSpeed = 0;
    this.owner = null;
    this.phase = fault ? 'fault' : 'stopped';
    this.stopReason = reason;
    this.clearJobs();
    // Only this line's simulation arms are affected. Never send automatic physical commands.
    for (const id of this.robotIds) {
      const robot = this.controller.robots.get(id);
      if (robot?.definition.mode === 'simulation') this.controller.stop(robot, reason);
    }
    this.controller.log(this.lineId, `${this.name}: ${reason}`);
    return { ok: true };
  }
  spawn() {
    if (this.process !== 'welding') return;
    this.parts.push({ id: `panel-${String(this.nextPart++).padStart(5, '0')}`, position: 0, processed: 0, status: 'raw', robotId: '', processProgress: 0, weldProgress: 0, coating: 0 });
  }
  canReceive() {
    return this.process === 'painting' && this.running && !this.unavailable() &&
      this.parts.every(part => part.position >= productionLayout.spacing - 1e-7);
  }
  receive(part) {
    if (!this.canReceive() || part.weldProgress < 1 - 1e-7 || this.parts.some(item => item.id === part.id)) return false;
    this.parts.push({ ...part, position: 0, processed: 0, processProgress: 0, coating: 0, status: 'raw', robotId: '' });
    return true;
  }
  tick(now, elapsedMs) {
    this.currentSpeed = 0;
    if (!this.running) return;
    const owner = [...this.controller.clients].find(client => client.id === this.owner?.clientId && client.authenticated);
    if (!owner || now - this.owner.lastHeartbeat > this.controller.config.leaseTimeoutMs) return this.stop('Потеря оператора или heartbeat AUTO', true);
    if (!Number.isFinite(elapsedMs) || elapsedMs <= 0 || elapsedMs > this.controller.config.commandTimeoutMs) return this.stop('Задержка серверного цикла AUTO', true);
    if (this.unavailable()) return this.stop('Конфигурация AUTO больше не разрешена', true);
    for (const station of this.stations) {
      const robot = this.controller.robots.get(station.robotId);
      if (robot.lease || robot.stopLatched || robot.driver.snapshot().fault) return this.stop('Ручное управление или остановка робота прервали AUTO', true);
    }
    const dt = Math.min(elapsedMs / 1000, 0.05);
    if (!this.parts.length && this.process === 'painting') { this.phase = 'waiting_input'; return; }
    let processing = false;
    for (let index = 0; index < this.stations.length; index++) {
      const station = this.stations[index];
      if (!station.job) {
        const part = this.parts.find(p => p.processed === index && Math.abs(p.position - station.position) < 1e-7);
        if (part) { station.job = { part, step: 0, issued: false, dwell: 0, completedWork: 0 }; part.status = 'processing'; part.robotId = station.robotId; }
      }
      if (station.job) {
        processing = true;
        if (!this.advance(station, now, dt)) return;
      }
    }
    if (processing) { this.phase = 'processing'; return; }
    this.phase = 'transport';
    let distance = this.beltSpeed * dt;
    for (const part of this.parts) {
      if (part.processed < 2) distance = Math.min(distance, Math.max(0, this.stations[part.processed].position - part.position));
      else distance = Math.min(distance, Math.max(0, productionLayout.length - part.position));
    }
    this.position += distance;
    this.currentSpeed = distance / dt;
    for (const part of this.parts) {
      part.position += distance;
      if (Math.abs(part.position - productionLayout.length) < 1e-7) part.position = productionLayout.length;
    }
    const exiting = this.parts.filter(part => part.position >= productionLayout.length - 1e-7);
    if (exiting.some(part => part.processed !== 2)) return this.stop('Деталь дошла до выхода без обработки', true);
    for (const part of exiting) {
      if (this.process === 'welding' && !this.controller.transfer?.accept(part)) { this.phase = 'waiting_transfer'; this.currentSpeed = 0; continue; }
      if (this.process === 'painting' && !this.controller.finishing?.accept(part)) { this.phase = 'waiting_finishing'; this.currentSpeed = 0; continue; }
      this.completed++;
      this.lastCompletedId = part.id;
      this.completedIds.push(part.id);
      if (this.completedIds.length > 16) this.completedIds.shift();
      this.parts.splice(this.parts.indexOf(part), 1);
    }
    if (!this.parts.length || this.parts.at(-1).position >= productionLayout.spacing - 1e-7) this.spawn();
  }
  advance(station, now, dt) {
    const robot = this.controller.robots.get(station.robotId), job = station.job;
    const step = this.steps[job.step];
    station.phase = step.phase;
    station.operation = step.operation || '';
    if (!job.issued) {
      const home = forwardKinematics(robot.driver.config, robot.driver.config.home);
      const target = step.pose || { position: home.position, orientation: home.orientation };
      job.origin = robot.driver.snapshot().tool.position;
      job.target = target.position;
      robot.driver.setDeadman(true);
      const result = robot.driver.command(target, 0.5);
      if (!result.ok) { this.stop(`AUTO: ${station.robotId}: ${result.error}`, true); return false; }
      robot.deadman = true;
      job.issued = true;
    }
    // The server is the producer of this bounded simulation command stream.
    // Scheduler and operator heartbeat guards above must pass before refreshing it.
    robot.lastCommand = now;
    const state = robot.driver.snapshot();
    station.active = step.phase === 'process' && (this.process === 'painting' || !state.moving);
    const travel = distance(job.origin, job.target);
    const travelProgress = !state.moving ? 1 : travel > 1e-6 ? Math.min(1, distance(job.origin, state.tool.position) / travel) : 0;
    if (!state.moving) job.dwell += dt;
    station.stepProgress = Math.min(1, this.process === 'welding' && step.phase === 'process'
      ? job.dwell / (step.dwell || 1) : travelProgress * .85 + Math.min(1, job.dwell / (step.dwell || dt)) * .15);
    if (step.phase === 'process') {
      job.part.processProgress = Math.max(job.part.processProgress, (job.part.processed + (job.completedWork + station.stepProgress) / this.processSteps) / this.stations.length);
      job.part[this.process === 'painting' ? 'coating' : 'weldProgress'] = job.part.processProgress;
    }
    if (!state.moving) {
      if (job.dwell + 1e-9 >= (step.dwell || 0)) {
        if (step.phase === 'process') job.completedWork++;
        job.step++;
        job.issued = false;
        job.dwell = 0;
        if (job.step === this.steps.length) {
          job.part.processed++;
          job.part.status = job.part.processed === 2 ? 'finished' : 'processed-1';
          job.part.robotId = '';
          station.cycles++;
          station.phase = 'idle';
          station.active = false;
          station.operation = '';
          station.stepProgress = 0;
          station.job = null;
          robot.deadman = false;
          robot.driver.setDeadman(false);
        }
      }
    }
    return true;
  }
  snapshot() {
    return {
      lineId: this.lineId, name: this.name, process: this.process,
      available: !this.unavailable(), running: this.running, phase: this.phase,
      conveyorPosition: this.position, speed: this.running ? this.currentSpeed : 0,
      beltSpeed: this.beltSpeed, length: productionLayout.length, completed: this.completed,
      lastCompletedId: this.lastCompletedId, completedIds: [...this.completedIds],
      cycles: this.stations.reduce((sum, station) => sum + station.cycles, 0), owner: this.owner?.name || '',
      stopReason: this.stopReason || this.unavailable(), parts: this.parts.map(part => ({ ...part })),
      stations: this.stations.map(({ robotId, position, phase, cycles, job, active, operation, stepProgress }) => ({ robotId, position, phase, cycles, process: this.process, active, operation, stepProgress, step: job?.step ?? 0, stepCount: this.steps.length }))
    };
  }
}

export const transferLayout = Object.freeze({ length: 8, capacity: 1, automaticSpeed: .65, manualSpeed: .35,
  path: Object.freeze([Object.freeze({ x: 0, y: .95, z: 9 }), Object.freeze({ x: -8, y: .95, z: 9 })]) });

/** One-body cross conveyor. Body ownership changes atomically at the endpoints. */
export class TransferConveyor {
  constructor(controller) {
    this.controller = controller;
    this.mode = 'auto';
    this.running = false;
    this.owner = null;
    this.parts = [];
    this.position = 0;
    this.completed = 0;
    this.sequence = 0;
    this.history = new Map([[0, controller.now()]]);
    this.phase = 'stopped';
    this.stopReason = '';
    this.fault = false;
    this.deadman = false;
    this.direction = 0;
    this.speed = 0;
    this.lastJog = -Infinity;
    this.waiting = false;
    this.waitingReason = '';
  }
  get welding() { return this.controller.productionLines.get('welding'); }
  get painting() { return this.controller.productionLines.get('painting'); }
  unavailable() {
    return this.welding.unavailable() || this.painting.unavailable();
  }
  sessionFresh(now = this.controller.now()) {
    return this.running && this.owner && now - this.owner.lastHeartbeat <= this.controller.config.leaseTimeoutMs &&
      [...this.controller.clients].some(client => client.id === this.owner.clientId && client.authenticated);
  }
  owns(client, transferId) {
    if (!this.sessionFresh()) {
      if (this.running) this.stop('Потеря heartbeat грузового конвейера', true);
      return false;
    }
    return this.owner.clientId === client.id && this.owner.id === transferId;
  }
  setMode(mode) {
    if (!['auto', 'manual'].includes(mode)) return { ok: false, error: 'Режим грузового конвейера: auto или manual.' };
    if (this.running) return { ok: false, error: 'Остановите грузовой конвейер перед сменой режима.' };
    this.mode = mode;
    this.deadman = false; this.direction = 0;
    return { ok: true };
  }
  start(client) {
    const unavailable = this.unavailable();
    if (unavailable) return { ok: false, error: unavailable };
    if (this.running) return { ok: false, error: 'Грузовой конвейер уже занят.' };
    if (this.fault) return { ok: false, error: 'Для грузового конвейера требуется явный сброс.' };
    const ids = [...this.welding.robotIds, ...this.painting.robotIds];
    if (ids.some(id => !client.subscriptions.has(id))) return { ok: false, error: 'Сначала подпишитесь на телеметрию всех постов (watchAll).' };
    this.owner = { id: randomUUID(), clientId: client.id, name: client.name, lastHeartbeat: this.controller.now(), commandSequence: -1 };
    this.running = true; this.phase = 'waiting_input'; this.stopReason = '';
    this.deadman = false; this.direction = this.mode === 'auto' ? 1 : 0; this.lastJog = -Infinity;
    this.controller.log('transfer', `${client.name}: грузовой конвейер ${this.mode.toUpperCase()}`);
    return { ok: true, transferId: this.owner.id };
  }
  heartbeat(client, transferId) {
    if (!this.owns(client, transferId)) return { ok: false, error: 'Нет действующего сеанса грузового конвейера.' };
    this.owner.lastHeartbeat = this.controller.now();
    return { ok: true };
  }
  jog(client, message) {
    if (!this.owns(client, message.transferId)) return { ok: false, error: 'Нет действующего сеанса грузового конвейера.' };
    if (this.mode !== 'manual') return { ok: false, error: 'Ручное движение доступно только в режиме MANUAL.' };
    if (!Number.isSafeInteger(message.sequence) || message.sequence <= this.owner.commandSequence) {
      this.stop('Повторная или устаревшая ручная команда грузового конвейера', true);
      return { ok: false, error: 'Нарушен порядок команд грузового конвейера.' };
    }
    this.owner.commandSequence = message.sequence;
    if (message.deadman !== true || message.direction === 0) {
      this.deadman = false; this.direction = 0; this.speed = 0; this.phase = 'idle';
      return { ok: true };
    }
    if (![1, -1].includes(message.direction)) {
      this.stop('Неверное направление грузового конвейера', true);
      return { ok: false, error: 'Направление движения должно быть -1, 0 или 1.' };
    }
    const observedAt = this.history.get(message.observedSequence);
    if (!client.subscriptions.size || observedAt === undefined || this.controller.now() - observedAt > this.controller.config.telemetryMaxAgeMs) {
      this.stop('Устаревшая телеметрия грузового конвейера', true);
      return { ok: false, error: 'Нет свежей телеметрии грузового конвейера.' };
    }
    this.deadman = true; this.direction = message.direction; this.lastJog = this.controller.now();
    return { ok: true };
  }
  stop(reason = 'Грузовой конвейер остановлен оператором', fault = false) {
    this.running = false; this.owner = null; this.deadman = false; this.direction = 0; this.speed = 0;
    this.fault = true; this.phase = fault ? 'fault' : 'stopped'; this.stopReason = reason;
    this.waiting = false; this.waitingReason = '';
    this.controller.log('transfer', reason);
    return { ok: true };
  }
  reset() {
    const unavailable = this.unavailable();
    if (unavailable) return { ok: false, error: unavailable };
    if (this.running) return { ok: false, error: 'Остановите грузовой конвейер перед сбросом.' };
    this.fault = false; this.stopReason = ''; this.phase = 'stopped';
    this.deadman = false; this.direction = 0; this.speed = 0;
    return { ok: true, scope: 'simulation', hardwareReset: false };
  }
  accept(part) {
    const now = this.controller.now();
    if (!this.sessionFresh(now) || this.unavailable() || this.parts.length || part.processed !== 2 || part.weldProgress < 1 - 1e-7) return false;
    if (this.mode === 'manual' && (!this.deadman || this.direction !== 1 || now - this.lastJog > this.controller.config.commandTimeoutMs)) return false;
    this.parts.push({ ...part, position: 0, status: 'transfer', robotId: '' });
    return true;
  }
  tick(now, elapsedMs) {
    this.sequence++;
    this.history.set(this.sequence, now);
    for (const [sequence, at] of this.history) if (now - at > this.controller.config.telemetryMaxAgeMs) this.history.delete(sequence);
    this.speed = 0; this.waiting = false; this.waitingReason = '';
    if (!this.running) return;
    if (!this.sessionFresh(now)) return this.stop('Потеря оператора или heartbeat грузового конвейера', true);
    if (!Number.isFinite(elapsedMs) || elapsedMs <= 0 || elapsedMs > this.controller.config.commandTimeoutMs)
      return this.stop('Задержка серверного цикла грузового конвейера', true);
    if (this.unavailable()) return this.stop('Грузовой конвейер доступен только для связанных линий симуляции', true);
    if (this.mode === 'manual' && this.deadman && now - this.lastJog > this.controller.config.commandTimeoutMs)
      return this.stop('Потеря ручных команд грузового конвейера', true);
    if (this.mode === 'manual' && !this.deadman) { this.phase = 'idle'; return; }
    if (!this.parts.length) { this.phase = 'waiting_input'; this.waiting = true; this.waitingReason = 'Ожидание сваренного кузова'; return; }
    const part = this.parts[0], dt = Math.min(elapsedMs / 1000, .05);
    const velocity = this.direction * (this.mode === 'auto' ? transferLayout.automaticSpeed : transferLayout.manualSpeed);
    const nextPosition = Math.max(0, Math.min(transferLayout.length, part.position + velocity * dt));
    this.speed = (nextPosition - part.position) / dt;
    this.position += nextPosition - part.position;
    part.position = nextPosition;
    this.phase = this.direction < 0 ? 'reverse' : 'transport';
    if (part.position >= transferLayout.length - 1e-7 && this.direction > 0) {
      if (this.painting.receive(part)) { this.parts.shift(); this.completed++; this.phase = 'waiting_input'; }
      else { this.speed = 0; this.phase = 'waiting_output'; this.waiting = true; this.waitingReason = !this.painting.running ? 'Покрасочная линия остановлена' : 'Вход покрасочной линии занят'; }
    }
    else if (part.position <= 1e-7 && this.direction < 0) { this.speed = 0; this.phase = 'at_input'; this.waiting = true; this.waitingReason = 'Достигнут вход грузового конвейера'; }
  }
  snapshot() {
    const queued = this.welding.parts.filter(part => part.processed === 2 && part.position >= productionLayout.length - 1e-7).length;
    return {
      available: !this.unavailable(), mode: this.mode, running: this.running, moving: this.running && Math.abs(this.speed) > 1e-7,
      deadman: this.deadman, direction: this.direction, phase: this.phase, stopReason: this.stopReason, fault: this.fault,
      owner: this.owner?.name || '', sequence: this.sequence, length: transferLayout.length,
      speed: this.running ? this.speed : 0, conveyorPosition: this.position, position: this.position,
      capacity: transferLayout.capacity, occupied: this.parts.length > 0, waiting: this.waiting, waitingReason: this.waitingReason, queued, completed: this.completed,
      parts: this.parts.map(part => ({ ...part })), path: transferLayout.path.map(point => ({ ...point }))
    };
  }
}

export const finishingLayout = Object.freeze({
  capacity: 1, parkingCapacity: 4, speed: .8, liftSpeed: .25, assemblySeconds: 6, inspectionSeconds: 4,
  entry: Object.freeze({ x: -8, y: .95, z: -2 }),
  assemblyEntry: Object.freeze({ x: -8, y: .95, z: -7 }),
  assembly: Object.freeze({ x: -8, y: 1.13, z: -7 }),
  inspection: Object.freeze({ x: -8, y: 1.13, z: -11.5 }),
  release: Object.freeze({ x: -8, y: 1.13, z: -14.3 }),
  floor: Object.freeze({ x: -8, y: .30, z: -14.3 }),
  slots: Object.freeze([-11, -7, -3, 1].map(x => Object.freeze({ x, y: .24, z: -19 })))
});

/** Bounded simulation of assembly, inspection and retained finished vehicles.
 * Measurements describe this simulation only; they are not physical certification.
 */
export class FinishingStation {
  constructor(controller) {
    this.controller = controller;
    this.running = false; this.owner = null; this.fault = false; this.stopReason = '';
    this.phase = 'stopped'; this.sequence = 0; this.parts = [];
    this.accepted = 0; this.completed = 0; this.speed = 0;
    this.waiting = false; this.waitingReason = '';
  }
  unavailable() { return this.controller.transfer.unavailable(); }
  get active() { return this.parts.find(part => part.status !== 'parked'); }
  sessionFresh(now = this.controller.now()) {
    return this.running && this.owner && now - this.owner.lastHeartbeat <= this.controller.config.leaseTimeoutMs &&
      [...this.controller.clients].some(client => client.id === this.owner.clientId && client.authenticated);
  }
  start(client) {
    const unavailable = this.unavailable();
    if (unavailable) return { ok: false, error: unavailable };
    if (this.running) return { ok: false, error: 'Участок выпуска уже работает.' };
    if (this.fault) return { ok: false, error: 'Для участка выпуска требуется явный сброс.' };
    if (productionDefinitions.flatMap(line => line.robotIds).some(id => !client.subscriptions.has(id)))
      return { ok: false, error: 'Сначала подпишитесь на телеметрию всех постов (watchAll).' };
    this.owner = { id: randomUUID(), clientId: client.id, name: client.name, lastHeartbeat: this.controller.now() };
    this.running = true; this.phase = this.active?.status || 'waiting_input'; this.stopReason = '';
    this.controller.log('finishing', `${client.name}: сборка, ОТК и стоянка запущены`);
    return { ok: true, finishingId: this.owner.id };
  }
  heartbeat(client, finishingId) {
    if (!this.sessionFresh()) {
      if (this.running) this.stop('Потеря heartbeat участка выпуска', true);
      return { ok: false, error: 'Нет действующего сеанса участка выпуска.' };
    }
    if (this.owner.clientId !== client.id || this.owner.id !== finishingId)
      return { ok: false, error: 'Нет действующего сеанса участка выпуска.' };
    this.owner.lastHeartbeat = this.controller.now();
    return { ok: true };
  }
  stop(reason = 'Участок выпуска остановлен оператором', fault = false) {
    this.running = false; this.owner = null; this.fault = true; this.stopReason = reason;
    this.phase = fault ? 'fault' : 'stopped'; this.speed = 0; this.waiting = false; this.waitingReason = '';
    this.controller.log('finishing', reason);
    return { ok: true };
  }
  reset() {
    const unavailable = this.unavailable();
    if (unavailable) return { ok: false, error: unavailable };
    if (this.running) return { ok: false, error: 'Остановите участок выпуска перед сбросом.' };
    // Never clear the reserved slots, QC verdicts, positions or work progress.
    this.fault = false; this.stopReason = ''; this.phase = 'stopped'; this.speed = 0;
    return { ok: true, scope: 'simulation', hardwareReset: false };
  }
  accept(part) {
    if (!this.sessionFresh() || this.unavailable() || this.active || this.parts.length >= finishingLayout.parkingCapacity ||
      this.parts.some(item => item.id === part.id) || part.processed !== 2 ||
      !Number.isFinite(part.weldProgress) || part.weldProgress < 1 - 1e-7 ||
      !Number.isFinite(part.coating) || part.coating < 1 - 1e-7) return false;
    const slotIndex = finishingLayout.slots.findIndex((_, index) => !this.parts.some(item => item.slotIndex === index));
    if (slotIndex < 0) return false;
    this.parts.push({ ...part, robotId: '', status: 'to_assembly', positionWorld: { ...finishingLayout.entry }, yaw: 0,
      routeDistance: 0, assemblyProgress: 0, inspectionProgress: 0, quality: 'pending', qualityChecks: [],
      weldPassed: false, paintPassed: false, assemblyPassed: false, slotIndex, ready: false, parkingStep: 0 });
    this.accepted++;
    return true;
  }
  move(part, target, dt, speed = finishingLayout.speed) {
    const remaining = distance(part.positionWorld, target), step = Math.min(remaining, speed * dt);
    if (remaining < 1e-7) { part.positionWorld = { ...target }; return true; }
    for (const axis of ['x', 'y', 'z']) part.positionWorld[axis] += (target[axis] - part.positionWorld[axis]) * step / remaining;
    part.routeDistance += step; this.speed = step / dt;
    if (remaining - step < 1e-7) { part.positionWorld = { ...target }; return true; }
    return false;
  }
  inspect(part) {
    part.qualityChecks = [['weld', part.weldProgress], ['paint', part.coating], ['assembly', part.assemblyProgress]]
      .map(([name, measured]) => ({ name, measured, required: 1, passed: Number.isFinite(measured) && measured >= 1 - 1e-7 && measured <= 1 + 1e-7 }));
    [part.weldPassed, part.paintPassed, part.assemblyPassed] = part.qualityChecks.map(check => check.passed);
    part.quality = part.qualityChecks.every(check => check.passed) ? 'passed' : 'rejected';
    part.status = part.quality === 'passed' ? 'to_release' : 'rejected';
  }
  tick(now, elapsedMs) {
    this.sequence++; this.speed = 0; this.waiting = false; this.waitingReason = '';
    if (!this.running) return;
    if (!this.sessionFresh(now)) return this.stop('Потеря оператора или heartbeat участка выпуска', true);
    if (!Number.isFinite(elapsedMs) || elapsedMs <= 0 || elapsedMs > this.controller.config.commandTimeoutMs)
      return this.stop('Задержка серверного цикла участка выпуска', true);
    if (this.unavailable()) return this.stop('Участок выпуска доступен только для линий симуляции', true);
    const part = this.active, dt = Math.min(elapsedMs / 1000, .05);
    if (!part) {
      this.phase = this.parts.length >= finishingLayout.parkingCapacity ? 'parking_full' : 'waiting_input';
      this.waiting = true; this.waitingReason = this.phase === 'parking_full' ? 'Все места стоянки заняты' : 'Ожидание окрашенного кузова';
      return;
    }
    switch (part.status) {
      case 'to_assembly': if (this.move(part, finishingLayout.assemblyEntry, dt)) part.status = 'lifting'; break;
      case 'lifting': if (this.move(part, finishingLayout.assembly, dt, finishingLayout.liftSpeed)) part.status = 'assembling'; break;
      case 'assembling':
        part.assemblyProgress = Math.min(1, part.assemblyProgress + dt / finishingLayout.assemblySeconds);
        if (part.assemblyProgress >= 1) part.status = 'to_inspection';
        break;
      case 'to_inspection': if (this.move(part, finishingLayout.inspection, dt)) part.status = 'inspecting'; break;
      case 'inspecting':
        part.inspectionProgress = Math.min(1, part.inspectionProgress + dt / finishingLayout.inspectionSeconds);
        if (part.inspectionProgress >= 1) this.inspect(part);
        break;
      case 'to_release': if (this.move(part, finishingLayout.release, dt)) part.status = 'lowering'; break;
      case 'lowering': if (this.move(part, finishingLayout.floor, dt, finishingLayout.liftSpeed)) part.status = 'parking'; break;
      case 'parking': {
        const slot = finishingLayout.slots[part.slotIndex];
        const target = part.parkingStep === 0 ? { ...finishingLayout.floor, x: slot.x } : part.parkingStep === 1 ? { ...slot, y: finishingLayout.floor.y } : slot;
        if (this.move(part, target, dt, part.parkingStep === 2 ? finishingLayout.liftSpeed : finishingLayout.speed)) {
          if (part.parkingStep < 2) part.parkingStep++;
          else { part.status = 'parked'; part.ready = part.quality === 'passed'; this.completed++; }
        }
        break;
      }
      case 'rejected': this.waiting = true; this.waitingReason = 'ОТК симуляции: проверки не пройдены; автомобиль сохранён на посту'; break;
      default: return this.stop('Неизвестное состояние участка выпуска', true);
    }
    this.phase = part.status;
  }
  snapshot() {
    return { available: !this.unavailable(), running: this.running, phase: this.phase, stopReason: this.stopReason,
      fault: this.fault, owner: this.owner?.name || '', sequence: this.sequence, speed: this.running ? this.speed : 0,
      capacity: finishingLayout.capacity, parkingCapacity: finishingLayout.parkingCapacity,
      parkedCount: this.parts.filter(part => part.status === 'parked').length,
      waiting: this.waiting, waitingReason: this.waitingReason, accepted: this.accepted, completed: this.completed,
      parts: this.parts.map(part => ({ ...part, positionWorld: { ...part.positionWorld }, qualityChecks: part.qualityChecks.map(check => ({ ...check })) })),
      slots: finishingLayout.slots.map((positionWorld, index) => {
        const part = this.parts.find(item => item.slotIndex === index);
        return { index, positionWorld: { ...positionWorld }, partId: part?.id || '', occupied: part?.status === 'parked', reserved: !!part };
      }) };
  }
}
