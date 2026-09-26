using System.Reflection;

namespace SmartClassroom.App;

/// <summary>
/// 当前应用版本（取自 csproj 的 &lt;Version&gt;）。
/// 更新检查用它跟 GitHub 上的最新 tag/Release 比较。
/// </summary>
public static class AppVersion
{
    /// <summary>形如 "0.22.0"；读不到就给 "0.0.0"（更新检查会当成"很旧"，不会崩）。</summary>
    public static string Current { get; } = Read();

    private static string Read()
    {
        var info = typeof(AppVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(info))
            return typeof(AppVersion).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        // SourceLink 会附加 "+<commit sha>"，去掉
        var plus = info.IndexOf('+');
        return plus > 0 ? info[..plus] : info;
    }
}
