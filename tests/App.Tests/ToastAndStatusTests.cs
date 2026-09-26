using Avalonia.Headless.XUnit;
using SmartClassroom.App;
using SmartClassroom.App.ViewModels;
using SmartClassroom.App.Views;
using SmartClassroom.Core;
using SmartClassroom.Core.QQ;
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

        vm.AcceptRisk();

        Assert.True(vm.RiskAccepted);
        Assert.True(new SettingsViewModel(_path).RiskAccepted);   // 落盘，重启不再问
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

    // ================= SnowLuma 协议同意 =================
    //
    // SnowLuma 停在"等待同意"时不会注入，OneBot 也不会起来 —— 用户只会看到"检测不到账号"。
    // 所以启动前必须把协议正文给用户看并征得同意。

    private void WriteSnowLumaDocs(string installDir, string eula = "# 用户协议\n\n第一条。")
    {
        Directory.CreateDirectory(installDir);
        File.WriteAllText(Path.Combine(installDir, "EULA.md"), eula);
        File.WriteAllText(Path.Combine(installDir, "PRIVACY.md"), "# 隐私政策\n\n只在本机处理。");
    }

    [AvaloniaFact]
    public async Task Agreements_PromptsOnce_ThenRemembers()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sc-agree-" + Guid.NewGuid().ToString("N"));
        WriteSnowLumaDocs(dir);
        try
        {
            var vm = new SettingsViewModel(_path) { InstallDir = dir };
            var prompted = 0;
            IReadOnlyList<SnowlumaAgreement>? shown = null;
            vm.ConsentPrompt = docs => { prompted++; shown = docs; return Task.FromResult(true); };

            Assert.True(await vm.EnsureAgreementsAcceptedAsync());
            Assert.Equal(1, prompted);
            Assert.Equal(2, shown!.Count);                       // 用户协议 + 隐私政策都给了
            Assert.False(vm.NeedsWebUiSetup);

            // 同一个版本再启动：不再弹
            var again = new SettingsViewModel(_path) { InstallDir = dir };
            var second = 0;
            again.ConsentPrompt = _ => { second++; return Task.FromResult(true); };
            Assert.True(await again.EnsureAgreementsAcceptedAsync());
            Assert.Equal(0, second);
        }
        finally { Directory.Delete(dir, true); }
    }

    [AvaloniaFact]
    public async Task Agreements_Declined_BlocksStart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sc-agree-no-" + Guid.NewGuid().ToString("N"));
        WriteSnowLumaDocs(dir);
        try
        {
            var vm = new SettingsViewModel(_path) { InstallDir = dir };
            vm.ConsentPrompt = _ => Task.FromResult(false);

            Assert.False(await vm.EnsureAgreementsAcceptedAsync());
            Assert.True(vm.NeedsWebUiSetup);                     // 界面提示"需要同意协议"
        }
        finally { Directory.Delete(dir, true); }
    }

    [AvaloniaFact]
    public async Task Agreements_TextChanged_AsksAgain()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sc-agree-chg-" + Guid.NewGuid().ToString("N"));
        WriteSnowLumaDocs(dir);
        try
        {
            var vm = new SettingsViewModel(_path) { InstallDir = dir };
            vm.ConsentPrompt = _ => Task.FromResult(true);
            Assert.True(await vm.EnsureAgreementsAcceptedAsync());

            // SnowLuma 更新了条款 → 指纹变 → 必须重新征得同意
            WriteSnowLumaDocs(dir, eula: "# 用户协议\n\n第一条。新增：第二条。");
            var after = new SettingsViewModel(_path) { InstallDir = dir };
            var prompted = 0;
            after.ConsentPrompt = _ => { prompted++; return Task.FromResult(true); };
            Assert.True(await after.EnsureAgreementsAcceptedAsync());
            Assert.Equal(1, prompted);
        }
        finally { Directory.Delete(dir, true); }
    }

    [AvaloniaFact]
    public async Task Agreements_MissingFiles_DoesNotPretendAccepted()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sc-agree-none-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var vm = new SettingsViewModel(_path) { InstallDir = dir };
            var prompted = 0;
            vm.ConsentPrompt = _ => { prompted++; return Task.FromResult(true); };

            Assert.False(await vm.EnsureAgreementsAcceptedAsync());   // 读不到协议就不启动
            Assert.Equal(0, prompted);
            Assert.True(vm.NeedsWebUiSetup);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// 协议弹窗必须接到**真实的**视图模型上。
    /// 踩过的坑：接线写在 SettingsView 构造函数里，而外部是
    /// `new SettingsView { DataContext = vm }`——构造时 DataContext 还是 null，
    /// 接线接到了兜底的临时实例上，结果点「启动」根本不弹窗，只显示"未同意协议"。
    /// </summary>
    [AvaloniaFact]
    public void SettingsView_WiresConsentPromptToTheRealViewModel()
    {
        var vm = new SettingsViewModel(_path);
        Assert.Null(vm.ConsentPrompt);

        _ = new SettingsView { DataContext = vm };

        Assert.NotNull(vm.ConsentPrompt);
    }

    // ================= 单实例 =================

    [AvaloniaFact]
    public void AlreadyRunningNotice_DoesNotShowInTaskbar()
    {
        // 进任务栏的话，用户会看到第二个图标，以为"多开没被拦住"
        var notice = SingleInstance.CreateAlreadyRunningNotice();
        Assert.False(notice.ShowInTaskbar);
        Assert.Contains("已在运行", notice.Title);
    }

    [AvaloniaFact]
    public void SingleInstance_SecondAcquireIsBlocked()
    {
        // 多开会抢同一个 SnowLuma 进程、同一个桥接端口、同一份 state.json。
        // 用独立的锁文件与互斥量名：本机可能正跑着应用，别受它影响。
        var lockPath = Path.Combine(Path.GetTempPath(), "sc-si-" + Guid.NewGuid().ToString("N") + ".lock");
        var mutexName = "sc-test-" + Guid.NewGuid().ToString("N");
        try
        {
            Assert.Null(SingleInstance.TryAcquire(lockPath, mutexName));     // 第一个：抢到
            Assert.NotNull(SingleInstance.TryAcquire(lockPath, mutexName));  // 第二个：必须被挡住
        }
        finally
        {
            SingleInstance.ReleaseLocks();
            if (File.Exists(lockPath)) File.Delete(lockPath);
        }
    }

    [AvaloniaFact]
    public void SingleInstance_LockFileAloneAlsoBlocks()
    {
        // 命名互斥量在 Unix 上跟"持有它的线程"绑死，线程一退就失效（实测被它坑过：
        // 用户机器上两个实例同时在跑）。所以锁文件是主力，单独测一遍。
        var path = Path.Combine(Path.GetTempPath(), "sc-lock-" + Guid.NewGuid().ToString("N") + ".lock");
        try
        {
            Assert.True(SingleInstance.TryAcquireLockFile(path));
            Assert.False(SingleInstance.TryAcquireLockFile(path));      // 已被自己占着
            SingleInstance.ReleaseLocks();
            Assert.True(SingleInstance.TryAcquireLockFile(path));       // 释放后又能抢
            SingleInstance.ReleaseLocks();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
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
