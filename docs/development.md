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

`README.md` 与 `docs/*.md` 会被渲染成一个文档站（**Fumadocs** 主题，与 SnowLuma 文档同款：
侧栏导航、⌘K 本地搜索、深浅色、代码高亮）。

```bash
node tools/docs-site/build.mjs --base /smartclassroom   # 只构建 → docs-site-dist/
bash tools/docs-site/deploy.sh                          # 构建 + 同步服务器脚本 + 上传 + 自检
```

构建脚本做四件事：把 Markdown 转成 Fumadocs 的 `content/docs/*.mdx`（补 front-matter、
改写站内链接、转义 MDX 敏感字符）、生成侧栏 `meta.json`、写入 `basePath`、跑 `next build` 静态导出。

部署目标（Cloudflare 隧道后面的 Windows 服务器）：

| 项 | 值 |
|---|---|
| 站点目录 | `C:\data\web\smartclassroom-docs` |
| 静态服务 | `C:\data\web\docs-server.mjs`（无依赖的 Node 服务器，端口 6187） |
| 启动方式 | `C:\data\web\start-docs.bat` + 计划任务 `SmartClassroomDocs`（SYSTEM、开机自启、失败重试 3 次） |
| 隧道入口 | `docs.galaxy1108.top → http://127.0.0.1:6187`（`C:\data\web\config.yml`） |
| 公开地址 | <https://docs.galaxy1108.top/smartclassroom/> |

> 经验：给 Windows 服务器传脚本要用 **base64 写字节**（`[IO.File]::WriteAllBytes`）——
> 直接 `Set-Content` 会把换行与中文写坏，症状是批处理里注释把命令吞掉、服务静默不启动。

## 版本号与发版

`主版本.次版本.修订号`，规则见仓库根目录的 [CHANGELOG.md](../CHANGELOG.md)：

- **修订号**（第三位）是默认选择：修 bug、小改进、小功能都只动它；
- **次版本**只在中型功能变更时动（新增功能板块、明显改变使用方式）；
- **主版本**用于不兼容变更（配置格式不兼容、插件接口变更、需要用户迁移）；
- 发版时四处版本号要一起改：`src/App/SmartClassroom.App.csproj` 的 `<Version>`、
  `pkg/PKGBUILD` 的 `pkgver`、`src/ClassIslandPlugin/manifest.yml` 的 `version`、
  `src/ClassIslandPlugin/BridgeServer.cs` 的 `PluginVersion`。

Release 说明的写法（与 CHANGELOG 同一套结构）：

```markdown
## 修复
- **一句话说清用户遇到的现象**：原因与现在的结果。

## 改进
- ...

## 安装
| 平台 | 文件 |
|---|---|
| Linux | `smartclassroom-<版本>-1-x86_64.pkg.tar.zst`（`sudo pacman -U`） |
| Windows | `smartclassroom-<版本>-win-x64.zip`（解压覆盖旧目录） |
| ClassIsland 插件 | `smartclassroom-classisland-plugin.cipx` |
```

要点：先说**用户看到的现象**，再说原因；不写内部实现细节；不出现真实姓名与 QQ 号。
