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

    // ---- AI ----
    private async void LoadCatalog_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await Vm.LoadCatalogAsync();

    private async void TestAi_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await Vm.TestAiAsync();

    private void RefreshSidecar_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Vm.RefreshSidecarInfo();

    // ---- ClassIsland 集成 ----
    private async void ProbePlugin_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await Vm.ProbePluginAsync();

    private void PluginTokenHelp_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Vm.ShowPluginTokenHelp();

    // ---- 教师映射 ----
    private void Save_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Vm.SaveSettings();

    private void AddTeacher_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Vm.AddTeacher();

    private void RemoveTeacher_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is TeacherRow row)
            Vm.RemoveTeacher(row);
    }

    // ---- SnowLuma ----
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

    private void AutoConnect_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Vm.AutoConnect();
}
