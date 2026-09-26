using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;
using SmartClassroom.Core;
using SmartClassroom.Core.QQ;

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
            : candidates.Count > 0
                // 有候选就别说"没检测到"——这些就是从 SnowLuma 日志里读出来的登录账号
                ? $"OneBot 暂时没应答，但从 SnowLuma 的日志里读到了这些登录过的账号" +
                  "（当前在线的那个可能还没起来）。选一个，或手动输入。"
                : "没有检测到在线的 QQ 实例。可以手动输入 QQ 号。";

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

    /// <summary>
    /// 展示 SnowLuma 的用户协议 / 隐私政策并征得同意。
    /// 正文来自 SnowLuma 自己的 /api/agreements（不是我们转述），同意后由它自己记录。
    /// </summary>
    public static async Task<bool> ConsentAsync(IReadOnlyList<SnowlumaAgreement> documents)
    {
        var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null)
            return false;

        var selector = new ComboBox
        {
            ItemsSource = documents.Select(d => d.Title.Length > 0 ? d.Title : d.Id).ToList(),
            SelectedIndex = 0,
            MinWidth = 200,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        var body = new SelectableTextBlock
        {
            Text = documents.FirstOrDefault()?.Text ?? "",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.9
        };
        selector.SelectionChanged += (_, _) =>
        {
            var idx = selector.SelectedIndex;
            if (idx >= 0 && idx < documents.Count)
                body.Text = documents[idx].Text;
        };

        var content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = "SnowLuma 需要先同意以下协议才会注入 QQ。请阅读后选择。",
                    TextWrapping = TextWrapping.Wrap, MaxWidth = 520, Opacity = 0.85
                },
                selector,
                new ScrollViewer
                {
                    Height = 300,
                    MinWidth = 520,
                    Content = body,
                    Background = Brushes.Transparent
                }
            }
        };

        var dialog = new ContentDialog
        {
            Title = "SnowLuma 用户协议与隐私政策",
            Content = content,
            PrimaryButtonText = "同意并继续",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        try
        {
            return await dialog.ShowAsync(owner) == ContentDialogResult.Primary;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 让用户输入一段文本（如 SnowLuma 的 WebUI 密码）。
    /// 返回 null = 取消；返回 "" = 明确要清空（改回自动生成）。
    /// </summary>
    public static async Task<string?> PromptAsync(string title, string message,
        string watermark = "", bool password = false, string primaryText = "保存")
    {
        var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null)
            return null;

        var box = new TextBox
        {
            Watermark = watermark,
            MinWidth = 320,
            PasswordChar = password ? '•' : default
        };
        var content = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 420, Opacity = 0.85 },
                box
            }
        };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            PrimaryButtonText = primaryText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        try
        {
            return await dialog.ShowAsync(owner) == ContentDialogResult.Primary ? box.Text ?? "" : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 从群列表里多选要监听的群（省得手打群号）。返回 null = 取消。
    /// </summary>
    public static async Task<IReadOnlyList<long>?> PickGroupsAsync(
        IReadOnlyList<GroupInfoData> groups, IReadOnlyCollection<long> selected)
    {
        var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null)
            return null;

        var labels = groups
            .Select(g => $"{g.GroupName}（{g.GroupId}）· {g.MemberCount} 人")
            .ToList();
        var list = new ListBox
        {
            ItemsSource = labels,
            SelectionMode = SelectionMode.Multiple,
            MaxHeight = 320,
            MinWidth = 420
        };
        for (var i = 0; i < groups.Count; i++)
        {
            if (selected.Contains(groups[i].GroupId))
                list.SelectedItems!.Add(labels[i]);
        }

        var content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = "勾选要监听的群（只处理这些群的消息）。班级号所在的群可能很多，"
                         + "建议只勾班级群，避免在无关群里触发 AI 与通知。",
                    TextWrapping = TextWrapping.Wrap, MaxWidth = 440, Opacity = 0.85
                },
                list
            }
        };
        var dialog = new ContentDialog
        {
            Title = "选择要监听的群",
            Content = content,
            PrimaryButtonText = "使用选中的群",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

        ContentDialogResult result;
        try { result = await dialog.ShowAsync(owner); }
        catch { return null; }
        if (result != ContentDialogResult.Primary)
            return null;

        var picked = new List<long>();
        foreach (var item in list.SelectedItems ?? new List<object>())
        {
            var idx = labels.IndexOf(item as string ?? "");
            if (idx >= 0)
                picked.Add(groups[idx].GroupId);
        }
        return picked;
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
