using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using SmartClassroom.App.ViewModels;
using SmartClassroom.App.Views;

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
            desktop.MainWindow = new MainWindow
            {
                DataContext = vm,
            };
            desktop.Exit += (_, _) => Runtime.Stop();
            Runtime.Start(vm);
            AppShell.Setup(desktop);   // 托盘 + 关闭到托盘 + 受密码保护的退出
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
            Runtime.Feed.Append("ui", "界面线程异常（已拦截，应用未退出）",
                e.Exception.GetType().Name + ": " + e.Exception.Message);
            e.Handled = true;   // 不让它终结进程
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                Runtime.Feed.Append("crash", "未处理异常", ex.GetType().Name + ": " + ex.Message);
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Runtime.Feed.Append("crash", "后台任务异常", e.Exception.GetType().Name + ": " + e.Exception.Message);
            e.SetObserved();
        };
    }
}