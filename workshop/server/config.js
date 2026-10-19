import { readFile } from 'node:fs/promises';
import path from 'node:path';

export async function loadConfig(env = process.env) {
  const file = env.CONFIG_FILE ? JSON.parse(await readFile(env.CONFIG_FILE, 'utf8')) : {};
  const config = {
    host: env.HOST || file.host || '127.0.0.1',
    port: Number(env.PORT || file.port || 4182),
    allurBriefFile: env.ALLUR_BRIEF_FILE || file.allurBriefFile || '',
    accessToken: env.VR_ACCESS_TOKEN || file.accessToken || 'local-training-only',
    tlsCert: env.TLS_CERT_FILE || file.tlsCert,
    tlsKey: env.TLS_KEY_FILE || file.tlsKey,
    dataDir: path.resolve(env.DATA_DIR || file.dataDir || 'data'),
    tickMs: 20, commandTimeoutMs: 300, leaseTimeoutMs: 2000,
    telemetryMaxAgeMs: 250,
    production: { enabled: file.production?.enabled !== false, beltSpeed: file.production?.beltSpeed ?? 0.35, lines: file.production?.lines || {} },
    robots: file.robots || [
      { id: 'training-arm', name: 'Сварочная линия · Пост 1', mode: 'simulation', lineId: 'welding', process: 'welding' },
      { id: 'training-arm-2', name: 'Сварочная линия · Пост 2', mode: 'simulation', lineId: 'welding', process: 'welding' },
      { id: 'paint-arm', name: 'Покрасочная линия · Пост 1', mode: 'simulation', lineId: 'painting', process: 'painting' },
      { id: 'paint-arm-2', name: 'Покрасочная линия · Пост 2', mode: 'simulation', lineId: 'painting', process: 'painting' },
    ],
    ollama: { ...file.ollama, url: env.OLLAMA_URL || file.ollama?.url || 'http://127.0.0.1:11434', model: env.OLLAMA_MODEL || file.ollama?.model || '', timeoutMs: env.OLLAMA_TIMEOUT_MS ? Number(env.OLLAMA_TIMEOUT_MS) : file.ollama?.timeoutMs },
    siemens: file.siemens || {},
  };
  const loopback = ['127.0.0.1', '::1', 'localhost'].includes(config.host);
  if (!loopback && (!config.tlsCert || !config.tlsKey || config.accessToken === 'local-training-only' || config.accessToken.length < 24)) {
    throw new Error('Доступ из очков требует TLS_CERT_FILE, TLS_KEY_FILE и собственного VR_ACCESS_TOKEN (не менее 24 символов).');
  }
  if (!Number.isInteger(config.port) || config.port < 0 || config.port > 65535) throw new Error('Неверный PORT.');
  if (!Array.isArray(config.robots) || !config.robots.length || config.robots.length > 16) throw new Error('Нужно от 1 до 16 роботов.');
  const ids = new Set();
  for (const robot of config.robots) {
    if (!/^[a-z0-9-]{1,64}$/.test(robot.id) || ids.has(robot.id)) throw new Error('ID роботов должны быть уникальными: a-z, 0-9, дефис.');
    if (!['simulation', 'rosbridge'].includes(robot.mode)) throw new Error('Неизвестный тип робота.');
    ids.add(robot.id);
  }
  return config;
}
