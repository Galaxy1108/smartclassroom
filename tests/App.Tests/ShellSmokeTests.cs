using Avalonia.Headless.XUnit;
using SmartClassroom.App.ViewModels;
using SmartClassroom.App.Views;
using Xunit;

namespace SmartClassroom.App.Tests;

/// <summary>v0.1 Shell冒烟：主窗口与主视图模型在无头环境下可实例化。</summary>
public sealed class ShellSmokeTests
{
    [AvaloniaFact]
    public void MainWindow_OpensWithTitle()
    {
        TestSetup.EnsureApp();
        var window = new MainWindow { DataContext = new MainViewModel() };
        window.Show();
        Assert.Equal("智慧课堂", window.Title);
        Assert.Equal("智慧课堂", ((MainViewModel)window.DataContext).Title);
        window.Navigate("events");
        window.Navigate("settings");
        window.Close();
    }
}
