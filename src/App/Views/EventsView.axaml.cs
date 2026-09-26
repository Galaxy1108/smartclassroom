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

    private async void Retry_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is PendingRow row)
            await Vm.RetryAsync(row);
    }

    private void Edit_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is PendingRow row)
            Vm.BeginEdit(row);
    }

    private void Ignore_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is PendingRow row)
            Vm.Ignore(row);
    }

    private async void Submit_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await Vm.SubmitEditAsync();

    private void Cancel_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Vm.CancelEdit();
}
