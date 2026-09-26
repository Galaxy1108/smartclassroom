using System.Security.Cryptography;
using System.Text.Json;
using System.Text;

namespace SmartClassroom.Core.QQ;

/// <summary>一份协议（用户协议 / 隐私政策）。</summary>
public sealed record SnowlumaAgreement(string Id, string Title, string Text);

/// <summary>
/// SnowLuma 的协议同意。
///
/// 为什么需要它：SnowLuma 启动后会停在"等待同意 EULA/隐私政策"，**在此之前不会注入**，
/// OneBot 的 HTTP 服务也不会起来——用户只会看到"检测不到账号"。
///
/// 走它的官方环境变量开关（两个必须同时给，只给一个会被忽略）：
///   SNOWLUMA_ACCEPT_EULA=1 / SNOWLUMA_ACCEPT_PRIVACY=1
/// 正文直接读它安装目录里的 EULA.md / PRIVACY.md（就是它自己展示的那两份），
/// 不经过 WebUI，也不需要抓它随机生成的初始密码（那个 API 需要先登录）。
///
/// 协议文本一改（SnowLuma 更新条款）指纹就变，会重新征得同意。
/// </summary>
public static class SnowlumaAgreements
{
    public const string EulaAcceptEnv = "SNOWLUMA_ACCEPT_EULA";
    public const string PrivacyAcceptEnv = "SNOWLUMA_ACCEPT_PRIVACY";

    /// <summary>协议文件（与 SnowLuma 内部一致）。</summary>
    public static readonly (string Id, string File)[] Files = [("eula", "EULA.md"), ("privacy", "PRIVACY.md")];

    /// <summary>读安装目录下的协议正文；文件缺失的条目会被跳过。</summary>
    public static IReadOnlyList<SnowlumaAgreement> ReadFrom(string installDir)
    {
        var docs = new List<SnowlumaAgreement>();
        foreach (var (id, file) in Files)
        {
            try
            {
                var path = Path.Combine(installDir, file);
                if (!File.Exists(path))
                    continue;
                var text = File.ReadAllText(path);
                if (text.Trim().Length == 0)
                    continue;
                docs.Add(new SnowlumaAgreement(id, TitleOf(id, text), text));
            }
            catch { /* 读不了就当没有 */ }
        }
        return docs;
    }

    /// <summary>标题取正文第一行的 "# xxx"，取不到用中文名。</summary>
    private static string TitleOf(string id, string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("# "))
            {
                var title = t[2..].Trim();
                if (title.Length > 0)
                    return title;
            }
        }
        return id == "eula" ? "用户协议" : "隐私政策";
    }

    /// <summary>
    /// 协议版本号 —— 与 SnowLuma 内部算法**逐字节一致**：
    /// sha256( id + "\0" + text + "\0" ... ) 的十六进制前 16 位。
    /// 用它的算法而不是自己另算一个，才能直接和它写下的 config/consent.json 对照，
    /// 从而认出"用户已经在 WebUI 里同意过"。
    /// </summary>
    public static string ComputeVersion(IReadOnlyList<SnowlumaAgreement> docs)
    {
        if (docs.Count == 0)
            return "";
        using var sha = SHA256.Create();
        var buffer = new List<byte>();
        foreach (var d in docs)
        {
            buffer.AddRange(Encoding.UTF8.GetBytes(d.Id));
            buffer.Add(0);
            buffer.AddRange(Encoding.UTF8.GetBytes(d.Text));
            buffer.Add(0);
        }
        var hash = sha.ComputeHash(buffer.ToArray());
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    /// <summary>SnowLuma 自己记下的同意记录（它在 WebUI 同意、或上次应用带环境变量启动后写的）。</summary>
    public static string? ReadConsentVersion(string installDir)
    {
        try
        {
            var path = Path.Combine(installDir, "config", "consent.json");
            if (!File.Exists(path))
                return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
        }
        catch { return null; }
    }

    /// <summary>SnowLuma 那边已经同意过当前版本的协议（不用再问用户一次）。</summary>
    public static bool AlreadyConsented(string installDir, IReadOnlyList<SnowlumaAgreement> docs)
    {
        var current = ComputeVersion(docs);
        return current.Length > 0 && ReadConsentVersion(installDir) == current;
    }

    /// <summary>同意后传给 SnowLuma 的环境变量（两个必须同时给）。</summary>
    public static IReadOnlyDictionary<string, string> AcceptanceEnvironment()
        => new Dictionary<string, string>
        {
            [EulaAcceptEnv] = "1",
            [PrivacyAcceptEnv] = "1"
        };
}
