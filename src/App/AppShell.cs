using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using SmartClassroom.App.Views;
using SmartClassroom.Core;

namespace SmartClassroom.App;

/// <summary>
/// 外壳行为：托盘图标、关闭到托盘、以及"受密码保护的彻底退出"。
/// 托盘在部分 Linux 桌面环境下不可用（没有 StatusNotifier 宿主），
/// 因此创建过程全程 try/catch：拿不到托盘就退回"关闭即退出"，不会让用户关不掉应用。
/// </summary>
public static class AppShell
{
    private static TrayIcon? _tray;
    private static bool _reallyQuitting;

    /// <summary>托盘是否可用（决定了关闭窗口是收起到托盘还是直接退出）。</summary>
    public static bool TrayAvailable { get; private set; }

    /// <summary>
    /// 是否"由开机自启动拉起"。命令行带 --tray 时为 true：
    /// 只启动、不弹窗口，直接待在托盘里（用户明确要求）。
    /// </summary>
    public static bool StartHidden { get; set; }

    public static void Setup(IClassicDesktopStyleApplicationLifetime desktop)
    {
        CreateTray(desktop);

        // 自启动拉起时隐藏主窗口 —— 但**托盘不可用就不隐藏**，
        // 否则应用会变成"看不见也点不到"的幽灵进程。
        if (StartHidden && TrayAvailable && desktop.MainWindow is { } main)
            main.Hide();

        // 托盘可用时才拦截关闭；否则保持正常退出语义。
        desktop.ShutdownMode = TrayAvailable
            ? ShutdownMode.OnExplicitShutdown
            : ShutdownMode.OnMainWindowClose;

        if (desktop.MainWindow is { } window)
        {
            window.Closing += (_, e) =>
            {
                if (_reallyQuitting || !TrayAvailable || !Runtime.Settings.MinimizeToTray)
                    return;
                e.Cancel = true;   // 收回到托盘而不退出
                window.Hide();
            };
        }
    }

    private static void CreateTray(IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            var icon = new WindowIcon(AssetLoader.Open(
                new Uri("avares://SmartClassroom.App/Assets/app-icon.png")));
            var menu = new NativeMenu();
            var open = new NativeMenuItem { Header = "打开主界面" };
            open.Click += (_, _) => ShowMainWindow(desktop);
            var quit = new NativeMenuItem { Header = "退出智慧课堂" };
            quit.Click += async (_, _) => await RequestQuitAsync(desktop);
            menu.Items.Add(open);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(quit);

            _tray = new TrayIcon
            {
                Icon = icon,
                ToolTipText = "智慧课堂",
                Menu = menu,
                IsVisible = true
            };
            _tray.Clicked += (_, _) => ShowMainWindow(desktop);
            TrayIcon.SetIcons(Application.Current!, new TrayIcons { _tray });
            TrayAvailable = true;
        }
        catch
        {
            TrayAvailable = false;
            _tray = null;
        }
    }

    public static void ShowMainWindow(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (desktop.MainWindow is not { } window)
            return;
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
    }

    /// <summary>彻底退出：设置了管理员密码时**必须**再次验证（不受会话免密窗口影响）。</summary>
    public static async Task RequestQuitAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var ok = await Runtime.Auth.RequireAsync(
            reason => PasswordDialog.PromptAsync("退出确认", reason),
            "退出智慧课堂需要管理员密码", forcePrompt: true);
        if (!ok)
            return;

        _reallyQuitting = true;
        Runtime.Stop();
        if (desktop.MainWindow is { } window)
            window.Close();          // 此时 Closing 不再拦截
        desktop.Shutdown();
    }
}
