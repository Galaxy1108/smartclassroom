using Avalonia.Headless.XUnit;
using SmartClassroom.App.ViewModels;
using SmartClassroom.Core.AI;
using Xunit;

namespace SmartClassroom.App.Tests;

/// <summary>
/// 功能开关的前置依赖。
/// 没配 QQ / AI / ClassIsland 就允许打开开关，只会得到"看起来开着、其实什么也不做"的假象，
/// 所以这里锁住三件事：开关被禁用、页面给出缺什么、以及绕过界面直接赋值也会被拒绝。
/// </summary>
public sealed class FeatureGatingTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "sc-gate-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [AvaloniaFact]
    public void Defaults_EverythingIsBlocked_WithReasons()
    {
        var vm = new SettingsViewModel(_path);

        Assert.False(vm.QqReady);                 // 没填群号
        Assert.False(vm.AiReady);                 // 没填模型/地址
        Assert.False(vm.CanEnableSummon);
        Assert.False(vm.CanEnableHomework);
        Assert.False(vm.CanEnableExchange);
        Assert.False(vm.CanEnableFileArchive);
        Assert.False(vm.CanEnableCoursewarePopup);

        Assert.Contains("QQ 连接", vm.SummonGateHint);
        Assert.Contains("AI", vm.HomeworkGateHint);
        Assert.Contains("ClassIsland", vm.ExchangeGateHint);
        Assert.True(vm.HasMissingIntegration);
        Assert.Contains("QQ 连接", vm.IntegrationSummary);
    }

    [AvaloniaFact]
    public void EnablingFeatureWithoutIntegrations_IsRejected_AndNotSaved()
    {
        var vm = new SettingsViewModel(_path);

        vm.FeatureSummon = true;                  // 界面上开关是灰的，直接赋值也要拦住

        Assert.False(vm.FeatureSummon);
        Assert.False(new SettingsViewModel(_path).FeatureSummon);   // 不能偷偷落盘
        Assert.Contains("无法开启", vm.Log);
    }

    [AvaloniaFact]
    public void QqAndAiConfigured_UnlocksSummonHomeworkAndArchive()
    {
        var vm = new SettingsViewModel(_path)
        {
            GroupIdsText = "123456",
            AiBaseUrl = "https://api.deepseek.com/v1",
            AiModel = "deepseek-chat"
        };

        Assert.True(vm.QqReady);
        Assert.True(vm.AiReady);
        Assert.True(vm.CanEnableSummon);
        Assert.True(vm.CanEnableHomework);
        Assert.True(vm.CanEnableFileArchive);      // 归档只需要 QQ
        Assert.False(vm.CanEnableExchange);        // 换课还要 ClassIsland

        vm.FeatureSummon = true;
        vm.FeatureFileArchive = true;
        Assert.True(vm.FeatureSummon);
        Assert.True(vm.FeatureFileArchive);
        Assert.True(new SettingsViewModel(_path).FeatureSummon);
    }

    [AvaloniaFact]
    public void ClassIslandToken_UnlocksExchangeAndCoursewarePopup()
    {
        var vm = new SettingsViewModel(_path)
        {
            GroupIdsText = "123456",
            AiBaseUrl = "u",
            AiModel = "m",
            PluginToken = "tok-abc"
        };

        Assert.True(vm.ClassIslandReady);
        Assert.True(vm.CanEnableExchange);
        Assert.True(vm.CanEnableCoursewarePopup);

        vm.FeatureCoursewarePopup = true;
        Assert.True(vm.FeatureCoursewarePopup);
        Assert.False(vm.HasMissingIntegration);
    }

    [AvaloniaFact]
    public void LosingDependency_TurnsEnabledFeatureOff()
    {
        var vm = new SettingsViewModel(_path)
        {
            GroupIdsText = "123456",
            AiBaseUrl = "u",
            AiModel = "m"
        };
        vm.FeatureSummon = true;
        Assert.True(vm.FeatureSummon);

        vm.GroupIdsText = "";                     // 群号被清空 → QQ 不再就绪

        Assert.False(vm.CanEnableSummon);
        Assert.False(vm.FeatureSummon);           // 自动关掉，不留空转的开关
        Assert.False(new SettingsViewModel(_path).FeatureSummon);
        Assert.Contains("已自动关闭", vm.Log);
    }

    [AvaloniaFact]
    public void PiAiEngine_NeedsModelButNotBaseUrl()
    {
        var vm = new SettingsViewModel(_path) { GroupIdsText = "123456" };
        vm.EngineOption = vm.Engines.First(e => e.Engine == AiEngine.PiAiSidecar);
        vm.AiModel = "gpt-5";

        Assert.True(vm.AiReady);
        Assert.True(vm.CanEnableSummon);
    }
}
