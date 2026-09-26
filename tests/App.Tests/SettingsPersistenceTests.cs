using Avalonia.Headless.XUnit;
using SmartClassroom.App.ViewModels;
using SmartClassroom.Core.AI;
using Xunit;

namespace SmartClassroom.App.Tests;

public sealed class SettingsPersistenceTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "sc-persist-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [AvaloniaFact]
    public void AiEngine_PersistsAcrossReload()
    {
        var vm = new SettingsViewModel(_path);
        Assert.Equal(AiEngine.HttpGateway, vm.AiEngine);

        vm.EngineOption = vm.Engines.First(e => e.Engine == AiEngine.PiAiSidecar);
        Assert.True(vm.IsPiAi);
        Assert.True(vm.ShowSidecarSettings);

        var reloaded = new SettingsViewModel(_path);
        Assert.Equal(AiEngine.PiAiSidecar, reloaded.AiEngine);
        Assert.True(reloaded.IsPiAi);
    }

    [AvaloniaFact]
    public void PluginBridge_PersistsPortAndToken()
    {
        var vm = new SettingsViewModel(_path) { PluginPort = 6201, PluginToken = "tok-abc" };
        vm.SaveSettings();

        var reloaded = new SettingsViewModel(_path);
        Assert.Equal(6201, reloaded.PluginPort);
        Assert.Equal("tok-abc", reloaded.PluginToken);
    }

    [AvaloniaFact]
    public void AiProviderAndModel_PersistFromDirectEngine()
    {
        var vm = new SettingsViewModel(_path)
        {
            AiBaseUrl = "https://api.deepseek.com/v1",
            AiModel = "deepseek-chat",
            AiApiKey = "sk-x"
        };
        vm.SaveSettings();

        var reloaded = new SettingsViewModel(_path);
        Assert.Equal("https://api.deepseek.com/v1", reloaded.AiBaseUrl);
        Assert.Equal("deepseek-chat", reloaded.AiModel);
        Assert.Equal("sk-x", reloaded.AiApiKey);
    }
}
