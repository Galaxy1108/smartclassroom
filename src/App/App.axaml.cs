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

            // 首次启动弹一次封号风险警告（只一次；「启动」按钮仍然把关）
            Dispatcher.UIThread.Post(async () => await RiskNotice.ShowOnFirstLaunchAsync());
        }

        base.OnFrameworkInitializationCompleted();
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
                Runtime.Feed.Append("crash", "未处理异常", Describe(ex), ActivitySeverity.Error);
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Runtime.Feed.Append("crash", "后台任务异常", Describe(e.Exception), ActivitySeverity.Error);
            e.SetObserved();
        };
    }

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