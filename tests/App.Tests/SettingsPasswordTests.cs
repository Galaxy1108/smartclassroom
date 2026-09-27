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

/// <summary>
/// 解锁窗口不能是 0。踩过两次：
/// ① AuthGate 默认 0 → 解锁立刻过期；
/// ② 默认修好了，但 Runtime.ApplyAuthPolicy 又把它覆盖回 0 →
///    "密码输对了、对话框关了，界面还是锁的，点什么都没反应"。
/// </summary>
public sealed class UnlockWindowTests
{
    [AvaloniaFact]
    public void DefaultPolicy_KeepsAUsableWindow()
    {
        Runtime.Settings.RememberUnlock = false;
        Runtime.ApplyAuthPolicyForTests(Runtime.Settings);
        Assert.True(Runtime.Auth.SessionMinutes >= 1,
            "解锁窗口必须 >= 1 分钟，否则解锁等于没解");

        Runtime.Settings.RememberUnlock = true;
        Runtime.ApplyAuthPolicyForTests(Runtime.Settings);
        Assert.Equal(10, Runtime.Auth.SessionMinutes);

        Runtime.Settings.RememberUnlock = false;
        Runtime.ApplyAuthPolicyForTests(Runtime.Settings);
    }

    [AvaloniaFact]
    public void UnlockWithCorrectPassword_LeavesGateOpen()
    {
        var hash = SmartClassroom.Core.PasswordHasher.Hash("test-pw-1234");
        Runtime.Settings.AdminPasswordHash = hash;
        Runtime.ApplyAuthPolicyForTests(Runtime.Settings);
        Runtime.Auth.Lock();

        Assert.False(Runtime.Auth.IsUnlocked);
        Assert.True(Runtime.Auth.TryUnlock("test-pw-1234"));
        Assert.True(Runtime.Auth.IsUnlocked);       // 解锁后必须真的打开

        Runtime.Settings.AdminPasswordHash = "";
        Runtime.Auth.Lock();
    }
}
