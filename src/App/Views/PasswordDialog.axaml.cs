using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace SmartClassroom.App.Views;

/// <summary>管理员密码输入框（模态）。取消返回 null，输入则返回字符串。</summary>
public partial class PasswordDialog : Window
{
    private string? _result;

    public PasswordDialog()
    {
        InitializeComponent();
        Opened += (_, _) => PasswordBox.Focus();   // 打开就能直接输密码
    }

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        var text = PasswordBox.Text ?? "";
        if (text.Length == 0)
        {
            ErrorText.Text = "请输入密码。";
            ErrorText.IsVisible = true;
            return;
        }
        _result = text;
        Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        _result = null;
        Close();
    }

    /// <summary>弹出密码框并等待输入。取消/关窗返回 null。</summary>
    public static async Task<string?> PromptAsync(string title, string reason)
    {
        var owner = (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;

        var dialog = new PasswordDialog { Title = title, Topmost = true };
        dialog.ReasonText.Text = reason;

        if (owner is not null && owner.IsVisible)
        {
            await dialog.ShowDialog(owner);
        }
        else
        {
            // ⚠️ 主窗口收起到托盘时只能 Show()，而 Show() **立即返回** ——
            // 直接读 _result 会永远是 null，被当成"用户取消"，现象就是"怎么都解不开锁"
            //（实测反馈："还是解锁不了"）。这里必须等它真正关闭。
            var closed = new TaskCompletionSource();
            dialog.Closed += (_, _) => closed.TrySetResult();
            dialog.Show();
            await closed.Task;
        }

        // 关窗后取输入；用 Dispatcher 保证在 UI 线程读取。
        return await Dispatcher.UIThread.InvokeAsync(() => dialog._result);
    }
}
