# ClassIsland 集成

> 插件安装、通知渠道与模板、课表接口、临时层、下课队列、上课课件弹窗。

## ClassIsland 集成

插件经 `127.0.0.1:5199`（可改）提供 `/status`、`/notify`、`/exchange`，需 Bearer token：
token 由插件首次启动时在 ClassIsland 配置目录的 `smartclassroom.bridge/bridge.token` 生成，
复制到设置页「ClassIsland 集成 → 桥接 Token」，点「探测」应显示插件版本与课表加载状态。

### ClassIsland 插件（应用自带，一键安装）

配套插件在 `src/ClassIslandPlugin/`，**安装包里自带一份**。它现在真的能用了 ——
之前有四个坑，全都踩过并修掉了（见本节末尾）。

- 设置页 → ClassIsland 集成 → **「安装插件」**：应用把它复制到
  `<ClassIsland 根>/Plugins/smartclassroom.bridge/`，重启 ClassIsland 后状态就会变成"已连接"；
- 也单独提供 `smartclassroom-classisland-plugin.cipx`（ClassIsland 的插件包，就是个 zip），
  可以自己在 ClassIsland 里导入；
- 找不到 ClassIsland 目录时会明确告诉你手动把 `classisland-plugin` 里的文件放到哪。

> 之前几个版本只发布了应用、**没有发布插件** —— 所以 ClassIsland 一直是"未连接"。

#### 提醒内容必须用 ClassIsland 的模板数据

给 ClassIsland 发提醒时，`MaskContent`/`OverlayContent` 的 `Content` **不能塞纯字符串** ——
那没有对应的模板，渲染出来就是"没图标、文字偏移、字号不对"。要用它的模板数据类：

- 遮罩：`TwoIconsMaskTemplateData { Text, LeftIconSource, HasRightIcon }`
- 正文：`SimpleTextTemplateData { Text }`
- 模板**必须显式给资源键**：`ContentTemplateResourceKey = "NotificationTwoIconsMaskTemplate"`
  （正文是 `"NotificationSimpleTextOverlayTemplate"`）。不给键的话它不按类型自动匹配，
  直接把数据对象 `ToString()` 出来 —— 通知里会显示一长串类型名（实测踩到）。
- 图标：`new FluentIconSource("\uF8009")`。码位要取自 **FluentAvalonia 的 `Symbol` 枚举**
  （ClassIsland 用的就是这套 Fluent System Icons 字体）；照 Segoe MDL2 猜会错位
  （实测 `E7E7` 在这套字体里是笑脸）。当前：召唤=`F8009`(AlertUrgent)、
  换课=`E15F`(arrow_swap)、手动=`E9E4`(info)，右侧统一用 `E025`(alert，Fluent 里就是铃铛；
  别用 `service_bell` —— 那个在这个尺寸下看着像餐盘盖)。

三个渠道各配了图标（召唤=铃铛、换课=提示、手动=警告），渠道属性里也带上了 `iconGlyph`。

#### 插件踩过的四个坑

1. **没发布**：打包脚本只发应用，插件一直留在源码里。
2. **SDK 太旧**：插件用 `1.7.106.2-dev-v2` 编译，而 ClassIsland 已经 2.1 ——
   提醒提供方要用官方的 `AddNotificationProvider<T>()`（`ClassIsland.Core.Extensions.Registry`）
   注册，用 `AddHostedService`/`AddSingleton` 都不行，ClassIsland 的提醒宿主找不到它
   （报"没有找到与 … 对应的提醒提供方"）。现在 SDK 跟着 ClassIsland 版本走（`2.1.0.1`）。
3. **依赖 ASP.NET**：桥接原本用 Kestrel，而 ClassIsland 是普通 .NET 应用、不带 ASP.NET 运行时 →
   `Could not load file or assembly 'Microsoft.AspNetCore'`。现在用 BCL 的 `TcpListener`
   自己处理那三个接口，零框架依赖。
4. **线程**：ClassIsland 的提醒 API 必须在 **UI 线程**调用，否则抛 `Call from invalid thread`
   （HTTP 线程直接调会得到空响应）。现在通过 `Dispatcher.UIThread.InvokeAsync` 切过去。

**实测结果**（用真实课表跑的）：

| 用例 | 结果 |
|---|---|
| `GET /status` | `{"pluginVersion":"0.35.0","classPlanLoaded":false}` |
| `POST /notify`（召唤） | `{"ok":true}` —— ClassIsland 真的弹了通知并语音播报 |
| `POST /exchange` 周一第2节语文 ↔ 第4节数学 | `legal:true`「已将09-28第2节（语文）与第4节（数学）对调。（已写入临时层课表）」 |
| `POST /exchange` 今天（周六） | `legal:false`「09-26 当天没有课表，无法自动换课」 |
| `POST /exchange` 周一第3节英语 → 自习 | `legal:true`「已将09-28第3节（英语）替换为自习」 |

> 换课会**真的写进临时层课表**（ClassIsland 里会显示调换后的课），验证完记得清掉。

#### 目录包安装（Linux）的路径

ClassIsland 的目录包安装把用户数据放在 `<应用目录>/data/`：
插件放 `data/Plugins/<id>/`，配置（含我们的 `bridge.token`）在 `data/Config/Plugins/<id>/`。
应用会自动在这些位置里找 token（「自动查找」按钮），找不到时才需要手填。

#### 启动画面卡住（ClassIsland 2.1.0.1 的问题）

实测 ClassIsland 2.1.0.1 的"正在启动…"窗口有时不会自己关（**与插件无关**，拿掉插件也一样）。
它自己有开关：`data/Settings.json` 里 `IsSplashEnabled: false` 就不再显示启动画面。

### ClassIsland 的状态与语音播报

- 「连接状态」只显示**短状态**（`已连接 · 插件 vX · 课表已加载` / `未连接`），
  排查细节（ClassIsland 是否启动、插件是否加载、端口与 token）放在**悬停提示**里，不再把状态行撑爆。
- **召唤通知不拦 ClassIsland 未连接**：连不上时降级为应用内横幅 + 系统通知，功能照用。
  但如果你装 ClassIsland 就是为了**语音播报**，那降级就没有播报了 —— 所以未连接时
  召唤那一行会明确提示：`ClassIsland 未连接：只发应用内通知，没有语音播报`。
- 「上课课件弹窗」不同：它**必须**有 ClassIsland（上课事件只能由它提供），所以那一项是硬门槛。

### 换课只会有"一个"临时层

同一天连做多次换课（对调 + 替换）后，仍然只有**一个** `周一（临时层）`：
写入前先看那天有没有临时层，有就**合并进去**（`OrderedSchedules` 里始终只有一条），
不会每换一次就新建一份。

### 通知什么时候发出去

**不在上课就立刻发**；正在上课才排队、等下课再发。
（踩过的坑：以前一律排队、只有"下课"事件才 flush —— 周末/假期没有下课事件，
通知就永远卡在队列里，事件页写着"已转发（已排队）"而 ClassIsland 一条都没收到。）

## 上课课件弹窗：没有当天该科的课件就不弹

弹窗触发链路是「ClassIsland 上课事件 → 当前课次科目/老师 → 当天归档课件」。

- **没有课件就不弹**（不会弹一个空窗口）。
- **只弹当天的、当科的**：同一科目才算命中；昨天/别的科目的文件不会被翻出来。
  科目来自教师映射（归档时写进 `meta.json`）；课表没给科目时会用教师映射里的科目补，
  都补不出来就**不弹**（宁可少弹，也不要上数学课弹语文课件）。
- 认不出科目（`未分类`）的文件，只有在能确认是**本节课这位老师**发的时才会一起弹出。
- 同一节课（日期+科目）只弹一次。

「课件」页的「预览」按钮与设置页「上课课件弹窗 → 测试弹窗」都可以**手动弹一次**（空列表也照常打开并显示空状态），
不需要真的等到上课，也不会受功能开关/课表限制。

### 上课弹窗

- 窗口**置顶 + 居中 + 主动激活**（以前会落在主窗口后面，看起来像"没弹"）；
- 有课件时顶部提示「今天老师发过这些文件，**双击文件即可打开**」；
- 空状态判定同时看扁平列表和科目列表（以前弹窗里明明有文件，下面还挂着"还没有归档到课件"）。

### 老师通知转发（第 6 个功能）

老师发的**活动/集合/时间地点/催交**这类通知，整理成一条简短提醒，经 ClassIsland 通知学生
（上课排队、下课时发，和召唤同一套调度门）。默认**关**，需要 QQ + AI 就绪才能开。

分类里专门区分了"布置作业"和"催交作业"：

| 消息 | 判定 |
|---|---|
| 今天数学作业：练习册P10 | homework（新任务） |
| 昨天作业 12,13,14 号没有交，快点交上来 | **notice**（催交，不是新作业） |
| 今天你们下午有个活动，2:00 到大礼堂 | notice → 标题「今天下午2:00到大礼堂」 |

### 召唤通知里会写清"哪个老师、哪一科"

群里常只说"老师叫你过去一趟"，光凭这句话谁也判断不出是谁。现在解析时会带上上下文：

- 发送者是谁（是否在老师名单里、教什么）；
- **当前课程的科目与科任老师**（来自 ClassIsland 的当前课程）；
- 班里已知老师的名单（用于把"数学老师"匹配到具体的人）。

AI 据此把"老师"落到具体的人，通知就变成「张老师（数学）请小明过去」，
而不是含糊的「老师请小明过去」。
