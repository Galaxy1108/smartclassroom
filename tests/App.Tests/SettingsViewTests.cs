using Avalonia.Headless.XUnit;
using SmartClassroom.App.ViewModels;
using SmartClassroom.Core.AI;
using SmartClassroom.App.Views;
using Xunit;

namespace SmartClassroom.App.Tests;

public sealed class SettingsViewTests
{
    [AvaloniaFact]
    public void SettingsView_BuildsWithDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), "sc-set-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var vm = new SettingsViewModel(path);
            var view = new SettingsView { DataContext = vm };

            Assert.Contains("127.0.0.1:3000", vm.OneBotHttp);
            Assert.Contains("snowluma", vm.InstallDir);
            Assert.Same(vm, view.DataContext);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void SettingsViewModel_EngineDefaultsToHttpGateway()
    {
        var path = Path.Combine(Path.GetTempPath(), "sc-set-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var vm = new SettingsViewModel(path);
            Assert.Equal(AiEngine.HttpGateway, vm.AiEngine);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
