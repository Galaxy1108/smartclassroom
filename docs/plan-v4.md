# 实施计划 v4（已批准，2026-09-25）

> 完整版以评审通过的 plan 为准，本文件为要点存档。

## 已确认事实

- ClassIsland 2.x 跨平台（Win/Linux/macOS 基础 ✅，`ClassIsland.Core` = net8.0 + Avalonia，release 2.1.1.1）。
- 插件 Win ✅ / Linux ✅（纯 managed，禁 native 依赖）/ macOS ❌。
- Linux 无系统 TTS → 通知语音用 **EdgeTTS**（在线，不受影响）。
- Url 协议注册 Linux ❌ → App↔插件走 localhost HTTP（Kestrel，127.0.0.1 + token）。
- 插件模板以 NuGet `ClassIsland.PluginTemplate.Packaging` + `cipx-template`（apiVersion 2.0.0.0）实际生成为准。

## 功能

1. 召唤通知（上课排队/下课发，urgent 词"现在/立刻/马上/立即"立刻发）
2. 作业增强（AI 结构化，同科目同日合并）
3. 换课（调换/替换/跨天；插件按 ClassPlan+TimeLayout 校验合法性；非法下课通知手动；永不无确认自动跨天）
4. 文件自动归档（`<ArchiveRoot>/<老师>/<yyyy-MM-dd>/<文件>` + `.meta.json`，同 file_id 去重）
5. 课件弹窗"您可能需要的课件"（上课触发，每节课一次，缩略图+双击打开）
6. SnowLuma 下载器（不随包；Releases 拉 win-x64 完整/精简版；启停；注入三态）
7. git 规范（本仓库，feat 分支 + conventional commits + 里程碑 tag）

## 技术选型

Avalonia (.NET 8) + FluentTheme｜SnowLuma OneBot v11 为主（:5099/:3000/:3001）、NapCat 备选｜
`get_group_file_url` 文件直链（字段实测确认，备选轮询）｜`ClassIsland.Shared.Ipc`
（`IPublicLessonsService`：CurrentState/CurrentSubject/CurrentTimeLayoutItem/OnBreakingTimeLeftTime/IsClassPlanLoaded；
`IpcRoutedNotifyIds` 事件；另 `IPublicProfileService`、`IPublicUriNavigationService`）｜
插件提醒（NotificationChannelInfo + NotificationContent）｜OpenAI-compatible AI 网关（超时 20s，schema 失败重试一次转人工）。

## 里程碑

v0.1-app-shell → v0.2-qq-ai → v0.3-plugin → v0.4-courseware；双端验收 7 项（见评审 plan §8）。
