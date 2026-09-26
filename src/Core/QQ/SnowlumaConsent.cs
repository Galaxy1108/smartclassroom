using System.Security.Cryptography;
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

    /// <summary>协议内容指纹（内容变了就得重新同意）。</summary>
    public static string Fingerprint(IReadOnlyList<SnowlumaAgreement> docs)
    {
        if (docs.Count == 0)
            return "";
        var sb = new StringBuilder();
        foreach (var d in docs)
            sb.Append(d.Id).Append('\u0001').Append(d.Text).Append('\u0002');
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    /// <summary>同意后传给 SnowLuma 的环境变量（两个必须同时给）。</summary>
    public static IReadOnlyDictionary<string, string> AcceptanceEnvironment()
        => new Dictionary<string, string>
        {
            [EulaAcceptEnv] = "1",
            [PrivacyAcceptEnv] = "1"
        };
}
