using System.Collections.ObjectModel;
using Avalonia.Threading;
using SmartClassroom.App.Views;

namespace SmartClassroom.App;

/// <summary>一条应用内通知。</summary>
public sealed record ToastItem(Guid Id, string Title, string Message, NoticeSeverity Severity);

/// <summary>
/// 应用内弹窗通知（右下角浮出、几秒后自动消失）。
///
/// 为什么需要它：启动/停止 SnowLuma、下载 Node 这类操作要几秒，
/// 而原来的反馈只有设置页里一行小字（甚至要翻到日志区），用户根本注意不到，
/// 于是"没反应 = 失败了"。凡是**有副作用且耗时**的操作，完成后都该弹一条。
///
/// 线程约定：后台线程可以直接调用 <see cref="Show"/>，内部会切到 UI 线程。
/// </summary>
public static class Toasts
{
    /// <summary>默认停留时长（秒）。操作类通知够看完一行字。</summary>
    public const int DefaultSeconds = 5;

    public static ObservableCollection<ToastItem> Items { get; } = new();

    /// <summary>弹一条通知。可在任意线程调用。</summary>
    public static void Show(string title, string message = "",
        NoticeSeverity severity = NoticeSeverity.Informational, int seconds = DefaultSeconds)
    {
        var item = new ToastItem(Guid.NewGuid(), title, message, severity);
        Post(() =>
        {
            Items.Add(item);
            if (seconds <= 0)
                return;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Remove(item);
            };
            timer.Start();
        });
    }

    /// <summary>操作成功。</summary>
    public static void Success(string title, string message = "") => Show(title, message, NoticeSeverity.Success);

    /// <summary>操作失败 / 明显异常。</summary>
    public static void Error(string title, string message = "") => Show(title, message, NoticeSeverity.Error);

    /// <summary>需要留意但不算失败。</summary>
    public static void Warn(string title, string message = "") => Show(title, message, NoticeSeverity.Warning);

    /// <summary>手动关闭（也可等自动消失）。</summary>
    public static void Remove(ToastItem item) => Post(() => Items.Remove(item));

    private static void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }
}
