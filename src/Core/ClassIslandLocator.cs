using System.Runtime.InteropServices;

namespace SmartClassroom.Core;

/// <summary>
/// 定位 ClassIsland 的插件配置目录，取出桥接 token。
/// 路径规则（据 ClassIsland 源码）：
///   &lt;ClassIsland 根&gt;/Config/Plugins/&lt;插件 id&gt;/bridge.token
/// 根目录随安装方式而异，所以按候选列表逐个探测，不做全盘搜索。
/// </summary>
public static class ClassIslandLocator
{
    public const string PluginId = "smartclassroom.bridge";
    public const string TokenFileName = "bridge.token";

    /// <summary>相对 ClassIsland 根的 token 路径。</summary>
    public static string RelativeTokenPath =>
        Path.Combine("Config", "Plugins", PluginId, TokenFileName);

    /// <summary>候选根目录（去重、按优先级）。</summary>
    public static IReadOnlyList<string> CandidateRoots()
    {
        var list = new List<string>();
        void Add(string? p)
        {
            if (!string.IsNullOrWhiteSpace(p) && !list.Contains(p, StringComparer.Ordinal))
                list.Add(p);
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // 应用数据目录（ClassIsland 的 AppDataFolderPath）
        Add(Path.Combine(localAppData, "ClassIsland"));

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Add(Path.Combine(appData, "ClassIsland"));
            Add(Path.Combine(localAppData, "Programs", "ClassIsland"));
            Add(@"C:\ClassIsland");
            foreach (var pf in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                     })
                Add(Path.Combine(pf, "ClassIsland"));
        }
        else
        {
            // 便携版通常解压在用户目录；常见安装位置一并探测。
            Add(Path.Combine(home, "ClassIsland"));
            Add(Path.Combine(home, ".local", "share", "ClassIsland"));
            Add(Path.Combine(home, "Applications", "ClassIsland"));
            Add("/opt/ClassIsland");
            Add("/opt/classisland");
            Add("/usr/share/ClassIsland");
        }

        return list;
    }

    /// <summary>在候选根下找 token 文件；找不到返回 null。</summary>
    public static string? FindTokenFile(IEnumerable<string>? roots = null)
    {
        foreach (var root in roots ?? CandidateRoots())
        {
            try
            {
                var candidate = Path.Combine(root, RelativeTokenPath);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {
                // 路径非法（权限/字符）时跳过
            }
        }
        return null;
    }

    /// <summary>读取 token 内容；文件不存在或读失败返回 null。</summary>
    public static string? TryReadToken(IEnumerable<string>? roots = null)
    {
        var file = FindTokenFile(roots);
        if (file is null)
            return null;
        try
        {
            var token = File.ReadAllText(file).Trim();
            return token.Length > 0 ? token : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>给用户看的说明：找到了就给路径，没找到就把探测过的位置列出来。</summary>
    public static string ExplainSearch(IEnumerable<string>? roots = null)
    {
        var list = (roots ?? CandidateRoots()).ToList();
        var file = FindTokenFile(list);
        if (file is not null)
            return $"已找到 token 文件：\n{file}";

        var lines = string.Join("\n", list.Select(r => "  · " + Path.Combine(r, RelativeTokenPath)));
        return "未找到 token 文件。已探测以下位置：\n" + lines +
               "\n\n提示：token 在 ClassIsland 首次加载本插件时生成；" +
               "若插件尚未加载过，请先启动一次 ClassIsland。";
    }
}
