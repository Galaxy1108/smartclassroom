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
        var vm = new SettingsViewModel(_path) { TokenLocator = () => null };

        Assert.False(vm.QqReady);                 // 没填群号
        Assert.False(vm.AiReady);                 // 没填模型/地址
        Assert.False(vm.CanEnableSummon);
        Assert.False(vm.CanEnableHomework);
        Assert.False(vm.CanEnableExchange);
        Assert.False(vm.CanEnableFileArchive);
        Assert.False(vm.CanEnableCoursewarePopup);

        // 缺哪一项就显示哪一项
        Assert.Contains("监听群号", vm.SummonGateHint);
        Assert.Contains("AI", vm.HomeworkGateHint);
        Assert.Contains("ClassIsland", vm.ExchangeGateHint);
        Assert.Contains("监听群号", vm.ArchiveGateHint);
        Assert.Contains("ClassIsland", vm.CoursewareGateHint);
    }

    /// <summary>
    /// 只差"监听群号"时，不该只是把开关变灰干等着 —— 直接把选群弹窗推给用户，
    /// 选完自动把刚才想开的开关打开（"在线了却开不了"最让人困惑）。
    /// </summary>
    [AvaloniaFact]
    public async Task EnablingFeature_WithoutGroups_AsksForGroups_ThenTurnsOn()
    {
        var vm = new SettingsViewModel(_path)
        {
            TokenLocator = () => null,
            OneBotHttp = "http://127.0.0.1:3000",   // 在线信息有了，只差群
            AiBaseUrl = "u",
            AiModel = "m"
        };
        var asked = 0;
        vm.GroupPicker = () =>
        {
            asked++;
            vm.ApplyGroups([1077826412]);          // 用户选了班级群
            return Task.FromResult(true);
        };

        vm.FeatureSummon = true;                   // 界面上这个开关本来是灰的
        await Task.Delay(50);                      // 让注入的异步回调跑完

        Assert.Equal(1, asked);
        Assert.Equal("1077826412", vm.GroupIdsText);
        Assert.True(vm.FeatureSummon);             // 选完群后自动打开
    }

    /// <summary>
    /// 装 ClassIsland 的目的就是用它的语音播报 —— 没连上时虽然能降级（应用内通知），
    /// 但必须明说"没有语音播报"，而不是悄悄降级。
    /// </summary>
    [AvaloniaFact]
    public async Task SummonWithoutClassIsland_WarnsAboutMissingVoice()
    {
        var vm = new SettingsViewModel(_path) { TokenLocator = () => null, PluginToken = "" };
        vm.RefreshIntegrationState();

        Assert.False(vm.ClassIslandReady);
        Assert.True(vm.HasSummonVoiceHint);
        Assert.Contains("没有语音播报", vm.SummonVoiceHint);
        Assert.True(vm.PluginStatus.Length <= 4, $"状态行要短，别被截断：{vm.PluginStatus}");

        // 探测失败后：状态仍然短，排查细节挪到 Tooltip
        vm.PluginToken = "tok";
        vm.PluginPort = 1;                       // 必然连不上
        await vm.ProbePluginAsync();
        Assert.Equal("未连接", vm.PluginStatus);
        Assert.Contains("请确认", vm.PluginStatusDetail);

        vm.PluginToken = "tok";
        vm.RefreshIntegrationState();
        Assert.False(vm.HasSummonVoiceHint);
        Assert.True(vm.ClassIslandReady);
    }

    /// <summary>
    /// 只监听老师私聊也是合法配置 —— 用户要的就是私聊，不该逼他选群。
    /// </summary>
    [AvaloniaFact]
    public void PrivateOnly_CountsAsQqReady()
    {
        var vm = new SettingsViewModel(_path)
        {
            TokenLocator = () => null,
            OneBotHttp = "http://127.0.0.1:3000",
            AiBaseUrl = "u",
            AiModel = "m"
        };
        Assert.False(vm.QqReady);                 // 没群也没私聊

        vm.ListenTeacherPrivate = true;
        Assert.True(vm.QqReady);                  // 私聊够了
        Assert.Equal("", vm.SummonGateHint);      // 召唤可以开

        vm.ListenTeacherPrivate = false;
        Assert.False(vm.QqReady);
        Assert.Contains("监听群号或老师私聊", vm.SummonGateHint);   // 说清两种选择
    }

    [AvaloniaFact]
    public void EnablingFeatureWithoutIntegrations_IsRejected_AndNotSaved()
    {
        var vm = new SettingsViewModel(_path) { TokenLocator = () => null };

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
            TokenLocator = () => null,
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
            TokenLocator = () => null,
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
        Assert.True(vm.CanEnableCoursewarePopup);
    }

    [AvaloniaFact]
    public void LosingDependency_TurnsEnabledFeatureOff()
    {
        var vm = new SettingsViewModel(_path)
        {
            TokenLocator = () => null,
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
        var vm = new SettingsViewModel(_path) { TokenLocator = () => null, GroupIdsText = "123456" };
        vm.EngineOption = vm.Engines.First(e => e.Engine == AiEngine.PiAiSidecar);
        vm.AiModel = "gpt-5";

        Assert.True(vm.AiReady);
        Assert.True(vm.CanEnableSummon);
    }
}
