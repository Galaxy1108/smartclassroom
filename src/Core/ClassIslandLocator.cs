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
        var dataRoots = DataFolderRoots();
        if (dataRoots.Count > 0)
            return dataRoots.Concat(CandidateRootsStatic()).ToList();
        return CandidateRootsStatic();
    }

    private static IReadOnlyList<string> CandidateRootsStatic()
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
    /// <summary>插件 DLL 应该放的位置：&lt;ClassIsland 根&gt;/Plugins/&lt;插件 id&gt;/。</summary>
    public static string RelativePluginDir => Path.Combine("Plugins", PluginId);

    /// <summary>已存在的 ClassIsland 根目录（用来决定插件往哪装）。</summary>
    public static string? FindExistingRoot(IEnumerable<string>? roots = null)
        => (roots ?? CandidateRoots()).FirstOrDefault(Directory.Exists);

    /// <summary>
    /// 目录包安装（Linux 的 ClassIsland_app_linux_x64_selfContained_folder 那种）里，
    /// 用户数据在 <c>&lt;应用目录&gt;/data</c>：插件放 <c>data/Plugins/&lt;id&gt;/</c>，
    /// 配置（含我们插件的 token）在 <c>data/Config/Plugins/&lt;id&gt;/</c>。
    /// 这里在常见安装位置里找这样的 data 目录。
    /// </summary>
    public static IReadOnlyList<string> DataFolderRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var bases = new List<string>
        {
            Path.Combine(home, "Downloads"),
            Path.Combine(home, "下载"),
            Path.Combine(home, "Applications"),
            Path.Combine(home, "Desktop"),
            Path.Combine(home, "桌面"),
            Path.Combine(home, ".local", "share"),
            "/opt"
        };
        var found = new List<string>();
        foreach (var b in bases.Where(Directory.Exists))
        {
            try
            {
                // 深度有限：data 一般就在应用目录下一层
                foreach (var dir in Directory.EnumerateDirectories(b, "data", SearchOption.AllDirectories)
                             .Where(d => Directory.Exists(Path.Combine(d, "Config"))
                                         && Directory.Exists(Path.Combine(d, "Plugins"))))
                {
                    if (dir.Contains("/Trash/", StringComparison.OrdinalIgnoreCase))
                        continue;   // 回收站里的不算
                    // 它的**父目录本身**得是 ClassIsland 的应用目录（名字带 ClassIsland，
                    // 或者里面直接躺着 ClassIsland 的启动器）——不能只看"旁边有没有 ClassIsland"，
                    // 否则 ~/Downloads/data 这种也会被误认成 ClassIsland 的数据目录。
                    var parent = Path.GetDirectoryName(dir)!;
                    // 只看父目录的名字：目录包安装时它叫 ClassIsland_xxx_selfContained_folder。
                    // 别再去看"旁边有没有 ClassIsland 文件" —— ~/Downloads 里就有那个 zip，
                    // 会把 ~/Downloads/data 误认成 ClassIsland 的数据目录。
                    var parentName = Path.GetFileName(parent);
                    var looksLikeClassIsland =
                        parentName.Contains("ClassIsland", StringComparison.OrdinalIgnoreCase);
                    if (looksLikeClassIsland && !found.Contains(dir))
                        found.Add(dir);
                }
            }
            catch { /* 没权限就跳过 */ }
        }
        return found;
    }

    /// <summary>
    /// 把插件文件装进 ClassIsland。
    /// 从 <paramref name="sourceDir"/>（应用自带的 classisland-plugin 目录）复制到
    /// &lt;ClassIsland 根&gt;/Plugins/&lt;插件 id&gt;/。返回装到了哪里；失败抛异常。
    /// </summary>
    public static string InstallPlugin(string sourceDir, string? root = null)
    {
        var target = root ?? FindExistingRoot();
        if (target is null || !Directory.Exists(target))
        {
            // 不要凭空造一个 ClassIsland 目录出来：宁可告诉用户手动放哪
            throw new InvalidOperationException(
                "没找到 ClassIsland 安装目录。请手动把 classisland-plugin 里的文件复制到 "
                + "ClassIsland 的 Plugins/" + PluginId + " 目录，然后重启 ClassIsland。");
        }
        var dir = Path.Combine(target, RelativePluginDir);
        Directory.CreateDirectory(dir);
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(dir, Path.GetFileName(file)), overwrite: true);
        return dir;
    }

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
