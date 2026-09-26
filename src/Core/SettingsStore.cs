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

    /// <summary>pi-ai 推理强度（minimal/low/medium/high）。默认 minimal，避免"只有思考、没有输出"。</summary>
    public string AiReasoning { get; set; } = "minimal";

    public string OneBotHttp { get; set; } = "http://127.0.0.1:3000";
    public string OneBotWs { get; set; } = "ws://127.0.0.1:3001";
    public string OneBotToken { get; set; } = "";
    public List<long> GroupIds { get; set; } = [];

    /// <summary>
    /// 用户自己指定的 SnowLuma WebUI 初始密码（空 = 由应用随机生成一个）。
    /// 启动时通过官方环境变量 SNOWLUMA_WEBUI_BOOTSTRAP_PASSWORD 传给它。
    /// </summary>
    public string SnowLumaWebUiPassword { get; set; } = "";

    /// <summary>
    /// 已同意的 SnowLuma 协议版本号（空 = 没同意过）。
    /// 就是 SnowLuma 自己算的那个内容哈希：协议文本一改版本就变，会重新征得同意。
    /// </summary>
    public string SnowLumaAgreementsVersion { get; set; } = "";

    /// <summary>选定的班级 QQ 账号（0 = 未指定）。一台机器上登过好几个号时用它区分。</summary>
    public long QqAccount { get; set; }

    /// <summary>见过的账号（检测到的 + 手填的），下次打开选择框时作为候选。</summary>
    public List<QqAccount> QqAccounts { get; set; } = [];
    /// <summary>群文件归档根目录；留空用默认（&lt;LocalAppData&gt;/SmartClassroom/archive）。</summary>
    public string ArchiveRoot { get; set; } = "";

    /// <summary>true = 下载群里所有人的文件；false（默认）= 只下载教师映射命中的发送者。</summary>
    public bool ArchiveDownloadAll { get; set; } = false;


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

    /// <summary>界面与字体缩放（0.8~1.6，默认 1.0）。</summary>
    public double UiScale { get; set; } = 1.0;

    /// <summary>管理员密码的 PBKDF2 哈希；为空表示未设置（不拦截任何操作）。</summary>
    public string AdminPasswordHash { get; set; } = "";

    /// <summary>
    /// 恢复默认值（"清空所有设置"用）。
    /// 必须连**内存里那份**一起复位：设置页与 Runtime 共用同一个对象，
    /// 只删文件的话退出时 Runtime 又会把旧值写回去，等于没清。
    /// </summary>
    public void ResetToDefaults()
    {
        var d = new AppSettings();
        AiBaseUrl = d.AiBaseUrl;
        AiApiKey = d.AiApiKey;
        AiModel = d.AiModel;
        AiEngine = d.AiEngine;
        AiProvider = d.AiProvider;
        AiReasoning = d.AiReasoning;
        OneBotHttp = d.OneBotHttp;
        OneBotWs = d.OneBotWs;
        OneBotToken = d.OneBotToken;
        GroupIds = [];
        QqAccount = 0;
        QqAccounts = [];
        SnowLumaAgreementsVersion = "";
        SnowLumaWebUiPassword = "";
        ArchiveRoot = d.ArchiveRoot;
        ArchiveDownloadAll = d.ArchiveDownloadAll;
        PluginToken = d.PluginToken;
        PluginPort = d.PluginPort;
        Teachers = [];
        RiskAccepted = d.RiskAccepted;
        FeatureSummon = d.FeatureSummon;
        FeatureHomework = d.FeatureHomework;
        FeatureExchange = d.FeatureExchange;
        FeatureFileArchive = d.FeatureFileArchive;
        FeatureCoursewarePopup = d.FeatureCoursewarePopup;
        MinimizeToTray = d.MinimizeToTray;
        UiScale = d.UiScale;
        AdminPasswordHash = d.AdminPasswordHash;
    }

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

/// <summary>记住的 QQ 账号（候选列表用）。</summary>
public sealed class QqAccount
{
    public long Uin { get; set; }
    public string Nickname { get; set; } = "";
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
        catch
        {
            // 坏文件不能默默当"没配过"——那会把 API Key 之类的配置一起丢掉。
            // 留一份 .bad 供排查，同时照常返回默认值让应用能起来。
            TryBackup(path);
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings, string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // 原子替换：写临时文件再覆盖，避免写一半断电留下坏文件（坏文件 = 配置全丢）。
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>删除设置文件（"清空所有设置"）。不存在也不报错。</summary>
    public static void Reset(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
                File.Delete(path);
            var tmp = path + ".tmp";
            if (File.Exists(tmp))
                File.Delete(tmp);
        }
        catch { /* 删不掉就让 Load 的默认值兜底 */ }
    }

    private static void TryBackup(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Copy(path, path + ".bad", overwrite: true);
        }
        catch { /* 备份失败不影响启动 */ }
    }
}
