using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;

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
            Content = new ScrollViewer
            {
                MaxHeight = 360,
                Content = new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    HorizontalAlignment = HorizontalAlignment.Left
                }
            },
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
}
