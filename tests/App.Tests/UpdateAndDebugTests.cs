using System.Net;
using Avalonia.Headless.XUnit;
using SmartClassroom.App.ViewModels;
using SmartClassroom.App.Views;
using SmartClassroom.Core;
using SmartClassroom.Core.Updates;
using Xunit;

namespace SmartClassroom.App.Tests;

/// <summary>
/// 软件更新 + 调试（清空所有设置）。
/// 更新检查用注入的假 GitHub 响应，不碰网络。
/// </summary>
public sealed class UpdateAndDebugTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "sc-upd-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        Toasts.Items.Clear();
        foreach (var p in new[] { _path, _path + ".tmp", _path + ".bad" })
            if (File.Exists(p)) File.Delete(p);
    }

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> fn) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken t)
            => Task.FromResult(fn(req));
    }

    private int _requests;

    private SettingsViewModel Vm(string releaseJson, AppSettings? shared = null)
    {
        var http = new HttpClient(new Stub(_ =>
        {
            _requests++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(releaseJson) };
        }));
        return new SettingsViewModel(_path, null, shared, new UpdateChecker(http));
    }

    private const string NewerRelease = """
        {"tag_name":"v9.9.9","name":"v9.9.9","html_url":"https://github.com/x/y/releases/tag/v9.9.9",
         "body":"新版本说明",
         "assets":[{"name":"smartclassroom-9.9.9-win-x64.zip","browser_download_url":"https://dl/win.zip"}]}
        """;

    private const string SameRelease = """{"tag_name":"v0.0.1","assets":[]}""";

    // ================= 更新检查 =================

    [AvaloniaFact]
    public async Task CheckForUpdates_NewerRelease_ShowsUpdate()
    {
        var vm = Vm(NewerRelease);
        Toasts.Items.Clear();

        await vm.CheckForUpdatesAsync();

        Assert.True(vm.HasUpdate);
        Assert.Equal(NoticeSeverity.Warning, vm.UpdateSeverity);
        Assert.Contains("发现新版本 9.9.9", vm.UpdateStatusText);
        Assert.Contains("9.9.9", vm.LatestVersionText);
        Assert.True(vm.HasReleasePage);
        Assert.Contains(Toasts.Items, t => t.Title == "发现新版本");
    }

    [AvaloniaFact]
    public async Task CheckForUpdates_UpToDate_IsSuccess()
    {
        // 远端 tag 比当前版本旧 → 已是最新
        var vm = Vm(SameRelease);
        Toasts.Items.Clear();

        await vm.CheckForUpdatesAsync();

        Assert.False(vm.HasUpdate);
        Assert.Equal(NoticeSeverity.Success, vm.UpdateSeverity);
        Assert.Equal("已是最新版本", vm.UpdateStatusText);
        Assert.Contains(Toasts.Items, t => t.Title == "已是最新版本");
    }

    [AvaloniaFact]
    public async Task CheckForUpdates_Unreachable_IsWarningNotCrash()
    {
        var http = new HttpClient(new Stub(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)));
        var vm = new SettingsViewModel(_path, null, null, new UpdateChecker(http));

        await vm.CheckForUpdatesAsync();

        Assert.False(vm.HasUpdate);
        Assert.Equal(NoticeSeverity.Warning, vm.UpdateSeverity);
        Assert.Contains("检查失败", vm.UpdateStatusText);
    }

    [AvaloniaFact]
    public async Task CanInstallUpdate_IsDisabledOnLinux()
    {
        var vm = Vm(NewerRelease);
        await vm.CheckForUpdatesAsync();

        // Linux：应用由包管理器安装，绝不允许自己替换 /opt 下的文件
        Assert.False(vm.CanSelfUpdate);
        Assert.False(vm.CanInstallUpdate);
        Assert.Equal("Linux 平台暂不支持应用内自动更新", vm.UpdatePlatformHint);
    }

    [AvaloniaFact]
    public void CurrentVersion_ComesFromAssembly()
    {
        var vm = new SettingsViewModel(_path);
        Assert.Contains("当前版本", vm.CurrentVersionText);
        Assert.Matches(@"\d+\.\d+\.\d+", vm.CurrentVersionText);
    }

    [AvaloniaFact]
    public async Task AutoCheck_RunsOnlyOncePerProcess()
    {
        var vm = Vm(NewerRelease);

        await vm.CheckForUpdatesOnceAsync();
        await vm.CheckForUpdatesOnceAsync();
        await vm.CheckForUpdatesOnceAsync();

        Assert.Equal(1, _requests);   // 无认证 GitHub API 每小时只有 60 次，别反复打
        Assert.True(vm.HasUpdate);

        // 手动点「检查更新」不受限制
        await vm.CheckForUpdatesAsync();
        Assert.Equal(2, _requests);
    }

    // ================= Windows 覆盖脚本 =================

    [AvaloniaFact]
    public void UpdateScript_WaitsForExit_CopiesAndRestarts()
    {
        var root = Path.Combine(Path.GetTempPath(), "sc-updscript-" + Guid.NewGuid().ToString("N"));
        var stage = Path.Combine(root, "update", "9.9.9");
        var appDir = Path.Combine(root, "app dir");   // 带空格，必须被引号包住
        Directory.CreateDirectory(stage);
        Directory.CreateDirectory(appDir);
        try
        {
            var script = UpdateInstaller.WriteScript(stage, appDir);
            var text = File.ReadAllText(script);

            Assert.True(File.Exists(script));
            Assert.Contains($"\"{stage}\\*\"", text);        // 源目录（带引号）
            Assert.Contains($"\"{appDir}\\\"", text);        // 目标目录
            Assert.Contains("SmartClassroom.App.exe", text);
            Assert.Contains("goto wait", text);              // 等旧进程退出
            Assert.Contains("start \"\"", text);             // 重启
            Assert.Contains("del \"%~f0\"", text);           // 自删
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // ================= 调试：清空所有设置 =================

    [AvaloniaFact]
    public void ResetAllSettings_ClearsFileAndMemoryAndUi()
    {
        var shared = new AppSettings();                 // 相当于 Runtime.Settings
        var vm = new SettingsViewModel(_path, null, shared)
        {
            AiApiKey = "sk-x",
            AiModel = "m",
            GroupIdsText = "123456",
            PluginToken = "tok",
            ArchiveRoot = "/tmp/archive"
        };
        vm.FeatureSummon = true;                        // 需要前置条件，先补齐
        vm.SaveSettings();
        vm.MergeCandidate(new QqAccount { Uin = 10001, Nickname = "班号" });
        vm.ApplyQqAccount(new QqAccount { Uin = 10001, Nickname = "班号" });
        vm.AcceptRisk();
        Toasts.Items.Clear();
        Assert.True(File.Exists(_path));

        vm.ResetAllSettings();

        // 1) 文件没了
        Assert.False(File.Exists(_path));
        // 2) 内存里那份也复位（否则退出时 Runtime 会把旧值写回去）
        Assert.Equal("", shared.AiApiKey);
        Assert.Equal(0, shared.QqAccount);
        Assert.Empty(shared.Teachers);
        Assert.False(shared.FeatureSummon);
        Assert.False(shared.RiskAccepted);
        // 3) 界面回到默认
        Assert.Equal("", vm.AiApiKey);
        Assert.Equal("", vm.GroupIdsText);
        Assert.False(vm.FeatureSummon);
        Assert.Equal(0, vm.QqAccount);
        Assert.Empty(vm.QqCandidates);
        Assert.False(vm.RiskAccepted);
        Assert.Contains(Toasts.Items, t => t.Title == "已清空所有设置");
    }

    [AvaloniaFact]
    public void ResetAllSettings_AlsoClearsAdminPassword()
    {
        var vm = new SettingsViewModel(_path)
        {
            NewPassword = "abcd",
            ConfirmPassword = "abcd"
        };
        vm.ApplyPassword();
        Assert.True(vm.HasPassword);

        vm.ResetAllSettings();

        Assert.False(vm.HasPassword);
        Assert.False(vm.IsLocked);   // 密码没了就不该再锁着
    }

    [AvaloniaFact]
    public void ResetAllSettings_LeavesHomeworkAndTimelineAlone()
    {
        // 数据（作业/事件）不属于"设置"，清设置不该动它们
        Runtime.Homework.ReplaceAll([
            new SmartClassroom.Contracts.HomeworkItem
            {
                HomeworkId = "h1", Subject = "数学", Date = DateOnly.FromDateTime(DateTime.Now),
                Items = ["P10"],
                Sender = new SmartClassroom.Contracts.SenderInfo { UserId = 1 },
                Source = new SmartClassroom.Contracts.MessageRef { GroupId = 1, MessageId = 1 }
            }
        ]);
        var before = Runtime.Feed.Entries.Count;

        new SettingsViewModel(_path).ResetAllSettings();

        Assert.Single(Runtime.Homework.All);
        Assert.Equal(before, Runtime.Feed.Entries.Count);
    }
}
