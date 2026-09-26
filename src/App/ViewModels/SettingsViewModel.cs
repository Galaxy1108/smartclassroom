using System.Collections.ObjectModel;
using System.IO;
using SmartClassroom.Core;
using SmartClassroom.Core.AI;
using SmartClassroom.App.Views;
using SmartClassroom.Core.QQ;
using SmartClassroom.Core.Updates;

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

    /// <summary>与 Runtime 共用的设置对象（测试里为 null）。见构造函数注释。</summary>
    private readonly AppSettings? _shared;

    private readonly UpdateChecker _updates;

    /// <summary>正在从文件/共享对象装载设置：此时不允许任何自动落盘，避免写回半截数据。</summary>
    private bool _loading;
    private string _log = "";
    private string _status = "未检测";
    private double _progress;
    private bool _busy;
    private bool _riskAccepted;
    private bool _qqDetected;
    private string _pluginStatus = "未检测";
    private string _sidecarInfo = "";
    private string _aiTestResult = "";
    private string _aiReasoning = "minimal";
    private string _archiveRoot = "";
    private bool _archiveDownloadAll;
    private bool _isNodeReady;
    private bool _isLocked;
    private bool _minimizeToTray = true;
    private double _uiScale = ContentZoom.Default;
    private string _passwordResult = "";
    private bool _featureSummon;
    private bool _featureHomework;
    private bool _featureExchange;
    private bool _featureFileArchive;
    private bool _featureCoursewarePopup;
    private double _nodeProgress;

    public SettingsViewModel() : this(SettingsStore.DefaultPath, AppContext.BaseDirectory,
        // 只有 Runtime 已经装载过设置时才共用；否则自己读文件，避免用空对象覆盖用户配置。
        Runtime.SettingsLoaded ? Runtime.Settings : null) { }

    /// <param name="shared">
    /// 真实运行时传入 <see cref="Runtime.Settings"/>：设置页与 Runtime 必须操作**同一个对象**。
    /// 否则会出现"设置页存了 API Key，退出时 Runtime 又拿启动时的旧快照覆盖一遍"，
    /// 结果每次重启/更新后配置都回到原样（用户看到的就是"key 又没了"）。
    /// 传 null（测试用临时文件）时退化为"自己读自己写"。
    /// </param>
    public SettingsViewModel(string settingsPath, string? appDir = null, AppSettings? shared = null,
        UpdateChecker? updates = null)
    {
        SettingsPath = settingsPath;
        _appDir = appDir ?? AppContext.BaseDirectory;
        _shared = shared;
        _updates = updates ?? new UpdateChecker();
        _manager.OnLog += line => AppendLog(line);
        InstallDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SmartClassroom", "snowluma");
        LoadSettings();
        AutostartEnabled = AutostartManager.IsEnabled(InstallDir);
        RefreshSidecarInfo();
        RefreshNodeStatus();
        OnPropertyChanged(nameof(FeatureSummary));
        RefreshIntegrationState();   // 判定 QQ / AI / ClassIsland 是否就绪 → 决定功能开关能否开
        // 用 Ctrl+滚轮改了缩放时，设置页滑块要跟着动（不再反向触发保存）
        ContentZoom.Changed += OnExternalScaleChanged;
    }

    public string SettingsPath { get; }

    private void AppendLog(string line) => Log += line + "\n";

    /// <summary>供视图写入日志（例如打开目录失败）。</summary>
    public void AppendLogFromView(string line) => AppendLog(line);

    /// <summary>清空日志框（只清内存里的这段文本，不影响事件时间线）。</summary>
    public void ClearLog()
    {
        Log = "";
        AppendLog("日志已清空。");
    }

    /// <summary>监听群号（设置里是逗号分隔的文本）。</summary>
    private List<long> ParseGroupIds()
        => GroupIdsText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(g => long.TryParse(g, out var n) ? n : 0).Where(n => n > 0).ToList();

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

    /// <summary>作业内容区缩放；改动立即生效并落盘。</summary>
    public double UiScale
    {
        get => _uiScale;
        set
        {
            var clamped = ContentZoom.Clamp(value);
            if (!Set(ref _uiScale, clamped))
                return;
            ContentZoom.Scale = clamped;        // 立刻应用到已打开的窗口
            OnPropertyChanged(nameof(UiScaleLabel));
            SaveSettings();
        }
    }

    public string UiScaleLabel => ContentZoom.Describe(_uiScale);

    private void OnExternalScaleChanged(double scale)
    {
        if (Math.Abs(scale - _uiScale) < 0.001)
            return;
        _uiScale = scale;
        OnPropertyChanged(nameof(UiScale));
        OnPropertyChanged(nameof(UiScaleLabel));
    }

    /// <summary>托盘在当前桌面环境是否可用（不可用时关闭窗口即退出）。</summary>
    public string TrayHint => AppShell.TrayAvailable
        ? "关闭主窗口会收回到托盘；从托盘菜单可重新打开或退出。"
        : "当前桌面环境未提供系统托盘，关闭主窗口将直接退出应用。";

    /// <summary>刷新锁定状态（打开设置页 / 认证成功后调用），顺便重判集成状态。</summary>
    public void RefreshLockState()
    {
        IsLocked = Runtime.Auth.IsEnabled && !Runtime.Auth.IsUnlocked;
        RefreshIntegrationState();
    }

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
    // 另外：开关依赖的外部集成必须先配置好，否则开了也只是空转
    //（没有 QQ 就收不到消息、没有 ClassIsland 就发不出通知也落不了课），
    // 因此依赖不满足时**直接不允许开启**，并在页面上说明缺什么。

    // ---------- 集成是否就绪 ----------

    /// <summary>QQ 连接就绪：有监听群号，且有 OneBot 地址。</summary>
    public bool QqReady
        => ParseGroupIds().Count > 0
           && (OneBotHttp.Trim().Length > 0 || OneBotWs.Trim().Length > 0);

    /// <summary>AI 就绪：有模型；内置直连还要求服务地址，pi-ai 走 provider 目录。</summary>
    public bool AiReady
        => AiModel.Trim().Length > 0 && (IsPiAi || AiBaseUrl.Trim().Length > 0);

    private bool _classIslandReady;
    /// <summary>ClassIsland 集成就绪：填过桥接 token，或能在磁盘上找到插件生成的 token。</summary>
    public bool ClassIslandReady
    {
        get => _classIslandReady;
        private set => Set(ref _classIslandReady, value);
    }

    /// <summary>重新判断集成就绪情况（改设置、打开设置页、自动查找 token 后调用）。</summary>
    public void RefreshIntegrationState()
    {
        bool located;
        try { located = ClassIslandLocator.TryReadToken() is not null; }
        catch { located = false; }
        ClassIslandReady = PluginToken.Trim().Length > 0 || located;
        RefreshFeatureGates();
    }

    // ---------- 各开关的前置条件 ----------

    private List<string> MissingFor(bool needQq, bool needAi, bool needClassIsland)
    {
        var missing = new List<string>();
        if (needQq && !QqReady)
            missing.Add("QQ 连接（下方「QQ 连接」：填 OneBot 地址 + 监听群号）");
        if (needAi && !AiReady)
            missing.Add("AI（上方「AI」：选引擎并填模型，内置直连还要填服务地址）");
        if (needClassIsland && !ClassIslandReady)
            missing.Add("ClassIsland 集成（「ClassIsland 集成」：先让插件生成 token，再点「自动查找」）");
        return missing;
    }

    private string _summonGateHint = "";
    private string _homeworkGateHint = "";
    private string _exchangeGateHint = "";
    private string _archiveGateHint = "";
    private string _coursewareGateHint = "";

    public string SummonGateHint { get => _summonGateHint; private set => Set(ref _summonGateHint, value); }
    public string HomeworkGateHint { get => _homeworkGateHint; private set => Set(ref _homeworkGateHint, value); }
    public string ExchangeGateHint { get => _exchangeGateHint; private set => Set(ref _exchangeGateHint, value); }
    public string ArchiveGateHint { get => _archiveGateHint; private set => Set(ref _archiveGateHint, value); }
    public string CoursewareGateHint { get => _coursewareGateHint; private set => Set(ref _coursewareGateHint, value); }

    public bool CanEnableSummon => SummonGateHint.Length == 0;
    public bool CanEnableHomework => HomeworkGateHint.Length == 0;
    public bool CanEnableExchange => ExchangeGateHint.Length == 0;
    public bool CanEnableFileArchive => ArchiveGateHint.Length == 0;
    public bool CanEnableCoursewarePopup => CoursewareGateHint.Length == 0;

    private static string GateHint(List<string> missing)
        => missing.Count == 0 ? "" : "⛔ 需先完成：" + string.Join("；", missing);

    /// <summary>重算五个开关的前置条件，并把不满足条件的开关关掉。</summary>
    public void RefreshFeatureGates()
    {
        SummonGateHint = GateHint(MissingFor(needQq: true, needAi: true, needClassIsland: false));
        HomeworkGateHint = GateHint(MissingFor(needQq: true, needAi: true, needClassIsland: false));
        ExchangeGateHint = GateHint(MissingFor(needQq: true, needAi: true, needClassIsland: true));
        ArchiveGateHint = GateHint(MissingFor(needQq: true, needAi: false, needClassIsland: false));
        CoursewareGateHint = GateHint(MissingFor(needQq: false, needAi: false, needClassIsland: true));

        OnPropertyChanged(nameof(CanEnableSummon));
        OnPropertyChanged(nameof(CanEnableHomework));
        OnPropertyChanged(nameof(CanEnableExchange));
        OnPropertyChanged(nameof(CanEnableFileArchive));
        OnPropertyChanged(nameof(CanEnableCoursewarePopup));
        OnPropertyChanged(nameof(IntegrationSummary));
        OnPropertyChanged(nameof(HasMissingIntegration));

        TurnOffUnavailableFeatures();
    }

    /// <summary>前置条件不满足却处于开启状态的开关，一律关掉（设置文件被手改也一样）。</summary>
    private void TurnOffUnavailableFeatures()
    {
        if (_enforcingGates)
            return;
        _enforcingGates = true;
        try
        {
            var turnedOff = new List<string>();
            if (_featureSummon && !CanEnableSummon) { _featureSummon = false; turnedOff.Add("召唤通知"); }
            if (_featureHomework && !CanEnableHomework) { _featureHomework = false; turnedOff.Add("作业自动录入"); }
            if (_featureExchange && !CanEnableExchange) { _featureExchange = false; turnedOff.Add("换课自动处理"); }
            if (_featureFileArchive && !CanEnableFileArchive) { _featureFileArchive = false; turnedOff.Add("群文件自动归档"); }
            if (_featureCoursewarePopup && !CanEnableCoursewarePopup) { _featureCoursewarePopup = false; turnedOff.Add("上课课件弹窗"); }
            if (turnedOff.Count == 0)
                return;

            OnPropertyChanged(nameof(FeatureSummon));
            OnPropertyChanged(nameof(FeatureHomework));
            OnPropertyChanged(nameof(FeatureExchange));
            OnPropertyChanged(nameof(FeatureFileArchive));
            OnPropertyChanged(nameof(FeatureCoursewarePopup));
            OnPropertyChanged(nameof(FeatureSummary));
            AppendLog($"前置集成未完成，已自动关闭：{string.Join("、", turnedOff)}");
            SaveSettings();
        }
        finally { _enforcingGates = false; }
    }

    private bool _enforcingGates;

    /// <summary>总览：哪些集成还没配好。放在功能开关区顶部，用户一眼能看到原因。</summary>
    public string IntegrationSummary
    {
        get
        {
            var missing = MissingFor(needQq: true, needAi: true, needClassIsland: true);
            return missing.Count == 0 ? "" : "以下集成尚未配置完成，相关开关暂时无法开启：" + string.Join("；", missing);
        }
    }

    /// <summary>有集成没配好（决定总览提示是否显示）。</summary>
    public bool HasMissingIntegration => IntegrationSummary.Length > 0;

    public bool FeatureSummon
    {
        get => _featureSummon;
        set => SetFeature(ref _featureSummon, value, CanEnableSummon, "召唤通知");
    }

    public bool FeatureHomework
    {
        get => _featureHomework;
        set => SetFeature(ref _featureHomework, value, CanEnableHomework, "作业自动录入");
    }

    public bool FeatureExchange
    {
        get => _featureExchange;
        set => SetFeature(ref _featureExchange, value, CanEnableExchange, "换课自动处理");
    }

    public bool FeatureFileArchive
    {
        get => _featureFileArchive;
        set => SetFeature(ref _featureFileArchive, value, CanEnableFileArchive, "群文件自动归档");
    }

    public bool FeatureCoursewarePopup
    {
        get => _featureCoursewarePopup;
        set => SetFeature(ref _featureCoursewarePopup, value, CanEnableCoursewarePopup, "上课课件弹窗");
    }

    /// <summary>开启前统一检查前置条件；不允许就直接拒绝并写日志（界面上开关本来就是禁用的）。</summary>
    private void SetFeature(ref bool field, bool value, bool allowed, string name)
    {
        if (value && !allowed)
        {
            AppendLog($"「{name}」的前置集成还没配好，无法开启。");
            OnPropertyChanged(nameof(FeatureSummon));
            OnPropertyChanged(nameof(FeatureHomework));
            OnPropertyChanged(nameof(FeatureExchange));
            OnPropertyChanged(nameof(FeatureFileArchive));
            OnPropertyChanged(nameof(FeatureCoursewarePopup));
            return;
        }
        if (!Set(ref field, value))
            return;
        OnFeatureChanged();
    }

    /// <summary>当前开关摘要（保存后重启生效）。</summary>
    public string FeatureSummary => CurrentFlags().Describe();

    private void OnFeatureChanged()
    {
        SaveSettings();
        OnPropertyChanged(nameof(FeatureSummary));
        OnPropertyChanged(nameof(IntegrationSummary));
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
            RefreshFeatureGates();   // pi-ai 不要求服务地址，切换引擎会改变 AI 是否就绪
        }
    }

    // 注意：LoadSettings() 期间 EngineOption 可能还没赋值，
    // 而 AiModel 等 setter 会立刻重算功能开关（依赖 AiEngine），所以这里必须容忍 null。
    public AiEngine AiEngine => EngineOption?.Engine ?? AiEngine.HttpGateway;
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
    public string AiBaseUrl
    {
        get => _aiBaseUrl;
        set { if (Set(ref _aiBaseUrl, value)) { RefreshFeatureGates(); AutoSaveSoon(); } }
    }

    private string _aiApiKey = "";
    public string AiApiKey
    {
        get => _aiApiKey;
        set { if (Set(ref _aiApiKey, value)) AutoSaveSoon(); }
    }

    private string _aiModel = "";
    public string AiModel
    {
        get => _aiModel;
        set { if (Set(ref _aiModel, value)) { RefreshFeatureGates(); AutoSaveSoon(); } }
    }

    public string AiTestResult { get => _aiTestResult; private set => Set(ref _aiTestResult, value); }

    /// <summary>pi-ai 推理强度可选值。</summary>
    public List<string> ReasoningLevels { get; } = ["minimal", "low", "medium", "high"];

    /// <summary>
    /// pi-ai 推理强度。默认 minimal：长思考对"整理成 JSON"没有帮助，反而可能
    /// 让推理模型只输出思考、final text 为空。
    /// </summary>
    public string AiReasoning
    {
        get => _aiReasoning;
        set { if (Set(ref _aiReasoning, value)) SaveSettings(); }
    }

    public List<AiProviderPreset> AiProviders { get; } =
    [
        new("OpenAI 官方", "https://api.openai.com/v1"),
        // opencode.ai 要求 x-opencode-session 路由头：内置直连与 pi-ai 边车都会自动补，
        // 所以这里只需要填地址+Key+模型（如 deepseek-v4.1-flash）。
        new("OpenCode Go（opencode.ai）", "https://opencode.ai/zen/go/v1"),
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
        ["gpt-4o-mini", "deepseek-chat", "deepseek-v4.1-flash", "qwen-flash", "qwen-plus"];

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
        AiTestSeverity = NoticeSeverity.Informational;
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
            if (text.Length > 0)
            {
                AiTestResult = $"可用：{Trim(text)}";
                AiTestSeverity = NoticeSeverity.Success;
                Toasts.Success("AI 可用", Trim(text));
            }
            else
            {
                AiTestResult = "可用，但返回为空";
                AiTestSeverity = NoticeSeverity.Warning;
                Toasts.Warn("AI 连通但返回为空", "换个模型或调低推理强度再试。");
            }
        }
        catch (Exception ex)
        {
            AiTestResult = $"失败：{ex.Message}";
            AiTestSeverity = NoticeSeverity.Error;
            Toasts.Error("AI 测试失败", ex.Message);
        }
    }

    private static string Trim(string s) => s.Length > 40 ? s[..40] + "…" : s;

    private PiAiSidecarClient NewSidecar()
    {
        var client = new PiAiSidecarClient(new SidecarOptions
        {
            SidecarDir = NodeRuntime.SidecarDir(_appDir),
            Provider = SelectedProvider?.Id ?? AiModelProviderHint,
            Reasoning = AiReasoning,
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

    // ================= 文件归档 =================

    /// <summary>归档根目录；留空表示用默认位置。</summary>
    public string ArchiveRoot
    {
        get => _archiveRoot;
        set
        {
            if (!Set(ref _archiveRoot, value))
                return;
            OnPropertyChanged(nameof(EffectiveArchiveRoot));
            SaveSettings();
        }
    }

    /// <summary>真正生效的目录（空则默认），界面上要显示出来，否则用户找不到文件。</summary>
    public string EffectiveArchiveRoot => ArchiveRoot.Length > 0 ? ArchiveRoot : Runtime.DefaultArchiveRoot;

    /// <summary>下载群里所有人的文件（默认只下载教师）。</summary>
    public bool ArchiveDownloadAll
    {
        get => _archiveDownloadAll;
        set { if (Set(ref _archiveDownloadAll, value)) SaveSettings(); }
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
            Toasts.Success("Node 安装完成", NodeStatusText);
        }
        catch (Exception ex)
        {
            AppendLog($"Node 安装失败：{ex.Message}");
            Toasts.Error("Node 安装失败", ex.Message);
        }
        finally { Busy = false; NodeProgress = 0; }
    }

    // ================= ClassIsland 集成 =================

    private string _pluginToken = "";
    public string PluginToken
    {
        get => _pluginToken;
        set { if (Set(ref _pluginToken, value)) { RefreshIntegrationState(); AutoSaveSoon(); } }
    }

    private int _pluginPort = 5199;
    public int PluginPort
    {
        get => _pluginPort;
        set { if (Set(ref _pluginPort, value)) AutoSaveSoon(); }
    }

    public string PluginStatus { get => _pluginStatus; private set => Set(ref _pluginStatus, value); }

    /// <summary>探测 ClassIsland 插件桥接是否在线。</summary>
    public async Task ProbePluginAsync()
    {
        PluginStatus = "探测中…";
        PluginSeverity = NoticeSeverity.Informational;
        try
        {
            var link = new PluginLink($"http://127.0.0.1:{PluginPort}", PluginToken);
            var status = await link.StatusAsync();
            if (status is null)
            {
                PluginStatus = "未连接（请确认 ClassIsland 已启动、插件已加载、端口与 token 正确）";
                PluginSeverity = NoticeSeverity.Error;
                Toasts.Error("ClassIsland 插件未连接", "确认 ClassIsland 已启动、插件已加载，端口与 token 正确。");
            }
            else
            {
                PluginStatus = $"已连接 · 插件 v{status.PluginVersion} · 课表{(status.ClassPlanLoaded ? "已加载" : "未加载")}";
                PluginSeverity = status.ClassPlanLoaded ? NoticeSeverity.Success : NoticeSeverity.Warning;
                if (status.ClassPlanLoaded)
                    Toasts.Success("ClassIsland 插件已连接", $"v{status.PluginVersion}");
                else
                    Toasts.Warn("插件已连接，但课表未加载", "在 ClassIsland 里确认课表已启用。");
            }
        }
        catch (Exception ex)
        {
            PluginStatus = $"探测失败：{ex.Message}";
            PluginSeverity = NoticeSeverity.Error;
            Toasts.Error("插件探测失败", ex.Message);
        }
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
        RefreshIntegrationState();   // token 到位 → 依赖 ClassIsland 的开关可以开了
        AppendLog("已自动填入插件 token 并保存。");
        Toasts.Success("已找到插件 token", "已自动填入并保存。");
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

    // ================= 状态徽标（图标 + 文案） =================
    //
    // 原来的纯文本状态有歧义：「已注入 / 服务未就绪」到底算成功没有？
    // 现在一律给出 (级别, 文案) 两件套 —— 级别决定图标与颜色：
    //   成功=对钩(绿) / 警告=感叹号(橙) / 失败=叉(红) / 进行中=信息(蓝)

    private NoticeSeverity _qqStatusSeverity = NoticeSeverity.Informational;
    public NoticeSeverity QqStatusSeverity
    {
        get => _qqStatusSeverity;
        private set => Set(ref _qqStatusSeverity, value);
    }

    private string _qqStatusText = "未检测";
    public string QqStatusText { get => _qqStatusText; private set => Set(ref _qqStatusText, value); }

    private NoticeSeverity _pluginSeverity = NoticeSeverity.Informational;
    public NoticeSeverity PluginSeverity
    {
        get => _pluginSeverity;
        private set => Set(ref _pluginSeverity, value);
    }

    private NoticeSeverity _aiTestSeverity = NoticeSeverity.Informational;
    public NoticeSeverity AiTestSeverity
    {
        get => _aiTestSeverity;
        private set => Set(ref _aiTestSeverity, value);
    }

    /// <summary>启动/停止/探测期间为 true —— 按钮要转圈，否则用户以为点了没反应。</summary>
    public bool IsQqBusy => IsStarting || IsStopping || IsProbing;

    private bool _isStarting;
    public bool IsStarting
    {
        get => _isStarting;
        private set { if (Set(ref _isStarting, value)) OnPropertyChanged(nameof(IsQqBusy)); }
    }

    private bool _isStopping;
    public bool IsStopping
    {
        get => _isStopping;
        private set { if (Set(ref _isStopping, value)) OnPropertyChanged(nameof(IsQqBusy)); }
    }

    private bool _isProbing;
    public bool IsProbing
    {
        get => _isProbing;
        private set { if (Set(ref _isProbing, value)) OnPropertyChanged(nameof(IsQqBusy)); }
    }

    /// <summary>正在启动时按钮上显示的文字。</summary>
    public string StartButtonText => IsStarting ? "启动中…" : "启动";
    public string StopButtonText => IsStopping ? "停止中…" : "停止";
    public string ProbeButtonText => IsProbing ? "探测中…" : "探测";

    public bool IsInstalled => File.Exists(Path.Combine(InstallDir, "index.mjs"));

    public bool RiskAccepted
    {
        get => _riskAccepted;
        set
        {
            if (!Set(ref _riskAccepted, value))
                return;
            SaveSettings();
            OnPropertyChanged(nameof(NeedsRiskConfirmation));
            OnPropertyChanged(nameof(RiskBadgeSeverity));
            OnPropertyChanged(nameof(RiskBadgeText));
        }
    }

    /// <summary>还没确认风险（启动注入前要先弹窗确认）。</summary>
    public bool NeedsRiskConfirmation => !RiskAccepted;

    /// <summary>风险提示原文（弹窗与横幅共用一份，避免两处说法不一致）。</summary>
    public const string RiskWarningText =
        "QQ 官方可能检测到第三方登录方式并封禁账号。强烈建议不要使用全新注册的 QQ 号操作；" +
        "班级号请确认可以接受该风险后再启动注入。";

    /// <summary>风险提示原文（弹窗与横幅共用一份，避免两处说法不一致）。</summary>
    public string RiskWarningMessage => RiskWarningText;

    /// <summary>风险确认状态徽标（未确认=感叹号，已确认=对钩）。</summary>
    public NoticeSeverity RiskBadgeSeverity =>
        RiskAccepted ? NoticeSeverity.Success : NoticeSeverity.Warning;

    public string RiskBadgeText => RiskAccepted ? "已确认风险" : "尚未确认风险";

    /// <summary>用户在弹窗里确认风险后调用。</summary>
    public void AcceptRisk()
    {
        if (RiskAccepted)
            return;
        RiskAccepted = true;
        AppendLog("已确认风险，允许启动注入。");
        Toasts.Success("已确认风险", "现在可以启动注入了。");
    }

    // ================= QQ 账号（多账号时选一个） =================

    /// <summary>候选账号（检测到的 + 以前选过的）。</summary>
    public ObservableCollection<QqAccount> QqCandidates { get; } = new();

    private long _qqAccount;
    public long QqAccount
    {
        get => _qqAccount;
        private set { if (Set(ref _qqAccount, value)) OnPropertyChanged(nameof(QqAccountLabel)); }
    }

    public string QqAccountLabel
    {
        get
        {
            if (QqAccount <= 0)
                return "未选择（单账号可以不管；多账号请选一下，避免注入到别的号）";
            var nick = QqCandidates.FirstOrDefault(a => a.Uin == QqAccount)?.Nickname;
            return string.IsNullOrWhiteSpace(nick) ? $"已选：{QqAccount}" : $"已选：{QqAccount}（{nick}）";
        }
    }

    private string _onlineQqText = "未检测";
    public string OnlineQqText { get => _onlineQqText; private set => Set(ref _onlineQqText, value); }

    /// <summary>探测当前注入实例登录的 QQ，并把它并入候选列表。返回检测到的账号（可能为 null）。</summary>
    public async Task<QqAccount?> DetectOnlineQqAsync()
    {
        IsProbing = true;
        try
        {
            await using var oneBot = new OneBotClient(OneBotHttp, OneBotWs,
                OneBotToken.Length > 0 ? OneBotToken : null);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var info = await oneBot.GetLoginInfoAsync(cts.Token);
            if (info is { UserId: > 0 })
            {
                var found = new QqAccount { Uin = info.UserId, Nickname = info.Nickname };
                MergeCandidate(found);
                OnlineQqText = string.IsNullOrWhiteSpace(found.Nickname)
                    ? found.Uin.ToString()
                    : $"{found.Uin}（{found.Nickname}）";
                return found;
            }
        }
        catch (Exception ex)
        {
            AppendLog($"检测 QQ 账号失败：{ex.Message}");
        }
        finally { IsProbing = false; }

        OnlineQqText = "未检测到（OneBot 未连接或未登录）";
        return null;
    }

    /// <summary>记住一个候选账号（同号更新昵称）。</summary>
    public void MergeCandidate(QqAccount account)
    {
        if (account.Uin <= 0)
            return;
        var existing = QqCandidates.FirstOrDefault(a => a.Uin == account.Uin);
        if (existing is null)
        {
            QqCandidates.Add(account);
            return;
        }
        if (!string.IsNullOrWhiteSpace(account.Nickname) && existing.Nickname != account.Nickname)
        {
            var idx = QqCandidates.IndexOf(existing);
            QqCandidates[idx] = new QqAccount { Uin = account.Uin, Nickname = account.Nickname };
        }
    }

    /// <summary>用户选定了账号。</summary>
    public void ApplyQqAccount(QqAccount account)
    {
        MergeCandidate(account);
        QqAccount = account.Uin;
        OnPropertyChanged(nameof(QqAccountLabel));
        SaveSettings();
        var label = string.IsNullOrWhiteSpace(account.Nickname)
            ? account.Uin.ToString()
            : $"{account.Uin}（{account.Nickname}）";
        AppendLog($"已选择 QQ 账号：{label}");
        Toasts.Success("已选择 QQ 账号", label);
    }

    public void NoteQqAccountCanceled()
        => Toasts.Show("未选择 QQ 账号", "保持原选择不变。", NoticeSeverity.Informational);

    public bool QqDetected { get => _qqDetected; private set => Set(ref _qqDetected, value); }

    private string _oneBotHttp = "http://127.0.0.1:3000";
    public string OneBotHttp
    {
        get => _oneBotHttp;
        set { if (Set(ref _oneBotHttp, value)) { RefreshFeatureGates(); AutoSaveSoon(); } }
    }

    private string _oneBotWs = "ws://127.0.0.1:3001";
    public string OneBotWs
    {
        get => _oneBotWs;
        set { if (Set(ref _oneBotWs, value)) { RefreshFeatureGates(); AutoSaveSoon(); } }
    }

    private string _oneBotToken = "";
    /// <summary>OneBot 访问令牌（SnowLuma 配了 token 时必填，否则连不上）。</summary>
    public string OneBotToken
    {
        get => _oneBotToken;
        set { if (Set(ref _oneBotToken, value)) AutoSaveSoon(); }
    }

    private string _groupIds = "";
    public string GroupIdsText
    {
        get => _groupIds;
        set { if (Set(ref _groupIds, value)) { RefreshFeatureGates(); AutoSaveSoon(); } }
    }

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
            AppendLog("SnowLuma 就绪。请阅读封号警告并确认知晓后启动。");
            OnPropertyChanged(nameof(IsInstalled));
            Toasts.Success("SnowLuma 下载完成", "下一步：确认风险提示，然后点「启动」。");
        }
        catch (Exception ex)
        {
            AppendLog($"下载失败：{ex.Message}");
            Toasts.Error("SnowLuma 下载失败", ex.Message);
        }
        finally { Busy = false; Progress = 0; }
    }

    public async Task StartAsync()
    {
        IsStarting = true;
        QqStatusText = "正在启动…";
        QqStatusSeverity = NoticeSeverity.Informational;
        OnPropertyChanged(nameof(StartButtonText));
        try
        {
            await _manager.StartAsync(InstallDir);
            AppendLog("SnowLuma 已启动，5 秒后自动检测 QQ…");
            await Task.Delay(5000);
            await ProbeAsync();     // 探测结果自己会弹通知
        }
        catch (Exception ex)
        {
            AppendLog($"启动失败：{ex.Message}");
            QqStatusText = "启动失败";
            QqStatusSeverity = NoticeSeverity.Error;
            Toasts.Error("SnowLuma 启动失败", ex.Message);
        }
        finally
        {
            IsStarting = false;
            OnPropertyChanged(nameof(StartButtonText));
        }
    }

    /// <summary>停止注入。以前是同步的、界面毫无反馈，用户以为点了没反应。</summary>
    public async Task StopAsync()
    {
        IsStopping = true;
        OnPropertyChanged(nameof(StopButtonText));
        try
        {
            await Task.Run(() => _manager.Stop());
            QqDetected = false;
            QqStatusText = "已停止";
            QqStatusSeverity = NoticeSeverity.Informational;
            AppendLog("SnowLuma 已停止。");
            Toasts.Success("SnowLuma 已停止", "注入已关闭，QQ 恢复原状。");
            await Task.Delay(400);
            await ProbeAsync();
        }
        catch (Exception ex)
        {
            AppendLog($"停止失败：{ex.Message}");
            Toasts.Error("停止失败", ex.Message);
        }
        finally
        {
            IsStopping = false;
            OnPropertyChanged(nameof(StopButtonText));
        }
    }

    /// <summary>
    /// 探测注入三态。
    /// 文案必须能一眼看出"成功没有"：在线=成功(对钩)、已注入但服务未就绪=警告(感叹号)、
    /// 其余=失败(叉)。原来的「已注入 / 服务未就绪」看不出是哪种。
    /// </summary>
    public async Task ProbeAsync()
    {
        QqDetected = false;
        IsProbing = true;
        OnPropertyChanged(nameof(ProbeButtonText));
        QqStatusText = "探测中…";
        QqStatusSeverity = NoticeSeverity.Informational;
        try
        {
            await using var oneBot = new OneBotClient(OneBotHttp, OneBotWs,
                OneBotToken.Length > 0 ? OneBotToken : null);
            var s = await _manager.ProbeAsync(InstallDir, oneBot);
            (QqStatusSeverity, QqStatusText) = s switch
            {
                SnowlumaStatus.Online => (NoticeSeverity.Success, "在线（注入正常）"),
                SnowlumaStatus.InjectedNotLoggedIn =>
                    (NoticeSeverity.Warning, "已注入，但服务未就绪（通常是还没登录完成）"),
                SnowlumaStatus.QqNotFound => (NoticeSeverity.Error, "QQ 未运行（请先启动并登录班级 QQ）"),
                SnowlumaStatus.NotInstalled => (NoticeSeverity.Error, "未安装 SnowLuma"),
                _ => (NoticeSeverity.Error, s.ToString())
            };
            Status = QqStatusText;
            QqDetected = s is SnowlumaStatus.Online or SnowlumaStatus.InjectedNotLoggedIn;

            switch (s)
            {
                case SnowlumaStatus.Online:
                    Toasts.Success("QQ 在线", "注入正常，消息管线可以工作。");
                    break;
                case SnowlumaStatus.InjectedNotLoggedIn:
                    Toasts.Warn("已注入，但服务未就绪", "多数情况是 QQ 还没登录完成，稍等再探测一次。");
                    break;
                case SnowlumaStatus.QqNotFound:
                    Toasts.Error("QQ 未运行", "请先启动并登录班级 QQ，再启动注入。");
                    break;
                case SnowlumaStatus.NotInstalled:
                    Toasts.Error("未安装 SnowLuma", "先在下方「SnowLuma 下载器」里下载。");
                    break;
            }

            // 多账号场景：注入的号和我们选的不一致，用户必须知道
            if (QqAccount > 0 && s is SnowlumaStatus.Online)
            {
                var info = await TryGetLoginInfoAsync(oneBot);
                if (info is { UserId: > 0 } && info.UserId != QqAccount)
                    Toasts.Warn("注入的 QQ 账号与所选不一致",
                        $"当前在线 {info.UserId}，你在设置里选的是 {QqAccount}。");
            }
        }
        catch (Exception ex)
        {
            Status = $"探测失败：{ex.Message}";
            QqStatusText = "探测失败";
            QqStatusSeverity = NoticeSeverity.Error;
            Toasts.Error("探测失败", ex.Message);
        }
        finally
        {
            IsProbing = false;
            OnPropertyChanged(nameof(ProbeButtonText));
        }
    }

    private static async Task<LoginInfoData?> TryGetLoginInfoAsync(OneBotClient oneBot)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            return await oneBot.GetLoginInfoAsync(cts.Token);
        }
        catch { return null; }
    }

    public void AutoConnect()
    {
        OneBotHttp = "http://127.0.0.1:3000";
        OneBotWs = "ws://127.0.0.1:3001";
        QqDetected = false;
        SaveSettings();
        AppendLog("已填入默认 OneBot 地址并保存，重启 App 后管线自动连接。");
        Toasts.Success("已填入默认 OneBot 地址", "重启应用后消息管线会自动连接。");
    }

    // ================= 软件更新 =================
    //
    // 只做"查 + 下载"。真正替换文件仅限 Windows（见 UpdateInstaller）；
    // Linux 的应用由包管理器安装，自己替换 /opt 会和 pacman 数据库不一致，故禁用。

    public string CurrentVersionText => $"当前版本 {AppVersion.Current}";

    private string _latestVersionText = "尚未检查";
    public string LatestVersionText { get => _latestVersionText; private set => Set(ref _latestVersionText, value); }

    private NoticeSeverity _updateSeverity = NoticeSeverity.Informational;
    public NoticeSeverity UpdateSeverity { get => _updateSeverity; private set => Set(ref _updateSeverity, value); }

    private string _updateStatusText = "尚未检查更新";
    public string UpdateStatusText { get => _updateStatusText; private set => Set(ref _updateStatusText, value); }

    private string _updateNotes = "";
    public string UpdateNotes { get => _updateNotes; private set => Set(ref _updateNotes, value); }

    public bool HasUpdateNotes => UpdateNotes.Length > 0;

    private bool _hasUpdate;
    public bool HasUpdate
    {
        get => _hasUpdate;
        private set
        {
            if (!Set(ref _hasUpdate, value))
                return;
            OnPropertyChanged(nameof(CanInstallUpdate));
        }
    }

    private bool _isCheckingUpdate;
    public bool IsCheckingUpdate
    {
        get => _isCheckingUpdate;
        private set
        {
            if (Set(ref _isCheckingUpdate, value))
                OnPropertyChanged(nameof(CheckUpdateButtonText));
        }
    }

    public string CheckUpdateButtonText => IsCheckingUpdate ? "检查中…" : "检查更新";

    private bool _isDownloadingUpdate;
    public bool IsDownloadingUpdate
    {
        get => _isDownloadingUpdate;
        private set
        {
            if (!Set(ref _isDownloadingUpdate, value))
                return;
            OnPropertyChanged(nameof(CanInstallUpdate));
            OnPropertyChanged(nameof(DownloadUpdateButtonText));
        }
    }

    public string DownloadUpdateButtonText => IsDownloadingUpdate ? "下载中…" : "下载并安装";

    private double _updateProgress;
    public double UpdateProgress { get => _updateProgress; private set => Set(ref _updateProgress, value); }

    private string? _assetUrl;
    private string? _releaseUrl;

    /// <summary>本平台是否允许应用自己替换文件（Windows 可以，Linux 不行）。</summary>
    public bool CanSelfUpdate => UpdateInstaller.CanSelfUpdate;

    /// <summary>能点"下载并安装"：有新版本 + 平台允许 + Release 里确实传了安装包。</summary>
    public bool CanInstallUpdate => HasUpdate && CanSelfUpdate && _assetUrl is not null && !IsDownloadingUpdate;

    /// <summary>Linux 下的说明（为什么按钮是灰的）。</summary>
    public string UpdatePlatformHint => CanSelfUpdate
        ? "Windows：可以直接下载并自动替换（关闭应用后由脚本完成覆盖并重启）。"
        : "Linux：应用由系统包管理器安装，程序不会自行替换 /opt 下的文件。" +
          "请用包管理器更新（例如 sudo pacman -U 新版 .pkg.tar.zst），或到 Releases 页下载。";

    public bool HasReleasePage => _releaseUrl is not null;

    private string? _pendingUpdateScript;
    /// <summary>下载完成后生成的"覆盖并重启"脚本路径（视图层负责提示用户运行）。</summary>
    public string? PendingUpdateScript { get => _pendingUpdateScript; private set => Set(ref _pendingUpdateScript, value); }

    private bool _autoCheckedUpdate;

    /// <summary>
    /// 打开设置页时自动查一次（每个进程只查一次）。
    /// 无认证的 GitHub API 每小时只有 60 次，反复进设置页不该反复打。
    /// </summary>
    public async Task CheckForUpdatesOnceAsync()
    {
        if (_autoCheckedUpdate)
            return;
        _autoCheckedUpdate = true;
        await CheckForUpdatesAsync();
    }

    public async Task CheckForUpdatesAsync()
    {
        IsCheckingUpdate = true;
        UpdateSeverity = NoticeSeverity.Informational;
        UpdateStatusText = "检查中…";
        try
        {
            // 只有 Windows 需要安装包；Linux 由包管理器更新，不必挑资产
            var pattern = CanSelfUpdate ? "win-x64.zip" : null;
            var info = await _updates.CheckAsync(AppVersion.Current, assetPattern: pattern);
            _assetUrl = info.AssetUrl;
            _releaseUrl = info.ReleaseUrl;
            UpdateNotes = info.Notes ?? "";
            OnPropertyChanged(nameof(HasUpdateNotes));
            OnPropertyChanged(nameof(HasReleasePage));

            if (!info.RemoteReachable)
            {
                HasUpdate = false;
                LatestVersionText = "未获取到";
                UpdateSeverity = NoticeSeverity.Warning;
                UpdateStatusText = "检查失败：拿不到远端版本（网络不通或仓库没有 tag/Release）";
                Toasts.Warn("更新检查失败", "没拿到远端版本信息，稍后再试。");
            }
            else if (info.HasUpdate)
            {
                HasUpdate = true;
                LatestVersionText = $"最新版本 {info.LatestVersion}";
                UpdateSeverity = NoticeSeverity.Warning;
                UpdateStatusText = $"发现新版本 {info.LatestVersion}（当前 {AppVersion.Current}）";
                Toasts.Show("发现新版本", $"{AppVersion.Current} → {info.LatestVersion}", NoticeSeverity.Warning);
            }
            else
            {
                HasUpdate = false;
                LatestVersionText = $"最新版本 {info.LatestVersion}";
                UpdateSeverity = NoticeSeverity.Success;
                UpdateStatusText = "已是最新版本";
                Toasts.Success("已是最新版本", $"当前 {AppVersion.Current}");
            }
            AppendLog($"更新检查：{UpdateStatusText}（来源 {info.Source}）");
        }
        catch (Exception ex)
        {
            HasUpdate = false;
            UpdateSeverity = NoticeSeverity.Error;
            UpdateStatusText = $"检查失败：{ex.Message}";
            AppendLog($"更新检查失败：{ex.Message}");
            Toasts.Error("更新检查失败", ex.Message);
        }
        finally
        {
            IsCheckingUpdate = false;
            OnPropertyChanged(nameof(CanInstallUpdate));
        }
    }

    /// <summary>下载新版本并生成覆盖脚本（仅 Windows 可用）。</summary>
    public async Task DownloadUpdateAsync()
    {
        if (_assetUrl is null)
        {
            Toasts.Warn("没有可下载的安装包", "该 Release 里还没有上传 win-x64.zip。");
            return;
        }
        IsDownloadingUpdate = true;
        UpdateProgress = 0;
        try
        {
            var stage = await UpdateInstaller.StageAsync(_assetUrl, LatestVersionText, _appDir,
                new Progress<double>(p => UpdateProgress = p));
            PendingUpdateScript = UpdateInstaller.WriteScript(stage, _appDir);
            AppendLog($"更新已下载并解压到 {stage}；覆盖脚本：{PendingUpdateScript}");
            Toasts.Success("更新已下载", "关闭应用后运行 apply-update.cmd 即可覆盖并重启。");
        }
        catch (Exception ex)
        {
            AppendLog($"下载更新失败：{ex.Message}");
            Toasts.Error("下载更新失败", ex.Message);
        }
        finally
        {
            IsDownloadingUpdate = false;
            UpdateProgress = 0;
        }
    }

    /// <summary>Release 页面地址（视图层负责用浏览器打开）。</summary>
    public string? ReleasePageUrl => _releaseUrl;

    // ================= 调试 =================

    /// <summary>配置文件位置（清空前让用户知道动的是哪个文件）。</summary>
    public string SettingsPathHint => $"配置文件：{SettingsPath}";

    /// <summary>
    /// 清空所有设置：删掉 settings.json，并把内存里那份一起复位
    /// （设置页与 Runtime 共用对象，只删文件的话退出时又会被写回来）。
    /// 作业/待处理/事件时间线属于数据，不在"设置"范围内，不动。
    /// </summary>
    public void ResetAllSettings()
    {
        SettingsStore.Reset(SettingsPath);
        _shared?.ResetToDefaults();
        LoadSettings();               // 回到默认值（有共享对象时读的就是刚复位的那份）
        RefreshIntegrationState();    // 开关前置条件重新判定
        RefreshLockState();           // 管理员密码也清了
        OnPropertyChanged(nameof(FeatureSummary));
        AppendLog("已清空所有设置（作业/事件等数据未动）。");
        Toasts.Success("已清空所有设置", "重启应用后完全生效。");
    }

    // ================= 持久化 =================

    public void LoadSettings()
    {
        // 有共享对象时以它为准（Runtime 启动时已从同一个文件装载过），
        // 这样"设置页看到的值"和"Runtime 手里那份"永远是同一份。
        var s = _shared ?? SettingsStore.Load(SettingsPath);
        _loading = true;
        try
        {
            AiBaseUrl = s.AiBaseUrl;
            AiApiKey = s.AiApiKey;
            AiModel = s.AiModel;
            AiModelProviderHint = s.AiProvider.Length > 0 ? s.AiProvider : "deepseek";
            _aiReasoning = s.AiReasoning.Length > 0 ? s.AiReasoning : "minimal";
            _archiveRoot = s.ArchiveRoot;
            _archiveDownloadAll = s.ArchiveDownloadAll;
            OneBotHttp = s.OneBotHttp;
            OneBotWs = s.OneBotWs;
            GroupIdsText = string.Join(",", s.GroupIds);
            OneBotToken = s.OneBotToken;
            PluginToken = s.PluginToken;
            PluginPort = s.PluginPort;
            _qqAccount = s.QqAccount;
            QqCandidates.Clear();
            foreach (var a in s.QqAccounts)
                QqCandidates.Add(a);
            _riskAccepted = s.RiskAccepted;
            _adminHash = s.AdminPasswordHash;
            _minimizeToTray = s.MinimizeToTray;
            _uiScale = ContentZoom.Clamp(s.UiScale);
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
        finally { _loading = false; }
    }

    /// <summary>
    /// 落盘。有共享对象时**写进那个对象再存**——Runtime 退出时还会用它再存一次，
    /// 两者必须是同一份数据，否则设置页的改动会被启动快照覆盖（API Key 就是这样丢的）。
    /// </summary>
    /// <param name="quiet">true = 自动保存（不写"设置已保存"日志，避免每敲一个字刷一行）。</param>
    public void SaveSettings(bool quiet = false)
    {
        var groups = ParseGroupIds();
        var s = _shared ?? new AppSettings();
        s.AiEngine = AiEngine.ToStorage();
        s.AiProvider = SelectedProvider?.Id ?? AiModelProviderHint;
        s.AiReasoning = AiReasoning;
        s.ArchiveRoot = ArchiveRoot;
        s.ArchiveDownloadAll = ArchiveDownloadAll;
        s.AiBaseUrl = AiBaseUrl;
        s.AiApiKey = AiApiKey;
        s.AiModel = AiModel;
        s.OneBotHttp = OneBotHttp;
        s.OneBotWs = OneBotWs;
        s.GroupIds = groups;
        s.OneBotToken = OneBotToken;
        s.PluginToken = PluginToken;
        s.PluginPort = PluginPort;
        s.QqAccount = QqAccount;
        s.QqAccounts = QqCandidates.ToList();
        s.Teachers = Teachers.Select(t => new Teacher
        {
            Qq = long.TryParse(t.Qq, out var n) ? n : 0,
            Name = t.Name,
            Subject = t.Subject
        }).ToList();
        s.RiskAccepted = RiskAccepted;
        s.FeatureSummon = FeatureSummon;
        s.FeatureHomework = FeatureHomework;
        s.FeatureExchange = FeatureExchange;
        s.FeatureFileArchive = FeatureFileArchive;
        s.FeatureCoursewarePopup = FeatureCoursewarePopup;
        s.AdminPasswordHash = _adminHash;
        s.MinimizeToTray = MinimizeToTray;
        s.UiScale = UiScale;

        SettingsStore.Save(s, SettingsPath);
        _pendingAutoSave = false;
        if (!quiet)
            AppendLog("设置已保存。");
    }

    // ================= 文本框的自动保存 =================
    //
    // 服务地址 / API Key / 模型 / 群号这些是**手打**的。以前它们只在
    // "碰巧触发了别的保存动作"（切开关、点保存设置）时才落盘，
    // 打完 key 直接退出/更新就白打了。这里给它们一个防抖自动保存。

    private System.Timers.Timer? _autoSaveTimer;
    private bool _pendingAutoSave;

    /// <summary>改动后延迟落盘（连续输入只写最后一次）。</summary>
    private void AutoSaveSoon()
    {
        if (_loading)
            return;
        _pendingAutoSave = true;
        if (_shared is null)
        {
            // 测试用临时文件：没有 UI 事件循环，直接落盘，行为可预期。
            SaveSettings(quiet: true);
            return;
        }
        _autoSaveTimer?.Stop();
        _autoSaveTimer?.Dispose();
        _autoSaveTimer = new System.Timers.Timer(600) { AutoReset = false };
        _autoSaveTimer.Elapsed += (_, _) =>
        {
            try { SaveSettings(quiet: true); } catch { /* 自动保存失败不打断输入 */ }
        };
        _autoSaveTimer.Start();
    }

    /// <summary>立刻落盘还没写完的改动（切页、关闭窗口、退出前调用）。</summary>
    public void FlushPendingSaves()
    {
        _autoSaveTimer?.Stop();
        _autoSaveTimer?.Dispose();
        _autoSaveTimer = null;
        if (!_pendingAutoSave)
            return;
        try { SaveSettings(quiet: true); } catch { /* 退出路径不抛 */ }
    }
}
