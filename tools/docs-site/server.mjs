#!/usr/bin/env node
// 极简静态文件服务器：给 Cloudflare 隧道用的（Windows 服务器上没有 nginx/caddy）。
//
//   node server.mjs [--root <目录>] [--port 6187] [--base /smartclassroom]
//
// 只做三件事：把 <base>/ 映射到 root；补上 index.html；其它路径原样取文件。
// 没有目录穿越、没有执行、没有任何依赖。

import { createServer } from 'node:http';
import { readFile, stat } from 'node:fs/promises';
import { extname, join, normalize, resolve, sep } from 'node:path';

const args = process.argv.slice(2);
const argOf = (name, fallback) => {
  const i = args.indexOf(name);
  return i >= 0 && args[i + 1] ? args[i + 1] : fallback;
};
const root = resolve(argOf('--root', '.'));
const port = Number(argOf('--port', '6187'));
const base = '/' + argOf('--base', '/smartclassroom').replace(/^\/|\/$/g, '');

const TYPES = {
  '.html': 'text/html; charset=utf-8', '.css': 'text/css; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8', '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml', '.png': 'image/png', '.jpg': 'image/jpeg', '.webp': 'image/webp',
  '.ico': 'image/x-icon', '.txt': 'text/plain; charset=utf-8', '.md': 'text/markdown; charset=utf-8',
};

const server = createServer(async (req, res) => {
  try {
    let path = decodeURIComponent((req.url ?? '/').split('?')[0]);
    if (path === '/' || path === base) {
      res.writeHead(302, { Location: base + '/' });
      return res.end();
    }
    if (!path.startsWith(base + '/')) {
      res.writeHead(404, { 'Content-Type': 'text/plain; charset=utf-8' });
      return res.end('404');
    }
    let rel = path.slice(base.length + 1);
    if (rel === '' || rel.endsWith('/')) rel += 'index.html';
    const full = normalize(join(root, rel));
    if (!full.startsWith(root + sep)) {           // 目录穿越保护
      res.writeHead(403); return res.end();
    }
    const info = await stat(full);
    const file = info.isDirectory() ? join(full, 'index.html') : full;
    const body = await readFile(file);
    res.writeHead(200, {
      'Content-Type': TYPES[extname(file).toLowerCase()] ?? 'application/octet-stream',
      'Cache-Control': 'public, max-age=300',
    });
    res.end(body);
  } catch {
    res.writeHead(404, { 'Content-Type': 'text/plain; charset=utf-8' });
    res.end('404');
  }
});

server.listen(port, '127.0.0.1', () => {
  console.log(`docs server: http://127.0.0.1:${port}${base}/  (root=${root})`);
});
