using Avalonia.Headless.XUnit;
using SmartClassroom.App.ViewModels;
using SmartClassroom.App.Views;
using SmartClassroom.Core;
using Xunit;

namespace SmartClassroom.App.Tests;

/// <summary>
/// 应用内通知（右下角浮出）与状态徽标。
///
/// 起因：启动/停止 SnowLuma、下载 Node 这些操作要好几秒，完成后只有设置页里一行小字，
/// 用户看不到 → 以为失败；状态又只有纯文本（「已注入 / 服务未就绪」）→ 看不出成功没有。
/// </summary>
public sealed class ToastAndStatusTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "sc-toast-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        Toasts.Items.Clear();
        foreach (var p in new[] { _path, _path + ".tmp", _path + ".bad" })
            if (File.Exists(p)) File.Delete(p);
    }

    [AvaloniaFact]
    public void Show_AddsToast_AndRemoveTakesItAway()
    {
        Toasts.Items.Clear();

        Toasts.Success("SnowLuma 已停止", "注入已关闭");

        var item = Assert.Single(Toasts.Items);
        Assert.Equal("SnowLuma 已停止", item.Title);
        Assert.Equal(NoticeSeverity.Success, item.Severity);

        Toasts.Remove(item);
        Assert.Empty(Toasts.Items);
    }

    [AvaloniaFact]
    public void Show_KeepsNewestLast_SoTheyStackDownwards()
    {
        Toasts.Items.Clear();
        Toasts.Success("第一条");
        Toasts.Error("第二条");

        Assert.Equal(["第一条", "第二条"], Toasts.Items.Select(t => t.Title));
    }

    // ================= 状态徽标：三态可辨 =================

    [AvaloniaFact]
    public void StatusBadge_MapsSeverityToDistinctIcon()
    {
        var badge = new StatusBadge { Severity = NoticeSeverity.Success, Text = "在线" };
        Assert.True(badge.CheckIcon.IsVisible);      // 对钩
        Assert.False(badge.WarnIcon.IsVisible);
        Assert.False(badge.ErrorIcon.IsVisible);

        badge.Severity = NoticeSeverity.Warning;
        Assert.True(badge.WarnIcon.IsVisible);       // 感叹号
        Assert.False(badge.CheckIcon.IsVisible);

        badge.Severity = NoticeSeverity.Error;
        Assert.True(badge.ErrorIcon.IsVisible);      // 叉
        Assert.False(badge.WarnIcon.IsVisible);
        Assert.Equal("在线", badge.Label.Text);
    }

    [AvaloniaFact]
    public async Task Probe_NotInstalled_IsErrorNotAmbiguousText()
    {
        var vm = new SettingsViewModel(_path) { InstallDir = Path.Combine(Path.GetTempPath(), "sc-no-snowluma-" + Guid.NewGuid().ToString("N")) };
        Toasts.Items.Clear();

        await vm.ProbeAsync();

        Assert.Equal(NoticeSeverity.Error, vm.QqStatusSeverity);
        Assert.Contains("未安装", vm.QqStatusText);
        Assert.Contains(Toasts.Items, t => t.Title.Contains("未安装 SnowLuma"));
    }

    [AvaloniaFact]
    public void BusyState_InitiallyIdle_WithPlainButtonLabels()
    {
        var vm = new SettingsViewModel(_path);
        Assert.False(vm.IsQqBusy);
        Assert.Equal("启动", vm.StartButtonText);
        Assert.Equal("停止", vm.StopButtonText);
        Assert.Equal("探测", vm.ProbeButtonText);
    }

    // ================= 风险确认改成弹窗 =================

    [AvaloniaFact]
    public void AcceptRisk_FlipsBadgeAndPersists()
    {
        var vm = new SettingsViewModel(_path);
        Assert.False(vm.RiskAccepted);
        Assert.True(vm.NeedsRiskConfirmation);
        Assert.Equal(NoticeSeverity.Warning, vm.RiskBadgeSeverity);
        Assert.Contains("尚未确认", vm.RiskBadgeText);

        vm.AcceptRisk();

        Assert.True(vm.RiskAccepted);
        Assert.False(vm.NeedsRiskConfirmation);
        Assert.Equal(NoticeSeverity.Success, vm.RiskBadgeSeverity);
        Assert.Equal("已确认风险", vm.RiskBadgeText);
        Assert.True(new SettingsViewModel(_path).RiskAccepted);
    }

    // ================= QQ 账号选择 =================

    [AvaloniaFact]
    public void MergeCandidate_DedupsAndRefreshesNickname()
    {
        var vm = new SettingsViewModel(_path);

        vm.MergeCandidate(new QqAccount { Uin = 10001, Nickname = "一班班号" });
        vm.MergeCandidate(new QqAccount { Uin = 10001, Nickname = "一班班号" });
        vm.MergeCandidate(new QqAccount { Uin = 10001, Nickname = "改过的昵称" });
        vm.MergeCandidate(new QqAccount { Uin = 10002, Nickname = "" });
        vm.MergeCandidate(new QqAccount { Uin = 0 });          // 非法账号忽略

        Assert.Equal(2, vm.QqCandidates.Count);
        Assert.Equal("改过的昵称", vm.QqCandidates[0].Nickname);
    }

    [AvaloniaFact]
    public void ApplyQqAccount_SelectsPersistsAndLabels()
    {
        var vm = new SettingsViewModel(_path);

        vm.ApplyQqAccount(new QqAccount { Uin = 10001, Nickname = "一班班号" });

        Assert.Equal(10001, vm.QqAccount);
        Assert.Contains("10001", vm.QqAccountLabel);
        Assert.Contains("一班班号", vm.QqAccountLabel);

        var reloaded = new SettingsViewModel(_path);
        Assert.Equal(10001, reloaded.QqAccount);
        Assert.Single(reloaded.QqCandidates);
        Assert.Contains("一班班号", reloaded.QqAccountLabel);
    }

    [AvaloniaFact]
    public void NoAccountSelected_SaysWhyItMatters()
    {
        var vm = new SettingsViewModel(_path);
        Assert.Equal(0, vm.QqAccount);
        Assert.Equal("未选择", vm.QqAccountLabel);
    }

    // ================= 首次启动的风险警告 =================

    [AvaloniaFact]
    public void RiskNotice_ShowsOnlyOnce()
    {
        var settings = new AppSettings();
        Assert.True(RiskNotice.ShouldShow(settings));    // 全新安装：要弹

        settings.RiskWarningShown = true;
        Assert.False(RiskNotice.ShouldShow(settings));   // 弹过就不再自动弹

        settings.ResetToDefaults();
        Assert.True(RiskNotice.ShouldShow(settings));    // 清空设置后又该弹
    }

    [AvaloniaFact]
    public void SyncFromShared_PicksUpRiskAcceptedElsewhere()
    {
        // 启动时弹窗接受 → 写的是共享对象；设置页必须能同步到这个状态，
        // 否则会出现"已经接受了，设置页还显示尚未确认、点启动又弹一次"
        var shared = new AppSettings();
        var vm = new SettingsViewModel(_path, null, shared);
        Assert.True(vm.NeedsRiskConfirmation);

        shared.RiskAccepted = true;
        vm.SyncFromShared();

        Assert.True(vm.RiskAccepted);
        Assert.False(vm.NeedsRiskConfirmation);
        Assert.Equal("已确认风险", vm.RiskBadgeText);
    }

    // ================= OneBot Token 也要能填 =================

    [AvaloniaFact]
    public void OneBotToken_Persists()
    {
        var vm = new SettingsViewModel(_path) { OneBotToken = "tok-123" };
        vm.SaveSettings();

        Assert.Equal("tok-123", new SettingsViewModel(_path).OneBotToken);
    }
}
