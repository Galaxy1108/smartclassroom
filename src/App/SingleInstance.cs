using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace SmartClassroom.App;

/// <summary>
/// 单实例：不允许同时开两个智慧课堂。
///
/// 多开会同时抢同一个 SnowLuma 进程、同一个 5199 桥接端口、同一份 state.json
/// （后写的把先写的覆盖掉），所以必须挡住。
///
/// 双重判断：命名互斥量 + 独占锁文件（任一说"已经有人在跑"就拦）。
/// 第二个实例给出提示后**强制退出进程**——踩过的坑：只调 desktop.Shutdown()，
/// 而第二个实例没有主窗口，Shutdown 不会真的结束进程，提示窗就一直挂在屏幕上，
/// 用户看到的现象就是"多开没被拦住"。
/// </summary>
public static class SingleInstance
{
    private const string MutexName = "SmartClassroom.SingleInstance.v1";

    private static Mutex? _mutex;
    private static FileStream? _lock;

    /// <summary>锁文件路径（用户数据目录下）。</summary>
    public static string DefaultLockPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SmartClassroom", ".instance.lock");

    /// <summary>
    /// 抢单实例。返回 null = 抢到了；否则返回被谁挡住的说明（"mutex" / "lock"），便于排查。
    /// </summary>
    public static string? TryAcquire(string? lockPath = null, string? mutexName = null)
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: true, mutexName ?? MutexName, out var createdNew);
            if (!createdNew)
                return "mutex";
        }
        catch
        {
            // 某些平台/环境拿不到命名互斥量：不致命，继续用锁文件判断
        }

        return TryAcquireLockFile(lockPath ?? DefaultLockPath) ? null : "lock";
    }

    /// <summary>独占锁文件（FileShare.None）。true = 抢到了。</summary>
    public static bool TryAcquireLockFile(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _lock = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;   // 已被另一个实例占着
        }
        catch
        {
            return true;    // 判断不了就别拦着用户用
        }
    }

    /// <summary>
    /// 提示"已经在运行"。**不进任务栏**：否则用户看到任务栏多一个图标，
    /// 会以为"多开没被拦住"。
    /// </summary>
    public static Window CreateAlreadyRunningNotice() => new Window
    {
        Title = "智慧课堂已在运行",
        Width = 380,
        Height = 170,
        CanResize = false,
        ShowInTaskbar = false,
        WindowStartupLocation = WindowStartupLocation.CenterScreen,
        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 14,
            Children =
            {
                new TextBlock
                {
                    Text = "智慧课堂已经在运行了。",
                    FontWeight = FontWeight.SemiBold,
                    FontSize = 15
                },
                new TextBlock
                {
                    Text = "请用已在运行的那个窗口（可能在系统托盘里）。同时开两个会互相覆盖设置与状态。",
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.8,
                    FontSize = 12
                },
                new Button
                {
                    Content = "知道了",
                    HorizontalAlignment = HorizontalAlignment.Right
                }
            }
        }
    };

    /// <summary>显示提示窗，几秒后**强制退出**（不依赖 desktop.Shutdown）。</summary>
    public static void ShowAlreadyRunningAndExit(int seconds = 4)
    {
        try
        {
            var window = CreateAlreadyRunningNotice();
            if (window.Content is StackPanel panel && panel.Children[^1] is Button button)
                button.Click += (_, _) => Exit();
            window.Closed += (_, _) => Exit();
            window.Show();
        }
        catch
        {
            Exit();   // 连提示窗都开不出来就别拖着
            return;
        }
        DispatcherTimer.RunOnce(Exit, TimeSpan.FromSeconds(seconds));
    }

    private static void Exit()
    {
        ReleaseLocks();
        Environment.Exit(0);
    }

    /// <summary>释放锁（正常退出与测试用）。</summary>
    public static void ReleaseLocks()
    {
        try { _lock?.Dispose(); } catch { /* 忽略 */ }
        _lock = null;
        try { _mutex?.ReleaseMutex(); } catch { /* 没持有/已释放 */ }
        try { _mutex?.Dispose(); } catch { /* 忽略 */ }
        _mutex = null;
    }
}
