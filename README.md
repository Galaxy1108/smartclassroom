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
