using System.Text.Json;

namespace SmartClassroom.Core;

/// <summary>应用设置（AI / OneBot / 归档 / 教师映射），JSON 落盘。
/// 默认路径 LocalApplicationData/SmartClassroom/settings.json；密钥明文存放（单机教室机，文档注明）。</summary>
public sealed class AppSettings
{
    public string AiBaseUrl { get; set; } = "";
    public string AiApiKey { get; set; } = "";
    public string AiModel { get; set; } = "";

    /// <summary>"http"（默认，手写网关）或 "pi-ai"（Node 边车）。</summary>
    public string AiEngine { get; set; } = "http";

    /// <summary>pi-ai provider id（如 deepseek、openai）；为空时走 AiBaseUrl 自定义端点。</summary>
    public string AiProvider { get; set; } = "";

    public string OneBotHttp { get; set; } = "http://127.0.0.1:3000";
    public string OneBotWs { get; set; } = "ws://127.0.0.1:3001";
    public string OneBotToken { get; set; } = "";
    public List<long> GroupIds { get; set; } = [];
    public string ArchiveRoot { get; set; } = "";

    /// <summary>ClassIsland 插件桥接 token（插件首次启动生成，写在插件配置目录 bridge.token）。</summary>
    public string PluginToken { get; set; } = "";

    /// <summary>插件桥接端口，默认 5199。</summary>
    public int PluginPort { get; set; } = 5199;

    public List<Teacher> Teachers { get; set; } = [];
    public bool RiskAccepted { get; set; } = false;

    // ---- 功能开关：默认全部关闭 ----
    // 这些功能会读班级群消息并产生副作用（发通知、落课、下文件），必须显式开启。
    public bool FeatureSummon { get; set; } = false;
    public bool FeatureHomework { get; set; } = false;
    public bool FeatureExchange { get; set; } = false;
    public bool FeatureFileArchive { get; set; } = false;
    public bool FeatureCoursewarePopup { get; set; } = false;

    // ---- 关闭行为 / 管理员密码 ----

    /// <summary>关闭主窗口时收回到托盘（默认开启）。托盘不可用时自动退化为直接退出。</summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>管理员密码的 PBKDF2 哈希；为空表示未设置（不拦截任何操作）。</summary>
    public string AdminPasswordHash { get; set; } = "";

    /// <summary>把设置里的开关投影成管线用的 FeatureFlags。</summary>
    public FeatureFlags ToFeatureFlags() => new()
    {
        Summon = FeatureSummon,
        Homework = FeatureHomework,
        Exchange = FeatureExchange,
        FileArchive = FeatureFileArchive,
        CoursewarePopup = FeatureCoursewarePopup
    };
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SmartClassroom", "settings.json");

    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new AppSettings();
        }
        catch { /* 损坏则回默认 */ }
        return new AppSettings();
    }

    public static void Save(AppSettings settings, string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(settings, Json));
    }
}
