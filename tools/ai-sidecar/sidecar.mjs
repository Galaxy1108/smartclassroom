#!/usr/bin/env node
// 智慧课堂 AI 边车（pi-ai）
//
// 协议：stdin 逐行 JSON 请求，stdout 逐行 JSON 响应（一行一个，不带其它输出）。
//   {"id":"1","cmd":"ping"}
//   {"id":"2","cmd":"providers"}
//   {"id":"3","cmd":"models","provider":"openai"}
//   {"id":"4","cmd":"complete","provider":"openai","model":"gpt-4o-mini","system":"...","user":"...","apiKey":"..."}
//   {"id":"5","cmd":"complete","baseUrl":"https://x/v1","model":"qwen-flash","system":"...","user":"..."}  // 自定义 OpenAI 兼容端点
//
// 响应：
//   {"id":"1","ok":true,"data":{...}}
//   {"id":"4","ok":false,"error":"..."}
//
// 约定：任何错误都转成 {ok:false,error}，绝不 crash，绝不往 stdout 写日志（日志走 stderr）。

import { createInterface } from 'node:readline';
import { createModels, createProvider } from '@earendil-works/pi-ai';
import {
  builtinModels,
  getBuiltinModel,
  getBuiltinModels,
  getBuiltinProviders,
} from '@earendil-works/pi-ai/providers/all';
import { openAICompletionsApi } from '@earendil-works/pi-ai/api/openai-completions.lazy';

const log = (...a) => process.stderr.write(`[sidecar] ${a.join(' ')}\n`);

let builtin = null;
function models() {
  if (!builtin) {
    try {
      builtin = builtinModels();
    } catch (e) {
      log('builtinModels() failed:', e?.message ?? String(e));
      builtin = createModels();
    }
  }
  return builtin;
}

/** 端点是否为「自定义 OpenAI 兼容」——有 baseUrl 就自己造一个临时 provider。 */
function customProvider(baseUrl, modelId, apiKey) {
  const model = {
    id: modelId,
    name: modelId,
    api: 'openai-completions',
    provider: 'smartclassroom-custom',
    baseUrl,
    reasoning: false,
    input: ['text'],
    cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
    contextWindow: 128000,
    maxTokens: 8192,
  };
  return createProvider({
    id: 'smartclassroom-custom',
    name: '自定义端点',
    baseUrl,
    auth: {
      apiKey: {
        name: 'API Key',
        resolve: async () => ({ auth: apiKey ? { apiKey } : {} }),
      },
    },
    models: [model],
    api: openAICompletionsApi(),
  });
}

async function handle(req) {
  const { cmd } = req;

  if (cmd === 'ping') {
    return { pong: true, pid: process.pid, node: process.version };
  }

  if (cmd === 'providers') {
    let list = [];
    try {
      // getBuiltinProviders() 返回 provider id 字符串数组（少数版本可能给对象），两种都兼容。
      list = getBuiltinProviders().map((p) =>
        typeof p === 'string' ? { id: p, name: p } : { id: p.id, name: p.name ?? p.id },
      );
    } catch (e) {
      log('getBuiltinProviders failed:', e?.message ?? String(e));
    }
    return { providers: list };
  }

  if (cmd === 'models') {
    const provider = req.provider;
    let list = [];
    try {
      list = getBuiltinModels(provider).map((m) => ({
        id: m.id,
        name: m.name ?? m.id,
        contextWindow: m.contextWindow ?? 0,
        vision: Array.isArray(m.input) && m.input.includes('image'),
        reasoning: !!m.reasoning,
      }));
    } catch (e) {
      log(`getBuiltinModels(${provider}) failed:`, e?.message ?? String(e));
    }
    return { models: list };
  }

  if (cmd === 'complete') {
    const context = {
      systemPrompt: req.system ?? '',
      messages: [{ role: 'user', content: req.user ?? '', timestamp: Date.now() }],
    };

    let model;
    if (req.baseUrl) {
      const provider = customProvider(req.baseUrl, req.model, req.apiKey);
      const m = createModels();
      m.setProvider(provider);
      model = m.getModel('smartclassroom-custom', req.model);
      if (!model) throw new Error(`自定义端点模型解析失败：${req.model}`);
      const res = await m.complete(model, context);
      return { text: textOf(res), usage: usageOf(res) };
    }

    model = getBuiltinModel(req.provider, req.model);
    if (!model) throw new Error(`未知模型：${req.provider}/${req.model}`);
    const res = await models().complete(model, context, req.apiKey ? { apiKey: req.apiKey } : undefined);
    return { text: textOf(res), usage: usageOf(res) };
  }

  throw new Error(`未知命令：${cmd}`);
}

function textOf(message) {
  const blocks = Array.isArray(message?.content) ? message.content : [];
  return blocks
    .filter((b) => b?.type === 'text')
    .map((b) => b.text ?? '')
    .join('')
    .trim();
}

function usageOf(message) {
  const u = message?.usage;
  if (!u) return null;
  return {
    input: u.input ?? 0,
    output: u.output ?? 0,
    costTotal: u.cost?.total ?? 0,
  };
}

const rl = createInterface({ input: process.stdin, crlfDelay: Infinity });

let inflight = 0;
let closing = false;

function maybeExit() {
  if (closing && inflight === 0) process.exit(0);
}

rl.on('line', async (line) => {
  const trimmed = line.trim();
  if (!trimmed) return;
  let req;
  try {
    req = JSON.parse(trimmed);
  } catch (e) {
    process.stdout.write(JSON.stringify({ id: null, ok: false, error: `请求不是合法 JSON：${e.message}` }) + '\n');
    return;
  }
  inflight += 1;
  try {
    const data = await handle(req);
    process.stdout.write(JSON.stringify({ id: req.id ?? null, ok: true, data }) + '\n');
  } catch (e) {
    const msg = e?.message ?? String(e);
    log(`cmd=${req?.cmd} failed:`, msg);
    process.stdout.write(JSON.stringify({ id: req.id ?? null, ok: false, error: msg }) + '\n');
  } finally {
    inflight -= 1;
    maybeExit();
  }
});

// stdin 关闭说明父进程走了。等在途请求写完再退出，否则会掐断最后一次调用。
rl.on('close', () => {
  closing = true;
  maybeExit();
});

log('ready', process.version);
