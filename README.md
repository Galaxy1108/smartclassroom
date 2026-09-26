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

## 「事件」页是什么

两条用途：

1. **待处理（有出口）**——AI 失败或返回非法时，**群消息不会被丢掉**：原文、发送者、失败原因都留在待处理区，
   可以「重新解析 / 手动录入 / 忽略」。手动录入的作业直接上墙；手动录入的换课**仍走插件合法性校验**，
   不会绕过校验直接改课表。输入非法（如作业科目为空）时不会消费掉这条记录。
2. **决策时间线**——记录应用对每条群消息做了什么、为什么没做（召唤为何排队、AI 判定非召唤、换课是否被驳回、
   QQ 断线重连、课件弹窗失败等）。这是排查"老师说了话但没反应"的第一现场。

局限：时间线与待处理都是**内存态**，重启清空（尚未做持久化）。

## 功能开关：默认全部关闭

装完**什么都不会做**——不会被读群消息后自动发通知、改课表、下文件。五个开关在
设置页最上方，逐项开启（开启即保存，重启生效）：

| 开关 | 开启后做什么 | 关闭时 |
|---|---|---|
| 召唤通知 | 「xxx 来一下」→ ClassIsland 通知（上课排队，带「现在」立刻发） | 不发通知 |
| 作业自动录入 | AI 整理老师作业上墙到「作业」页 | 不上墙（可用「手动添加作业」） |
| 换课自动处理 | AI 解析后交插件校验，**合法才落课**（临时层） | 绝不下发到插件 |
| 群文件自动归档 | 老师发的文件按发送者分类下载 | 连取下载直链都不做 |
| 上课课件弹窗 | 上课时弹「您可能需要的课件」 | 不弹 |

未开启的功能命中关键词时，事件页会记一条「『xxx』未启用，已跳过」（同类只提示一次，不刷屏），
这样"老师说了话但没反应"有据可查。

## 持久化

作业、待处理事项、事件时间线写入 `state.json`（`~/.local/share/SmartClassroom/`），
30 秒定时 + 退出时落盘，写入用「临时文件 + 原子替换」。手动添加作业后立即落盘。

选 JSON 而非 SQLite：数据量小、零额外依赖、不引入 SQLite 原生库、出问题可直接打开看。
坏文件不会导致启动失败，会另存一份 `.bad` 供排查。

## 关闭行为与管理员密码

- **关闭主窗口默认收回到托盘**（托盘菜单可重新打开或退出）。托盘在部分 Linux 桌面环境不可用，
  此时自动退化为「关闭即退出」，不会出现关不掉的情况。设置页另有「退出应用」按钮作为第二条退出路径。
- **管理员密码**（PBKDF2-SHA256，随机盐 10 万次迭代，只存哈希不存明文）：
  设置后，改设置、切换功能开关、增删作业、处理待确认事项、以及**彻底退出**都需要验证。
  验证一次后 10 分钟内免重复输入；但**退出永远重新询问**。
  设置密码后立即生效（无需重启），且当前会话保持已解锁，不会被自己锁在外面。

## 作业卡片

WinUI3 风格卡片：左侧按科目稳定取色的色条、相对日期（今天/明天/还有 N 天）、条目列表、
来源与条数。**按住卡片左侧手柄可拖动排序**，顺序随 `state.json` 一起持久化。

## 作业页布局

**响应式卡片网格**：卡片固定宽度 344，由 `WrapPanel` 按窗口宽度自动换行成多列，
窗口拉伸时列数跟着变（不是单列列表）。条目显示为 `1. 2. 3.` 编号。
拖动左侧手柄可重排——命中判断按**卡片实际矩形**做（X+Y），而不是只看 Y，
否则多列网格里会把不同列的卡片串在一起。

## 界面与字体缩放

设置页「外观 → 界面与字体缩放」提供 80%~160% 滑块，改完立即生效、随设置持久化。
实现方式是 `LayoutTransformControl` 整体缩放：字号、图标、卡片一起放大，
**保住了主题原本的字号层级**（标题/正文/说明的相对大小），
比逐个覆盖 FontSize 更不容易把界面搞乱；卡片网格也会随缩放重新排列。
