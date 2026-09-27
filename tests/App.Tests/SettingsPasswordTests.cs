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

/// <summary>
/// 老师信息要能就地修改（用户："无法修改老师信息，删除重建以后还是原科目"）。
/// 以前 TeacherRow 是 record（init-only），只能删了重建。
/// </summary>
public sealed class TeacherEditTests
{
    [AvaloniaFact]
    public void EditTeacher_UpdatesTheRowInPlace()
    {
        var vm = new SettingsViewModel(Path.Combine(Path.GetTempPath(), "sc-teacher-" + Guid.NewGuid().ToString("N") + ".json"));
        vm.NewTeacherQq = "2131023099";
        vm.NewTeacherName = "王子诚";
        vm.NewTeacherSubject = "信息";
        vm.AddTeacher();

        var row = Assert.Single(vm.Teachers);
        Assert.Equal("信息", row.Subject);

        vm.BeginEditTeacher(row);
        Assert.True(vm.IsEditingTeacher);
        Assert.Equal("保存修改", vm.TeacherFormTitle);
        Assert.Equal("信息", vm.NewTeacherSubject);      // 表单被填上了

        vm.NewTeacherSubject = "信息技术";
        vm.AddTeacher();                                  // 保存修改

        Assert.False(vm.IsEditingTeacher);
        Assert.Single(vm.Teachers);                       // 没有多出一行
        Assert.Equal("信息技术", vm.Teachers[0].Subject); // 就地改掉了
        Assert.Equal("", vm.TeacherEditHint);            // 保存后退出编辑态
    }

    [AvaloniaFact]
    public void CancelEdit_ClearsTheForm()
    {
        var vm = new SettingsViewModel(Path.Combine(Path.GetTempPath(), "sc-teacher-" + Guid.NewGuid().ToString("N") + ".json"));
        vm.NewTeacherQq = "1"; vm.NewTeacherName = "A"; vm.NewTeacherSubject = "语文";
        vm.AddTeacher();
        vm.BeginEditTeacher(vm.Teachers[0]);

        vm.CancelEditTeacher();

        Assert.False(vm.IsEditingTeacher);
        Assert.Equal("", vm.NewTeacherName);
        Assert.Equal("", vm.NewTeacherSubject);
    }
}
