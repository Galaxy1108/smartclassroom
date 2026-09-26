using System.Text.Json;

namespace SmartClassroom.Core.Updates;

/// <summary>
/// 够用就好的语义化版本：支持 "0.22.0" / "v0.22.0" / "0.22.0-beta.1"。
/// 只用 .NET 的 <see cref="Version"/> 表达不了 pre-release，而更新检查必须能区分
/// "0.23.0" 和 "0.23.0-beta.1"（后者不该被当成正式更新推给所有人）。
/// </summary>
public readonly record struct AppVersion(int Major, int Minor, int Patch, string? Pre)
    : IComparable<AppVersion>
{
    public static readonly AppVersion Zero = new(0, 0, 0, null);

    public bool IsPrerelease => !string.IsNullOrEmpty(Pre);

    /// <summary>解析；无法识别时返回 <see cref="Zero"/>。</summary>
    public static AppVersion Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Zero;
        var s = raw.Trim();
        if (s.StartsWith('v') || s.StartsWith('V'))
            s = s[1..];

        string? pre = null;
        var dash = s.IndexOf('-');
        if (dash >= 0)
        {
            pre = s[(dash + 1)..];
            s = s[..dash];
        }
        // 0.22 → 0.22.0
        var parts = s.Split('.');
        if (parts.Length is < 2 or > 4)
            return Zero;
        var nums = new int[3];
        for (var i = 0; i < 3; i++)
        {
            if (i >= parts.Length)
                break;
            if (!int.TryParse(parts[i], out nums[i]))
                return Zero;
        }
        return new AppVersion(nums[0], nums[1], nums[2], pre);
    }

    public int CompareTo(AppVersion other)
    {
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        // 数字相同：正式版 > 预发布版
        if (!IsPrerelease && !other.IsPrerelease) return 0;
        if (!IsPrerelease) return 1;
        if (!other.IsPrerelease) return -1;
        return string.CompareOrdinal(Pre, other.Pre);
    }

    public static bool operator >(AppVersion a, AppVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(AppVersion a, AppVersion b) => a.CompareTo(b) < 0;
    public static bool operator >=(AppVersion a, AppVersion b) => a.CompareTo(b) >= 0;
    public static bool operator <=(AppVersion a, AppVersion b) => a.CompareTo(b) <= 0;

    public override string ToString() => IsPrerelease ? $"{Major}.{Minor}.{Patch}-{Pre}" : $"{Major}.{Minor}.{Patch}";
}

/// <summary>更新检查结果。</summary>
public sealed record UpdateInfo
{
    public required string CurrentVersion { get; init; }

    /// <summary>远端最新版本（拿不到就是 null）。</summary>
    public string? LatestVersion { get; init; }

    public bool HasUpdate { get; init; }

    /// <summary>数据来源："release"（GitHub Release）/"tag"（只有 tag）/"none"。</summary>
    public string Source { get; init; } = "none";

    public string? ReleaseName { get; init; }
    public string? ReleaseUrl { get; init; }

    /// <summary>Release 说明（Markdown 原文，界面上按纯文本展示）。</summary>
    public string? Notes { get; init; }

    /// <summary>匹配到的安装包直链（Release 里上传的资产）。</summary>
    public string? AssetUrl { get; init; }
    public string? AssetName { get; init; }

    /// <summary>能查到远端版本。</summary>
    public bool RemoteReachable => LatestVersion is not null;
}

/// <summary>
/// 软件更新检查：查 GitHub 的 Releases（仓库还没发 Release 时退回 tags）。
///
/// 只负责"查"。怎么装因平台而异（Windows 直接换文件，Linux 是系统包必须走包管理器），
/// 那部分在 App 层，且 Linux 下明确禁用自动替换。
/// </summary>
public sealed class UpdateChecker(HttpClient? http = null)
{
    public const string DefaultOwner = "Galaxy1108";
    public const string DefaultRepo = "smartclassroom";

    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>检查更新。<paramref name="assetPattern"/> 用于在 Release 资产里挑安装包（如 "win-x64.zip"）。</summary>
    public async Task<UpdateInfo> CheckAsync(
        string currentVersion,
        string owner = DefaultOwner,
        string repo = DefaultRepo,
        string? assetPattern = null,
        CancellationToken cancel = default)
    {
        var current = AppVersion.Parse(currentVersion);

        // 1) 最新 Release：有说明、有资产
        var release = await GetJsonAsync($"https://api.github.com/repos/{owner}/{repo}/releases/latest", cancel)
            .ConfigureAwait(false);
        if (release is { } rel && rel.ValueKind == JsonValueKind.Object
            && rel.TryGetProperty("tag_name", out var tagEl) && tagEl.ValueKind == JsonValueKind.String)
        {
            var tag = tagEl.GetString() ?? "";
            var latest = AppVersion.Parse(tag);
            var (assetUrl, assetName) = PickAsset(rel, assetPattern);
            return new UpdateInfo
            {
                CurrentVersion = current.ToString(),
                LatestVersion = latest.ToString(),
                HasUpdate = latest > current,
                Source = "release",
                ReleaseName = Str(rel, "name") ?? tag,
                ReleaseUrl = Str(rel, "html_url"),
                Notes = Str(rel, "body"),
                AssetUrl = assetUrl,
                AssetName = assetName
            };
        }

        // 2) 还没有 Release（仓库只打了 tag）→ 用 tags 兜底，至少能提示"有新版本"
        var tags = await GetJsonAsync($"https://api.github.com/repos/{owner}/{repo}/tags", cancel)
            .ConfigureAwait(false);
        if (tags is { } arr && arr.ValueKind == JsonValueKind.Array)
        {
            AppVersion? best = null;
            string? bestTag = null;
            foreach (var item in arr.EnumerateArray())
            {
                var name = Str(item, "name");
                if (name is null)
                    continue;
                var v = AppVersion.Parse(name);
                if (v == AppVersion.Zero || v.IsPrerelease)
                    continue;
                if (best is null || v > best.Value)
                {
                    best = v;
                    bestTag = name;
                }
            }
            if (best is not null)
            {
                return new UpdateInfo
                {
                    CurrentVersion = current.ToString(),
                    LatestVersion = best.Value.ToString(),
                    HasUpdate = best.Value > current,
                    Source = "tag",
                    ReleaseName = bestTag,
                    ReleaseUrl = $"https://github.com/{owner}/{repo}/releases"
                };
            }
        }

        return new UpdateInfo { CurrentVersion = current.ToString(), Source = "none" };
    }

    private async Task<JsonElement?> GetJsonAsync(string url, CancellationToken cancel)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        // GitHub API 强制要求 User-Agent，缺了直接 403
        req.Headers.TryAddWithoutValidation("User-Agent", "SmartClassroom-Updater");
        req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        using var res = await _http.SendAsync(req, cancel).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
            return null;
        var text = await res.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
        try { return JsonDocument.Parse(text).RootElement.Clone(); }
        catch (JsonException) { return null; }
    }

    private static string? Str(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static (string? Url, string? Name) PickAsset(JsonElement release, string? pattern)
    {
        if (pattern is null || !release.TryGetProperty("assets", out var assets)
            || assets.ValueKind != JsonValueKind.Array)
            return (null, null);

        foreach (var asset in assets.EnumerateArray())
        {
            var name = Str(asset, "name");
            if (name is null || !name.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                continue;
            return (Str(asset, "browser_download_url"), name);
        }
        return (null, null);
    }
}
