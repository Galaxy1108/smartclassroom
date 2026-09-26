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

    public static AppSettings Settings { get; private set; } = new();
    public static HomeworkStore Homework { get; } = new();
    public static ActivityFeed Feed { get; } = new();
    public static CoursewareService Courseware { get; } = new();
    public static ScheduleGate? Gate { get; private set; }
    public static PipelineService? Pipeline { get; private set; }

    public static void Start(MainViewModel status)
    {
        _cts = new CancellationTokenSource();
        var cancel = _cts.Token;
        Settings = SettingsStore.Load();
        if (Settings.ArchiveRoot.Length > 0)
            Courseware.RebuildFromArchive(Settings.ArchiveRoot);

        var configured = Settings.GroupIds.Count > 0
            && Settings.AiBaseUrl.Length > 0 && Settings.AiModel.Length > 0;
        status.StatusText = configured ? "正在连接 QQ…" : "未配置：在设置页填写 AI / QQ 后重启生效";

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
        var oneBot = new OneBotClient(Settings.OneBotHttp, Settings.OneBotWs, Settings.OneBotToken);
        var plugin = new PluginLink($"http://127.0.0.1:{Settings.PluginPort}", Settings.PluginToken);

        // AI 引擎二选一：内置直连（零依赖）或 pi-ai Node 边车。
        IAiClient ai = AiEngineParser.Parse(Settings.AiEngine) == AiEngine.PiAiSidecar
            ? new PiAiSidecarClient(new SidecarOptions
            {
                SidecarDir = NodeRuntime.SidecarDir(AppContext.BaseDirectory),
                Provider = Settings.AiProvider.Length > 0 ? Settings.AiProvider : null,
                Model = Settings.AiModel,
                ApiKey = Settings.AiApiKey,
                BaseUrl = Settings.AiBaseUrl
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
            Root = Settings.ArchiveRoot.Length > 0 ? Settings.ArchiveRoot
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SmartClassroom", "archive")
        });
        var pipeline = new PipelineService(teachers, new AiAnalyzer(ai), gate, plugin,
            oneBot, archive, Courseware, Homework, Feed);
        Pipeline = pipeline;
        pipeline.CoursewareSuggested += files => Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var vm = CoursewareViewModel.FromFiles(files
                    .Where(f => f.LocalPath is not null)
                    .Select(f => (f.FileName, f.LocalPath!, f.Size)));
                new CoursewareWindow { DataContext = vm }.Show();
            }
            catch (Exception ex) { Feed.Append("courseware", "课件弹窗失败", ex.Message); }
        });

        _ = RunQqLoopAsync(oneBot, pipeline, status, cancel);
        _ = TryConnectClassIslandAsync(statusProvider, status, cancel, pipeline);
    }

    public static void Stop()
    {
        try { _cts?.Cancel(); } catch { }
    }

    private static async Task RunQqLoopAsync(OneBotClient oneBot, PipelineService pipeline, MainViewModel status, CancellationToken cancel)
    {
        var groups = new HashSet<long>(Settings.GroupIds);
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                await oneBot.RunEventLoopAsync(async (ev, ct) =>
                {
                    switch (ev)
                    {
                        case GroupMessageEvent m when groups.Contains(m.GroupId):
                            await pipeline.OnGroupMessageAsync(m, ct);
                            break;
                        case GroupUploadEvent u when groups.Contains(u.GroupId):
                            await pipeline.OnGroupUploadAsync(u, ct);
                            break;
                    }
                }, cancel);
                Dispatcher.UIThread.Post(() => status.StatusText = "QQ 已连接");
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Feed.Append("qq", "QQ 连接断开，5 秒后重连", ex.Message);
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
            Feed.Append("classisland", "ClassIsland 未连接（稍后可单独用 App 通知降级）", ex.Message);
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
        catch (Exception ex) { Feed.Append("classisland", "上课事件处理失败", ex.Message); }
    }
}
