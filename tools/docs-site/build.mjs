#!/usr/bin/env node
// 把仓库里的 Markdown 文档渲染成 Fumadocs 文档站（与 SnowLuma 文档同款主题）。
//
//   node tools/docs-site/build.mjs [--base /smartclassroom]
//
// 做四件事：
//   1. README.md 与 docs/*.md → fumadocs/content/docs/*.mdx（自动补 front-matter、改写链接）
//   2. 生成侧栏顺序 meta.json
//   3. 写入 next.config 需要的 basePath（子路径部署）
//   4. 跑 next build（静态导出），把 out/ 复制成 docs-site-dist/

import { readFileSync, writeFileSync, mkdirSync, rmSync, cpSync, existsSync } from 'node:fs';
import { dirname, join, resolve, basename } from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync } from 'node:child_process';

const here = dirname(fileURLToPath(import.meta.url));
const repo = resolve(here, '..', '..');
const app = join(here, 'fumadocs');

const args = process.argv.slice(2);
const argOf = (n, d) => {
  const i = args.indexOf(n);
  return i >= 0 && args[i + 1] ? args[i + 1] : d;
};
const base = argOf('--base', '/smartclassroom').replace(/\/$/, '');
const outDir = resolve(repo, argOf('--out', 'docs-site-dist'));

// 文档清单：源文件 → 路由名 / 侧栏标题 / 一句话描述
// "---标题---" 是 Fumadocs 的侧栏分组分隔符，用来把文档站分成几个区块
const PAGES = [
  ['README.md', 'index', '总览', '智慧课堂是什么、装什么、怎么开始'],
  ['docs/getting-started.md', 'getting-started', '快速开始', '十分钟从安装到验证'],
  ['docs/configuration.md', 'configuration', '配置参考', '每个设置项、功能门槛、管理员密码'],
  ['---核心机制---', '', '', ''],
  ['docs/architecture.md', 'architecture', '架构与数据流', '一条消息从 QQ 到教室经历了什么'],
  ['docs/ai.md', 'ai', 'AI 引擎与提示词', '引擎选择、各角色输出契约、视觉解析'],
  ['docs/events.md', 'events', '事件与排查记录', '判定来源、耗时、三种结果、待处理'],
  ['---集成---', '', '', ''],
  ['docs/qq-snowluma.md', 'qq-snowluma', 'QQ / SnowLuma', '启动注入、端口与令牌、文件归档'],
  ['docs/classisland.md', 'classisland', 'ClassIsland', '插件、通知渠道、课表、临时层、课件弹窗'],
  ['---其它---', '', '', ''],
  ['docs/troubleshooting.md', 'troubleshooting', '常见问题与排查', '症状对照表、平台差异、日志位置'],
  ['docs/glossary.md', 'glossary', '术语表', '文档与界面里出现的名词'],
  ['docs/development.md', 'development', '开发与发布', '构建、测试、打包、发版、文档站'],
];

const contentDir = join(app, 'content', 'docs');

/** 取文档的一级标题与首段，作为 front-matter 的 title/description */
function frontMatter(md, fallbackTitle, fallbackDesc) {
  const title = (md.match(/^#\s+(.+)$/m)?.[1] ?? fallbackTitle).trim();
  const body = md.replace(/^#\s+.+$/m, '').trimStart();
  const firstLine = body.split('\n').find((l) => l.trim() && !l.startsWith('**') && !l.startsWith('-'))
    ?? fallbackDesc;
  const desc = firstLine.replace(/[#>*`]/g, '').trim().slice(0, 80);
  return { title, desc };
}

/**
 * MDX 安全化：MDX 会把 < > { } 当成 JSX/表达式，正文里出现 `<QQ>`、`</config>` 这类
 * 写法会直接编译失败。代码块与行内代码必须原样保留，所以按它们切分后再转义。
 */
function mdxSafe(md) {
  return md
    .split(/(```[\s\S]*?```|`[^`\n]*`)/)
    .map((part, i) => (i % 2 === 1 ? part : part
      .replace(/</g, '&lt;')
      .replace(/\{/g, '&#123;')
      .replace(/\}/g, '&#125;')))
    .join('');
}

/** 把仓库内的相对链接改写成 Fumadocs 路由 */
function rewriteLinks(md) {
  return md
    // docs/ai.md → /docs/ai
    .replace(/\]\(docs\/([a-z-]+)\.md(#[^)]*)?\)/g, (_, name, hash = '') => `](/docs/${name}${hash})`)
    // ai.md（同目录互链）→ /docs/ai；README.md → /docs
    .replace(/\]\((?!https?:|\/|#)([a-z-]+)\.md(#[^)]*)?\)/g, (_, name, hash = '') =>
      name.toLowerCase() === 'readme' ? `](/docs${hash})` : `](/docs/${name}${hash})`)
    .replace(/\]\(README\.md(#[^)]*)?\)/g, (_, hash = '') => `](/docs${hash})`);
}

console.log(`① 生成内容（base=${base}）`);
rmSync(contentDir, { recursive: true, force: true });
mkdirSync(contentDir, { recursive: true });

for (const [src, route, title, desc] of PAGES) {
  if (route === '') continue;              // 侧栏分隔符
  const raw = readFileSync(join(repo, src), 'utf8');
  const fm = frontMatter(raw, title, desc);
  const body = mdxSafe(rewriteLinks(raw.replace(/^#\s+.+$/m, '').trimStart()));
  const file = join(contentDir, `${route}.mdx`);
  const yaml = (s) => `"${s.replace(/"/g, '\\"')}"`;
  writeFileSync(file,
    `---\ntitle: ${yaml(fm.title)}\ndescription: ${yaml(fm.desc || desc)}\n---\n\n${body}`);
  console.log(`  ✓ ${src} → content/docs/${route}.mdx`);
}

// 侧栏：分组分隔符用 ---标题--- 形式
writeFileSync(join(contentDir, 'meta.json'),
  JSON.stringify({
    title: '文档',
    pages: PAGES.map((p) => (p[1] === '' ? `---${p[0].replace(/^-+|-+$/g, '')}---` : p[1])),
  }, null, 2) + '\n');

console.log('② 配置 basePath');
const nextConfig = `import { createMDX } from 'fumadocs-mdx/next';

const withMDX = createMDX();

/** @type {import('next').NextConfig} */
const config = {
  output: 'export',
  reactStrictMode: true,
  basePath: '${base}',
  images: { unoptimized: true },
};

export default withMDX(config);
`;
writeFileSync(join(app, 'next.config.mjs'), nextConfig);

console.log('③ next build（静态导出）');
execFileSync('npm', ['run', 'build'], { cwd: app, stdio: 'inherit' });

console.log('④ 收集产物');
rmSync(outDir, { recursive: true, force: true });
const out = join(app, 'out');
if (!existsSync(out)) throw new Error('next build 没有生成 out/');
cpSync(out, outDir, { recursive: true });
console.log(`\n文档站已生成：${outDir}`);
