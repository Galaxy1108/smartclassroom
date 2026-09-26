using Avalonia.Headless.XUnit;
using SmartClassroom.App.ViewModels;
using SmartClassroom.Core;
using SmartClassroom.Core.AI;
using Xunit;

namespace SmartClassroom.App.Tests;

public sealed class SettingsPersistenceTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "sc-persist-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        foreach (var p in new[] { _path, _path + ".tmp", _path + ".bad" })
            if (File.Exists(p)) File.Delete(p);
    }

    // ================= 设置页与 Runtime 共用同一个设置对象 =================
    //
    // 真实事故：用户填好 API Key，重启（更新）后又没了。
    // 原因是设置页把 key 写进文件，而退出时 Runtime.Stop() 又拿**启动时装载的那份旧快照**
    // 覆盖了一遍同一个文件——设置页存了什么都会被冲掉。

    [AvaloniaFact]
    public void SharedInstance_SurvivesRuntimeSaveOnExit()
    {
        var shared = new AppSettings();                       // 相当于 Runtime.Settings
        SettingsStore.Save(shared, _path);

        var vm = new SettingsViewModel(_path, null, shared);
        vm.AiApiKey = "oc_sk_live";
        vm.SaveSettings();

        SettingsStore.Save(shared, _path);                    // ← Runtime.Stop() 的动作

        Assert.Equal("oc_sk_live", SettingsStore.Load(_path).AiApiKey);
        Assert.Equal("oc_sk_live", shared.AiApiKey);          // 同一份数据，不可能互相覆盖
    }

    [AvaloniaFact]
    public void LoadSettings_UsesSharedInstance()
    {
        SettingsStore.Save(new AppSettings { AiApiKey = "stale-on-disk" }, _path);
        var shared = new AppSettings { AiApiKey = "from-runtime", AiModel = "m1" };

        var vm = new SettingsViewModel(_path, null, shared);

        Assert.Equal("from-runtime", vm.AiApiKey);
        Assert.Equal("m1", vm.AiModel);
    }

    // ================= 手打的字段不用"碰巧触发别的保存"才落盘 =================

    [AvaloniaFact]
    public void FlushPendingSaves_WritesImmediately()
    {
        var shared = new AppSettings();
        var vm = new SettingsViewModel(_path, null, shared) { AiApiKey = "sk-flush" };

        Assert.NotEqual("sk-flush", SettingsStore.Load(_path).AiApiKey);   // 还在防抖窗口内
        vm.FlushPendingSaves();

        Assert.Equal("sk-flush", SettingsStore.Load(_path).AiApiKey);
    }

    [AvaloniaFact]
    public async Task TypedApiKey_AutoSavesAfterDebounce()
    {
        var shared = new AppSettings();
        var vm = new SettingsViewModel(_path, null, shared) { AiApiKey = "sk-auto" };

        await Task.Delay(1500);   // 防抖 600ms

        Assert.Equal("sk-auto", SettingsStore.Load(_path).AiApiKey);
    }

    [AvaloniaFact]
    public void FlushPendingSaves_WithNothingPending_DoesNotWrite()
    {
        var vm = new SettingsViewModel(_path);
        vm.FlushPendingSaves();
        Assert.False(File.Exists(_path));   // 没改过东西就不该凭空生成文件
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
