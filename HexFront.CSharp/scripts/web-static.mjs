// Static host for the legacy web/ client with same-origin proxy to the C# API (:5018).
// Usage: node web-static.mjs [port]
import http from 'node:http';
import net from 'node:net';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../web');
const PORT = Number(process.argv[2] || 8080);
const API = { host: 'localhost', port: 5018 };

const MIME = {
  '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8', '.json': 'application/json', '.png': 'image/png',
  '.svg': 'image/svg+xml', '.ico': 'image/x-icon', '.map': 'application/json',
};

const server = http.createServer((req, res) => {
  if (req.url.startsWith('/api')) {
    const p = http.request({ ...API, path: req.url, method: req.method, headers: { ...req.headers, host: `localhost:${API.port}` } },
      (r) => { res.writeHead(r.statusCode, r.headers); r.pipe(res); });
    p.on('error', (e) => { res.writeHead(502); res.end(String(e)); });
    req.pipe(p);
    return;
  }
  const rel = req.url.split('?')[0];
  const file = path.join(ROOT, rel === '/' ? 'index.html' : rel);
  if (!file.startsWith(ROOT)) { res.writeHead(403); res.end(); return; }
  fs.readFile(file, (err, data) => {
    if (err) { res.writeHead(404); res.end('not found'); return; }
    res.writeHead(200, { 'content-type': MIME[path.extname(file)] || 'application/octet-stream' });
    res.end(data);
  });
});

server.on('upgrade', (req, sock, head) => {
  if (!req.url.startsWith('/ws')) { sock.destroy(); return; }
  const up = net.connect(API.port, API.host, () => {
    const lines = [`${req.method} ${req.url} HTTP/${req.httpVersion}`];
    for (let i = 0; i < req.rawHeaders.length; i += 2) lines.push(`${req.rawHeaders[i]}: ${req.rawHeaders[i + 1]}`);
    up.write(lines.join('\r\n') + '\r\n\r\n');
    if (head.length) up.write(head);
    sock.pipe(up); up.pipe(sock);
  });
  up.on('error', () => sock.destroy());
  sock.on('error', () => up.destroy());
});

server.listen(PORT, () => console.log(`web client at http://localhost:${PORT} (proxy /api,/ws -> :${API.port})`));
