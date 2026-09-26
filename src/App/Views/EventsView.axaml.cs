using Avalonia.Controls;
using SmartClassroom.App.ViewModels;

namespace SmartClassroom.App.Views;

public partial class EventsView : UserControl
{
    public EventsView()
    {
        InitializeComponent();
        DataContext ??= new EventsViewModel();
    }

    private EventsViewModel Vm => (EventsViewModel)DataContext!;

    /// <summary>受保护操作：处理待确认事项需要管理员密码。</summary>
    private static Task<bool> AuthorizeAsync(string action)
        => Runtime.Auth.RequireAsync(
            reason => PasswordDialog.PromptAsync(action, reason),
            action + "需要管理员密码");

    private async void Retry_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not PendingRow row)
            return;
        if (!await AuthorizeAsync("重新解析"))
            return;
        await Vm.RetryAsync(row);
        Runtime.SaveState();
    }

    private void Edit_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is PendingRow row)
            Vm.BeginEdit(row);
    }

    private async void Ignore_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not PendingRow row)
            return;
        if (!await AuthorizeAsync("忽略待确认事项"))
            return;
        Vm.Ignore(row);
        Runtime.SaveState();
    }

    private async void Submit_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!await AuthorizeAsync("提交人工录入"))
            return;
        await Vm.SubmitEditAsync();
        Runtime.SaveState();
    }

    private void Cancel_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Vm.CancelEdit();

    // ================= 决策时间线：复制 / 清空 =================

    private async void CopyEntry_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ActivityRow row)
            return;
        await CopyAsync(Vm.CopyEntryText(row), "已复制这条记录。");
    }

    private async void CopyAll_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await CopyAsync(Vm.CopyAllText(), $"已复制 {Vm.Entries.Count} 条记录。");

    private void ClearTimeline_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var n = Vm.ClearTimeline();
        Runtime.SaveState();   // 清空立刻落盘，否则要等 30 秒定时器或退出
        Vm.Report($"已清空决策时间线（{n} 条）。");
    }

    /// <summary>写剪贴板；失败只回报一句，不影响其它操作。</summary>
    private async Task CopyAsync(string text, string okMessage)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
            {
                Vm.Report("当前环境没有剪贴板，无法复制（可手动选中文本复制）。");
                return;
            }
            await clipboard.SetTextAsync(text);
            Vm.Report(okMessage);
        }
        catch (Exception ex)
        {
            Vm.Report($"复制失败：{App.Describe(ex)}");
        }
    }
}
