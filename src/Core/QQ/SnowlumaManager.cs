using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SmartClassroom.Core.QQ;

/// <summary>SnowLuma 发行版信息（GitHub Releases）。</summary>
public sealed record SnowlumaRelease(string Tag, IReadOnlyList<SnowlumaAsset> Assets);
public sealed record SnowlumaAsset(string Name, string DownloadUrl, long Size);

/// <summary>注入状态三态。</summary>
public enum SnowlumaStatus
{
    NotInstalled,
    QqNotFound,
    InjectedNotLoggedIn,
    Online
}

/// <summary>
/// SnowLuma 管理器：版本查询（SnowLuma/SnowLuma releases）→ 下载解压 →
/// 启停子进程 → 注入状态。应用不随包附带 SnowLuma，全部走设置页下载器。
/// Windows 用 hook 注入运行中的 QQ.exe；Linux 走 Docker 指引（本类仅管本地包）。
/// </summary>
public sealed class SnowlumaManager(HttpClient? http = null) : IDisposable
{
    public const string ReleasesApi = "https://api.github.com/repos/SnowLuma/SnowLuma/releases?per_page=10";

    private readonly HttpClient _http = http ?? new HttpClient();
    private Process? _process;

    public event Action<string>? OnLog;

    /// <summary>按当前平台挑包：win-x64→zip，linux→tar.gz；full 优先（内置 Node）。</summary>
    public static SnowlumaAsset? PickAsset(SnowlumaRelease release, string rid, bool preferFull = true)
    {
        var ext = rid.StartsWith("win") ? ".zip" : ".tar.gz";
        SnowlumaAsset? Match(bool full) => release.Assets.FirstOrDefault(a =>
            a.Name.Contains(rid) && a.Name.EndsWith(ext) &&
            (full ? !a.Name.Contains("-lite") : a.Name.Contains("-lite")));
        return (preferFull ? Match(true) ?? Match(false) : Match(false) ?? Match(true));
    }

    public static string CurrentRid()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "win-x64";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "osx-arm64";
        return RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64";
    }

    public async Task<IReadOnlyList<SnowlumaRelease>> ListReleasesAsync(CancellationToken cancel = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, ReleasesApi);
        req.Headers.UserAgent.ParseAdd("SmartClassroom/0.4");
        using var res = await _http.SendAsync(req, cancel).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(cancel).ConfigureAwait(false));
        return doc.RootElement.EnumerateArray().Select(r => new SnowlumaRelease(
            r.GetProperty("tag_name").GetString() ?? "",
            r.GetProperty("assets").EnumerateArray().Select(a => new SnowlumaAsset(
                a.GetProperty("name").GetString() ?? "",
                a.GetProperty("browser_download_url").GetString() ?? "",
                a.TryGetProperty("size", out var s) ? s.GetInt64() : 0)).ToList())).ToList();
    }

    public async Task DownloadAsync(string url, string destFile, IProgress<double>? progress, CancellationToken cancel)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
        using var res = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        var total = res.Content.Headers.ContentLength ?? -1L;
        await using var src = await res.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        await using var dst = File.Create(destFile);
        var buf = new byte[81920];
        long done = 0;
        int n;
        while ((n = await src.ReadAsync(buf, cancel).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), cancel).ConfigureAwait(false);
            done += n;
            if (total > 0)
                progress?.Report((double)done / total);
        }
    }

    public static void Extract(string archive, string destDir)
    {
        Directory.CreateDirectory(destDir);
        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archive, destDir, overwriteFiles: true);
        }
        else if (archive.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            using var fs = File.OpenRead(archive);
            using var gz = new System.IO.Compression.GZipStream(fs, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gz, destDir, overwriteFiles: true);
        }
        else throw new NotSupportedException($"不支持的压缩包格式：{archive}");
    }

    /// <summary>启动 SnowLuma（installDir 下 index.mjs）。node 解析顺序：内置 → PATH(≥22)。</summary>
    public Task StartAsync(string installDir, CancellationToken cancel = default)
    {
        if (_process is { HasExited: false })
            return Task.CompletedTask;
        var entry = Path.Combine(installDir, "index.mjs");
        if (!File.Exists(entry))
            throw new FileNotFoundException($"SnowLuma 未安装或目录不对：{entry}");
        var node = FindNode(installDir);
        _process = new Process
        {
            StartInfo = new ProcessStartInfo(node, $"\"{entry}\"")
            {
                WorkingDirectory = installDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };
        _process.OutputDataReceived += (_, e) => { if (e.Data is not null) OnLog?.Invoke(e.Data); };
        _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) OnLog?.Invoke(e.Data); };
        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        return Task.CompletedTask;
    }

    public void Stop()
    {
        try { _process?.Kill(entireProcessTree: true); } catch { /* 已退出则忽略 */ }
        _process?.Dispose();
        _process = null;
    }

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>综合状态：安装 → QQ 进程 → OneBot get_status。</summary>
    public async Task<SnowlumaStatus> ProbeAsync(string installDir, OneBotClient oneBot, CancellationToken cancel = default)
    {
        if (!File.Exists(Path.Combine(installDir, "index.mjs")))
            return SnowlumaStatus.NotInstalled;
        if (!IsQqRunning())
            return SnowlumaStatus.QqNotFound;
        try
        {
            var data = await oneBot.InvokeAsync<JsonElement>("get_status", new { }, cancel).ConfigureAwait(false);
            if (data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("online", out var on) && on.GetBoolean())
                return SnowlumaStatus.Online;
        }
        catch { /* OneBot 不通：已注入但未登录或服务未起 */ }
        return SnowlumaStatus.InjectedNotLoggedIn;
    }

    internal static bool IsQqRunning()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return Process.GetProcessesByName("QQ").Length > 0;
            return Process.GetProcessesByName("qq").Length > 0;
        }
        catch { return false; }
    }

    public static string FindNode(string installDir)
    {
        var exe = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "node.exe" : "node";
        foreach (var c in new[] { Path.Combine(installDir, exe), Path.Combine(installDir, "node", exe) })
            if (File.Exists(c))
                return c;
        return exe; // 回退 PATH（精简版要求用户自装 Node ≥ 22）
    }

    public void Dispose()
    {
        Stop();
        _http.Dispose();
    }
}
