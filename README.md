# Classroom Enhancement 智慧课堂

跨平台（Linux / Windows）教室应用 + 配套 ClassIsland 插件。

- `src/App/` — Avalonia + Fluent 主应用（类 WinUI3 外观）：作业墙、事件队列、课件弹窗、设置页（含 SnowLuma 下载器）。
- `src/Core/` — 跨平台类库：OneBot QQ 接入、AI 网关、教师映射、规则引擎（召唤/作业/换课）、通知排队、文件归档、课件索引、SnowLuma 管理。
- `src/Contracts/` — App 与插件共享的 DTO + JSON Schema。
- `src/ClassIslandPlugin/` — net8.0 纯托管 ClassIsland 插件：提醒渠道、localhost 执行接口、换课合法性校验与落课。
- `tests/Core.Tests/` — 核心逻辑单测。
- `docs/` — 方案与设计文档。

## 架构速览

```text
班级QQ ──OneBot WS──▶ App/Core ──AI──▶ 决策 ─┬─ 召唤/换课驳回 ──localhost HTTP──▶ 插件 ──▶ ClassIsland 通知/换课
   │   └─ 文件事件 ──下载归档──▶ 当天课件索引 ──上课事件──▶ 弹窗"您可能需要的课件"
   └─ ClassIsland 未运行时：App 内横幅 + 系统通知降级（Win/Linux 一致）
```

详细设计见 [`docs/plan-v4.md`](docs/plan-v4.md)。

## 开发前置

- .NET 8 SDK
- 教室机：NTQQ + ClassIsland ≥ 2.0；SnowLuma 由应用内下载器获取（不随包附带）
- 配置（永不入库）：`appsettings.local.json`（AI BaseUrl/Key/Model、SnowLuma 地址与 token、群号）、教师映射表、归档根目录

## Git 规范

`main` + `feat/<scope>` 分支；conventional commits；里程碑 tag（`v0.1`→`v0.7` 见 `git tag`）。

## UI 说明

WinUI3 外观来自 [FluentAvaloniaUI](https://github.com/amwx/FluentAvalonia)（MIT，与 ClassIsland 同款控件库：NavigationView、SettingsExpander、Win11 控件样式），Avalonia 钉在 ClassIsland 同款 11.3.17。
已知差距：设置行暂未配图标（Segoe 图标字体 Linux 下无系统 fallback，为防 tofu 先空着；后续方案：内嵌开源图标字体如 Lucide，ClassIsland 也是这么干的）。

## AI 引擎（双选）

设置页「AI → 接入引擎」二选一，随时切换、改动即存：

| 引擎 | 说明 | 依赖 |
|---|---|---|
| 内置直连（默认） | 直接 POST 到 OpenAI 兼容的 `/chat/completions`，零额外依赖 | 无 |
| pi-ai（Node 边车） | 经 Node 子进程使用 [`@earendil-works/pi-ai`](https://www.npmjs.com/package/@earendil-works/pi-ai)：自带 provider 与模型目录（41 个 provider）、统一鉴权与用量统计 | **Node ≥ 22.19** |

pi-ai 是 TypeScript/npm 包，.NET 无法直接引用，因此做成 stdio JSONL 边车（`tools/ai-sidecar/sidecar.mjs`）：
主程序 spawn Node，按行收发 JSON。边车崩溃会自动重启；任何失败都转成 `AiException`，上层按"保守降级"处理
（召唤排队、作业/换课转人工），不会让应用崩。

- 打包：Linux 包依赖 `nodejs`；Windows zip 需用户自备 Node ≥ 22.19（或安装 SnowLuma 完整版，其内置 Node 会被自动复用）。
- 自检：`tools/mock-openai.py` 是本地假端点，用于不花钱验证自定义 baseUrl 链路。

## ClassIsland 集成

插件经 `127.0.0.1:5199`（可改）提供 `/status`、`/notify`、`/exchange`，需 Bearer token：
token 由插件首次启动时在 ClassIsland 配置目录的 `smartclassroom.bridge/bridge.token` 生成，
复制到设置页「ClassIsland 集成 → 桥接 Token」，点「探测」应显示插件版本与课表加载状态。
