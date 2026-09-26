using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;
using SmartClassroom.Core;

namespace SmartClassroom.App.Views;

/// <summary>轻量弹窗封装（FluentAvalonia ContentDialog），用于需要用户明确知晓的提示。</summary>
public static class Dialogs
{
    public static async Task ShowAsync(string title, string message, string closeText = "知道了")
    {
        var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null)
            return;

        var dialog = new ContentDialog
        {
            Title = title,
            Content = WrapText(message),
            CloseButtonText = closeText,
            DefaultButton = ContentDialogButton.Close
        };
        try
        {
            await dialog.ShowAsync(owner);
        }
        catch
        {
            // 弹窗失败不应影响主流程（例如没有可用的 TopLevel）。
        }
    }

    /// <summary>确认框。返回 true = 用户点了主按钮（同意）。</summary>
    public static async Task<bool> ConfirmAsync(string title, string message,
        string primaryText = "确定", string closeText = "取消")
    {
        var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null)
            return false;

        var dialog = new ContentDialog
        {
            Title = title,
            Content = WrapText(message),
            PrimaryButtonText = primaryText,
            CloseButtonText = closeText,
            DefaultButton = ContentDialogButton.Primary
        };
        try
        {
            return await dialog.ShowAsync(owner) == ContentDialogResult.Primary;
        }
        catch
        {
            return false;   // 已有弹窗打开/没有 TopLevel：按"没同意"处理，不误开功能
        }
    }

    /// <summary>
    /// 选择 QQ 账号。一台机器上可能登过好几个号，所以要让用户明确挑一个。
    /// 返回 null 表示取消；手动输入时昵称为空。
    /// </summary>
    public static async Task<QqAccount?> PickQqAccountAsync(
        IReadOnlyList<QqAccount> candidates, long? onlineUin, string? onlineNickname)
    {
        var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null)
            return null;

        var hint = onlineUin is > 0
            ? $"当前注入实例登录的是 {onlineUin}（{onlineNickname ?? "未知昵称"}）。" +
              "如果你有多个 QQ 号，请选这个班用的那个。"
            : "没有检测到在线的 QQ 实例。可以从用过的账号里选，或手动输入 QQ 号。";

        var labels = candidates
            .Select(a => $"{a.Uin}（{a.Nickname}）{(a.Uin == onlineUin ? "  · 当前在线" : "")}")
            .ToList();
        var list = new ListBox
        {
            ItemsSource = labels,
            SelectedIndex = labels.Count > 0 ? 0 : -1,
            MaxHeight = 200,
            MinWidth = 340
        };
        var manual = new TextBox { Watermark = "或手动输入 QQ 号", Margin = new Thickness(0, 8, 0, 0) };

        var content = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, MaxWidth = 380, Opacity = 0.85 },
                list,
                manual
            }
        };

        var dialog = new ContentDialog
        {
            Title = "选择班级 QQ 账号",
            Content = content,
            PrimaryButtonText = "使用这个账号",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

        ContentDialogResult result;
        try { result = await dialog.ShowAsync(owner); }
        catch { return null; }
        if (result != ContentDialogResult.Primary)
            return null;

        var typed = manual.Text?.Trim() ?? "";
        if (typed.Length > 0)
        {
            // 手输优先：用户明确输入的就是他要的（允许填非数字内容时直接判错）
            return long.TryParse(typed, out var uin) && uin > 0
                ? new QqAccount { Uin = uin, Nickname = "" }
                : null;
        }
        if (list.SelectedIndex >= 0 && list.SelectedIndex < candidates.Count)
            return candidates[list.SelectedIndex];
        return null;
    }

    private static ScrollViewer WrapText(string message) => new()
    {
        MaxHeight = 360,
        Content = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Left
        }
    };
}
