# 开发与发布

> 环境准备、构建、测试、打包、发版流程、UI 预览、Git 规范。

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

### 界面预览（headless 截图）

`tools/ui-preview` 用 headless + Skia 把页面渲染成 PNG，方便在没有桌面/不想开应用时检查配色与排版：

```bash
dotnet run --project tools/ui-preview/UiPreview.csproj -- artifacts   # 生成 artifacts/timeline-{light,dark}.png
```

它不进入主循环（`SetupWithoutStarting`），因此不会真的连 QQ / ClassIsland，也不在 `SmartClassroom.sln` 内。

## Git 规范

`main` + `feat/<scope>` 分支；conventional commits；里程碑 tag（`v0.1`→`v0.7` 见 `git tag`）。

## UI 说明

WinUI3 外观来自 [FluentAvaloniaUI](https://github.com/amwx/FluentAvalonia)（MIT，与 ClassIsland 同款控件库：NavigationView、SettingsExpander、Win11 控件样式），Avalonia 钉在 ClassIsland 同款 11.3.17。
已知差距：设置行暂未配图标（Segoe 图标字体 Linux 下无系统 fallback，为防 tofu 先空着；后续方案：内嵌开源图标字体如 Lucide，ClassIsland 也是这么干的）。

### 软件更新 / 调试

见下文两节。

## 发版流程

```bash
# 1. 改版本号（四处要一致）
#    src/App/SmartClassroom.App.csproj 的 <Version>
#    pkg/PKGBUILD 的 pkgver
#    src/ClassIslandPlugin/manifest.yml 的 version
#    src/ClassIslandPlugin/BridgeServer.cs 的 PluginVersion
# 2. 测试
dotnet test SmartClassroom.sln
# 3. 打包（必须串行，共享 obj 目录）
bash pkg/build-linux.sh && bash pkg/build-windows.sh
# 4. 发 Release
gh release create vX.Y.Z pkg/*.pkg.tar.zst pkg/*-win-x64.zip pkg/*.cipx --notes-file notes.md
```

## 文档站

`README.md` 与 `docs/*.md` 会被渲染成一个静态站点（侧栏导航、跟随系统深浅色、零外网依赖）：

```bash
node tools/docs-site/build.mjs --base /smartclassroom/   # 只构建 → docs-site-dist/
bash tools/docs-site/deploy.sh                           # 构建 + 上传 + 自检
```

部署目标（Cloudflare 隧道后面的 Windows 服务器）：

| 项 | 值 |
|---|---|
| 站点目录 | `C:\data\web\smartclassroom-docs` |
| 静态服务 | `C:\data\web\docs-server.mjs`（端口 6187，无依赖的极简 Node 服务器） |
| 开机自启 | 计划任务 `SmartClassroomDocs`（SYSTEM 身份，失败自动重启 3 次） |
| 隧道入口 | `docs.galaxy1108.top → http://127.0.0.1:6187`（`C:\data\web\config.yml`） |
| 公开地址 | <https://docs.galaxy1108.top/smartclassroom/> |

改文档只要重跑 `deploy.sh`：服务器直接读磁盘文件，**不用重启任何服务**。
