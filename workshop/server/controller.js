import { randomUUID, timingSafeEqual } from 'node:crypto';
import { performance } from 'node:perf_hooks';
import { KinematicRobot } from './robot/index.js';
import { RosbridgeRobot } from './integrations/rosbridge.js';
import { OllamaAdvisor } from './integrations/ollama.js';
import { SiemensValidationGateway } from './integrations/siemens.js';
import { RecordingStore } from './recordings.js';
import { ProductionLine, TransferConveyor, FinishingStation, productionDefinitions } from './production.js';
import { rulesAdvice } from './integrations/rules.js';
import { AllurBrief } from './allur-brief.js';
import path from 'node:path';

export function equalToken(actual, expected) {
  if (typeof actual !== 'string') return false;
  const a = Buffer.from(actual), b = Buffer.from(expected);
  return a.length === b.length && timingSafeEqual(a, b);
}

export class WorkshopController {
  constructor(config, dependencies = {}) {
    this.config = config;
    this.serverSessionId = randomUUID();
    this.now = dependencies.now || (() => performance.now());
    this.robots = new Map(config.robots.map(definition => [definition.id, {
      definition, driver: dependencies.drivers?.[definition.id] || (definition.mode === 'rosbridge' ? new RosbridgeRobot(definition.connection) : new KinematicRobot(definition.kinematics)),
      lease: null, sequence: 0, lastCommand: -Infinity, deadman: false, stopLatched: false, appStopReason: '',
      history: new Map(), lastSnapshot: null, recordingBusy: false,
    }]));
    this.recordings = dependencies.recordings || new RecordingStore(path.join(config.dataDir, 'recordings'));
    this.advisor = dependencies.advisor || new OllamaAdvisor(config.ollama);
    this.validator = dependencies.validator || new SiemensValidationGateway(config.siemens);
    this.allur = dependencies.allur || new AllurBrief(config.allurBriefFile);
    this.nextAllurTelemetry = -Infinity;
    this.clients = new Set();
    this.operations = new Set();
    this.closing = false;
    this.events = [];
    this.lastTick = this.now();
    this.loopMs = 0;
    this.startedAt=this.lastTick; this.maxLoopMs=0; this.maxTickGapMs=0; this.lateTicks=0; this.disconnects=0;
    this.productionLines = new Map(productionDefinitions.map(line => [line.lineId,
      new ProductionLine(this, { ...config.production, ...config.production?.lines?.[line.lineId], lineId: line.lineId })]));
    this.production = this.productionLines.get('welding'); // Protocol v1 default remains the welding line.
    this.transfer = new TransferConveyor(this);
    this.finishing = new FinishingStation(this);
  }
  lineForRobot(robotId) { return [...this.productionLines.values()].find(line => line.robotIds.includes(robotId)); }
  log(robotId, event) {
    this.events.push({ time: Date.now(), robotId, event });
    if (this.events.length > 100) this.events.shift();
  }
  async connectDrivers() {
    await Promise.all([...this.robots.values()].map(async r => {
      if (r.definition.mode !== 'rosbridge') return;
      try { await r.driver.connect(); } catch (error) { this.log(r.definition.id, `Нет связи: ${error.message}`); }
    }));
  }
  attach(socket) {
    if (this.clients.size >= 24) { socket.close(1013, 'Server busy'); return; }
    const client = { socket, id: randomUUID(), authenticated: false, name: '', subscriptions: new Set(), received: [], aiBusy: false, validationBusy: false };
    this.clients.add(client);
    const authTimer = setTimeout(() => { if (!client.authenticated) socket.close(1008, 'Authentication timeout'); }, 5000);
    authTimer.unref();
    socket.on('message', (data, binary) => {
      try {
        const now = this.now();
        client.received = client.received.filter(t => now - t < 1000);
        if (client.received.length >= 120 || binary) throw new Error('Неверный формат или слишком много сообщений.');
        client.received.push(now);
        const message = JSON.parse(data.toString('utf8'));
        if (!message || typeof message !== 'object' || Array.isArray(message) || typeof message.type !== 'string') throw new Error('Неверный формат сообщения.');
        const operation = Promise.resolve(this.handle(client, message)).catch(error => this.ack(client, message, false, error.message));
        this.operations.add(operation);
        void operation.finally(() => this.operations.delete(operation));
      } catch {
        this.detach(client, 'Ошибка протокола');
        socket.close(1008, 'Invalid protocol');
      }
    });
    socket.on('close', (code) => { clearTimeout(authTimer); this.detach(client, `Клиент отключился (WebSocket ${code})`); });
    socket.on('error', () => { clearTimeout(authTimer); this.detach(client, 'Ошибка соединения'); });
    return client;
  }
  send(client, message) {
    if (client.socket.readyState !== 1) return false;
    if (client.socket.bufferedAmount > 256 * 1024) {
      this.detach(client, 'Клиент не принимает телеметрию');
      client.socket.close(1013, 'Slow receiver');
      return false;
    }
    client.socket.send(JSON.stringify(message), error => {
      if (error) this.detach(client, 'Ошибка отправки WebSocket');
    });
    return true;
  }
  ack(client, message, ok, error, extra = {}) { this.send(client, { type: 'ack', requestId: message.id, ok, ...(error ? { error } : {}), ...extra }); }
  detach(client, reason) {
    if (!this.clients.has(client)) return;
    this.disconnects++;
    this.log('connection', reason);
    console.log(`[connection] ${new Date().toISOString()} ${client.id}: ${reason}`);
    for (const line of this.productionLines.values()) if (line.owner?.clientId === client.id) line.stop(reason, true);
    if (this.transfer.owner?.clientId === client.id) this.transfer.stop(reason, true);
    if (this.finishing.owner?.clientId === client.id) this.finishing.stop(reason, true);
    this.clients.delete(client);
    client.authenticated = false;
    for (const r of this.robots.values()) if (r.lease?.clientId === client.id) this.revoke(r, reason);
  }
  stop(r, reason) {
    const line = this.lineForRobot(r.definition.id);
    if (line?.running) return line.stop(reason, true);
    r.stopLatched = true;
    r.appStopReason = reason;
    r.deadman = false;
    r.driver.setDeadman?.(false);
    r.driver.stop(reason);
    r.lastCommand = -Infinity;
  }
  revoke(r, reason) {
    this.stop(r, reason);
    r.lease = null;
    void this.recordings.stop(r.definition.id, reason).catch(error => this.log(r.definition.id, error.message));
    this.log(r.definition.id, reason);
  }
  owns(client, r, message) {
    return r.lease?.clientId === client.id && equalToken(message.leaseId, r.lease.id) && this.now() - r.lease.lastHeartbeat <= this.config.leaseTimeoutMs;
  }
  async handle(client, message) {
    if (this.closing) return this.ack(client, message, false, 'Сервер завершает работу.');
    if (typeof message.id !== 'string' || message.id.length > 96) return this.ack(client, message, false, 'Нужен строковый id до 96 символов.');
    if (!client.authenticated) {
      if (message.type !== 'auth' || message.protocolVersion !== 1 || !equalToken(message.accessToken, this.config.accessToken)) {
        this.ack(client, message, false, 'Авторизация отклонена.');
        client.socket.close(1008, 'Unauthorized');
        return;
      }
      client.authenticated = true;
      client.name = String(message.clientName || 'Оператор').slice(0, 80);
      return this.send(client, { type: 'welcome', protocolVersion: 1, serverTime: Date.now(), serverSessionId: this.serverSessionId, allur: this.allur.snapshot(),
        finishing: this.finishing.snapshot(), robots: [...this.robots.values()].map(r => ({ id: r.definition.id, name: r.definition.name, mode: r.definition.mode, lineId: this.lineForRobot(r.definition.id)?.lineId || '', process: this.lineForRobot(r.definition.id)?.process || '' })) });
    }
    if (message.type === 'ping') return this.send(client, { type: 'pong', requestId: message.id, clientTime: message.clientTime, serverTime: Date.now(), diagnostics:this.diagnostics(), integrations:this.integrationStatus() });
    if (message.type.startsWith('finishing.')) {
      let result;
      if (message.type === 'finishing.start') result = this.finishing.start(client);
      else if (message.type === 'finishing.stop') result = this.finishing.stop();
      else if (message.type === 'finishing.reset') result = this.finishing.reset();
      else if (message.type === 'finishing.heartbeat') result = this.finishing.heartbeat(client, message.finishingId);
      else result = { ok: false, error: 'Неизвестная команда участка выпуска.' };
      return this.ack(client, message, result.ok, result.error, { ...(result.finishingId ? { finishingId: result.finishingId } : {}),
        ...(result.scope ? { scope: result.scope, hardwareReset: false } : {}), finishing: this.finishing.snapshot() });
    }
    if (message.type.startsWith('transfer.')) {
      let result;
      if (message.type === 'transfer.mode') result = this.transfer.setMode(message.mode);
      else if (message.type === 'transfer.start') result = this.transfer.start(client);
      else if (message.type === 'transfer.stop') result = this.transfer.stop();
      else if (message.type === 'transfer.reset') result = this.transfer.reset();
      else if (message.type === 'transfer.heartbeat') result = this.transfer.heartbeat(client, message.transferId);
      else if (message.type === 'transfer.jog') result = this.transfer.jog(client, message);
      else result = { ok: false, error: 'Неизвестная команда грузового конвейера.' };
      return this.ack(client, message, result.ok, result.error, { ...(result.transferId ? { transferId: result.transferId } : {}),
        ...(result.scope ? { scope: result.scope, hardwareReset: false } : {}), transfer: this.transfer.snapshot() });
    }
    if (message.type.startsWith('production.')) {
      const lineId = message.lineId === undefined ? 'welding' : message.lineId;
      const line = this.productionLines.get(lineId);
      if (!line) return this.ack(client, message, false, 'Производственная линия не найдена.', { lineId: typeof lineId === 'string' ? lineId.slice(0, 64) : '' });
      let result;
      if (message.type === 'production.start') result = line.start(client);
      else if (message.type === 'production.stop') result = line.stop('Линия остановлена оператором');
      else if (message.type === 'production.reset') result = line.reset();
      else if (message.type === 'production.heartbeat') result = line.heartbeat(client, message.productionId);
      else result = { ok: false, error: 'Неизвестная команда линии.' };
      return this.ack(client, message, result.ok, result.error, { lineId, ...(result.productionId ? { productionId: result.productionId } : {}), ...(result.scope ? { scope: result.scope, hardwareReset: false } : {}), production: line.snapshot() });
    }
    const r = this.robots.get(message.robotId);
    if (!r) return this.ack(client, message, false, 'Робот не найден.');
    if (message.type === 'subscribe') {
      client.subscriptions.clear();
      // Watching other workstations grants telemetry only. Every command below
      // still checks the specific robot's lease, sequence and telemetry history.
      const watched = message.watchAll === true ? [...this.robots.values()] : [r];
      for (const watchedRobot of watched) {
        client.subscriptions.add(watchedRobot.definition.id);
        if (!watchedRobot.history.has(watchedRobot.sequence)) watchedRobot.history.set(watchedRobot.sequence, this.now());
      }
      this.ack(client, message, true);
      for (const watchedRobot of watched) this.send(client, this.snapshot(watchedRobot));
      return;
    }
    if (message.type === 'acquire') {
      const line = this.lineForRobot(r.definition.id);
      if (line?.running) line.stop('Переход на ручное управление: AUTO и конвейер этой линии остановлены');
      if (r.lease && this.now() - r.lease.lastHeartbeat > this.config.leaseTimeoutMs) this.revoke(r, 'Истёк сеанс оператора');
      if (r.lease) return this.ack(client, message, false, 'Робот занят.');
      r.lease = { id: randomUUID(), clientId: client.id, owner: client.name, lastHeartbeat: this.now(), sequence: -1 };
      this.log(message.robotId, `${client.name}: получено управление`);
      return this.ack(client, message, true, null, { leaseId: r.lease.id });
    }
    // Any authenticated observer can stop; only the owner may reset.
    if (message.type === 'stop') { this.stop(r, 'Остановлено оператором'); this.log(message.robotId, 'Остановка оператором'); return this.ack(client, message, true); }
    if (message.type === 'ai.request') {
      if (client.aiBusy) return this.ack(client, message, false, 'Дождитесь ответа ИИ.');
      if (typeof message.prompt !== 'string' || message.prompt.length > 2000) return this.ack(client, message, false, 'Запрос ИИ: до 2000 символов.');
      client.aiBusy = true;
      try {
        const snapshot = this.snapshot(r);
        if(message.cabinetContext!==undefined){
          if(typeof message.cabinetContext!=='string'||message.cabinetContext.length>6000)throw new Error('Cabinet context too large');
          const c=JSON.parse(message.cabinetContext);
          if(!c||typeof c!=='object'||Array.isArray(c))throw new Error('Invalid cabinet context');
          const allowed=['mode','mounted','wires','correctWires','mistakes','powered','fault','next','feedback'];
          snapshot.cabinet={clientReported:true,...Object.fromEntries(allowed.filter(k=>typeof c[k]==='string'&&c[k].length<=1200||typeof c[k]==='number'&&Number.isFinite(c[k])||typeof c[k]==='boolean').map(k=>[k,c[k]]))};
        }
        const advice = snapshot.cabinet?.mode==='Экзамен'&&/шкаф|сборк|провод|клемм|монтаж|предохран|неисправ/i.test(message.prompt)
          ? rulesAdvice(snapshot,message.prompt) : await this.advisor.advise(snapshot, message.prompt);
        const result = advice.ok ? advice : rulesAdvice(snapshot, message.prompt, advice.error);
        this.send(client, { ...result, type: 'ai.result', requestId: message.id });
      }
      finally { client.aiBusy = false; }
      return;
    }
    if (message.type === 'validation.request') {
      if (client.validationBusy) return this.ack(client, message, false, 'Проверка уже идёт.');
      client.validationBusy = true;
      try {
        const text = await this.recordings.read(message.recordingId, 8 * 1024 * 1024);
        const lines = text.trim().split('\n').map(line => JSON.parse(line));
        if (lines[0].robotId !== message.robotId || lines.at(-1).type !== 'footer') throw new Error('Нужна завершённая запись выбранного робота.');
        const trajectory = lines.filter(l => l.type === 'sample').map(l => ({ t: (l.time - lines[0].startedAt) / 1000, ...l.tool, ...(l.joints?.length ? { joints: l.joints } : {}) }));
        const result = await this.validator.validate({ robotId: message.robotId, trajectory, modelRevision: message.modelRevision });
        this.send(client, { ...result, type: 'validation.result', requestId: message.id });
      } finally { client.validationBusy = false; }
      return;
    }
    if (!this.owns(client, r, message)) return this.ack(client, message, false, 'Нет действующего права управления.');
    if (message.type === 'heartbeat') { r.lease.lastHeartbeat = this.now(); return this.ack(client, message, true); }
    if (message.type === 'release') { this.revoke(r, 'Управление освобождено'); return this.ack(client, message, true); }
    if (message.type === 'reset') {
      if (r.deadman) return this.ack(client, message, false, 'Сначала отпустите разрешающую кнопку.');
      const physical = r.definition.mode === 'rosbridge';
      const feedback = r.driver.snapshot();
      const result = physical
        ? { ok: feedback.connected === true && feedback.telemetryFresh === true, error: 'Для сброса приложения нужна свежая телеметрия контроллера.' }
        : r.driver.reset();
      if (result.ok) { r.stopLatched = false; r.appStopReason = ''; }
      return this.ack(client, message, result.ok, result.ok ? null : result.error, { scope: 'application', hardwareReset: false });
    }
    if (message.type === 'recording.start' || message.type === 'recording.stop') {
      if (r.recordingBusy) return this.ack(client, message, false, 'Операция записи ещё не завершена.');
      r.recordingBusy = true;
      try {
        const result = message.type === 'recording.start'
          ? await this.recordings.start(message.robotId, client.name, r.driver.snapshot().model)
          : await this.recordings.stop(message.robotId);
        if (message.type === 'recording.start' && !this.owns(client, r, message)) await this.recordings.stop(message.robotId, 'lease lost');
        this.ack(client, message, true, null, { recording: result });
      } finally { r.recordingBusy = false; }
      return;
    }
    if (message.type !== 'command') return this.ack(client, message, false, 'Неизвестная команда.');
    if (!Number.isSafeInteger(message.sequence) || message.sequence <= r.lease.sequence) {
      this.stop(r, 'Повторная или устаревшая команда');
      return this.ack(client, message, false, 'Нарушен порядок команд.');
    }
    r.lease.sequence = message.sequence;
    if (message.deadman !== true) {
      r.deadman = false;
      r.driver.setDeadman?.(false);
      if (r.definition.mode === 'rosbridge') r.driver.stop('deadman released');
      return this.ack(client, message, true);
    }
    if (r.stopLatched) return this.ack(client, message, false, r.appStopReason + ' — требуется Reset.');
    if(r.definition.mode==='simulation'&&message.payloadKg!==undefined){const load=r.driver.setPayload(message.payloadKg);if(!load.ok){this.stop(r,load.error);return this.ack(client,message,false,load.error);}}
    const observedAt = r.history.get(message.observedSequence);
    if (!client.subscriptions.has(message.robotId) || observedAt === undefined || this.now() - observedAt > this.config.telemetryMaxAgeMs) {
      this.stop(r, 'Устаревшая телеметрия');
      return this.ack(client, message, false, 'Нет свежей телеметрии.');
    }
    r.deadman = true;
    r.driver.setDeadman?.(true);
    let result;
    try {
      result = message.teleop === true && r.definition.mode === 'simulation'
        ? r.driver.commandTeleop(message.pose, message.gripper)
        : r.driver.command(message.pose, message.gripper);
    }
    catch (error) { result = { ok: false, error: error.message }; }
    if (!result.ok) this.stop(r, result.error || 'Команда отклонена');
    else r.lastCommand = this.now();
    return this.ack(client, message, result.ok, result.error);
  }
  snapshot(r, includeAllur = true) {
    const s = r.driver.snapshot();
    const physical = r.definition.mode === 'rosbridge';
    return { ...s, ...(physical ? { tool: s.telemetry || null, joints: s.telemetry?.joints || [], links: [], moving: false, motionKnown: false } : { motionKnown: true }),
      fault: r.stopLatched ? r.appStopReason : s.fault || null,
      stopReason: r.appStopReason || s.stopReason,
      type: 'telemetry', robotId: r.definition.id, name: r.definition.name,
      mode: r.definition.mode, connected: !physical || (s.connected === true && s.telemetryFresh === true),
      sequence: r.sequence, serverTime: Date.now(), leaseOwner: r.lease?.owner || '', recording: this.recordings.status(r.definition.id),
      lineId: this.lineForRobot(r.definition.id)?.lineId || '', process: this.lineForRobot(r.definition.id)?.process || '',
      production: (this.lineForRobot(r.definition.id) || this.production).snapshot(), transfer: this.transfer.snapshot(), finishing: this.finishing.snapshot(),
      ...(includeAllur ? { allur: this.allur.snapshot() } : {}) };
  }
  tick() {
    const started = this.now(), elapsed = started - this.lastTick;
    this.maxTickGapMs=Math.max(this.maxTickGapMs,elapsed);
    if(elapsed>this.config.tickMs*2) this.lateTicks++;
    this.lastTick = started;
    for (const line of this.productionLines.values()) line.tick(started, elapsed);
    this.transfer.tick(started, elapsed);
    this.finishing.tick(started, elapsed);
    const includeAllur = started >= this.nextAllurTelemetry;
    if (includeAllur) this.nextAllurTelemetry = started + 1000;
    for (const r of this.robots.values()) {
      if (r.lease && started - r.lease.lastHeartbeat > this.config.leaseTimeoutMs) this.revoke(r, 'Потеря heartbeat оператора');
      if (r.deadman && (started - r.lastCommand > this.config.commandTimeoutMs || elapsed > this.config.commandTimeoutMs)) this.stop(r, 'Потеря команд или задержка сервера');
      r.driver.tick(Math.min(elapsed / 1000, 0.05));
      r.sequence++;
      r.history.set(r.sequence, started);
      for (const [seq, time] of r.history) if (started - time > this.config.telemetryMaxAgeMs) r.history.delete(seq);
      r.lastSnapshot = this.snapshot(r, includeAllur);
      this.recordings.sample(r.definition.id, r.lastSnapshot, started);
      for (const client of this.clients) if (client.authenticated && client.subscriptions.has(r.definition.id)) this.send(client, r.lastSnapshot);
    }
    this.loopMs = this.now() - started;
    this.maxLoopMs=Math.max(this.maxLoopMs,this.loopMs);
  }
  diagnostics() {return {uptimeSeconds:Math.max(0,(this.now()-this.startedAt)/1000),loopMs:this.loopMs,maxLoopMs:this.maxLoopMs,maxTickGapMs:this.maxTickGapMs,lateTicks:this.lateTicks,disconnects:this.disconnects};}
  integrationStatus(){const amas=[...this.robots.values()].find(r=>r.definition.integration==='amas'&&r.definition.mode==='rosbridge');const s=amas?.driver.snapshot();return {ollama:this.advisor.snapshot(),siemens:this.validator.snapshot(),amas:{status:!amas?'NOT_CONFIGURED':s.connected&&s.telemetryFresh?'BRIDGE_TELEMETRY_LIVE':'BRIDGE_NO_FRESH_TELEMETRY',nativeApiVerified:false}};}
  status() {
    return { version: '0.2.0', protocolVersion: 1, serverSessionId: this.serverSessionId, tickMs: this.config.tickMs, loopMs: this.loopMs,
      diagnostics:this.diagnostics(), clients: this.clients.size, robots: [...this.robots.values()].map(r => this.snapshot(r, false)), allur: this.allur.snapshot(),
      integrations:this.integrationStatus(), production: this.production.snapshot(), productionLines: [...this.productionLines.values()].map(line => line.snapshot()), transfer: this.transfer.snapshot(), finishing: this.finishing.snapshot(), events: this.events };
  }
  async close() {
    this.closing = true;
    await this.allur.close();
    if (this.transfer.running) this.transfer.stop('Сервер выключается', true);
    if (this.finishing.running) this.finishing.stop('Сервер выключается', true);
    for (const r of this.robots.values()) { this.stop(r, 'Сервер выключается'); r.lease = null; r.driver.disconnect?.(); }
    for (const client of this.clients) client.socket.close(1001, 'Server shutdown');
    await Promise.all([...this.operations]);
    await this.recordings.close();
  }
}
