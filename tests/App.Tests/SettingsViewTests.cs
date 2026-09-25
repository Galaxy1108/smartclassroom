using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using SmartClassroom.App.ViewModels;
using SmartClassroom.App.Views;
using Xunit;

namespace SmartClassroom.App.Tests;

public sealed class SettingsViewTests
{
    [AvaloniaFact]
    public void SettingsView_BuildsWithDefaults()
    {
        var vm = new SettingsViewModel();
        var view = new SettingsView { DataContext = vm };
        var window = new Window { Content = view };
        window.Show();
        Assert.Contains("127.0.0.1:3000", vm.OneBotHttp);
        Assert.Contains("snowluma", vm.InstallDir);
        window.Close();
    }
}
