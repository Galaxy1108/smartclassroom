using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using SmartClassroom.App.ViewModels;
using SmartClassroom.App.Views;
using SmartClassroom.Core;

namespace SmartClassroom.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        InstallCrashGuards();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 单实例：第二个实例只给提示，不初始化（否则会抢 SnowLuma/端口/state.json）
            if (SingleInstance.TryAcquire() is { } blockedBy)
            {
                Console.Error.WriteLine($"智慧课堂已在运行（{blockedBy}），本次启动退出。");
                // 这里**不能**只靠 desktop.Shutdown()：第二个实例没有主窗口，
                // Shutdown 不会真的结束进程，提示窗会一直挂着 → 看起来像"多开没被拦住"。
                SingleInstance.ShowAlreadyRunningAndExit();
                base.OnFrameworkInitializationCompleted();
                return;
            }

            // 上次点了"重启以更新"：先把新版本文件覆盖进来（带进度窗口），再进主界面
            if (UpdateInstaller.ReadPending(AppContext.BaseDirectory) is { } pending)
                ShowUpdateWindowAndApply(desktop, pending);

            var vm = new MainViewModel();
            // 先 Start（它会把 settings.json 装进 Runtime.Settings），再建主窗口——
            // 设置页与 Runtime 必须共用同一个设置对象，否则退出时 Runtime 会用启动快照
            // 把设置页刚保存的值（API Key 等）覆盖回去。
            desktop.Exit += (_, _) => Runtime.Stop();
            Runtime.Start(vm);
            desktop.MainWindow = new MainWindow
            {
                DataContext = vm,
            };
            AppShell.Setup(desktop);   // 托盘 + 关闭到托盘 + 受密码保护的退出
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// 显示"正在更新"窗口并在后台覆盖文件；完成后关闭它、继续正常启动。
    /// 故意做成"先弹窗口再进主界面"，用户重启后第一眼就知道在更新。
    /// </summary>
    private static void ShowUpdateWindowAndApply(IClassicDesktopStyleApplicationLifetime desktop,
        PendingUpdate pending)
    {
        var window = new Views.UpdateProgressWindow();
        desktop.MainWindow = window;
        window.Show();
        _ = window.RunAsync(AppContext.BaseDirectory, pending.Version)
            .ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                // 更新完成：把主窗口交还给正常启动流程（下面会重新赋值 MainWindow）
                window.Close();
            }));
    }

    /// <summary>
    /// 崩溃兜底：界面线程上的未处理异常默认会直接终止应用。
    /// 这里把它拦下来记到事件流，用户还能继续操作（并能在事件页看到原因）。
    /// 进程级/后台任务的异常也一并记录，不静默消失。
    /// </summary>
    private static void InstallCrashGuards()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Runtime.Feed.Append("ui", "界面线程异常（已拦截，应用未退出）", Describe(e.Exception),
                ActivitySeverity.Error);
            e.Handled = true;   // 不让它终结进程
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                Report("未处理异常", ex);
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Report("后台任务异常", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>
    /// 记一条异常。**环境噪音不算崩溃**：例如 Linux 上 Avalonia 找输入法（Fcitx/IBus）
    /// 的 DBus 服务找不到时会抛 DBusException —— 无害，但记成"错误/crash"只会吓人。
    /// 这类只记一条说明性提示。
    /// </summary>
    private static void Report(string title, Exception ex)
    {
        var text = Describe(ex);
        if (IsEnvironmentNoise(text))
        {
            Runtime.Feed.Append("env", $"{title}（环境噪音，无害）",
                "输入法（Fcitx/IBus）的 DBus 服务不可用，Avalonia 找不到它就报这个；不影响功能。",
                ActivitySeverity.Info);
            return;
        }
        Runtime.Feed.Append("crash", title, text, ActivitySeverity.Error);
    }

    private static bool IsEnvironmentNoise(string text)
        => text.Contains("Tmds.DBus", StringComparison.Ordinal)
           || text.Contains("org.freedesktop.DBus", StringComparison.Ordinal)
           || text.Contains("DBusException", StringComparison.Ordinal)
           || text.Contains("GetNameOwnerAsync", StringComparison.Ordinal);

    /// <summary>
    /// 把异常压成可读文本。
    /// <see cref="AggregateException"/> 的第一句永远是"有任务的异常没人观察"——零信息量。
    /// 这里把它拆成内层真实异常（外加第一条有内容的栈帧），否则用户看到的
    /// 就是一串 AggregateException 噪音，根本不知道是谁炸了。
    /// </summary>
    internal static string Describe(Exception ex)
    {
        var lines = new List<string>();
        Collect(ex, 0);
        return lines.Count > 0 ? string.Join("\n", lines) : ex.GetType().Name;

        void Collect(Exception e, int depth)
        {
            if (depth > 4)
                return;
            if (e is AggregateException agg && agg.Flatten().InnerExceptions.Count > 0)
            {
                foreach (var inner in agg.Flatten().InnerExceptions)
                    Collect(inner, depth + 1);
                return;
            }
            var frame = e.StackTrace?.Split('\n')
                .Select(s => s.Trim())
                .FirstOrDefault(s => s.Length > 0);
            lines.Add(frame is null
                ? $"{e.GetType().Name}: {e.Message}"
                : $"{e.GetType().Name}: {e.Message}\n{frame}");
        }
    }
}