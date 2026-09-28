using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SmartClassroom.Core.AI;

/// <summary>
/// Node 运行时与 AI 边车定位。
/// 查找顺序：应用自带 node/ → SnowLuma 自带 Node（完整版内置）→ 系统 PATH。
/// pi-ai 要求 Node ≥ 22.19.0。
/// </summary>
public static class NodeRuntime
{
    public const string SidecarFolderName = "ai-sidecar";
    public const string SidecarScriptName = "sidecar.mjs";
    public static readonly Version MinimumNode = new(22, 19, 0);

    public static string NodeExeName =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "node.exe" : "node";

    /// <summary>
    /// 找 node 可执行文件。
    ///
    /// ⚠️ 不能"取第一个存在的"：SnowLuma 完整版自带的 Node 可能比系统里的旧，
    /// 而它排在候选前面 —— 于是部署 SnowLuma 之前一切正常，装完重启就突然
    /// 报"Node 版本过低"（用户实测）。这里改成**把候选都探一遍版本，挑最高的**，
    /// 优先挑满足最低版本要求的。
    /// </summary>
    public static string? FindNode(string? appDir = null)
    {
        var cacheKey = appDir ?? "";
        if (_cachedNode.TryGetValue(cacheKey, out var cached) && File.Exists(cached))
            return cached;

        string? best = null;
        Version? bestVersion = null;
        foreach (var candidate in Candidates(appDir).Append(NodeExeName))
        {
            if (candidate != NodeExeName && !File.Exists(candidate))
                continue;
            var version = ProbeExecutable(candidate);
            if (version is null)
                continue;
            // 先满足最低版本；都满足就比谁更新
            var better = bestVersion is null
                         || (version >= MinimumNode && bestVersion < MinimumNode)
                         || (version >= MinimumNode == bestVersion >= MinimumNode && version > bestVersion);
            if (better)
            {
                best = candidate;
                bestVersion = version;
            }
        }

        var result = best ?? NodeExeName;
        _cachedNode[cacheKey] = result;
        _cachedVersion[cacheKey] = bestVersion;
        return result;
    }

    private static readonly Dictionary<string, string> _cachedNode = new();
    private static readonly Dictionary<string, Version?> _cachedVersion = new();

    /// <summary>探测某个具体可执行文件的版本（内部用，带缓存）。</summary>
    private static Version? ProbeExecutable(string exe)
    {
        if (_cachedVersion.TryGetValue("exe:" + exe, out var cached))
            return cached;
        var v = ProbeVersionOf(exe);
        _cachedVersion["exe:" + exe] = v;
        return v;
    }

    private static IEnumerable<string> Candidates(string? appDir)
    {
        var exe = NodeExeName;
        // 1) 应用内自带（打包附带时）
        if (appDir is not null)
        {
            yield return Path.Combine(appDir, "node", exe);
            yield return Path.Combine(appDir, exe);
        }
        // 2) 应用内下载器装到用户目录的（见 NodeManager.InstallRoot）
        var userNode = NodeManager.InstallRoot;
        yield return Path.Combine(userNode, exe);
        yield return Path.Combine(userNode, "bin", exe);
        // SnowLuma 完整版内置 Node：<data>/SmartClassroom/snowluma/node[/exe]
        var data = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SmartClassroom");
        yield return Path.Combine(data, "snowluma", exe);
        yield return Path.Combine(data, "snowluma", "node", exe);
    }

    /// <summary>探测 node 版本；不可用或版本过低返回 null，可用返回版本号。</summary>
    public static Version? ProbeVersion(string? appDir = null)
    {
        // 命中缓存：FindNode 已经探过一遍，别再起进程
        if (_cachedNode.TryGetValue(appDir ?? "", out var found)
            && _cachedVersion.TryGetValue(appDir ?? "", out var cached))
            return cached;
        return ProbeVersionOf(FindNode(appDir) ?? NodeExeName);
    }

    private static Version? ProbeVersionOf(string exe)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (p is null)
                return null;
            var text = p.StandardOutput.ReadToEnd().Trim().TrimStart('v');
            if (!p.WaitForExit(5000) || p.ExitCode != 0)
                return null;
            return Version.TryParse(text, out var v) ? v : null;
        }
        catch
        {
            return null; // 没装 node / 无法执行
        }
    }

    public static bool IsUsable(string? appDir = null)
    {
        var v = ProbeVersion(appDir);
        return v is not null && v >= MinimumNode;
    }

    /// <summary>边车目录：应用目录下的 ai-sidecar（打包时随包附带）。</summary>
    public static string SidecarDir(string appDir) => Path.Combine(appDir, SidecarFolderName);

    public static bool ScriptExists(string sidecarDir)
        => File.Exists(Path.Combine(sidecarDir, SidecarScriptName));

    /// <summary>npm 依赖是否已就绪（node_modules/@earendil-works/pi-ai）。</summary>
    public static bool DependenciesInstalled(string sidecarDir)
        => Directory.Exists(Path.Combine(sidecarDir, "node_modules", "@earendil-works", "pi-ai"));

    /// <summary>一条可读的就绪状态说明，供设置页显示。</summary>
    public static string DescribeReadiness(string appDir)
    {
        var dir = SidecarDir(appDir);
        if (!ScriptExists(dir))
            return "缺少边车脚本（ai-sidecar/sidecar.mjs）";
        var v = ProbeVersion(appDir);
        if (v is null)
            return "未检测到 Node，请安装 Node ≥ 22.19 或先安装 SnowLuma 完整版（内置 Node）";
        if (v < MinimumNode)
            return $"Node {v} 版本过低，需要 ≥ {MinimumNode}";
        if (!DependenciesInstalled(dir))
            return "Node 就绪，但 pi-ai 依赖未安装（需执行一次 npm install）";
        return $"就绪（Node {v}）";
    }
}
