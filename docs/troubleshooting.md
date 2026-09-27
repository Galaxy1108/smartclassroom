# 常见问题与踩坑记录

> 开发过程中真实踩过的坑，以及对应的现象与修法。遇到怪事先翻这里。
> 细节实现分别在 [AI](ai.md) / [QQ](qq-snowluma.md) / [ClassIsland](classisland.md) /
> [设置](settings.md) / [事件页](events.md) 里。

## 平台兼容（Linux / Windows）

| 位置 | Windows | Linux |
|---|---|---|
| 归档根 / 锁文件 | `%LOCALAPPDATA%\SmartClassroom\…` | `~/.local/share/SmartClassroom/…` |
| 系统下载目录 | `%USERPROFILE%\Downloads` | `XDG_DOWNLOAD_DIR`（否则 `~/Downloads`） |
| 找 SnowLuma 进程 | **端口探测**（没有 `/proc`） | 扫 `/proc` + 端口探测 |
| ClassIsland 定位 | Program Files / `%LOCALAPPDATA%\Programs` / `C:\ClassIsland` | `/opt`、`~/.local/share`、`/usr/share` |
| 启动 SnowLuma | `node.exe` | `node` |
| 打开文件/文件夹 | `UseShellExecute`（两边一致） | 同左 |

## 症状 → 原因

| 现象 | 原因 / 修法 |
|---|---|
| 「怎么都解不开锁」 | 解锁窗口被设成 0 → 立刻过期；现在默认 2 分钟（`SessionMinutes`） |
| 「正在处理 xx.xs」永远不停 | 处理路径提前 return 没结束记录；启动时也会收尾残留记录 |
| 状态栏说"QQ 未连接"但消息一直在收 | 单条消息处理异常掀翻了事件循环；现在单条异常不影响连接 |
| 重启后课件消失 | 元数据搬到了 `.smartclassroom-meta/`，重建索引只扫了旧位置 |
| 「文件归档失败：Could not find a part of the path …meta」 | 元数据目录还不存在时去重扫描抛异常 |
| 自己发的文件被当成课件 | 自发消息（`post_type: message_sent`）没被排除 |
| 通知一次性全弹出来 | 排队通知没等上一条显示完；现在用带等待的 `ShowNotificationAsync` |
| 「填好的 API Key 更新后又没了」 | 更新包覆盖了设置文件；现在设置与程序分开存放 |
| AI 判定偶尔判错 | 先看事件行里的"判定来源 + 耗时"，再看 `ai-sidecar.log` |
| 图片消息被忽略 | 图片要下载后交给视觉模型；正文只有 `[图片]` 时要提示模型看图 |
| 换课"缺少节次" | 消息没说第几节 → 现在会把当天/相关日期的课表一起喂给 AI |
| 多账号抢端口 | 应用会跳过被占用的端口，并在启动前探测端点避免重复起实例 |

## Linux 上注入的两个关卡

SnowLuma 注入 QQ 需要：

1. `kernel.yama.ptrace_scope = 0`（否则只允许跟踪自己的子进程）——
   临时：`sudo sysctl kernel.yama.ptrace_scope=0`；永久：写进 `/etc/sysctl.d/99-snowluma.conf`；
2. SnowLuma 的「自动注入」开关（应用点「启动注入」时会替你打开）。

## 排查工具

- 事件页每条记录可「复制」；顶部有「复制全部」
- `<数据目录>/ai-sidecar.log`：每次 AI 请求一行
- ClassIsland 插件诊断：`<ClassIsland>/bridge-diag.log`
- SnowLuma 日志：`<SnowLuma>/logs/snowluma-YYYY-MM-DD.log`
