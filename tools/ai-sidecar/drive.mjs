#!/usr/bin/env node
// 手动驱动 ai-sidecar：不经过 App，直接用真实 key 跑一条补全，用来排查"AI 不工作"到底卡在哪。
//
//   OC_KEY=xxx node tools/ai-sidecar/drive.mjs providers
//   OC_KEY=xxx node tools/ai-sidecar/drive.mjs models opencode-go
//   OC_KEY=xxx node tools/ai-sidecar/drive.mjs complete opencode-go deepseek-v4.1-flash
//   OC_KEY=xxx node tools/ai-sidecar/drive.mjs complete "" deepseek-v4.1-flash https://opencode.ai/zen/go/v1
//
// 第三个参数传空串表示"用自定义 baseUrl"（等价于设置页里填了服务地址那条路径）。
// 任何情况下都不会把 key 写进日志或输出。

import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const KEY = process.env.OC_KEY ?? process.env.AI_API_KEY ?? '';
const [cmd, provider, model, baseUrl] = process.argv.slice(2);
const sidecar = join(dirname(fileURLToPath(import.meta.url)), 'sidecar.mjs');

const child = spawn(process.execPath, [sidecar], { stdio: ['pipe', 'pipe', 'inherit'] });
const rl = createInterface({ input: child.stdout });
const pending = new Map();
rl.on('line', (line) => {
  const msg = JSON.parse(line);
  const done = pending.get(msg.id);
  if (done) {
    pending.delete(msg.id);
    done(msg);
  }
});

let nextId = 1;
function send(obj) {
  const id = String(nextId++);
  return new Promise((resolve) => {
    pending.set(id, resolve);
    child.stdin.write(JSON.stringify({ id, ...obj }) + '\n');
  });
}

const show = (label, res) => {
  console.log(`${label}: ${res.ok ? JSON.stringify(res.data) : 'ERR: ' + res.error}`);
};

const providers = await send({ cmd: 'providers' });
const ids = providers.data?.providers?.map((p) => p.id) ?? [];
console.log(`providers: ${ids.length} 个${provider ? `，含 ${provider}=${ids.includes(provider)}` : ''}`);

if (cmd === 'providers') {
  console.log(ids.join(', '));
} else if (cmd === 'models') {
  show(`models(${provider})`, await send({ cmd: 'models', provider }));
} else if (cmd === 'complete') {
  if (!KEY) {
    console.log('缺少 OC_KEY（或 AI_API_KEY）环境变量');
  } else {
    const today = new Date().toISOString().slice(0, 10);
    const req = {
      cmd: 'complete',
      system: `你整理老师布置的作业。只输出 JSON：{"is_homework":true,"subject":"科目","date":"yyyy-MM-dd","items":["条目"],"due":"","confidence":0-1}。今天是 ${today}。`,
      user: '今天数学作业是练习册P10，明天交',
      model,
      apiKey: KEY,
    };
    if (baseUrl) req.baseUrl = baseUrl;
    else req.provider = provider;
    const t0 = Date.now();
    const res = await send(req);
    show(`complete(${baseUrl ? 'baseUrl=' + baseUrl : 'provider=' + provider}, ${model}) ${Date.now() - t0}ms`, res);
  }
} else {
  console.log('用法：node drive.mjs providers | models <provider> | complete <provider|""> <model> [baseUrl]');
}

child.stdin.end();
