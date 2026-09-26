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
}
