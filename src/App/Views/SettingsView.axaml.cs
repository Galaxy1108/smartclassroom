using Avalonia.Controls;
using SmartClassroom.App.ViewModels;

namespace SmartClassroom.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        DataContext ??= new SettingsViewModel();
    }

    private SettingsViewModel Vm => (SettingsViewModel)DataContext!;

    private async void Refresh_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await Vm.RefreshReleasesAsync();

    private async void Download_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await Vm.DownloadSelectedAsync();

    private async void Start_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await Vm.StartAsync();

    private void Stop_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Vm.Stop();

    private async void Probe_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await Vm.ProbeAsync();
}
