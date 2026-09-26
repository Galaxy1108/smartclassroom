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
import { randomUUID } from 'node:crypto';
import { createModels, createProvider } from '@earendil-works/pi-ai';
import {
  builtinModels,
  getBuiltinModel,
  getBuiltinModels,
  getBuiltinProviders,
} from '@earendil-works/pi-ai/providers/all';
import { openAICompletionsApi } from '@earendil-works/pi-ai/api/openai-completions.lazy';

const log = (...a) => process.stderr.write(`[sidecar] ${a.join(' ')}\n`);

// opencode.ai 的端点（/zen、/zen/go）要求每个请求带 x-opencode-session 做路由，
// 否则直接 400 MissingSessionID。内置 provider（opencode / opencode-go）只在
// options.sessionId 存在时才补这个头，所以这里必须给一个会话 id；
// 自定义 baseUrl（用户把服务地址填成 opencode 时走这条路）由下面手工塞进 model.headers。
const OPENCODE_SESSION_HEADER = 'x-opencode-session';
const SESSION_ID = 'smartclassroom-' + randomUUID();

function isOpenCodeEndpoint(baseUrl) {
  try {
    const host = new URL(baseUrl).hostname.toLowerCase();
    return host === 'opencode.ai' || host.endsWith('.opencode.ai');
  } catch {
    return false;
  }
}

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
function customProvider(baseUrl, modelId, apiKey, reasoningLevel, sessionId) {
  const model = {
    id: modelId,
    name: modelId,
    api: 'openai-completions',
    provider: 'smartclassroom-custom',
    baseUrl,
    // 由调用方声明的推理等级决定是否声明"支持推理"：
    // 不声明就不会给端点发它可能不认识的参数；声明了 pi-ai 才会按等级压低思考量。
    reasoning: !!reasoningLevel && reasoningLevel !== 'off',
    input: ['text'],
    cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
    contextWindow: 128000,
    maxTokens: 8192,
  };
  // 自定义 provider 不会走内置 provider 的 session 包装，opencode 的端点要自己补头。
  if (isOpenCodeEndpoint(baseUrl)) {
    model.headers = { [OPENCODE_SESSION_HEADER]: sessionId };
  }
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
      const provider = customProvider(req.baseUrl, req.model, req.apiKey, req.reasoning, sessionIdOf(req));
      const m = createModels();
      m.setProvider(provider);
      model = m.getModel('smartclassroom-custom', req.model);
      if (!model) throw new Error(`自定义端点模型解析失败：${req.model}`);
      const res = await completeWith(m, model, context, req);
      return { text: requireText(res), usage: usageOf(res) };
    }

    model = getBuiltinModel(req.provider, req.model);
    if (!model) throw new Error(`未知模型：${req.provider}/${req.model}`);
    const res = await completeWith(models(), model, context, req);
    return { text: requireText(res), usage: usageOf(res) };
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

/**
 * 解释"为什么没有文本内容"。
 * 空内容绝不能当成成功返回——上层只会看到"连通但什么都没得到"。
 * 常见原因：推理模型把输出都花在 thinking 上、输出被长度上限截断、只返回了工具调用。
 */
function describeEmpty(message) {
  const blocks = Array.isArray(message?.content) ? message.content : [];
  const types = [...new Set(blocks.map((b) => b?.type).filter(Boolean))];
  const stop =
    message?.stopReason ?? message?.finishReason ?? message?.stop_reason ?? message?.finish_reason ?? 'unknown';

  const parts = ['AI 返回了空内容'];
  parts.push(`内容块类型: ${types.length ? types.join(',') : '无'}`);
  parts.push(`结束原因: ${stop}`);
  if (types.includes('thinking') || types.includes('reasoning')) {
    const thinkingChars = blocks
      .filter((b) => b?.type === 'thinking' || b?.type === 'reasoning')
      .reduce((n, b) => n + (b.thinking ?? b.text ?? '').length, 0);
    parts.push(`只有思考内容（${thinkingChars} 字，推理模型把输出用在思考上）`);
  }
  if (types.includes('toolCall')) parts.push('只返回了工具调用');
  parts.push('建议改用非推理模型，或放宽输出长度上限');
  return parts.join('；');
}

/**
 * 统一的补全调用。
 * 默认用 minimal 推理强度：我们的任务是"把群消息整理成 JSON"，
 * 不需要长思考——推理模型把输出全花在思考上时，final text 会是空的。
 * sessionId 一律带上：opencode 端点靠它路由（否则 400），其它端点只会用于缓存亲和性。
 */
async function completeWith(collection, model, context, req) {
  const options = { sessionId: sessionIdOf(req) };
  if (req.apiKey) options.apiKey = req.apiKey;
  if (req.reasoning ?? 'minimal') options.reasoning = req.reasoning ?? 'minimal';
  if (req.maxTokens) options.maxTokens = req.maxTokens;

  try {
    return await collection.completeSimple(model, context, options);
  } catch (e) {
    // 只有"端点不认推理参数"这类错误才值得换默认参数重试一次；
    // 认证/路由/网络错误重试只是再花一次钱，还会拖慢报错。
    const msg = e?.message ?? String(e);
    if (!/reasoning|thinking|effort|unsupported|unrecognized|unknown (field|parameter|argument)/i.test(msg)) {
      throw e;
    }
    log('completeSimple(options) failed, retrying with defaults:', msg);
    return await collection.completeSimple(model, context, { sessionId: options.sessionId });
  }
}

function sessionIdOf(req) {
  return typeof req?.sessionId === 'string' && req.sessionId ? req.sessionId : SESSION_ID;
}

/** 取出最终文本；为空则抛错（优先透出 pi-ai 记录的真实失败原因）。 */
function requireText(message) {
  const text = textOf(message);
  if (text) return text;

  // stopReason=error 时 pi-ai 把 HTTP 状态与响应体放在 errorMessage 里。
  // 必须优先透出它——否则用户只会看到"空内容"，完全不知道是 400、401 还是网络问题
  //（真实案例：opencode 端点缺 x-opencode-session，报的是 MissingSessionID）。
  const reason = typeof message?.errorMessage === 'string' ? message.errorMessage.trim() : '';
  if (reason) throw new Error(`AI 请求失败：${reason}${hintFor(reason)}`);

  throw new Error(describeEmpty(message));
}

/** 给常见失败原因补一句"该怎么办"。 */
function hintFor(reason) {
  if (/MissingSessionID/i.test(reason)) return '（该端点要求 x-opencode-session 路由头）';
  if (/\b401\b|unauthor|invalid.*api.?key|api key/i.test(reason)) return '（检查 API Key 是否正确、是否过期）';
  if (/\b404\b|model.*not.*found|unknown model/i.test(reason)) return '（模型名可能不对，或该 provider 没有这个模型）';
  if (/\b429\b|rate.?limit|quota/i.test(reason)) return '（额度/频率限制，稍后再试或换 key）';
  if (/abort/i.test(reason)) return '（请求被取消）';
  return '';
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
