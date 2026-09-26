using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SmartClassroom.Core.QQ;

/// <summary>SnowLuma 发行版信息（GitHub Releases）。</summary>
public sealed record SnowlumaRelease(string Tag, IReadOnlyList<SnowlumaAsset> Assets);
public sealed record SnowlumaAsset(string Name, string DownloadUrl, long Size);

/// <summary>
/// 注入状态。原来只有"三态"，把"没注入"和"注入了但服务没起来"混成一个，
/// 结果停止之后还显示"已注入"——没登录/没注入怎么可能已注入。
/// </summary>
public enum SnowlumaStatus
{
    /// <summary>没装 SnowLuma。</summary>
    NotInstalled,

    /// <summary>QQ 没运行。</summary>
    QqNotFound,

    /// <summary>SnowLuma 没在跑 → 未注入。</summary>
    NotRunning,

    /// <summary>SnowLuma 在跑，但注入没生效（未登录 / 还没完成首次设置）。</summary>
    StartedNotInjected,

    /// <summary>已注入，但 QQ 未登录（OneBot 能应答且 online=false）。</summary>
    InjectedNotLoggedIn,

    /// <summary>已注入且在线。</summary>
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

    /// <summary>接管来的实例 pid（不是本进程启动的，但「停止」要能停掉它）。</summary>
    private int? _adoptedPid;

    /// <summary>最近若干行输出（用于识别"等待 WebUI 首次设置"这类状态）。</summary>
    private readonly Queue<string> _recentOutput = new();

    public event Action<string>? OnLog;

    /// <summary>pid 文件名（记录本应用启动的 SnowLuma，重启后也能判断它还在不在）。</summary>
    public const string PidFileName = ".smartclassroom.pid";

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

    /// <summary>SnowLuma 是否在跑：本进程启动的 / pid 文件记录的 / Linux 上扫 /proc 认出来的。</summary>
    public bool IsRunning(string installDir) => FindRunningPid(installDir) is not null;

    /// <summary>找出正在跑的 SnowLuma 进程：本进程启动的 / pid 文件里的 / Linux 扫 /proc 的。</summary>
    public int? FindRunningPid(string installDir)
    {
        if (_process is { HasExited: false })
            return _process.Id;
        var pid = ReadPid(installDir);
        if (pid is > 0 && IsProcessAlive(pid.Value))
            return pid;
        return RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? FindIndexProcessPid(installDir) : null;
    }

    /// <summary>
    /// SnowLuma 是否卡在"等待 WebUI 首次设置"。
    /// 实测：它启动后要先在 WebUI 里同意 EULA/隐私政策才会注入，
    /// 在此之前 OneBot 的 HTTP 服务根本不会起来——不告诉用户，就只会看到"检测不到账号"。
    /// </summary>
    public bool NeedsWebUiSetup => _recentOutput.Any(l =>
        l.Contains("awaiting EULA", StringComparison.OrdinalIgnoreCase)
        || l.Contains("EULA/PRIVACY consent", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 读 SnowLuma 的"发现 QQ 进程就自动注入"开关（config/runtime.json 的 hookAutoLoad）。
    /// 它默认 false：只起 WebUI，**不会自动注入**——用户点了「启动注入」却没注入，
    /// 十有八九是这里没开（日志里也只会看到 WebUI 起来了）。
    /// 返回 null = 文件缺失或读不出来。
    /// </summary>
    public static bool? ReadHookAutoLoad(string installDir)
    {
        try
        {
            var path = RuntimeConfigPath(installDir);
            if (!File.Exists(path))
                return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("hookAutoLoad", out var v))
                return null;
            return v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null
            };
        }
        catch { return null; }
    }

    /// <summary>
    /// 打开"自动注入"（保留 runtime.json 里其它字段）。
    /// 只在该文件能正常解析时才改写，避免把用户配置写坏。
    /// </summary>
    public static bool SetHookAutoLoad(string installDir, bool value)
    {
        try
        {
            var path = RuntimeConfigPath(installDir);
            if (!File.Exists(path))
                return false;
            var root = JsonNode.Parse(File.ReadAllText(path));
            if (root is not JsonObject obj)
                return false;
            obj["hookAutoLoad"] = value;
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, path, overwrite: true);   // 原子替换
            return true;
        }
        catch { return false; }
    }

    private static string RuntimeConfigPath(string installDir)
        => Path.Combine(installDir, "config", "runtime.json");

    /// <summary>WebUI 地址（端口读 config/runtime.json，读不到按默认 5099）。</summary>
    public static string WebUiUrl(string installDir)
    {
        var port = 5099;
        try
        {
            var path = Path.Combine(installDir, "config", "runtime.json");
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("webuiPort", out var p) && p.TryGetInt32(out var v) && v > 0)
                    port = v;
            }
        }
        catch { /* 读不到就用默认端口 */ }
        return $"http://127.0.0.1:{port}";
    }

    private static int? ReadPid(string installDir)
    {
        try
        {
            var path = Path.Combine(installDir, PidFileName);
            return File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out var pid) ? pid : null;
        }
        catch { return null; }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch { return false; }
    }

    /// <summary>Linux：扫 /proc/*/cmdline 找 index.mjs，返回 pid（用户手动启动也能认出来）。</summary>
    internal static int? FindIndexProcessPid(string installDir)
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                var name = Path.GetFileName(dir);
                if (!int.TryParse(name, out var pid))
                    continue;
                var cmdline = Path.Combine(dir, "cmdline");
                if (!File.Exists(cmdline))
                    continue;
                string text;
                try { text = File.ReadAllText(cmdline).Replace('\0', ' '); }
                catch { continue; }   // 别的用户的进程读不到
                if (text.Contains("index.mjs") && text.Contains(installDir))
                    return pid;
            }
        }
        catch { /* 扫不了就算了 */ }
        return null;
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
    public Task StartAsync(string installDir, bool acceptAgreements = false, CancellationToken cancel = default)
    {
        if (_process is { HasExited: false })
            return Task.CompletedTask;

        // 只看这一次启动的输出（否则上一次"等待同意"的日志会让状态一直错）
        _recentOutput.Clear();

        // 已经在跑（上次应用启动的 / 用户自己启动的）就接管，别再开一个：
        // 两个实例会抢同一个 WebUI 端口，而且「停止」按钮会停不掉真正在跑的那个。
        if (FindRunningPid(installDir) is { } runningPid)
        {
            _adoptedPid = runningPid;
            OnLog?.Invoke($"SnowLuma 已在运行（pid {runningPid}），接管现有实例");
            return Task.CompletedTask;
        }
        var entry = Path.Combine(installDir, "index.mjs");
        if (!File.Exists(entry))
            throw new FileNotFoundException($"SnowLuma 未安装或目录不对：{entry}");
        var node = FindNode(installDir);
        var startInfo = new ProcessStartInfo(node, $"\"{entry}\"")
        {
            WorkingDirectory = installDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        // 用户已经同意协议：用 SnowLuma 的官方开关带过去，否则它会一直停在"等待同意"而不注入
        if (acceptAgreements)
        {
            foreach (var (key, value) in SnowlumaAgreements.AcceptanceEnvironment())
                startInfo.Environment[key] = value;
        }
        _process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
        _process.OutputDataReceived += (_, e) => Note(e.Data);
        _process.ErrorDataReceived += (_, e) => Note(e.Data);
        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        WritePid(installDir, _process.Id);
        return Task.CompletedTask;
    }

    public void Stop(string? installDir = null)
    {
        try { _process?.Kill(entireProcessTree: true); } catch { /* 已退出则忽略 */ }
        _process?.Dispose();
        _process = null;

        // 接管来的实例也要能停掉，否则「停止」按了等于没按
        if (_adoptedPid is { } pid)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                p.Kill(entireProcessTree: true);
            }
            catch { /* 已经退出了 */ }
            _adoptedPid = null;
        }
        if (installDir is not null)
            DeletePid(installDir);
    }

    /// <summary>本进程启动的那个 SnowLuma 是否还活着（判断"在不在跑"请用 IsRunning(installDir)）。</summary>
    public bool StartedByThisApp => _process is { HasExited: false };

    /// <summary>记一行输出（顺便保留最近 40 行用于状态判断）。</summary>
    private void Note(string? line)
    {
        if (line is null)
            return;
        _recentOutput.Enqueue(line);
        while (_recentOutput.Count > 40)
            _recentOutput.Dequeue();
        OnLog?.Invoke(line);
    }

    private static void WritePid(string installDir, int pid)
    {
        try { File.WriteAllText(Path.Combine(installDir, PidFileName), pid.ToString()); }
        catch { /* 写不了就算了，只是重启后判断不出"在跑" */ }
    }

    private static void DeletePid(string installDir)
    {
        try
        {
            var path = Path.Combine(installDir, PidFileName);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { /* 忽略 */ }
    }

    /// <summary>综合状态：安装 → QQ 进程 → OneBot get_status。</summary>
    public async Task<SnowlumaStatus> ProbeAsync(string installDir, OneBotClient oneBot, CancellationToken cancel = default)
    {
        if (!File.Exists(Path.Combine(installDir, "index.mjs")))
            return SnowlumaStatus.NotInstalled;
        if (!IsQqRunning())
            return SnowlumaStatus.QqNotFound;

        // OneBot 能应答 = 注入成功；再看 QQ 是否已登录
        try
        {
            var data = await oneBot.InvokeAsync<JsonElement>("get_status", new { }, cancel).ConfigureAwait(false);
            if (data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("online", out var on))
                return on.GetBoolean() ? SnowlumaStatus.Online : SnowlumaStatus.InjectedNotLoggedIn;
        }
        catch { /* OneBot 不通：还没注入，或注入后服务没起来 */ }

        // 没应答：区分"根本没启动"和"启动了但没注入"
        return IsRunning(installDir) ? SnowlumaStatus.StartedNotInjected : SnowlumaStatus.NotRunning;
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
