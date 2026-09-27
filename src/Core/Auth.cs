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
    /// <summary>
    /// 解锁后的免输入窗口（分钟）。
    ///
    /// 默认 2 分钟：**每次要做需要密码的操作都会先验证**，但刚验证过的一小段时间内不重复问。
    /// 踩过的坑：一度把它设成 0（"每次操作都要密码"），结果 TryUnlock 立刻过期 ——
    /// 用户点「解锁」后界面还是锁的，等于**根本解不开**（实测反馈"设置了以后我没法解锁"）。
    /// 想更严格就在设置里把它调小；想省事可以打开"解锁后 10 分钟内免重复输入"。
    /// </summary>
    public int SessionMinutes { get; set; } = 2;
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
        // SessionMinutes = 0 时立刻过期 → 下一次操作还要再输一次
        _unlockedUntil = DateTimeOffset.Now.AddMinutes(SessionMinutes);
        return true;
    }

    /// <summary>立即锁定（例如密码被改后）。</summary>
    public void Lock() => _unlockedUntil = null;

    /// <summary>
    /// 受保护操作的统一入口。<paramref name="prompt"/> 负责弹窗要密码；
    /// <paramref name="forcePrompt"/> 为 true 时即使会话已认证也重新询问（用于退出）。
    /// </summary>
    /// <param name="onWrongPassword">
    /// 密码错误时的回调（界面用它提示"密码不正确"）。不传就静默重试。
    /// 实测反馈："没有密码不正确的提示" —— 输错了只是弹窗又出现，用户不知道错在哪。
    /// </param>
    public async Task<bool> RequireAsync(Func<string, Task<string?>> prompt, string reason,
        bool forcePrompt = false, Action<int>? onWrongPassword = null)
    {
        if (!IsEnabled)
            return true;
        if (!forcePrompt && IsUnlocked)
            return true;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var input = await prompt(reason);
            if (input is null)
                return false; // 用户取消
            if (TryUnlock(input))
                return true;
            onWrongPassword?.Invoke(attempt);
        }
        return false;
    }
}
