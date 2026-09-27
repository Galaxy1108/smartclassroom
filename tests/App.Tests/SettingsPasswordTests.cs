using Avalonia.Headless.XUnit;
using SmartClassroom.App.ViewModels;
using SmartClassroom.Core;
using Xunit;

namespace SmartClassroom.App.Tests;

/// <summary>管理员密码：设置、校验、清除，以及对 Runtime 认证门的即时生效。</summary>
public sealed class SettingsPasswordTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "sc-pw-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
        // 每个用例后复原，避免影响其它用例（Runtime 是进程级单例）
        Runtime.Settings.AdminPasswordHash = "";
        Runtime.Auth.Lock();
    }

    [AvaloniaFact]
    public void ApplyPassword_RejectsMismatchAndShort()
    {
        var vm = new SettingsViewModel(_path);
        vm.NewPassword = "abcd";
        vm.ConfirmPassword = "abce";
        vm.ApplyPassword();
        Assert.Contains("不一致", vm.PasswordResult);
        Assert.False(vm.HasPassword);

        vm.NewPassword = "ab";
        vm.ConfirmPassword = "ab";
        vm.ApplyPassword();
        Assert.Contains("至少", vm.PasswordResult);
        Assert.False(vm.HasPassword);
    }

    [AvaloniaFact]
    public void ApplyPassword_TakesEffectImmediately_AndUnlocksCurrentSession()
    {
        var vm = new SettingsViewModel(_path);
        vm.NewPassword = "s3cret";
        vm.ConfirmPassword = "s3cret";
        vm.ApplyPassword();

        Assert.True(vm.HasPassword);
        Assert.Contains("已设置", vm.PasswordResult);
        Assert.True(Runtime.Auth.IsEnabled);       // 立刻生效，无需重启
        // 解锁窗口默认 2 分钟：设完密码后当前会话仍处于已验证状态
        //（窗口为 0 会导致"解锁后界面还是锁的、根本解不开"）
        Assert.True(Runtime.Auth.IsUnlocked);
        Assert.True(PasswordHasher.Verify("s3cret", Runtime.Settings.AdminPasswordHash));
    }

    [AvaloniaFact]
    public void ApplyPassword_PersistsHash_NotPlaintext()
    {
        var vm = new SettingsViewModel(_path);
        vm.NewPassword = "hunter2x";
        vm.ConfirmPassword = "hunter2x";
        vm.ApplyPassword();

        var raw = File.ReadAllText(_path);
        Assert.DoesNotContain("hunter2x", raw);     // 绝不落明文
        Assert.True(PasswordHasher.Verify("hunter2x", new SettingsViewModel(_path).AdminHashForTest()));
    }

    [AvaloniaFact]
    public void ClearPassword_DisablesGate()
    {
        var vm = new SettingsViewModel(_path);
        vm.NewPassword = "s3cret";
        vm.ConfirmPassword = "s3cret";
        vm.ApplyPassword();
        Assert.True(Runtime.Auth.IsEnabled);

        vm.NewPassword = "";
        vm.ConfirmPassword = "";
        vm.ApplyPassword();

        Assert.False(vm.HasPassword);
        Assert.False(Runtime.Auth.IsEnabled);
        Assert.Contains("已清除", vm.PasswordResult);
    }

    [AvaloniaFact]
    public void MinimizeToTray_DefaultsOn_AndSyncsToRuntime()
    {
        var vm = new SettingsViewModel(_path);
        Assert.True(vm.MinimizeToTray);

        vm.MinimizeToTray = false;
        Assert.False(Runtime.Settings.MinimizeToTray);
        Assert.False(new SettingsViewModel(_path).MinimizeToTray);
    }
}
