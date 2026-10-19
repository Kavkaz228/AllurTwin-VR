import { createWriteStream } from 'node:fs';
import { mkdir, readdir, readFile, stat } from 'node:fs/promises';
import path from 'node:path';
import { randomUUID } from 'node:crypto';

// Stream long sessions to disk, instead of retaining all samples in RAM.
export class RecordingStore {
  constructor(directory) { this.directory = directory; this.active = new Map(); this.errors = new Map(); this.pending = new Set(); }
  async start(robotId, owner, model) {
    if (this.active.has(robotId)) throw new Error('Запись уже идёт.');
    await mkdir(this.directory, { recursive: true });
    const id = `${Date.now()}-${randomUUID()}`;
    const stream = createWriteStream(path.join(this.directory, `${id}.jsonl`), { flags: 'wx' });
    await new Promise((resolve, reject) => { stream.once('open', resolve); stream.once('error', reject); });
    const record = { id, robotId, stream, samples: 0, lastSample: 0, startedAt: Date.now(), bytes: 0, error: null };
    stream.on('error', error => { record.error = error.message; this.errors.set(robotId, error.message); this.active.delete(robotId); });
    this.errors.delete(robotId);
    this.active.set(robotId, record);
    this.write(record, { type: 'header', version: 1, id, robotId, owner, startedAt: record.startedAt, model });
    return this.status(robotId);
  }
  write(record, data) {
    const line = JSON.stringify(data) + '\n';
    if (record.stream.writableLength > 1024 * 1024 || record.bytes + Buffer.byteLength(line) > 128 * 1024 * 1024) {
      record.error = 'Достигнут предел записи или диск не успевает записывать данные.';
      this.errors.set(record.robotId, record.error);
      void this.stop(record.robotId, record.error).catch(() => {});
      return false;
    }
    record.bytes += Buffer.byteLength(line);
    record.stream.write(line);
    return true;
  }
  sample(robotId, snapshot, now) {
    const record = this.active.get(robotId);
    if (!record || now - record.lastSample < 100) return;
    record.lastSample = now;
    if (this.write(record, { type: 'sample', time: Date.now(), sequence: snapshot.sequence, tool: snapshot.tool, joints: snapshot.joints, moving: snapshot.moving })) record.samples++;
  }
  async stop(robotId, reason = 'operator') {
    const record = this.active.get(robotId);
    if (!record) return { active: false };
    this.active.delete(robotId);
    const footer = JSON.stringify({ type: 'footer', samples: record.samples, endedAt: Date.now(), reason }) + '\n';
    const finished = new Promise((resolve, reject) => { record.stream.once('error', reject); record.stream.end(footer, resolve); });
    this.pending.add(finished);
    try { await finished; } finally { this.pending.delete(finished); }
    return { active: false, id: record.id, samples: record.samples, error: record.error };
  }
  status(robotId) {
    const record = this.active.get(robotId);
    return { active: !!record, id: record?.id || '', samples: record?.samples || 0, error: this.errors.get(robotId) || null };
  }
  async list() {
    await mkdir(this.directory, { recursive: true });
    const files = await readdir(this.directory);
    const activeIds = new Set([...this.active.values()].map(record => record.id));
    return files.filter(f => /^\d+-[a-f0-9-]{36}\.jsonl$/.test(f)).sort().reverse().map(f => ({ id: f.slice(0, -6), active: activeIds.has(f.slice(0, -6)) }));
  }
  async read(id, maxBytes = 128 * 1024 * 1024 + 8192) {
    if (!/^\d+-[a-f0-9-]{36}$/.test(id)) throw new Error('Неверный ID записи.');
    const file = path.join(this.directory, `${id}.jsonl`);
    if ((await stat(file)).size > maxBytes) throw new Error('Запись слишком велика для этой операции.');
    return readFile(file, 'utf8');
  }
  async close() {
    await Promise.all([...this.active.keys()].map(id => this.stop(id, 'server shutdown')));
    await Promise.all([...this.pending]);
  }
}
