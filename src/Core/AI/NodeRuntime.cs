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

    /// <summary>按优先级找 node 可执行文件；都找不到返回 null。</summary>
    public static string? FindNode(string? appDir = null)
    {
        foreach (var candidate in Candidates(appDir))
        {
            if (File.Exists(candidate))
                return candidate;
        }
        // 回退 PATH：返回裸名字，交给 Process 解析。
        return NodeExeName;
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
        try
        {
            using var p = Process.Start(new ProcessStartInfo(FindNode(appDir) ?? NodeExeName, "--version")
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
