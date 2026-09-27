# 智慧课堂 SmartClassroom

给班级教室用的一套小工具：**QQ 群/私聊 → AI 理解 → ClassIsland 提醒 / 作业墙 / 换课 / 课件归档**。

老师在 QQ 里说一句话，应用就能：把召唤变成 ClassIsland 通知、把作业整理上墙、
把换课写进课表（临时层）、把发的文件按科目归档，并在上课时弹出「您可能需要的课件」。

- 跨平台：**Linux + Windows**（Avalonia + FluentAvalonia，WinUI 3 外观）
- QQ 侧走 **SnowLuma**，不需要另开一个 QQ 客户端
- AI 走 **pi-ai**（内置 Node 边车）或任意 OpenAI 兼容端点
- ClassIsland 侧是一个**自带一键安装**的配套插件

> **本项目代码完全由 AI 生成，仅经过基本人工验证。**

## 功能一览

| 功能 | 说明 | 默认 |
|---|---|---|
| **召唤通知** | 老师叫某人过去 → ClassIsland 通知；上课排队、下课时发 | 关 |
| **作业墙** | 作业整理成卡片（科目/条目/截止），支持手动增删改、拖拽排序、配色 | 关 |
| **换课** | 解析换课消息 → 校验 → 写进 ClassIsland **临时层**（只保留一个） | 关 |
| **群文件自动归档** | 老师发的文件/图片按科目落到 `<归档根>/<科目>/`，并复制一份到系统下载目录 | 关 |
| **上课课件弹窗** | 上课时弹出当天该科的课件「您可能需要的课件」 | 关 |
| **老师通知转发** | 活动/集合/催交/表扬等 → 整理成一条 ClassIsland 提醒 | 关 |

功能开关都有**前置门槛**（集成没配好不允许开启），并且默认全部关闭。

## 安装

**Linux（Arch 系）**

```bash
sudo pacman -U smartclassroom-<版本>-1-x86_64.pkg.tar.zst
```

**Windows**：解压 `smartclassroom-<版本>-win-x64.zip`，运行 `SmartClassroom.App.exe`。

**ClassIsland 插件**：启动应用 → 设置页点「安装插件」；也可以手动安装随包附带的
`smartclassroom-classisland-plugin.cipx`。

## 快速开始

1. 启动应用，进「设置」。
2. 填 **AI**：选引擎（`pi-ai` 或 OpenAI 兼容端点）、服务商、模型、API Key。
3. 配 **QQ**：指定 SnowLuma 安装目录 → 点「启动注入」（首次会让你确认它的协议）。
4. 点「自动分配端口」让应用把 OneBot 地址与 token 填好。
5. 在「老师映射」里加上老师的 QQ、姓名、科目。
6. 按需打开功能开关，然后到「事件」页看每条消息的处理过程。

## 界面

- **作业**：卡片墙，长按拖拽排序，`Ctrl + 滚轮` 缩放
- **事件**：一条消息 = 一条记录（判定来源、耗时、已忽略/已执行/出现错误）
- **课件**：按科目分组 → 点进科目看时间轴
- **设置**：分组折叠，改动即时保存

## 仓库结构

```
src/Core/            业务核心（AI、QQ、规则、归档、调度、存储）
src/Contracts/       应用 ↔ 插件 的共享契约
src/App/             Avalonia 桌面应用
src/ClassIslandPlugin/  ClassIsland 配套插件（桥接 HTTP + 通知渠道）
tools/ai-sidecar/    pi-ai 的 Node 边车
tests/               Core.Tests / App.Tests
pkg/                 打包脚本与产物
```

## 文档

在线版：**<https://docs.galaxy1108.top/smartclassroom/>**（由 Cloudflare 隧道发布，源文件就是下面的 Markdown）

| 文档 | 内容 |
|---|---|
| [快速开始](docs/getting-started.md) | 十分钟从安装到验证 |
| [配置参考](docs/configuration.md) | 每个设置项、功能门槛、管理员密码、数据位置 |
| [架构与数据流](docs/architecture.md) | 一条消息从 QQ 到教室经历了什么 |
| [AI 引擎与提示词](docs/ai.md) | 引擎选择、各角色输出契约、视觉解析、失败重试 |
| [事件与排查记录](docs/events.md) | 判定来源、耗时、三种结果、待处理 |
| [QQ / SnowLuma](docs/qq-snowluma.md) | 启动注入、端口与令牌、文件与图片归档 |
| [ClassIsland](docs/classisland.md) | 插件、通知渠道、课表、临时层、课件弹窗 |
| [常见问题与排查](docs/troubleshooting.md) | 症状对照表、平台差异、日志位置 |
| [术语表](docs/glossary.md) | 文档与界面里出现的名词 |
| [开发与发布](docs/development.md) | 构建、测试、打包、发版、文档站 |

## 许可

[MIT](LICENSE)。第三方组件（ClassIsland、SnowLuma、pi-ai 等）遵循各自的许可。

> 再次提醒：**本项目代码完全由 AI 生成，仅经过基本人工验证**，请自行评估后再用于教学环境。
