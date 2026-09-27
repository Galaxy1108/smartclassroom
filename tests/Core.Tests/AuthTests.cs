using SmartClassroom.Core;
using Xunit;

namespace SmartClassroom.Core.Tests;

public sealed class PasswordHasherTests
{
    [Fact]
    public void Hash_IsNotPlaintext_AndVerifies()
    {
        var stored = PasswordHasher.Hash("correct horse");
        Assert.DoesNotContain("correct horse", stored);
        Assert.StartsWith("pbkdf2-sha256$", stored);
        Assert.True(PasswordHasher.Verify("correct horse", stored));
    }

    [Fact]
    public void Hash_SamePassword_DifferentSaltEachTime()
    {
        var a = PasswordHasher.Hash("same");
        var b = PasswordHasher.Hash("same");
        Assert.NotEqual(a, b);                       // 随机盐
        Assert.True(PasswordHasher.Verify("same", a));
        Assert.True(PasswordHasher.Verify("same", b));
    }

    [Theory]
    [InlineData("wrong")]
    [InlineData("")]
    [InlineData("Correct Horse")]     // 大小写敏感
    public void Verify_RejectsBadPassword(string attempt)
        => Assert.False(PasswordHasher.Verify(attempt, PasswordHasher.Hash("correct horse")));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("pbkdf2-sha256$notanumber$AAAA$BBBB")]
    [InlineData("pbkdf2-sha256$1000$!!!$!!!")]
    [InlineData("md5$1000$AAAA$BBBB")]
    public void Verify_BadStoredValue_ReturnsFalseWithoutThrowing(string? stored)
        => Assert.False(PasswordHasher.Verify("whatever", stored));
}

public sealed class AuthGateTests
{
    private static AuthGate WithPassword(string password)
    {
        var hash = PasswordHasher.Hash(password);
        return new AuthGate(() => hash);
    }

    private static Task<string?> Prompt(string? answer) => Task.FromResult(answer);

    [Fact]
    public void NoPassword_IsDisabledAndNeverBlocks()
    {
        var gate = new AuthGate(() => "");
        Assert.False(gate.IsEnabled);
        Assert.True(gate.IsUnlocked);   // 未设密码 = 不拦截
    }

    [Fact]
    public void WithPassword_StartsLocked()
    {
        var gate = WithPassword("1234");
        Assert.True(gate.IsEnabled);
        Assert.False(gate.IsUnlocked);
    }

    [Fact]
    public void TryUnlock_CorrectPassword_UnlocksSession()
    {
        var gate = WithPassword("1234");
        gate.SessionMinutes = 10;   // 打开"记住 10 分钟"才有会话窗口
        Assert.False(gate.TryUnlock("0000"));
        Assert.False(gate.IsUnlocked);
        Assert.True(gate.TryUnlock("1234"));
        Assert.True(gate.IsUnlocked);
    }

    [Fact]
    public async Task Require_NoPassword_DoesNotPrompt()
    {
        var gate = new AuthGate(() => "");
        var prompted = false;
        var ok = await gate.RequireAsync(_ => { prompted = true; return Prompt("x"); }, "reason");
        Assert.True(ok);
        Assert.False(prompted);
    }

    [Fact]
    public async Task Require_Cancel_ReturnsFalse()
    {
        var gate = WithPassword("1234");
        var ok = await gate.RequireAsync(_ => Prompt(null), "reason");
        Assert.False(ok);
    }

    [Fact]
    public async Task Require_RetriesUpToThreeTimes()
    {
        var gate = WithPassword("1234");
        var attempts = 0;
        var ok = await gate.RequireAsync(_ =>
        {
            attempts++;
            // 第三次才输对
            return Prompt(attempts == 3 ? "1234" : "bad");
        }, "reason");
        Assert.True(ok);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task Require_AfterUnlock_SkipsPromptForSession()
    {
        var gate = WithPassword("1234");
        gate.SessionMinutes = 10;   // 会话内免重复输入要显式打开（默认是每次都要）
        await gate.RequireAsync(_ => Prompt("1234"), "first");
        var prompted = false;
        var ok = await gate.RequireAsync(_ => { prompted = true; return Prompt("1234"); }, "second");
        Assert.True(ok);
        Assert.False(prompted);          // 会话内免重复输入
    }

    [Fact]
    public async Task Require_ForcePrompt_AlwaysAsks()
    {
        var gate = WithPassword("1234");
        await gate.RequireAsync(_ => Prompt("1234"), "first");
        var prompted = false;
        var ok = await gate.RequireAsync(_ => { prompted = true; return Prompt("1234"); },
            "退出", forcePrompt: true);
        Assert.True(ok);
        Assert.True(prompted);           // 退出永远重新验证
    }

    [Fact]
    public void Lock_InvalidatesSession()
    {
        var gate = WithPassword("1234");
        gate.SessionMinutes = 10;   // 同上
        gate.TryUnlock("1234");
        Assert.True(gate.IsUnlocked);
        gate.Lock();
        Assert.False(gate.IsUnlocked);
    }
}

/// <summary>
/// 默认策略：**每一次操作都要管理员密码**（SessionMinutes = 0）。
/// 用户明确要求"我需要每一次操作都需要管理员密码"。
/// </summary>
public sealed class AuthGateEveryOperationTests
{
    [Fact]
    public void DefaultPolicy_AsksEveryTime()
    {
        var gate = new AuthGate(() => PasswordHasher.Hash("pw"));   // 默认 SessionMinutes = 0

        Assert.False(gate.IsUnlocked);          // 一开始就是锁的
        Assert.True(gate.TryUnlock("pw"));      // 验证通过
        Assert.False(gate.IsUnlocked);          // 但下一次操作还要再输
    }

    [Fact]
    public void RememberWindow_KeepsUnlocked()
    {
        var gate = new AuthGate(() => PasswordHasher.Hash("pw")) { SessionMinutes = 10 };

        Assert.True(gate.TryUnlock("pw"));
        Assert.True(gate.IsUnlocked);
    }
}
