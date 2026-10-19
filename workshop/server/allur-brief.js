import { open } from 'node:fs/promises';

const MAX_BYTES = 32768;
const MAX_AGE_MS = 5000;
const empty = summary => ({ available: false, generatedAtUnixMs: 0, source: 'AllurTwin', summary,
  activeVehicles: 0, readyVehicles: 0, registeredVehicles: 0, qualityPassed: 0, qualityRejected: 0,
  alerts: [], robotInsights: [] });

export function validateBrief(value) {
  if (!value || typeof value !== 'object' || Array.isArray(value) || typeof value.available !== 'boolean') throw new Error('Invalid brief');
  const string = (text, limit) => {
    if (typeof text !== 'string' || text.length > limit || /[\u0000-\u0008\u000b\u000c\u000e-\u001f]/.test(text)) throw new Error('Invalid brief text');
    return text;
  };
  const count = number => {
    if (!Number.isSafeInteger(number) || number < 0 || number > 1000000000) throw new Error('Invalid counter');
    return number;
  };
  if (!Number.isSafeInteger(value.generatedAtUnixMs) || value.generatedAtUnixMs < 0) throw new Error('Invalid timestamp');
  if (!Array.isArray(value.alerts) || value.alerts.length > 8 || !Array.isArray(value.robotInsights) || value.robotInsights.length > 16) throw new Error('Invalid collection');
  const ids = new Set();
  return { available: value.available, generatedAtUnixMs: value.generatedAtUnixMs,
    source: string(value.source, 160), summary: string(value.summary, 1200),
    activeVehicles: count(value.activeVehicles), readyVehicles: count(value.readyVehicles),
    registeredVehicles: count(value.registeredVehicles), qualityPassed: count(value.qualityPassed), qualityRejected: count(value.qualityRejected),
    alerts: value.alerts.map(text => string(text, 360)),
    robotInsights: value.robotInsights.map(robot => {
      if (!robot || typeof robot !== 'object' || !/^[a-z0-9-]{1,64}$/.test(robot.robotId) || ids.has(robot.robotId)) throw new Error('Invalid robot');
      ids.add(robot.robotId);
      return { robotId: robot.robotId, status: string(robot.status, 120), cycles: count(robot.cycles), condition: string(robot.condition, 600) };
    }) };
}

// One bounded asynchronous file read per second, outside the motion loop.
// The Python service atomically replaces this file after committing observations.
export class AllurBrief {
  constructor(file, { now = Date.now, start = true } = {}) {
    this.file = file; this.now = now; this.closed = false; this.nextRead = -Infinity; this.pending = null;
    this.value = empty(file ? 'Ожидание аналитики AllurTwin.' : 'AllurTwin не настроен.');
    if (file && start) {
      void this.poll();
      this.timer = setInterval(() => { void this.poll(); }, 1000);
      this.timer.unref();
    }
  }
  poll() {
    if (this.closed || !this.file || this.pending || this.now() < this.nextRead) return this.pending || Promise.resolve();
    this.nextRead = this.now() + 1000;
    this.pending = this.read().finally(() => { this.pending = null; });
    return this.pending;
  }
  async read() {
    let file;
    try {
      file = await open(this.file, 'r');
      const buffer = Buffer.alloc(MAX_BYTES + 1);
      const { bytesRead } = await file.read(buffer, 0, buffer.length, 0);
      if (bytesRead > MAX_BYTES) throw new Error('Oversized brief');
      const value = validateBrief(JSON.parse(buffer.subarray(0, bytesRead).toString('utf8')));
      if (!this.closed) this.value = value;
    } catch {
      if (!this.closed) this.value = empty('Связь с аналитикой AllurTwin недоступна. Управление цехом работает независимо.');
    } finally { await file?.close(); }
  }
  snapshot() {
    const age = this.now() - this.value.generatedAtUnixMs;
    if (this.value.available && (age > MAX_AGE_MS || age < -1000))
      return { ...this.value, available: false, summary: 'Данные AllurTwin устарели. Последние результаты сохранены; дождитесь восстановления связи.' };
    return this.value;
  }
  async close() { this.closed = true; clearInterval(this.timer); await this.pending; }
}
