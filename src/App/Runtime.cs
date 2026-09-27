using Avalonia.Threading;
using SmartClassroom.App.ViewModels;
using SmartClassroom.App.Views;
using SmartClassroom.Core;
using SmartClassroom.Core.AI;
using SmartClassroom.Core.QQ;

namespace SmartClassroom.App;

/// <summary>
/// App 运行时总装：从设置构建管线，后台跑 QQ 事件循环 + ClassIsland 连接。
/// 一切外部连接都是 best-effort：失败只记动态流，应用照常启动。
/// </summary>
public static class Runtime
{
    private static CancellationTokenSource? _cts;
    private static System.Timers.Timer? _saveTimer;
    private static System.Timers.Timer? _scaleSaveTimer;

    public static AppSettings Settings { get; private set; } = new();

    /// <summary>
    /// settings.json 是否已经装进 <see cref="Settings"/>。
    /// 设置页只有在 true 时才与 Runtime 共用这个对象；否则（比如设计器/预览工具提前构造）
    /// 它得自己去读文件——绝不能拿一个空对象当"当前设置"再存回去，那会把用户配置清空。
    /// </summary>
    public static bool SettingsLoaded { get; private set; }
    public static HomeworkStore Homework { get; } = new();
    public static ActivityFeed Feed { get; } = new();
    public static CoursewareService Courseware { get; } = new();
    public static PendingStore Pending { get; } = new();

    /// <summary>管理员认证门；密码哈希随时从当前设置读取（改了密码立即生效）。</summary>
    public static AuthGate Auth { get; } = new(() => Settings.AdminPasswordHash);

    /// <summary>归档默认根目录（用户未指定时）。</summary>
    public static string DefaultArchiveRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SmartClassroom", "archive");
    public static ScheduleGate? Gate { get; private set; }
    public static PipelineService? Pipeline { get; private set; }

    /// <summary>已提示过"该群没监听"的群（每个群只提示一次）。</summary>
    private static readonly HashSet<long> _ignoredGroups = [];

    /// <summary>把"解锁后是否记住 10 分钟"应用到 AuthGate。</summary>
    private static void ApplyAuthPolicy(AppSettings s)
        => Auth.SessionMinutes = s.RememberUnlock ? 10 : 0;

    private static TeacherMap? _liveTeachers;
    private static PipelineService? _livePipeline;

    /// <summary>
    /// 设置保存后**热应用**：老师名单与功能开关立刻生效，不用重启。
    /// 以前都是启动时的快照 —— 用户加完老师还被当陌生人、开了功能没反应，
    /// 现象就是"我都加了怎么还被忽略"（实测踩到）。
    /// </summary>
    private static void OnSettingsSaved(AppSettings s)
    {
        try
        {
            ApplyAuthPolicy(s);
            _liveTeachers?.Reload(s.Teachers);
            _livePipeline?.UpdateFlags(s.ToFeatureFlags());
        }
        catch (Exception ex)
        {
            Feed.Append("qq", "热应用设置失败（重启后生效）", ex.Message, ActivitySeverity.Warning);
        }
    }

    /// <summary>消息摘要（时间线里一行放得下）。</summary>
    private static string Trim(string text)
        => text.Length <= 40 ? text : text[..40] + "…";

    public static void Start(MainViewModel status)
    {
        _cts = new CancellationTokenSource();
        var cancel = _cts.Token;
        Settings = SettingsStore.Load();
        ApplyAuthPolicy(Settings);
        SettingsLoaded = true;
        LoadState();
        PruneExpiredHomework();   // 过期作业直接删掉（不保留历史）
        if (Settings.ArchiveRoot.Length > 0)
            Courseware.RebuildFromArchive(Settings.ArchiveRoot);

        // 周期性落盘 + 退出时落盘（数据量小，直接整体写）。
        _saveTimer = new System.Timers.Timer(30_000) { AutoReset = true };
        _saveTimer.Elapsed += (_, _) =>
        {
            PruneExpiredHomework();   // 跨天之后也会被清掉，不用等重启
            SaveState();
        };
        _saveTimer.Start();

        var (configured, readiness) = EvaluateReadiness(Settings);
        status.StatusText = readiness;
        if (Settings.ListenAllGroups)
            Feed.Append("qq", "已开启「监听全部群」", "该账号所在的每个群都会被处理", ActivitySeverity.Warning);

        var statusProvider = new ClassIslandStatusProvider();
        var gate = new ScheduleGate(statusProvider);
        Gate = gate;

        if (!configured)
        {
            // 尝试只连 ClassIsland（读状态供状态栏），不跑 QQ。
            _ = TryConnectClassIslandAsync(statusProvider, status, cancel);
            return;
        }

        var teachers = new TeacherMap(Settings.Teachers);
        RepairOneBotEndpoint();   // 地址与 token 可能来自不同账号（手工改过就会这样）
        var oneBot = new OneBotClient(Settings.OneBotHttp, Settings.OneBotWs, Settings.OneBotToken,
            wsToken: Settings.OneBotWsToken);
        var plugin = new PluginLink($"http://127.0.0.1:{Settings.PluginPort}", Settings.PluginToken);

        // AI 引擎二选一：内置直连（零依赖）或 pi-ai Node 边车。
        IAiClient ai = AiEngineParser.Parse(Settings.AiEngine) == AiEngine.PiAiSidecar
            ? new PiAiSidecarClient(new SidecarOptions
            {
                SidecarDir = NodeRuntime.SidecarDir(AppContext.BaseDirectory),
                Provider = Settings.AiProvider.Length > 0 ? Settings.AiProvider : null,
                Model = Settings.AiModel,
                ApiKey = Settings.AiApiKey,
                BaseUrl = Settings.AiBaseUrl,
                Reasoning = Settings.AiReasoning
            })
            : new AiGateway(new AiOptions
            {
                BaseUrl = Settings.AiBaseUrl,
                ApiKey = Settings.AiApiKey,
                Model = Settings.AiModel
            });
        Feed.Append("ai", $"AI 引擎：{(ai is PiAiSidecarClient ? "pi-ai 边车" : "内置直连")}", Settings.AiModel);
        var archive = new FileArchive(new ArchiveOptions
        {
            Root = Settings.ArchiveRoot.Length > 0 ? Settings.ArchiveRoot : DefaultArchiveRoot,
            DownloadAll = Settings.ArchiveDownloadAll
        });
        var pipeline = new PipelineService(teachers, new AiAnalyzer(ai), gate, plugin,
            oneBot, archive, Courseware, Homework, Feed, Pending, Settings.ToFeatureFlags());
        Pipeline = pipeline;
        _livePipeline = pipeline;   // 设置保存时热更新功能开关用
        pipeline.CoursewareSuggested += files => Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var vm = CoursewareViewModel.FromFiles(files
                    .Where(f => f.LocalPath is not null)
                    .Select(f => (f.FileName, f.LocalPath!, f.Size)));
                new CoursewareWindow { DataContext = vm }.Show();
            }
            catch (Exception ex) { Feed.Append("courseware", "课件弹窗失败", ex.Message, ActivitySeverity.Error); }
        });

        _ = RunQqLoopAsync(oneBot, pipeline, status, cancel);
        _ = TryConnectClassIslandAsync(statusProvider, status, cancel, pipeline);
    }

    /// <summary>
    /// 就绪判定。**必须与设置页的门槛一致**，否则会出现"设置页说配置好了、运行时说没配置"
    /// （实测踩到：只监听私聊被判为未配置；pi-ai 引擎明明不需要服务地址却要求填）。
    /// 返回 (是否就绪, 状态栏文字)。
    /// </summary>
    public static (bool Ready, string Text) EvaluateReadiness(AppSettings s)
    {
        var missing = new List<string>();
        if (s.GroupIds.Count == 0 && !s.ListenAllGroups && !s.ListenTeacherPrivate)
            missing.Add("监听群号或老师私聊");
        if (s.OneBotHttp.Trim().Length == 0 && s.OneBotWs.Trim().Length == 0)
            missing.Add("OneBot 地址");

        // pi-ai 走本地边车，不需要服务地址；只有内置直连才要求填
        var aiReady = s.AiModel.Trim().Length > 0
                      && (AiEngineParser.Parse(s.AiEngine) == AiEngine.PiAiSidecar
                          || s.AiBaseUrl.Trim().Length > 0);
        if (!aiReady)
            missing.Add("AI 模型");

        return missing.Count == 0
            ? (true, "正在连接 QQ…")
            : (false, "未配置：" + string.Join("、", missing) + "（在设置页填好后重启生效）");
    }

    /// <summary>
    /// 校验并纠正 OneBot 连接信息：**地址与 token 必须是同一个账号的组合**。
    /// 实测踩到：URL 指着 3000/3001（班级号）而 token 是另一个号的，
    /// 于是 WS 升级 401、事件收不到（HTTP 探活能过、WS 过不了，很容易看错）。
    /// 这里的做法与设置页「检测并选择账号」一致：逐个账号试，用第一个能应答的组合覆盖。
    /// </summary>
    private static void RepairOneBotEndpoint()
    {
        var installDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SmartClassroom", "snowluma");
        try
        {
            if (SnowlumaManager.ReadOneBotAccounts(installDir).Count == 0)
                return;

            // 现组合能应答就不用动（本地调用很快）
            if (EndpointResponds(Settings.OneBotHttp, Settings.OneBotWs,
                    Settings.OneBotToken, Settings.OneBotWsToken))
                return;

            var order = SnowlumaManager.ReadOneBotAccounts(installDir)
                .OrderByDescending(u => u == Settings.QqAccount);   // 选中的账号优先
            foreach (var uin in order)
            {
                var ep = SnowlumaManager.ReadOneBotEndpoint(installDir, uin);
                if (ep is null || !EndpointResponds(ep.Http, ep.Ws, ep.Token, ep.WsToken))
                    continue;

                Feed.Append("qq", $"已按账号 {uin} 纠正 OneBot 连接信息",
                    $"{Settings.OneBotHttp} → {ep.Http}（地址与 token 必须是同一个账号的）",
                    ActivitySeverity.Warning);
                Settings.OneBotHttp = ep.Http;
                Settings.OneBotWs = ep.Ws;
                Settings.OneBotToken = ep.Token;
                Settings.OneBotWsToken = ep.WsToken;
                if (Settings.QqAccount <= 0)
                    Settings.QqAccount = uin;
                SettingsStore.Save(Settings, SettingsStore.DefaultPath);
                return;
            }
        }
        catch (Exception ex)
        {
            Feed.Append("qq", "校验 OneBot 连接信息时出错", ex.Message, ActivitySeverity.Warning);
        }
    }

    private static bool EndpointResponds(string http, string ws, string token, string wsToken)
    {
        try
        {
            var client = new OneBotClient(http, ws, token,
                wsToken: wsToken.Length > 0 ? wsToken : null);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            client.GetLoginInfoAsync(cts.Token).GetAwaiter().GetResult();
            client.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return true;
        }
        catch { return false; }
    }

    public static void Stop()
    {
        SaveState();
        _scaleSaveTimer?.Stop();
        _scaleSaveTimer?.Dispose();
        _scaleSaveTimer = null;
        try { SettingsStore.Save(Settings); } catch { /* 退出路径不抛 */ }
        _saveTimer?.Stop();
        _saveTimer?.Dispose();
        _saveTimer = null;
        try { _cts?.Cancel(); } catch { }
    }

    /// <summary>恢复上次的作业 / 待处理 / 事件时间线。</summary>
    public static void LoadState()
    {
        try
        {
            var state = AppStateStore.Load();
            if (state.Homework.Count > 0)
                Homework.ReplaceAll(state.Homework);
            if (state.Pending.Count > 0)
                Pending.ReplaceAll(state.Pending);
            if (state.Feed.Count > 0)
                Feed.ReplaceAll(state.Feed);
            Feed.Append("state", $"已恢复上次状态", 
                $"作业 {state.Homework.Count} 条 / 待处理 {state.Pending.Count} 条 / 事件 {state.Feed.Count} 条");
        }
        catch (Exception ex)
        {
            Feed.Append("state", "状态恢复失败", ex.Message, ActivitySeverity.Error);
        }
    }

    /// <summary>
    /// 记录界面缩放并防抖落盘。
    /// Ctrl+滚轮会连续触发，逐个事件写文件既浪费又可能写坏；
    /// 这里停顿 600ms 后才真正保存。
    /// </summary>
    public static void PersistZoom(double scale)
    {
        Settings.UiScale = scale;
        _scaleSaveTimer?.Stop();
        _scaleSaveTimer?.Dispose();
        _scaleSaveTimer = new System.Timers.Timer(600) { AutoReset = false };
        _scaleSaveTimer.Elapsed += (_, _) =>
        {
            try { SettingsStore.Save(Settings); } catch { /* 落盘失败不影响运行 */ }
        };
        _scaleSaveTimer.Start();
    }

    /// <summary>
    /// 清理过期作业：date &lt; 今天的一律删除（含 state.json，下次落盘时生效）。
    /// 会记一条时间线，避免"作业怎么没了"无从查证。
    /// </summary>
    public static int PruneExpiredHomework()
    {
        try
        {
            var removed = Homework.PruneExpired(DateOnly.FromDateTime(DateTime.Now));
            if (removed.Count == 0)
                return 0;
            var subjects = string.Join("、", removed.Select(h => h.Subject).Distinct());
            Feed.Append("homework", $"已删除 {removed.Count} 条过期作业", subjects);
            return removed.Count;
        }
        catch
        {
            return 0;   // 清理失败不影响运行
        }
    }

    /// <summary>落盘（原子替换，见 AppStateStore）。</summary>
    public static void SaveState()
    {
        try
        {
            AppStateStore.Save(new PersistedState
            {
                Homework = Homework.All.ToList(),
                Pending = Pending.All.ToList(),
                Feed = Feed.Entries.ToList()
            });
        }
        catch
        {
            // 落盘失败不影响运行
        }
    }

    private static async Task RunQqLoopAsync(OneBotClient oneBot, PipelineService pipeline, MainViewModel status, CancellationToken cancel)
    {
        var groups = new HashSet<long>(Settings.GroupIds);
        var listenAll = Settings.ListenAllGroups;
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                await oneBot.RunEventLoopAsync(async (ev, ct) =>
                {
                    // 只有开了「监听全部群」才不过滤。
                    // 注意：不能写成 groups.Count == 0 就全放行 —— 那样"只监听私聊"的用户
                    // 会意外处理所有群的消息。
                    var wanted = listenAll;
                    switch (ev)
                    {
                        case GroupMessageEvent m when wanted || groups.Contains(m.GroupId):
                        {
                            // 每条消息先建一条"进行中"的记录，处理过程中实时更新
                            var row = Feed.Begin("qq", "收到群消息",
                                $"{m.Card ?? m.Nickname ?? $"QQ{m.UserId}"}：{Trim(m.Text)}");
                            await pipeline.OnGroupMessageAsync(m, ct, row);
                            break;
                        }
                        case GroupMessageEvent ignored:
                            // 没监听的群也要给出**结果**（已忽略），否则用户只看到"什么都没发生"。
                            // 每个群只提示一次，免得几十个群刷屏。
                            if (_ignoredGroups.Add(ignored.GroupId))
                            {
                                var row = Feed.Begin("qq", "收到群消息",
                                    $"{ignored.Card ?? ignored.Nickname ?? $"QQ{ignored.UserId}"}：{Trim(ignored.Text)}");
                                Feed.Complete(row, "已忽略（该群没有监听）",
                                    $"群 {ignored.GroupId} 不在监听列表里；在「监听群号」里点「选择群…」把它加上",
                                    ActivitySeverity.Muted);   // 灰色：什么都没发生，别报警
                            }
                            break;
                        case GroupUploadEvent u when wanted || groups.Contains(u.GroupId):
                        {
                            var row = Feed.Begin("qq", "收到群文件",
                                $"{u.File.Name}（{u.File.Size / 1024 / 1024}MB）");
                            await pipeline.OnGroupUploadAsync(u, ct, row);
                            break;
                        }
                        case GroupUploadEvent privateFile when privateFile.GroupId == 0
                                                              && Settings.ListenTeacherPrivate:
                        {
                            // 私聊文件：行里直接显示文件名（跟消息内容一样）
                            var row = Feed.Begin("qq", "收到文件",
                                $"{privateFile.File.Name}（{privateFile.File.Size / 1024 / 1024}MB）");
                            await pipeline.OnGroupUploadAsync(privateFile, ct, row);
                            break;
                        }
                        case GroupUploadEvent ignoredFile:
                            // 没监听的群里的文件同样要给结果，并且**写出文件名**（否则只有一片空白）
                            var fileRow = Feed.Begin("qq", "收到群文件",
                                $"{ignoredFile.File.Name}（{ignoredFile.File.Size / 1024 / 1024}MB）");
                            Feed.Complete(fileRow, "已忽略（该群没有监听）",
                                $"群 {ignoredFile.GroupId} 不在监听列表里；在「监听群号」里点「选择群…」把它加上",
                                ActivitySeverity.Muted);
                            break;
                        case PrivateMessageEvent p when Settings.ListenTeacherPrivate:
                        {
                            var row = Feed.Begin("qq", "收到私聊",
                                $"{p.Nickname ?? $"QQ{p.UserId}"}：{Trim(p.Text)}");
                            await pipeline.OnPrivateMessageAsync(p, ct, row);
                            break;
                        }
                    }
                }, cancel,
                onHandlerError: (ev, ex) =>
                {
                    // 单条消息处理出错：写一条"出现错误"的结果行，连接保持不动
                    Feed.Append("qq", "出现错误：处理消息失败",
                        $"{ex.GetType().Name}：{ex.Message}", ActivitySeverity.Error);
                },
                onConnected: () => Dispatcher.UIThread.Post(() => status.StatusText = "QQ 已连接"));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                var hint = ex.Message.Contains("401")
                    ? "（401 = token 不对：SnowLuma 的 HTTP 与 WS 是两个 token，"
                      + "且地址与 token 必须是同一个账号的 —— 点「检测并选择账号」会自动配好）"
                    : "";
                Feed.Append("qq", "QQ 连接断开，5 秒后重连", ex.Message + hint, ActivitySeverity.Warning);
                Dispatcher.UIThread.Post(() => status.StatusText = "QQ 未连接（重连中…）");
                try { await Task.Delay(5000, cancel); } catch { break; }
            }
        }
    }

    private static async Task TryConnectClassIslandAsync(
        ClassIslandStatusProvider provider, MainViewModel status,
        CancellationToken cancel, PipelineService? pipeline = null)
    {
        try
        {
            if (pipeline is not null)
            {
                provider.AddNotifyHandler(ClassIsland.Shared.IPC.IpcRoutedNotifyIds.OnBreakingTimeNotifyId,
                    () => _ = pipeline.OnClassEndedAsync());
                provider.AddNotifyHandler(ClassIsland.Shared.IPC.IpcRoutedNotifyIds.OnAfterSchoolNotifyId,
                    () => _ = pipeline.OnClassEndedAsync());
                provider.AddNotifyHandler(ClassIsland.Shared.IPC.IpcRoutedNotifyIds.OnClassNotifyId,
                    () => _ = OnClassStartedAsync(provider, pipeline));
            }
            await provider.ConnectAsync(cancel);
            Dispatcher.UIThread.Post(() => status.StatusText += " · ClassIsland 已连接");
        }
        catch (Exception ex)
        {
            Feed.Append("classisland", "ClassIsland 未连接（稍后可单独用 App 通知降级）", ex.Message,
                ActivitySeverity.Warning);
        }
    }

    private static async Task OnClassStartedAsync(ClassIslandStatusProvider provider, PipelineService pipeline)
    {
        try
        {
            var lesson = await provider.GetCurrentLessonAsync();
            if (lesson is null)
                return;
            pipeline.OnClassStarted(DateOnly.FromDateTime(DateTime.Now), lesson.Subject, lesson.Teacher);
        }
        catch (Exception ex) { Feed.Append("classisland", "上课事件处理失败", ex.Message, ActivitySeverity.Error); }
    }
}
