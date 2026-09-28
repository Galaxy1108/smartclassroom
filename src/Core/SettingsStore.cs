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

    /// <summary>
    /// OneBot **WS** token。SnowLuma 给 HTTP 与 WS 各发一个 token（实测不同），
    /// 只填 HTTP 那个会让 WS 升级被拒（401）→ 事件收不到。留空则退回用 <see cref="OneBotToken"/>。
    /// </summary>
    public string OneBotWsToken { get; set; } = "";
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

    /// <summary>
    /// true = 也处理**老师私聊**（默认 false）。只认老师映射里的 QQ，
    /// 陌生人私聊一律忽略（名单为空时不做过滤）。
    /// </summary>
    public bool ListenTeacherPrivate { get; set; } = false;

    /// <summary>
    /// 老师名单里的人发的消息，没命中关键词也交给 AI 判断（默认 true）。
    /// </summary>
    public bool AiDecidesTeacherMessages { get; set; } = true;

    /// <summary>
    /// 只处理老师名单里的人发的消息（默认 true）。
    /// 关掉它，群里任何人都能触发换课/作业/召唤。
    /// </summary>
    public bool RequireKnownTeacher { get; set; } = true;

    /// <summary>true = 监听该账号所在的**全部群**（默认 false：只监听 GroupIds 里列的群）。</summary>
    public bool ListenAllGroups { get; set; } = false;

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

    /// <summary>老师通知转发（活动/集合/催交等 → ClassIsland 提醒）。默认关。</summary>
    public bool FeatureNotice { get; set; } = false;

    /// <summary>Windows：阻止普通用户用任务管理器结束本应用（默认关）。</summary>
    public bool ProtectFromKill { get; set; } = false;

    /// <summary>作业上墙后给同学们发一条 ClassIsland 通知（默认关）。</summary>
    public bool NotifyHomework { get; set; } = false;

    /// <summary>换课成功/需要人工处理后发一条通知（默认关）。</summary>
    public bool NotifyExchange { get; set; } = false;

    /// <summary>
    /// 归档后额外复制一份到系统"下载"目录（默认开）。
    /// 老师常在 QQ 里直接点开文件，而 QQ 只认自己下载目录里已有的文件。
    /// </summary>
    public bool CopyToDownloads { get; set; } = true;

    /// <summary>
    /// 界面主题：<c>default</c>（跟随系统）/ <c>light</c> / <c>dark</c>。
    /// </summary>
    public string Theme { get; set; } = "default";

    /// <summary>
    /// 解锁后 10 分钟内免重复输入管理员密码。**默认 false = 每次操作都要密码**。
    /// </summary>
    public bool RememberUnlock { get; set; } = false;

    /// <summary>
    /// 科目颜色（科目名 → #RRGGBB）。没配的科目按科目名稳定取默认色板里的颜色。
    /// 用户要求："我希望我能修改作业卡片的颜色"。
    /// </summary>
    public Dictionary<string, string> SubjectColors { get; set; } = new();

    // ---- 关闭行为 / 管理员密码 ----

    /// <summary>关闭主窗口时收回到托盘（默认开启）。托盘不可用时自动退化为直接退出。</summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>界面与字体缩放（0.8~1.6，默认 1.0）。</summary>
    public double UiScale { get; set; } = 1.0;

    /// <summary>应用自身的开机自启（Linux 写 ~/.config/autostart，Windows 写注册表 Run）。</summary>
    public bool AutoStart { get; set; } = false;

    /// <summary>作业卡片独立缩放（与整窗缩放分开）。</summary>
    public double CardScale { get; set; } = 1.0;

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
        OneBotWsToken = d.OneBotWsToken;
        GroupIds = [];
        ListenAllGroups = false;
        ListenTeacherPrivate = false;
        RequireKnownTeacher = true;
        AiDecidesTeacherMessages = true;
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
        FeatureNotice = d.FeatureNotice;
        ProtectFromKill = d.ProtectFromKill;
        NotifyHomework = d.NotifyHomework;
        NotifyExchange = d.NotifyExchange;
        SubjectColors = new Dictionary<string, string>(d.SubjectColors);
        RememberUnlock = d.RememberUnlock;
        Theme = d.Theme;
        CardScale = d.CardScale;
        AutoStart = d.AutoStart;
        CopyToDownloads = d.CopyToDownloads;
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
        CoursewarePopup = FeatureCoursewarePopup,
        Notice = FeatureNotice,
        NotifyHomework = NotifyHomework,
        NotifyExchange = NotifyExchange,
        RequireKnownTeacher = RequireKnownTeacher,
        AiDecidesTeacherMessages = AiDecidesTeacherMessages
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

    /// <summary>设置保存后触发（App 层据此把老师名单/功能开关**热应用**，不必重启）。</summary>
    public static event Action<AppSettings>? Saved;

    public static void Save(AppSettings settings, string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // 原子替换：写临时文件再覆盖，避免写一半断电留下坏文件（坏文件 = 配置全丢）。
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
        File.Move(tmp, path, overwrite: true);
        try { Saved?.Invoke(settings); } catch { /* 订阅方出错不影响保存 */ }
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
