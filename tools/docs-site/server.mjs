#!/usr/bin/env node
// 极简静态文件服务器：给 Cloudflare 隧道用的（Windows 服务器上没有 nginx/caddy）。
//
//   node server.mjs [--root <目录>] [--port 6187] [--base /smartclassroom]
//
// 只做三件事：把 <base>/ 映射到 root；补上 index.html；其它路径原样取文件。
// 没有目录穿越、没有执行、没有任何依赖。

import { createServer } from 'node:http';
import { appendFileSync } from 'node:fs';
import { readFile, stat } from 'node:fs/promises';
import { extname, join, normalize, resolve, sep, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const args = process.argv.slice(2);
const argOf = (name, fallback) => {
  const i = args.indexOf(name);
  return i >= 0 && args[i + 1] ? args[i + 1] : fallback;
};
const root = resolve(argOf('--root', '.'));
const port = Number(argOf('--port', '6187'));
const base = '/' + argOf('--base', '/smartclassroom').replace(/^\/|\/$/g, '');

// 启动与 404 都记一行日志：Windows 计划任务的参数一旦传错，root 会悄悄变成当前目录，
// 现象就是"文件明明在却全是 404"。有日志一眼能看出它到底用了哪个 root。
const logFile = join(dirname(fileURLToPath(import.meta.url)), 'docs-server.log');
const log = (msg) => {
  try { appendFileSync(logFile, `[${new Date().toISOString()}] ${msg}\n`); } catch { /* 忽略 */ }
};

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

    // Next 静态导出：/docs 既可能是 docs.html，也可能是 docs/index.html，两种都试
    const candidates = [rel];
    if (!rel.endsWith('.html') && !rel.includes('.')) {
      candidates.push(rel + '.html', rel + '/index.html');
    }
    let file = null;
    for (const cand of candidates) {
      const full = normalize(join(root, cand));
      if (!full.startsWith(root + sep)) {         // 目录穿越保护
        res.writeHead(403); return res.end();
      }
      try {
        const info = await stat(full);
        if (info.isFile()) { file = full; break; }
        if (info.isDirectory()) {
          const idx = join(full, 'index.html');
          try { if ((await stat(idx)).isFile()) { file = idx; break; } } catch { /* 继续 */ }
        }
      } catch { /* 试下一个 */ }
    }
    if (!file) {
      log(`404 ${path} (rel=${rel} root=${root})`);
      res.writeHead(404, { 'Content-Type': 'text/plain; charset=utf-8' });
      return res.end('404');
    }
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
  const msg = `listen http://127.0.0.1:${port}${base}/ root=${root} argv=${process.argv.slice(2).join(' ')}`;
  console.log(msg);
  log(msg);
});
