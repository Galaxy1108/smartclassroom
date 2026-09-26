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

/// <summary>SnowLuma 给某个账号开的 OneBot 接入点（地址 + access token）。</summary>
public sealed record OneBotEndpoint(string Http, string Ws, string Token, long Uin);

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

    /// <summary>pid 文件名（记录本应用启动的 SnowLuma，重启后也能判断它还在不在）。</summary>
    public const string PidFileName = ".smartclassroom.pid";

    /// <summary>
    /// WebUI 初始密码的官方环境变量。
    /// SnowLuma 默认每次启动随机生成一个初始密码，而且**只打印到 stdout**
    /// （GUI 启动时用户根本看不到）——用这个变量就能自己指定，省得去翻输出。
    /// </summary>
    public const string BootstrapPasswordEnv = "SNOWLUMA_WEBUI_BOOTSTRAP_PASSWORD";

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

    /// <summary>读最新一份日志的尾部（日志可能很大，只取最后 64KB）。</summary>
    private static string? ReadNewestLogTail(string installDir, int tailBytes = 64 * 1024)
    {
        try
        {
            var logDir = Path.Combine(installDir, "logs");
            if (!Directory.Exists(logDir))
                return null;
            var newest = new DirectoryInfo(logDir).GetFiles("*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (newest is null)
                return null;
            using var fs = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(Math.Max(0, fs.Length - tailBytes), SeekOrigin.Begin);
            using var reader = new StreamReader(fs);
            return reader.ReadToEnd();
        }
        catch { return null; }
    }

    /// <summary>
    /// 从日志里读出登录过的 QQ 号（多账号时不止一个）。
    /// SnowLuma 会给每个登录账号开一套 OneBot 适配器，而默认端口都是 3000/3001，
    /// 所以只有第一个能起来——多账号提示要用到这份名单。
    /// </summary>
    public static IReadOnlyList<long> ReadLoggedInUins(string installDir)
    {
        var text = ReadNewestLogTail(installDir);
        if (text is null)
            return [];
        var uins = new List<long>();
        foreach (var line in text.Split('\n'))
        {
            var idx = line.IndexOf("session started: UIN=", StringComparison.Ordinal);
            if (idx < 0)
                continue;
            var rest = line[(idx + "session started: UIN=".Length)..].Trim();
            var digits = new string(rest.TakeWhile(char.IsDigit).ToArray());
            if (digits.Length > 0 && long.TryParse(digits, out var uin) && !uins.Contains(uin))
                uins.Add(uin);
        }
        return uins;
    }

    /// <summary>
    /// 从日志里读出账号昵称（`self info: UIN=x nickname=y`）——账号列表里显示昵称更认得出。
    /// </summary>
    public static IReadOnlyDictionary<long, string> ReadAccountNicknames(string installDir)
    {
        var map = new Dictionary<long, string>();
        var text = ReadNewestLogTail(installDir);
        if (text is null)
            return map;
        foreach (var line in text.Split('\n'))
        {
            var idx = line.IndexOf("self info: UIN=", StringComparison.Ordinal);
            if (idx < 0)
                continue;
            var rest = line[(idx + "self info: UIN=".Length)..];
            var digits = new string(rest.TakeWhile(char.IsDigit).ToArray());
            var nickIdx = rest.IndexOf("nickname=", StringComparison.Ordinal);
            if (digits.Length == 0 || nickIdx < 0)
                continue;
            var nick = rest[(nickIdx + "nickname=".Length)..].Trim();
            if (nick.Length > 0 && long.TryParse(digits, out var uin))
                map[uin] = nick;
        }
        return map;
    }

    /// <summary>
    /// SnowLuma 为每个账号写下的 OneBot 连接信息（config/onebot_&lt;uin&gt;.json）：
    /// 地址 + **access token**。它默认要求鉴权（不带 token 一律 1401 unauthorized），
    /// 所以应用必须自己把它读出来，否则"检测不到账号 / 看不到在线"。
    /// </summary>
    public static OneBotEndpoint? ReadOneBotEndpoint(string installDir, long uin)
    {
        try
        {
            var path = Path.Combine(installDir, "config", $"onebot_{uin}.json");
            if (!File.Exists(path))
                return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("networks", out var networks))
                return null;

            var http = FirstOf(networks, "httpServers");
            var ws = FirstOf(networks, "wsServers");
            if (http is null)
                return null;

            var host = Str(http.Value, "host") ?? "127.0.0.1";
            var httpPort = Int(http.Value, "port") ?? 3000;
            var wsPort = ws is null ? 3001 : Int(ws.Value, "port") ?? 3001;
            return new OneBotEndpoint(
                $"http://{host}:{httpPort}",
                $"ws://{host}:{wsPort}",
                Str(http.Value, "accessToken") ?? "",
                uin);
        }
        catch { return null; }
    }

    /// <summary>
    /// 给每个账号分配**互不冲突**的 OneBot 端口（第一个 3000/3001，第二个 3010/3011…）。
    /// SnowLuma 默认让每个账号都用 3000/3001，于是只有先登录的那个能起来，
    /// 其余账号 EADDRINUSE 降级 —— 这就是"多账号只有一个能连上"的根因。
    /// 改写的是它自己的 config/onebot_&lt;uin&gt;.json（保留其它字段，原子替换），改完要重启才生效。
    /// </summary>
    public static IReadOnlyList<(long Uin, int Http, int Ws)> AssignDistinctPorts(
        string installDir, int startHttp = 3000, int step = 10)
    {
        var result = new List<(long, int, int)>();
        var accounts = ReadOneBotAccounts(installDir);
        var basePort = startHttp;
        foreach (var uin in accounts)
        {
            // 跳过已被别的程序占用的端口对（实测机器上 3000/3001 可能被另一个 SnowLuma 占着）
            while (basePort < startHttp + step * 100 && !PortPairFree(basePort))
                basePort += step;
            var httpPort = basePort;
            var wsPort = basePort + 1;
            basePort += step;
            try
            {
                var path = Path.Combine(installDir, "config", $"onebot_{uin}.json");
                if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root
                    || root["networks"] is not JsonObject networks)
                    continue;
                if (networks["httpServers"] is JsonArray { Count: > 0 } http
                    && http[0] is JsonObject http0)
                    http0["port"] = httpPort;
                if (networks["wsServers"] is JsonArray { Count: > 0 } ws
                    && ws[0] is JsonObject ws0)
                    ws0["port"] = wsPort;

                var tmp = path + ".tmp";
                File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, path, overwrite: true);
                result.Add((uin, httpPort, wsPort));
            }
            catch { /* 单个账号写失败就跳过 */ }
        }
        return result;
    }

    /// <summary>这一对端口是否空闲（用来避开别的程序已占用的端口）。</summary>
    private static bool PortPairFree(int httpPort)
    {
        foreach (var port in new[] { httpPort, httpPort + 1 })
        {
            try
            {
                var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
            }
            catch { return false; }
        }
        return true;
    }

    /// <summary>有 OneBot 配置的账号（config/onebot_*.json）。</summary>
    public static IReadOnlyList<long> ReadOneBotAccounts(string installDir)
    {
        try
        {
            var dir = Path.Combine(installDir, "config");
            if (!Directory.Exists(dir))
                return [];
            return Directory.EnumerateFiles(dir, "onebot_*.json")
                .Select(f => Path.GetFileNameWithoutExtension(f)["onebot_".Length..])
                .Select(s => long.TryParse(s, out var uin) ? uin : 0)
                .Where(u => u > 0)
                .OrderBy(u => u)
                .ToList();
        }
        catch { return []; }
    }

    private static JsonElement? FirstOf(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0
            ? arr[0]
            : null;

    private static string? Str(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : null;

    /// <summary>日志里最近一次端口冲突（多账号共用 3000/3001 时会出现）。</summary>
    public static string? LastPortConflict(string installDir)
    {
        var text = ReadNewestLogTail(installDir);
        if (text is null)
            return null;
        string? last = null;
        foreach (var line in text.Split('\n'))
        {
            if (line.Contains("EADDRINUSE", StringComparison.OrdinalIgnoreCase))
                last = line.Trim();
        }
        return last;
    }

    /// <summary>
    /// 从 SnowLuma 自己的日志里找最近一次注入失败的原因。
    /// 注入是在它进程里做的，失败只写日志（"已启动但未注入"就是这么来的）：
    /// 实测 Linux 上常见的是 `[Hook] load failed ... COMPONENT_LOAD_FAILED`。
    /// </summary>
    public static string? LastHookFailure(string installDir)
    {
        try
        {
            var text = ReadNewestLogTail(installDir);
            if (text is null)
                return null;

            // 只看**最近一次注入尝试**的结果：注入成功过（pipe connected / login detected）
            // 之后就不该再报更早的那次失败，否则会拿过期信息误导用户。
            string? last = null;
            var sawSuccessAfterFailure = false;
            foreach (var line in text.Split('\n'))
            {
                var isFailure = line.Contains("[Hook] load failed", StringComparison.OrdinalIgnoreCase)
                                || line.Contains("COMPONENT_LOAD_FAILED", StringComparison.OrdinalIgnoreCase);
                if (isFailure)
                {
                    last = line.Trim();
                    sawSuccessAfterFailure = false;
                    continue;
                }
                if (line.Contains("pipe connected", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("login detected", StringComparison.OrdinalIgnoreCase))
                {
                    sawSuccessAfterFailure = last is not null;
                }
            }
            return sawSuccessAfterFailure ? null : last;
        }
        catch { return null; }
    }

    /// <summary>
    /// 本机 QQ 的版本（Linux NTQQ 把版本写在 ~/.config/QQ/versions/config.json）。
    /// 注入失败时带上它，方便对照 SnowLuma 支持的版本或提 issue。
    /// </summary>
    public static string? ReadQqVersion()
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var candidates = new[]
            {
                Path.Combine(home, ".config", "QQ", "versions", "config.json"),
                "/opt/QQ/resources/app/package.json"
            };
            foreach (var path in candidates)
            {
                if (!File.Exists(path))
                    continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var key in new[] { "curVersion", "baseVersion", "version" })
                {
                    if (doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                        && (v.GetString()?.Length ?? 0) > 0)
                        return v.GetString();
                }
            }
        }
        catch { /* 读不到就算了 */ }
        return null;
    }

    /// <summary>
    /// Linux 的 ptrace 限制（/proc/sys/kernel/yama/ptrace_scope）。
    /// 值为 1 时只允许跟踪自己的子进程，而注入 QQ 是"跟踪一个已经在跑的进程" →
    /// 必然失败（日志里就是 COMPONENT_LOAD_FAILED）。返回 null = 非 Linux / 读不到。
    /// </summary>
    public static int? ReadPtraceScope()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return null;
        try
        {
            var path = "/proc/sys/kernel/yama/ptrace_scope";
            return File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out var v) ? v : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// WebUI 是否还在用"初始密码"（config/webui.json 的 mustChangePassword）。
    /// 只有还在用初始密码时才需要/才应该给它指定一个，否则会打扰用户已经改好的密码。
    /// </summary>
    public static bool? ReadWebUiMustChangePassword(string installDir)
    {
        try
        {
            var path = Path.Combine(installDir, "config", "webui.json");
            if (!File.Exists(path))
                return true;    // 还没有这个文件 = 还没设过密码
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("mustChangePassword", out var v))
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
    /// 重置 WebUI 凭据：把 config/webui.json 备份后删掉。
    ///
    /// 为什么必须删：SnowLuma 只在**没有这个文件**时才用
    /// SNOWLUMA_WEBUI_BOOTSTRAP_PASSWORD 播种凭据（源码里 envBootstrapPassword 那段），
    /// 文件已存在时它直接忽略环境变量、沿用旧的哈希 —— 表现就是"重启了密码还是不行"。
    /// </summary>
    public static bool ResetWebUiCredentials(string installDir)
    {
        try
        {
            var path = Path.Combine(installDir, "config", "webui.json");
            if (!File.Exists(path))
                return true;   // 本来就没有：下次启动会用环境变量播种
            var backup = path + ".bak-" + DateTime.Now.ToString("yyyyMMddHHmmss");
            File.Copy(path, backup, overwrite: true);
            File.Delete(path);
            return true;
        }
        catch { return false; }
    }

    /// <summary>日志里有没有"凭据由环境变量播种"的记录（用来确认密码真的生效了）。</summary>
    public static bool WebUiCredentialsSeededFromEnv(string installDir)
        => ReadNewestLogTail(installDir)?.Contains("credentials seeded from SNOWLUMA_WEBUI_BOOTSTRAP_PASSWORD",
            StringComparison.OrdinalIgnoreCase) == true;

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
                if (!text.Contains("index.mjs"))
                    continue;
                if (text.Contains(installDir))
                    return pid;
                // 用相对路径启动时（./node index.mjs）命令行里没有目录，只能看工作目录。
                // 踩过的坑：漏了这种情况 → 认不出已有实例 → 又开一个 → 两个实例抢端口。
                try
                {
                    var cwd = new DirectoryInfo($"/proc/{name}/cwd").LinkTarget;
                    if (cwd is not null
                        && Path.GetFullPath(cwd).TrimEnd('/') == Path.GetFullPath(installDir).TrimEnd('/'))
                        return pid;
                }
                catch { /* 读不到 cwd 就算了 */ }
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
    public Task StartAsync(string installDir, bool acceptAgreements = false,
        string? webUiPassword = null, CancellationToken cancel = default)
    {
        if (_process is { HasExited: false })
            return Task.CompletedTask;

        // 已经在跑（上次应用启动的 / 用户自己启动的）就接管，别再开一个：
        // 两个实例会抢同一个 WebUI 端口，而且「停止」按钮会停不掉真正在跑的那个。
        if (FindRunningPid(installDir) is { } runningPid)
        {
            _adoptedPid = runningPid;
            return Task.CompletedTask;
        }
        var entry = Path.Combine(installDir, "index.mjs");
        if (!File.Exists(entry))
            throw new FileNotFoundException($"SnowLuma 未安装或目录不对：{entry}");
        var node = FindNode(installDir);
        // 不重定向 stdout/stderr：一旦父进程退出，管道就断了，
        // SnowLuma 会陷入 "write EPIPE → 记日志 → 又写管道" 的死循环
        //（实测把它自己的日志刷到 128MB）。要看它的输出请读它的 logs 目录。
        var startInfo = new ProcessStartInfo(node, $"\"{entry}\"")
        {
            WorkingDirectory = installDir,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        // 用户已经同意协议：用 SnowLuma 的官方开关带过去，否则它会一直停在"等待同意"而不注入
        if (acceptAgreements)
        {
            foreach (var (key, value) in SnowlumaAgreements.AcceptanceEnvironment())
                startInfo.Environment[key] = value;
        }
        // 指定 WebUI 初始密码（否则它会随机生成并只打到 stdout，用户看不到）
        if (!string.IsNullOrWhiteSpace(webUiPassword))
            startInfo.Environment[BootstrapPasswordEnv] = webUiPassword;
        _process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
        _process.Start();
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
