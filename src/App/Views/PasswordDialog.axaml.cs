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

        var dialog = new PasswordDialog { Title = title };
        dialog.ReasonText.Text = reason;

        if (owner is not null && owner.IsVisible)
            await dialog.ShowDialog(owner);
        else
            dialog.Show();   // 主窗口已收起到托盘时也能弹出

        // ShowDialog 返回后取输入；用 Dispatcher 保证在 UI 线程读取。
        return await Dispatcher.UIThread.InvokeAsync(() => dialog._result);
    }
}
