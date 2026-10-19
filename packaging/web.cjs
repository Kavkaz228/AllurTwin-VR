// Portable static host and same-origin API proxy. No npm installation at runtime.
const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const root = path.resolve(__dirname, '../allur/frontend/dist');
const mime = {'.html':'text/html; charset=utf-8','.js':'text/javascript; charset=utf-8','.css':'text/css; charset=utf-8','.svg':'image/svg+xml','.png':'image/png','.jpg':'image/jpeg','.webp':'image/webp','.woff2':'font/woff2','.ico':'image/x-icon','.json':'application/json'};
http.createServer((req, res) => {
  if (!['127.0.0.1:8091', 'localhost:8091'].includes(req.headers.host)) {res.writeHead(403); res.end(); return;}
  if (req.url.startsWith('/api/')) {
    const upstream = http.request({host:'127.0.0.1',port:8184,path:req.url,method:req.method,headers:{...req.headers,host:'127.0.0.1:8184'}}, reply => {
      res.writeHead(reply.statusCode, reply.headers); reply.pipe(res);
    });
    upstream.setTimeout(190000, () => upstream.destroy());
    upstream.on('error', () => {if (!res.headersSent) res.writeHead(503, {'Content-Type':'application/json'}); res.end(JSON.stringify({detail:'Локальный сервер ещё запускается. Повторите через несколько секунд.'}));});
    req.on('aborted', () => upstream.destroy()); req.pipe(upstream); return;
  }
  if (!['GET','HEAD'].includes(req.method)) {res.writeHead(405); res.end(); return;}
  if (req.url === '/desktop-health') {res.writeHead(200, {'Content-Type':'application/json'}); res.end('{"application":"allur-desktop"}'); return;}
  let decoded;
  try { decoded = decodeURIComponent(new URL(req.url, 'http://127.0.0.1').pathname); } catch {res.writeHead(400); res.end(); return;}
  let file = path.resolve(root, '.' + decoded);
  if (file !== root && !file.startsWith(root + path.sep)) {res.writeHead(403); res.end(); return;}
  if (!fs.existsSync(file) || fs.statSync(file).isDirectory()) {
    if (path.extname(decoded)) {res.writeHead(404); res.end(); return;}
    file = path.join(root, 'index.html');
  }
  res.writeHead(200, {'Content-Type':mime[path.extname(file)] || 'application/octet-stream', 'Cache-Control':'no-cache','Referrer-Policy':'no-referrer','X-Content-Type-Options':'nosniff'});
  if (req.method === 'HEAD') res.end(); else fs.createReadStream(file).pipe(res);
}).listen(8091, '127.0.0.1');
