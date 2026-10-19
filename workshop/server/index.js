import http from 'node:http';
import https from 'node:https';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { WebSocketServer } from 'ws';
import { WorkshopController, equalToken } from './controller.js';
import { loadConfig } from './config.js';

const publicRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../public');
const mime = { '.html': 'text/html; charset=utf-8', '.css': 'text/css; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.svg': 'image/svg+xml' };
function json(res, status, value) {
  res.writeHead(status, { 'content-type': 'application/json; charset=utf-8', 'cache-control': 'no-store' });
  res.end(JSON.stringify(value));
}

export async function createApplication(config, dependencies = {}) {
  const controller = new WorkshopController(config, dependencies);
  const route = async (req, res) => {
    res.setHeader('X-Content-Type-Options', 'nosniff');
    res.setHeader('Referrer-Policy', 'no-referrer');
    res.setHeader('Content-Security-Policy', "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; frame-ancestors 'none'");
    const url = new URL(req.url, 'http://localhost');
    if (req.method !== 'GET') return json(res, 405, { ok: false, error: 'Управление доступно через WebSocket /ws.' });
    if (url.pathname === '/health') return json(res, 200, { ok: true, protocolVersion: 1 });
    if (url.pathname.startsWith('/api/')) {
      const bearer = req.headers.authorization?.startsWith('Bearer ') ? req.headers.authorization.slice(7) : '';
      if (!equalToken(bearer, config.accessToken)) return json(res, 401, { ok: false, error: 'Нужен Bearer token.' });
      if (url.pathname === '/api/status') return json(res, 200, controller.status());
      if (url.pathname === '/api/recordings') return json(res, 200, await controller.recordings.list());
      if (url.pathname.startsWith('/api/recordings/')) {
        const data = await controller.recordings.read(url.pathname.slice('/api/recordings/'.length));
        res.writeHead(200, { 'content-type': 'application/x-ndjson', 'content-disposition': 'attachment; filename="trajectory.jsonl"', 'cache-control': 'no-store' });
        return res.end(data);
      }
      return json(res, 404, { ok: false, error: 'Маршрут не найден.' });
    }
    const files = { '/': 'index.html', '/index.html': 'index.html', '/app.js': 'app.js', '/style.css': 'style.css' };
    const file = files[url.pathname];
    if (!file) return json(res, 404, { ok: false, error: 'Файл не найден.' });
    const content = await readFile(path.join(publicRoot, file));
    res.writeHead(200, { 'content-type': mime[path.extname(file)], 'cache-control': 'no-store' });
    res.end(content);
  };
  const listener = (req, res) => { route(req, res).catch(error => { if (!res.headersSent) json(res, 400, { ok: false, error: error.message }); else res.end(); }); };
  const server = config.tlsCert && config.tlsKey
    ? https.createServer({ cert: await readFile(config.tlsCert), key: await readFile(config.tlsKey) }, listener)
    : http.createServer(listener);
  const wss = new WebSocketServer({ noServer: true, maxPayload: 16 * 1024, perMessageDeflate: false });
  server.on('upgrade', (req, socket, head) => {
    // Native Unity has no Origin. Browser clients must be same-origin.
    const expectedOrigin = `${config.tlsCert ? 'https' : 'http'}://${req.headers.host}`;
    if (req.url !== '/ws' || (req.headers.origin && req.headers.origin !== expectedOrigin)) { socket.destroy(); return; }
    wss.handleUpgrade(req, socket, head, ws => controller.attach(ws));
  });
  let timer;
  return {
    server, controller,
    async listen() {
      await new Promise((resolve, reject) => { server.once('error', reject); server.listen(config.port, config.host, resolve); });
      timer = setInterval(() => controller.tick(), config.tickMs);
      timer.unref();
      void controller.connectDrivers();
      return server.address();
    },
    async close() {
      clearInterval(timer);
      await controller.close();
      for (const socket of wss.clients) socket.terminate();
      wss.close();
      await new Promise(resolve => server.close(resolve));
    },
  };
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const config = await loadConfig();
  const app = await createApplication(config);
  const address = await app.listen();
  console.log(`VR Workshop server: ${config.tlsCert ? 'https' : 'http'}://${config.host}:${address.port}`);
  console.log(`Unity endpoint: ${config.tlsCert ? 'wss' : 'ws'}://${config.host}:${address.port}/ws`);
  if (config.accessToken === 'local-training-only') console.log('Локальный учебный token: local-training-only. Для сети настройте TLS и VR_ACCESS_TOKEN.');
  for (const signal of ['SIGINT', 'SIGTERM']) process.once(signal, () => { void app.close().then(() => process.exit(0)); });
}
