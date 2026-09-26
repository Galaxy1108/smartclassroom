using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;

namespace SmartClassroom.App;

/// <summary>
/// 单实例：不允许同时开两个智慧课堂。
///
/// 多开会同时抢同一个 SnowLuma 进程、同一个 5199 桥接端口、同一份 state.json
/// （后写的把先写的覆盖掉），所以必须挡住。用命名互斥量判断，
/// 第二个实例给出提示后直接退出——静默退出会让用户以为"点了没反应"。
/// </summary>
public static class SingleInstance
{
    private const string MutexName = "SmartClassroom.SingleInstance.v1";

    private static Mutex? _mutex;

    /// <summary>抢到（或本来就是我们自己）返回 true；已有实例在跑返回 false。</summary>
    public static bool TryAcquire()
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
            return createdNew;
        }
        catch
        {
            return true;   // 判断不了就别拦着用户用
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
                        FontWeight = Avalonia.Media.FontWeight.SemiBold,
                        FontSize = 15
                    },
                    new TextBlock
                    {
                        Text = "请用已在运行的那个窗口（可能在系统托盘里）。同时开两个会互相覆盖设置与状态。",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
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
    public static void ShowAlreadyRunningNotice()
    {
        var window = CreateAlreadyRunningNotice();
        if (window.Content is StackPanel panel && panel.Children[^1] is Button button)
            button.Click += (_, _) => window.Close();
        window.Show();
    }

}
