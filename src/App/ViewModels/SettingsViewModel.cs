using System.Collections.ObjectModel;
using System.IO;
using SmartClassroom.Core;
using SmartClassroom.Core.AI;
using SmartClassroom.Core.QQ;

namespace SmartClassroom.App.ViewModels;

public sealed record AiProviderPreset(string Name, string BaseUrl);
public sealed record TeacherRow(string Qq, string Name, string Subject);
public sealed record EngineOption(AiEngine Engine, string Title, string Description);

/// <summary>
/// 设置页：AI（双引擎）/ 教师映射 / QQ 连接与 SnowLuma 下载器 / ClassIsland 集成。
/// 改动即落盘 settings.json；网络与进程操作全异步，失败只写日志行，不抛给 UI。
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private readonly SnowlumaManager _manager = new();
    private readonly NodeManager _node = new();
    private readonly string _appDir;
    private string _log = "";
    private string _status = "未检测";
    private double _progress;
    private bool _busy;
    private bool _riskAccepted;
    private bool _qqDetected;
    private string _pluginStatus = "未检测";
    private string _sidecarInfo = "";
    private string _aiTestResult = "";
    private bool _isNodeReady;
    private bool _isLocked;
    private bool _minimizeToTray = true;
    private string _passwordResult = "";
    private bool _featureSummon;
    private bool _featureHomework;
    private bool _featureExchange;
    private bool _featureFileArchive;
    private bool _featureCoursewarePopup;
    private double _nodeProgress;

    public SettingsViewModel() : this(SettingsStore.DefaultPath, AppContext.BaseDirectory) { }

    public SettingsViewModel(string settingsPath, string? appDir = null)
    {
        SettingsPath = settingsPath;
        _appDir = appDir ?? AppContext.BaseDirectory;
        _manager.OnLog += line => AppendLog(line);
        InstallDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SmartClassroom", "snowluma");
        LoadSettings();
        AutostartEnabled = AutostartManager.IsEnabled(InstallDir);
        RefreshSidecarInfo();
        RefreshNodeStatus();
        OnPropertyChanged(nameof(FeatureSummary));
    }

    public string SettingsPath { get; }

    private void AppendLog(string line) => Log += line + "\n";

    // ================= 管理员密码 / 关闭行为 =================

    /// <summary>设置了密码且当前会话未认证时，设置内容整体锁定（只读）。</summary>
    public bool IsLocked { get => _isLocked; private set => Set(ref _isLocked, value); }

    public bool HasPassword => !string.IsNullOrWhiteSpace(_adminHash);

    /// <summary>仅供测试断言落盘的是哈希而非明文。</summary>
    internal string AdminHashForTest() => _adminHash;

    private string _adminHash = "";
    private string _newPassword = "";
    private string _confirmPassword = "";

    public string NewPassword { get => _newPassword; set => Set(ref _newPassword, value); }
    public string ConfirmPassword { get => _confirmPassword; set => Set(ref _confirmPassword, value); }
    public string PasswordResult { get => _passwordResult; private set => Set(ref _passwordResult, value); }

    public string PasswordWatermark => HasPassword ? "新密码（留空清除）" : "设置密码";

    /// <summary>关闭主窗口时收回到托盘。</summary>
    public bool MinimizeToTray
    {
        get => _minimizeToTray;
        set
        {
            if (!Set(ref _minimizeToTray, value))
                return;
            Runtime.Settings.MinimizeToTray = value;   // AppShell 直接读这个值
            SaveSettings();
        }
    }

    /// <summary>托盘在当前桌面环境是否可用（不可用时关闭窗口即退出）。</summary>
    public string TrayHint => AppShell.TrayAvailable
        ? "关闭主窗口会收回到托盘；从托盘菜单可重新打开或退出。"
        : "当前桌面环境未提供系统托盘，关闭主窗口将直接退出应用。";

    /// <summary>刷新锁定状态（打开设置页 / 认证成功后调用）。</summary>
    public void RefreshLockState() => IsLocked = Runtime.Auth.IsEnabled && !Runtime.Auth.IsUnlocked;

    /// <summary>设置或清除管理员密码。</summary>
    public void ApplyPassword()
    {
        var NewPasswordSnapshot = NewPassword;   // 仅用于本次会话解锁，不落盘
        if (NewPassword.Length == 0)
        {
            // 空 = 清除密码
            _adminHash = "";
            PasswordResult = "已清除管理员密码（不再拦截操作）。";
        }
        else if (NewPassword != ConfirmPassword)
        {
            PasswordResult = "两次输入的密码不一致。";
            return;
        }
        else if (NewPassword.Length < 4)
        {
            PasswordResult = "密码至少 4 位。";
            return;
        }
        else
        {
            _adminHash = PasswordHasher.Hash(NewPassword);
            PasswordResult = "管理员密码已设置。";
        }
        NewPassword = "";
        ConfirmPassword = "";
        // Runtime.Settings 与设置页是两份实例，必须同步，否则认证门读不到新密码。
        Runtime.Settings.AdminPasswordHash = _adminHash;
        Runtime.Settings.MinimizeToTray = MinimizeToTray;
        if (_adminHash.Length == 0)
        {
            Runtime.Auth.Lock();
        }
        else
        {
            // 刚刚输入过密码，本次会话直接放行，避免设置完立刻被自己锁在外面。
            Runtime.Auth.Lock();
            Runtime.Auth.TryUnlock(NewPasswordSnapshot);
        }
        SaveSettings();
        RefreshLockState();
        OnPropertyChanged(nameof(HasPassword));
        OnPropertyChanged(nameof(PasswordWatermark));
    }

    // ================= 功能开关（默认全关） =================
    //
    // 这些能力会读班级群消息并产生副作用（发通知、改课表、下文件），
    // 所以默认全部关闭，由用户逐项开启。

    public bool FeatureSummon
    {
        get => _featureSummon;
        set { if (Set(ref _featureSummon, value)) OnFeatureChanged(); }
    }

    public bool FeatureHomework
    {
        get => _featureHomework;
        set { if (Set(ref _featureHomework, value)) OnFeatureChanged(); }
    }

    public bool FeatureExchange
    {
        get => _featureExchange;
        set { if (Set(ref _featureExchange, value)) OnFeatureChanged(); }
    }

    public bool FeatureFileArchive
    {
        get => _featureFileArchive;
        set { if (Set(ref _featureFileArchive, value)) OnFeatureChanged(); }
    }

    public bool FeatureCoursewarePopup
    {
        get => _featureCoursewarePopup;
        set { if (Set(ref _featureCoursewarePopup, value)) OnFeatureChanged(); }
    }

    /// <summary>当前开关摘要（保存后重启生效）。</summary>
    public string FeatureSummary => CurrentFlags().Describe();

    private void OnFeatureChanged()
    {
        SaveSettings();
        OnPropertyChanged(nameof(FeatureSummary));
    }

    private FeatureFlags CurrentFlags() => new()
    {
        Summon = FeatureSummon,
        Homework = FeatureHomework,
        Exchange = FeatureExchange,
        FileArchive = FeatureFileArchive,
        CoursewarePopup = FeatureCoursewarePopup
    };

    // ================= AI =================

    public List<EngineOption> Engines { get; } =
    [
        new(AiEngine.HttpGateway, "内置直连（默认）",
            "直接调用 OpenAI 兼容的 /chat/completions，零额外依赖。填地址+Key+模型即可。"),
        new(AiEngine.PiAiSidecar, "pi-ai（Node 边车）",
            "经 Node 边车使用 @earendil-works/pi-ai：自带 provider 与模型目录、统一鉴权与用量统计。需要 Node ≥ 22.19。"),
    ];

    private EngineOption _engineOption = null!;
    public EngineOption EngineOption
    {
        get => _engineOption;
        set
        {
            if (!Set(ref _engineOption, value))
                return;
            SaveSettings();
            OnPropertyChanged(nameof(AiEngine));
            OnPropertyChanged(nameof(IsPiAi));
            OnPropertyChanged(nameof(IsHttpGateway));
            OnPropertyChanged(nameof(ShowSidecarSettings));
        }
    }

    public AiEngine AiEngine => EngineOption.Engine;
    public bool IsPiAi => AiEngine == AiEngine.PiAiSidecar;
    public bool IsHttpGateway => AiEngine == AiEngine.HttpGateway;

    /// <summary>选 pi-ai 时显示边车相关设置。</summary>
    public bool ShowSidecarSettings => IsPiAi;

    /// <summary>边车就绪说明（Node 版本 / 依赖是否装好）。</summary>
    public string SidecarInfo { get => _sidecarInfo; private set => Set(ref _sidecarInfo, value); }

    public ObservableCollection<SidecarProvider> PiProviders { get; } = new();
    public ObservableCollection<SidecarModel> PiModels { get; } = new();

    private SidecarProvider? _selectedProvider;
    public SidecarProvider? SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            if (!Set(ref _selectedProvider, value))
                return;
            if (value is not null)
            {
                AiModelProviderHint = value.Id;
                SaveSettings();
            }
            _ = LoadModelsAsync();
        }
    }

    private SidecarModel? _selectedModel;
    public SidecarModel? SelectedModel
    {
        get => _selectedModel;
        set
        {
            if (!Set(ref _selectedModel, value) || value is null)
                return;
            AiModel = value.Id;
            SaveSettings();
        }
    }

    private string _aiBaseUrl = "";
    public string AiBaseUrl { get => _aiBaseUrl; set => Set(ref _aiBaseUrl, value); }

    private string _aiApiKey = "";
    public string AiApiKey { get => _aiApiKey; set => Set(ref _aiApiKey, value); }

    private string _aiModel = "";
    public string AiModel { get => _aiModel; set => Set(ref _aiModel, value); }

    public string AiTestResult { get => _aiTestResult; private set => Set(ref _aiTestResult, value); }

    public List<AiProviderPreset> AiProviders { get; } =
    [
        new("OpenAI 官方", "https://api.openai.com/v1"),
        new("DeepSeek", "https://api.deepseek.com/v1"),
        new("通义千问（兼容模式）", "https://dashscope.aliyuncs.com/compatible-mode/v1"),
        new("自定义", ""),
    ];

    private AiProviderPreset _aiProvider = null!;
    public AiProviderPreset AiProvider
    {
        get => _aiProvider;
        set
        {
            if (!Set(ref _aiProvider, value))
                return;
            if (value.Name != "自定义" && value.BaseUrl.Length > 0)
                AiBaseUrl = value.BaseUrl;
        }
    }

    public List<string> ModelPresets { get; } =
        ["gpt-4o-mini", "deepseek-chat", "qwen-flash", "qwen-plus"];

    /// <summary>pi-ai provider 提示（未联网拉目录时也记住上次选择）。</summary>
    private string AiModelProviderHint { get; set; } = "deepseek";

    public void RefreshSidecarInfo() => SidecarInfo = NodeRuntime.DescribeReadiness(_appDir);

    /// <summary>从 pi-ai 拉 provider 列表（需要 Node 与依赖就绪）。</summary>
    public async Task LoadCatalogAsync()
    {
        RefreshSidecarInfo();
        if (!NodeRuntime.DependenciesInstalled(NodeRuntime.SidecarDir(_appDir)))
        {
            AppendLog("pi-ai 依赖未安装：请在 ai-sidecar 目录执行一次 npm install。");
            return;
        }
        Busy = true;
        try
        {
            await using var client = NewSidecar();
            var providers = await client.ListProvidersAsync();
            PiProviders.Clear();
            foreach (var p in providers)
                PiProviders.Add(p);
            AppendLog($"pi-ai 提供 {providers.Length} 个 provider。");
            SelectedProvider = PiProviders.FirstOrDefault(p => p.Id == AiModelProviderHint)
                ?? PiProviders.FirstOrDefault();
        }
        catch (Exception ex) { AppendLog($"拉取 provider 失败：{ex.Message}"); }
        finally { Busy = false; }
    }

    private async Task LoadModelsAsync()
    {
        if (SelectedProvider is null)
            return;
        try
        {
            await using var client = NewSidecar();
            var models = await client.ListModelsAsync(SelectedProvider.Id);
            PiModels.Clear();
            foreach (var m in models)
                PiModels.Add(m);
            AppendLog($"{SelectedProvider.Id}: {models.Length} 个模型。");
        }
        catch (Exception ex) { AppendLog($"拉取模型失败：{ex.Message}"); }
    }

    /// <summary>用当前引擎发一条最小请求，验证配置可用。</summary>
    public async Task TestAiAsync()
    {
        AiTestResult = "测试中…";
        try
        {
            string text;
            if (IsPiAi)
            {
                await using var client = NewSidecar();
                text = await client.AskAsync("只回复两个字：可用", "测试");
            }
            else
            {
                var gw = new AiGateway(new AiOptions { BaseUrl = AiBaseUrl, ApiKey = AiApiKey, Model = AiModel });
                text = await gw.AskAsync("只回复两个字：可用", "测试");
            }
            AiTestResult = text.Length > 0 ? $"可用：{Trim(text)}" : "可用，但返回为空";
        }
        catch (Exception ex)
        {
            AiTestResult = $"失败：{ex.Message}";
        }
    }

    private static string Trim(string s) => s.Length > 40 ? s[..40] + "…" : s;

    private PiAiSidecarClient NewSidecar()
    {
        var client = new PiAiSidecarClient(new SidecarOptions
        {
            SidecarDir = NodeRuntime.SidecarDir(_appDir),
            Provider = SelectedProvider?.Id ?? AiModelProviderHint,
            Model = AiModel,
            ApiKey = AiApiKey,
            BaseUrl = AiBaseUrl
        });
        client.OnLog += l =>
        {
            if (!l.Contains("ready"))
                AppendLog("[pi-ai] " + l);
        };
        return client;
    }

    // ================= Node 运行时（pi-ai 前置依赖） =================

    public ObservableCollection<NodeRelease> NodeReleases { get; } = new();

    private NodeRelease? _selectedNode;
    public NodeRelease? SelectedNode { get => _selectedNode; set => Set(ref _selectedNode, value); }

    private string _nodeStatusText = "";
    public string NodeStatusText { get => _nodeStatusText; private set => Set(ref _nodeStatusText, value); }

    public double NodeProgress { get => _nodeProgress; private set => Set(ref _nodeProgress, value); }

    /// <summary>Node 是否可用（版本 ≥ 22.19）。</summary>
    public bool IsNodeReady { get => _isNodeReady; private set => Set(ref _isNodeReady, value); }

    /// <summary>Node 不可用时才显示提示与下载器。</summary>
    public bool ShowNodeInstaller => !IsNodeReady;

    public void RefreshNodeStatus()
    {
        var version = NodeRuntime.ProbeVersion(_appDir);
        IsNodeReady = version is not null && version >= NodeRuntime.MinimumNode;
        NodeStatusText = NodeRuntime.DescribeReadiness(_appDir);
        OnPropertyChanged(nameof(ShowNodeInstaller));
        OnPropertyChanged(nameof(SidecarInfo));
        if (IsNodeReady)
            AppendLog($"Node 就绪：v{version}");
    }

    /// <summary>拉取 nodejs.org 的 LTS 列表。</summary>
    public async Task LoadNodeVersionsAsync()
    {
        try
        {
            NodeReleases.Clear();
            foreach (var r in await _node.ListLtsAsync())
                NodeReleases.Add(r);
            SelectedNode = NodeReleases.FirstOrDefault();
            AppendLog($"可下载 Node LTS 版本 {NodeReleases.Count} 个。");
        }
        catch (Exception ex) { AppendLog($"拉取 Node 版本失败：{ex.Message}"); }
    }

    /// <summary>下载并解压 Node 到用户数据目录，随后自动重新探测。</summary>
    public async Task DownloadNodeAsync()
    {
        if (SelectedNode is null)
        {
            await LoadNodeVersionsAsync();
            if (SelectedNode is null)
                return;
        }
        var rid = NodeManager.Rid();
        var version = SelectedNode.Version;
        var archive = Path.Combine(NodeManager.InstallRoot + ".dl", NodeManager.ArchiveName(version, rid));
        try
        {
            Busy = true;
            AppendLog($"下载 Node {version}（{rid}）…");
            var prog = new Progress<double>(p => NodeProgress = p * 100);
            await _node.DownloadAsync(NodeManager.DownloadUrl(version, rid), archive, prog, CancellationToken.None);
            AppendLog("解压 Node…");
            await Task.Run(() => NodeManager.ExtractFlattened(archive, NodeManager.InstallRoot));
            AppendLog("Node 安装完成。");
            RefreshNodeStatus();
        }
        catch (Exception ex) { AppendLog($"Node 安装失败：{ex.Message}"); }
        finally { Busy = false; NodeProgress = 0; }
    }

    // ================= ClassIsland 集成 =================

    private string _pluginToken = "";
    public string PluginToken { get => _pluginToken; set => Set(ref _pluginToken, value); }

    private int _pluginPort = 5199;
    public int PluginPort { get => _pluginPort; set => Set(ref _pluginPort, value); }

    public string PluginStatus { get => _pluginStatus; private set => Set(ref _pluginStatus, value); }

    /// <summary>探测 ClassIsland 插件桥接是否在线。</summary>
    public async Task ProbePluginAsync()
    {
        PluginStatus = "探测中…";
        try
        {
            var link = new PluginLink($"http://127.0.0.1:{PluginPort}", PluginToken);
            var status = await link.StatusAsync();
            PluginStatus = status is null
                ? "未连接（请确认 ClassIsland 已启动、插件已加载、端口与 token 正确）"
                : $"已连接 · 插件 v{status.PluginVersion} · 课表{(status.ClassPlanLoaded ? "已加载" : "未加载")}";
        }
        catch (Exception ex) { PluginStatus = $"探测失败：{ex.Message}"; }
    }

    /// <summary>
    /// 查找 ClassIsland 的 bridge.token。找到就自动填入并返回消息，找不到则返回探测过的位置。
    /// 调用方负责用弹窗展示。
    /// </summary>
    public (bool Found, string Message) LocatePluginToken()
    {
        var token = ClassIslandLocator.TryReadToken();
        var explanation = ClassIslandLocator.ExplainSearch();
        if (token is null)
        {
            AppendLog(explanation);
            return (false, explanation);
        }
        PluginToken = token;
        SaveSettings();
        AppendLog("已自动填入插件 token 并保存。");
        return (true, explanation + "\n\ntoken 已自动填入并保存。");
    }

    // ================= 教师映射 =================

    public ObservableCollection<TeacherRow> Teachers { get; } = new();

    private string _newQq = "";
    public string NewTeacherQq { get => _newQq; set => Set(ref _newQq, value); }

    private string _newName = "";
    public string NewTeacherName { get => _newName; set => Set(ref _newName, value); }

    private string _newSubject = "";
    public string NewTeacherSubject { get => _newSubject; set => Set(ref _newSubject, value); }

    public void AddTeacher()
    {
        if (NewTeacherName.Trim().Length == 0 || NewTeacherSubject.Trim().Length == 0)
        {
            AppendLog("老师姓名和科目不能为空。");
            return;
        }
        Teachers.Add(new TeacherRow(NewTeacherQq.Trim(), NewTeacherName.Trim(), NewTeacherSubject.Trim()));
        NewTeacherQq = NewTeacherName = NewTeacherSubject = "";
        SaveSettings();
    }

    public void RemoveTeacher(TeacherRow row)
    {
        Teachers.Remove(row);
        SaveSettings();
    }

    // ================= QQ 连接 / SnowLuma =================

    public string InstallDir { get; set; }
    public ObservableCollection<SnowlumaRelease> Releases { get; } = new();

    private SnowlumaRelease? _selected;
    public SnowlumaRelease? SelectedRelease { get => _selected; set => Set(ref _selected, value); }

    public string Status { get => _status; private set => Set(ref _status, value); }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public bool Busy { get => _busy; private set => Set(ref _busy, value); }
    public string Log { get => _log; private set => Set(ref _log, value); }

    public bool IsInstalled => File.Exists(Path.Combine(InstallDir, "index.mjs"));

    public bool RiskAccepted
    {
        get => _riskAccepted;
        set { if (Set(ref _riskAccepted, value)) SaveSettings(); }
    }

    public bool QqDetected { get => _qqDetected; private set => Set(ref _qqDetected, value); }

    private string _oneBotHttp = "http://127.0.0.1:3000";
    public string OneBotHttp { get => _oneBotHttp; set => Set(ref _oneBotHttp, value); }

    private string _oneBotWs = "ws://127.0.0.1:3001";
    public string OneBotWs { get => _oneBotWs; set => Set(ref _oneBotWs, value); }

    private string _groupIds = "";
    public string GroupIdsText { get => _groupIds; set => Set(ref _groupIds, value); }

    private bool _autostart;
    public bool AutostartEnabled
    {
        get => _autostart;
        set
        {
            if (!Set(ref _autostart, value))
                return;
            try
            {
                AutostartManager.SetEnabled(InstallDir, SnowlumaManager.FindNode(InstallDir), value);
                AppendLog(value ? "已开启 SnowLuma 开机自启。" : "已关闭 SnowLuma 开机自启。");
            }
            catch (Exception ex) { AppendLog($"自启设置失败：{ex.Message}"); }
        }
    }

    public async Task RefreshReleasesAsync()
    {
        Busy = true;
        try
        {
            Releases.Clear();
            foreach (var r in await _manager.ListReleasesAsync())
                Releases.Add(r);
            SelectedRelease = Releases.Count > 0 ? Releases[0] : null;
            AppendLog($"找到 {Releases.Count} 个版本。");
        }
        catch (Exception ex) { AppendLog($"拉取版本失败：{ex.Message}"); }
        finally { Busy = false; }
    }

    public async Task DownloadSelectedAsync()
    {
        if (SelectedRelease is null || Busy)
            return;
        var asset = SnowlumaManager.PickAsset(SelectedRelease, SnowlumaManager.CurrentRid());
        if (asset is null)
        {
            AppendLog("当前平台无可用包。");
            return;
        }
        Busy = true;
        try
        {
            var archive = Path.Combine(InstallDir, "_dl", asset.Name);
            var prog = new Progress<double>(p => Progress = p * 100);
            AppendLog($"下载 {asset.Name}（{asset.Size / 1024 / 1024} MB）…");
            await _manager.DownloadAsync(asset.DownloadUrl, archive, prog, CancellationToken.None);
            AppendLog("解压中…");
            await Task.Run(() => SnowlumaManager.Extract(archive, InstallDir));
            AppendLog("SnowLuma 就绪。请阅读封号警告并勾选知晓后启动。");
            OnPropertyChanged(nameof(IsInstalled));
        }
        catch (Exception ex) { AppendLog($"下载失败：{ex.Message}"); }
        finally { Busy = false; Progress = 0; }
    }

    public async Task StartAsync()
    {
        try
        {
            await _manager.StartAsync(InstallDir);
            AppendLog("SnowLuma 已启动，5 秒后自动检测 QQ…");
            await Task.Delay(5000);
            await ProbeAsync();
        }
        catch (Exception ex) { AppendLog($"启动失败：{ex.Message}"); }
    }

    public void Stop()
    {
        _manager.Stop();
        QqDetected = false;
        AppendLog("SnowLuma 已停止。");
    }

    public async Task ProbeAsync()
    {
        QqDetected = false;
        try
        {
            await using var oneBot = new OneBotClient(OneBotHttp, OneBotWs);
            var s = await _manager.ProbeAsync(InstallDir, oneBot);
            Status = s switch
            {
                SnowlumaStatus.NotInstalled => "未安装",
                SnowlumaStatus.QqNotFound => "QQ 未运行",
                SnowlumaStatus.InjectedNotLoggedIn => "已注入 / 服务未就绪",
                SnowlumaStatus.Online => "在线",
                _ => s.ToString()
            };
            QqDetected = s is SnowlumaStatus.Online or SnowlumaStatus.InjectedNotLoggedIn;
        }
        catch (Exception ex) { Status = $"探测失败：{ex.Message}"; }
    }

    public void AutoConnect()
    {
        OneBotHttp = "http://127.0.0.1:3000";
        OneBotWs = "ws://127.0.0.1:3001";
        QqDetected = false;
        SaveSettings();
        AppendLog("已填入默认 OneBot 地址并保存，重启 App 后管线自动连接。");
    }

    // ================= 持久化 =================

    public void LoadSettings()
    {
        var s = SettingsStore.Load(SettingsPath);
        AiBaseUrl = s.AiBaseUrl;
        AiApiKey = s.AiApiKey;
        AiModel = s.AiModel;
        AiModelProviderHint = s.AiProvider.Length > 0 ? s.AiProvider : "deepseek";
        OneBotHttp = s.OneBotHttp;
        OneBotWs = s.OneBotWs;
        GroupIdsText = string.Join(",", s.GroupIds);
        PluginToken = s.PluginToken;
        PluginPort = s.PluginPort;
        _riskAccepted = s.RiskAccepted;
        _adminHash = s.AdminPasswordHash;
        _minimizeToTray = s.MinimizeToTray;
        _featureSummon = s.FeatureSummon;
        _featureHomework = s.FeatureHomework;
        _featureExchange = s.FeatureExchange;
        _featureFileArchive = s.FeatureFileArchive;
        _featureCoursewarePopup = s.FeatureCoursewarePopup;
        _engineOption = Engines.FirstOrDefault(e => e.Engine == AiEngineParser.Parse(s.AiEngine)) ?? Engines[0];
        Teachers.Clear();
        foreach (var t in s.Teachers)
            Teachers.Add(new TeacherRow(t.Qq.ToString(), t.Name, t.Subject));
        _aiProvider = AiProviders.FirstOrDefault(p => p.BaseUrl == s.AiBaseUrl && p.Name != "自定义")
            ?? AiProviders[^1];
    }

    public void SaveSettings()
    {
        var groups = GroupIdsText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(g => long.TryParse(g, out var n) ? n : 0).Where(n => n > 0).ToList();
        SettingsStore.Save(new AppSettings
        {
            AiEngine = AiEngine.ToStorage(),
            AiProvider = SelectedProvider?.Id ?? AiModelProviderHint,
            AiBaseUrl = AiBaseUrl,
            AiApiKey = AiApiKey,
            AiModel = AiModel,
            OneBotHttp = OneBotHttp,
            OneBotWs = OneBotWs,
            GroupIds = groups,
            PluginToken = PluginToken,
            PluginPort = PluginPort,
            Teachers = Teachers.Select(t => new Teacher
            {
                Qq = long.TryParse(t.Qq, out var n) ? n : 0,
                Name = t.Name,
                Subject = t.Subject
            }).ToList(),
            RiskAccepted = RiskAccepted,
            FeatureSummon = FeatureSummon,
            FeatureHomework = FeatureHomework,
            FeatureExchange = FeatureExchange,
            FeatureFileArchive = FeatureFileArchive,
            FeatureCoursewarePopup = FeatureCoursewarePopup,
            AdminPasswordHash = _adminHash,
            MinimizeToTray = MinimizeToTray
        }, SettingsPath);
        AppendLog("设置已保存。");
    }
}
