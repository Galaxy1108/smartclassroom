using System.Security.Cryptography;

namespace SmartClassroom.Core;

/// <summary>
/// 管理员密码的哈希与校验。
/// 只存 PBKDF2-SHA256 派生值（随机盐 + 迭代次数），不存明文、不可逆。
/// 存储格式：pbkdf2-sha256$迭代次数$Base64盐$Base64哈希
/// </summary>
public static class PasswordHasher
{
    public const int DefaultIterations = 100_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static string Hash(string password, int iterations = DefaultIterations)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"pbkdf2-sha256${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
    }

    /// <summary>校验；格式非法或密码错误都返回 false（不抛异常，避免登录路径崩）。</summary>
    public static bool Verify(string password, string? stored)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrWhiteSpace(stored))
            return false;
        try
        {
            var parts = stored.Split('$');
            if (parts.Length != 4 || parts[0] != "pbkdf2-sha256")
                return false;
            if (!int.TryParse(parts[1], out var iterations) || iterations <= 0)
                return false;
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// 管理员认证门。
/// 设置密码后，受保护操作（改设置、增删作业、处理待确认、彻底退出）需要先通过认证。
/// 一次认证后在 <see cref="SessionMinutes"/> 分钟内免重复输入；但「彻底退出」永远重新询问。
/// </summary>
public sealed class AuthGate(Func<string?> passwordHashProvider)
{
    public const int SessionMinutes = 10;
    private DateTimeOffset? _unlockedUntil;

    /// <summary>是否已设置密码。</summary>
    public bool IsEnabled => !string.IsNullOrWhiteSpace(passwordHashProvider());

    /// <summary>当前会话是否处于已认证状态（未设密码时恒为 true，即不拦截）。</summary>
    public bool IsUnlocked => !IsEnabled || (_unlockedUntil is not null && _unlockedUntil > DateTimeOffset.Now);

    /// <summary>校验密码；成功后开启免重复输入的窗口。</summary>
    public bool TryUnlock(string password)
    {
        if (!IsEnabled)
            return true;
        if (!PasswordHasher.Verify(password, passwordHashProvider()))
            return false;
        _unlockedUntil = DateTimeOffset.Now.AddMinutes(SessionMinutes);
        return true;
    }

    /// <summary>立即锁定（例如密码被改后）。</summary>
    public void Lock() => _unlockedUntil = null;

    /// <summary>
    /// 受保护操作的统一入口。<paramref name="prompt"/> 负责弹窗要密码；
    /// <paramref name="forcePrompt"/> 为 true 时即使会话已认证也重新询问（用于退出）。
    /// </summary>
    public async Task<bool> RequireAsync(Func<string, Task<string?>> prompt, string reason, bool forcePrompt = false)
    {
        if (!IsEnabled)
            return true;
        if (!forcePrompt && IsUnlocked)
            return true;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var input = await prompt(reason);
            if (input is null)
                return false; // 用户取消
            if (TryUnlock(input))
                return true;
        }
        return false;
    }
}
