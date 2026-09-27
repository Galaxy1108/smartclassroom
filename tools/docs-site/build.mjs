#!/usr/bin/env node
// 把仓库里的 Markdown 文档渲染成一个静态文档站。
//
//   node tools/docs-site/build.mjs [--base /smartclassroom/] [--out docs-site-dist]
//
// 产物：index.html + 每个文档一个 .html + 一份自带的样式，纯静态、可直接丢给 nginx/Caddy。
// 文档之间的 *.md 链接会被改写成 *.html，所以本地打开和线上都点得通。

import { readFileSync, writeFileSync, mkdirSync, existsSync, rmSync, cpSync } from 'node:fs';
import { dirname, join, resolve, basename } from 'node:path';
import { fileURLToPath } from 'node:url';
import { marked } from 'marked';

const here = dirname(fileURLToPath(import.meta.url));
const repo = resolve(here, '..', '..');

const args = process.argv.slice(2);
const argOf = (name, fallback) => {
  const i = args.indexOf(name);
  return i >= 0 && args[i + 1] ? args[i + 1] : fallback;
};
const base = argOf('--base', '/').replace(/\/?$/, '/');
const outDir = resolve(repo, argOf('--out', 'docs-site-dist'));

// 文档清单：文件名 → 侧栏标题
const PAGES = [
  ['README.md', '总览'],
  ['docs/ai.md', 'AI 引擎与提示词'],
  ['docs/qq-snowluma.md', 'QQ / SnowLuma 集成'],
  ['docs/classisland.md', 'ClassIsland 集成'],
  ['docs/settings.md', '设置项说明'],
  ['docs/events.md', '「事件」页'],
  ['docs/troubleshooting.md', '常见问题与排查'],
  ['docs/development.md', '开发与发布'],
];

const pageFile = (src) => (src === 'README.md' ? 'index.html' : basename(src).replace(/\.md$/, '.html'));

marked.setOptions({ gfm: true, breaks: false });

/** 渲染一个 Markdown 文件，并把站内 *.md 链接改成 *.html */
function render(src) {
  const md = readFileSync(join(repo, src), 'utf8');
  let html = marked.parse(md);
  html = html.replace(/href="([^"]+?)\.md(#[^"]*)?"/g, (_, path, hash = '') => {
    const target = path.includes('/') ? basename(path) : path;
    return `href="${target}.html${hash}"`;
  });
  // 标题里的第一个 h1 拿去当页面标题
  const title = (md.match(/^#\s+(.+)$/m)?.[1] ?? '智慧课堂').trim();
  return { html, title };
}

const nav = PAGES.map(([src, label]) =>
  `      <a href="${pageFile(src)}" data-page="${pageFile(src)}">${label}</a>`).join('\n');

function shell(title, body, current) {
  return `<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>${title} · 智慧课堂</title>
<link rel="stylesheet" href="${base}style.css">
</head>
<body>
<aside>
  <div class="brand"><a href="${base}">智慧课堂</a><span>SmartClassroom</span></div>
  <nav>
${nav}
  </nav>
  <div class="foot">代码由 AI 生成 · MIT</div>
</aside>
<main>
<article id="doc" data-current="${current}">
${body}
</article>
</main>
<script>
  // 高亮当前页
  const cur = document.getElementById('doc').dataset.current;
  document.querySelectorAll('nav a').forEach(a => {
    if (a.dataset.page === cur) a.classList.add('active');
  });
</script>
</body>
</html>
`;
}

rmSync(outDir, { recursive: true, force: true });
mkdirSync(outDir, { recursive: true });

for (const [src] of PAGES) {
  const { html, title } = render(src);
  const file = join(outDir, pageFile(src));
  writeFileSync(file, shell(title, html, pageFile(src)));
  console.log('  ✓', src, '→', basename(file));
}

// 站点自带样式（不依赖外网，部署后离线也能看）
writeFileSync(join(outDir, 'style.css'), readFileSync(join(here, 'style.css'), 'utf8'));

// 顺手带上 LICENSE，方便站点里引用
if (existsSync(join(repo, 'LICENSE'))) cpSync(join(repo, 'LICENSE'), join(outDir, 'LICENSE'));

console.log(`\n文档站已生成：${outDir}（base = ${base}）`);
