using System.Collections.ObjectModel;
using AutoStartHelper = SmartClassroom.App.AutoStart;
using System.IO;
using SmartClassroom.Core;
using SmartClassroom.Core.AI;
using SmartClassroom.App.Views;
using SmartClassroom.Core.QQ;
using SmartClassroom.Core.Updates;

namespace SmartClassroom.App.ViewModels;

public sealed record AiProviderPreset(string Name, string BaseUrl);
/// <summary>
/// 老师映射里的一行。**必须是可变的**：以前是 record（init-only），
/// 只能删了重建 —— 用户反馈"无法修改老师信息，删除重建以后还是原科目"。
/// 现在可以就地改，列表也会立刻刷新。
/// </summary>
public sealed class TeacherRow : ViewModelBase
{
    public TeacherRow(string qq, string name, string subject)
    {
        _qq = qq;
        _name = name;
        _subject = subject;
    }

    private string _qq;
    public string Qq { get => _qq; set => Set(ref _qq, value); }

    private string _name;
    public string Name { get => _name; set => Set(ref _name, value); }

    private string _subject;
    public string Subject { get => _subject; set => Set(ref _subject, value); }
}
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

    /// <summary>当前设置对象：真实运行时与 Runtime 共用同一份；测试里退化为本地一份。</summary>
    private AppSettings Settings => _shared ?? _localSettings;
    private readonly AppSettings _localSettings = new();

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
    internal List<long> ParseGroupIds()
        => GroupIdsText.Split(Separators,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(g => long.TryParse(g, out var n) ? n : 0).Where(n => n > 0).ToList();

    /// <summary>群号分隔符：中英文逗号、顿号、分号、空白都认（用户很可能打成中文逗号）。</summary>
    private static readonly char[] Separators = [',', '，', '、', ';', '；', ' ', '\n', '\t', '\r'];

    // ================= 管理员密码 / 关闭行为 =================

    /// <summary>
    /// 解锁后 10 分钟内免重复输入密码。**默认关 = 每一次操作都要密码**
    /// （用户要求："我需要每一次操作都需要管理员密码"）。
    /// </summary>
    public bool RememberUnlock
    {
        get => _rememberUnlock;
        set
        {
            if (!Set(ref _rememberUnlock, value))
                return;
            Runtime.Auth.SessionMinutes = value ? 10 : 0;
            SaveSettings();
        }
    }

    private bool _rememberUnlock;

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

    /// <summary>应用自身的开机自启。改完立刻写系统（不成功只记日志）。</summary>
    public bool AutoStart
    {
        get => _autoStart;
        set
        {
            if (!Set(ref _autoStart, value))
                return;
            if (!AutoStartHelper.Apply(value))
                AppendLog(value ? "开启自启动失败（写入系统项被拒绝）。" : "关闭自启动失败。");
            else
                AppendLog(value ? "已开启开机自启。" : "已关闭开机自启。");
            SaveSettings();
        }
    }

    private bool _autoStart;

    /// <summary>Windows：阻止普通用户结束本应用（任务管理器里会"拒绝访问"）。</summary>
    public bool ProtectFromKill
    {
        get => _protectFromKill;
        set
        {
            if (!Set(ref _protectFromKill, value))
                return;
            if (!ProcessGuard.Apply(value))
                AppendLog(value ? "开启防结束失败（可能权限不足）。" : "关闭防结束失败。");
            else
                AppendLog(value ? "已开启：普通用户无法结束本应用。" : "已关闭防结束保护。");
            SaveSettings();
        }
    }

    private bool _protectFromKill;

    /// <summary>防结束只支持 Windows；Linux 下这一行显示为不可用。</summary>
    public bool CanProtectFromKill => ProcessGuard.IsSupported;

    public string ProtectFromKillHint => ProcessGuard.IsSupported
        ? "Windows：打开后，普通用户（同学）在任务管理器里结束它会显示拒绝访问；管理员仍可结束"
        : "仅 Windows 支持（Linux 由系统权限管理）";

    /// <summary>作业上墙后也给同学们发一条通知（走 ClassIsland，同样排队）。</summary>
    public bool NotifyHomework
    {
        get => _notifyHomework;
        set
        {
            if (!Set(ref _notifyHomework, value))
                return;
            SaveSettings();
            AppendLog(value ? "已开启：作业上墙时发通知。" : "已关闭：作业上墙时发通知。");
        }
    }

    private bool _notifyHomework;

    /// <summary>换课结果也给同学们发通知。</summary>
    public bool NotifyExchange
    {
        get => _notifyExchange;
        set
        {
            if (!Set(ref _notifyExchange, value))
                return;
            SaveSettings();
            AppendLog(value ? "已开启：换课时发通知。" : "已关闭：换课时发通知。");
        }
    }

    private bool _notifyExchange;

    /// <summary>作业卡片独立缩放（作业页 Ctrl+滚轮也能调）。</summary>
    public double CardScale
    {
        get => _cardScale;
        set
        {
            var clamped = ContentZoom.Clamp(value);
            if (!Set(ref _cardScale, clamped))
                return;
            ContentZoom.CardScale = clamped;
            OnPropertyChanged(nameof(CardScaleLabel));
        }
    }

    private double _cardScale = ContentZoom.Default;

    public string CardScaleLabel => ContentZoom.Describe(_cardScale);

    /// <summary>主题选项（界面显示用）。</summary>
    public static IReadOnlyList<string> ThemeChoices { get; } = ["跟随系统", "浅色", "深色"];

    /// <summary>主题：跟随系统 / 浅色 / 深色，改完立刻应用。</summary>
    public string ThemeChoice
    {
        get => AppTheme.Describe(_theme);
        set
        {
            var next = value switch
            {
                "浅色" => AppTheme.Light,
                "深色" => AppTheme.Dark,
                _ => AppTheme.System
            };
            if (_theme == next)
                return;
            _theme = next;
            AppTheme.Apply(next);
            OnPropertyChanged();
            SaveSettings();
        }
    }

    private string _theme = AppTheme.System;

    private void OnExternalScaleChanged(double scale)
    {
        if (Math.Abs(scale - _uiScale) < 0.001)
            return;
        _uiScale = scale;
        OnPropertyChanged(nameof(UiScale));
        OnPropertyChanged(nameof(UiScaleLabel));
    }

    /// <summary>托盘在当前桌面环境是否可用（不可用时关闭窗口即退出）。</summary>
    /// <summary>只有托盘不可用时才需要说明（可用时行为已由开关本身表达）。</summary>
    public string TrayHint => AppShell.TrayAvailable
        ? ""
        : "当前桌面环境无系统托盘，关闭主窗口将直接退出";

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
        => (ParseGroupIds().Count > 0 || ListenAllGroups || ListenTeacherPrivate)
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

    private string _pluginStatusDetail = "";
    /// <summary>未连接时的排查提示（状态行只显示"未连接"，细节放这里）。</summary>
    public string PluginStatusDetail
    {
        get => _pluginStatusDetail;
        private set => Set(ref _pluginStatusDetail, value);
    }

    /// <summary>
    /// 召唤通知的"降级提醒"：ClassIsland 没连上时仍然能用（应用内横幅 + 系统通知），
    /// 但**没有语音播报** —— 用户装 ClassIsland 就是为了播报，所以要明说。
    /// 不是拦截条件，只是一句提示。
    /// </summary>
    public string SummonVoiceHint
    {
        get
        {
            if (!ClassIslandReady)
                return "ClassIsland 未连接：只发应用内通知，没有语音播报";
            return "";
        }
    }

    public bool HasSummonVoiceHint => SummonVoiceHint.Length > 0;

    /// <summary>应用自带的插件文件目录（打包时放在应用目录下）。</summary>
    public static string BundledPluginDir =>
        Path.Combine(AppContext.BaseDirectory, "classisland-plugin");

    public bool HasBundledPlugin => Directory.Exists(BundledPluginDir);

    /// <summary>
    /// 把自带插件装进 ClassIsland（用户"插件没给我"的正面解决：应用直接给）。
    /// </summary>
    public void InstallClassIslandPlugin()
    {
        if (!HasBundledPlugin)
        {
            Toasts.Error("安装包缺少插件文件", "请从 Release 页面下载 classisland-plugin 压缩包。");
            return;
        }
        try
        {
            var dir = ClassIslandLocator.InstallPlugin(BundledPluginDir);
            AppendLog($"已安装 ClassIsland 插件到 {dir}");
            Toasts.Success("插件已安装", "重启 ClassIsland 后即可连接（状态会自动变成已连接）。");
            RefreshIntegrationState();
        }
        catch (Exception ex)
        {
            AppendLog($"安装 ClassIsland 插件失败：{ex.Message}");
            Toasts.Error("安装插件失败", ex.Message);
        }
    }

    /// <summary>
    /// 查找 ClassIsland 插件 token 的方式（可注入）。
    /// 做成接缝是为了测试不受"本机有没有装 ClassIsland"影响。
    /// </summary>
    private Func<string?> _tokenLocator = () => ClassIslandLocator.TryReadToken();

    public Func<string?> TokenLocator
    {
        get => _tokenLocator;
        set
        {
            _tokenLocator = value;
            RefreshIntegrationState();   // 换了查找方式就重新判定（测试在构造后注入）
        }
    }

    /// <summary>重新判断集成就绪情况（改设置、打开设置页、自动查找 token 后调用）。</summary>
    public void RefreshIntegrationState()
    {
        bool located;
        try { located = TokenLocator() is not null; }
        catch { located = false; }
        ClassIslandReady = PluginToken.Trim().Length > 0 || located;
        if (ClassIslandReady)
            PluginStatusDetail = "";
        OnPropertyChanged(nameof(SummonVoiceHint));
        OnPropertyChanged(nameof(HasSummonVoiceHint));
        RefreshFeatureGates();
    }

    // ---------- 各开关的前置条件 ----------

    private List<string> MissingFor(bool needQq, bool needAi, bool needClassIsland,
        bool needsClassIslandForTrigger = false)
    {
        var missing = new List<string>();
        if (needQq)
        {
            // 精确到缺哪一项：只写"QQ 连接"会让"地址填了、群没选"的人一头雾水
            // 只监听私聊也是合法配置（老师私聊发作业/召唤）
            if (ParseGroupIds().Count == 0 && !ListenAllGroups && !ListenTeacherPrivate)
                missing.Add("监听群号或老师私聊");
            if (OneBotHttp.Trim().Length == 0 && OneBotWs.Trim().Length == 0)
                missing.Add("OneBot 地址");
        }
        if (needAi && !AiReady)
            missing.Add("AI");
        if (needClassIsland && !ClassIslandReady)
        {
            // 召唤有"应用内横幅 + 系统通知"兜底，课件弹窗没有：上课事件只能由 ClassIsland 提供
            missing.Add(needsClassIslandForTrigger ? "ClassIsland 集成（上课事件只能由它提供）"
                                                   : "ClassIsland 集成");
        }
        return missing;
    }

    private string _summonGateHint = "";
    private string _homeworkGateHint = "";
    private string _exchangeGateHint = "";
    private string _archiveGateHint = "";
    private string _coursewareGateHint = "";
    private string _noticeGateHint = "";

    public string SummonGateHint { get => _summonGateHint; private set => Set(ref _summonGateHint, value); }
    public string HomeworkGateHint { get => _homeworkGateHint; private set => Set(ref _homeworkGateHint, value); }
    public string ExchangeGateHint { get => _exchangeGateHint; private set => Set(ref _exchangeGateHint, value); }
    public string ArchiveGateHint { get => _archiveGateHint; private set => Set(ref _archiveGateHint, value); }
    public string CoursewareGateHint { get => _coursewareGateHint; private set => Set(ref _coursewareGateHint, value); }
    public string NoticeGateHint { get => _noticeGateHint; private set => Set(ref _noticeGateHint, value); }

    public bool CanEnableSummon => SummonGateHint.Length == 0;
    public bool CanEnableHomework => HomeworkGateHint.Length == 0;
    public bool CanEnableExchange => ExchangeGateHint.Length == 0;
    public bool CanEnableFileArchive => ArchiveGateHint.Length == 0;
    public bool CanEnableCoursewarePopup => CoursewareGateHint.Length == 0;
    public bool CanEnableNotice => NoticeGateHint.Length == 0;

    private static string GateHint(List<string> missing)
        => missing.Count == 0 ? "" : "开启前需先完成：" + string.Join("、", missing);

    /// <summary>重算五个开关的前置条件，并把不满足条件的开关关掉。</summary>
    public void RefreshFeatureGates()
    {
        SummonGateHint = GateHint(MissingFor(needQq: true, needAi: true, needClassIsland: false));
        HomeworkGateHint = GateHint(MissingFor(needQq: true, needAi: true, needClassIsland: false));
        ExchangeGateHint = GateHint(MissingFor(needQq: true, needAi: true, needClassIsland: true));
        ArchiveGateHint = GateHint(MissingFor(needQq: true, needAi: false, needClassIsland: false));
        CoursewareGateHint = GateHint(MissingFor(needQq: false, needAi: false, needClassIsland: true, needsClassIslandForTrigger: true));
        NoticeGateHint = GateHint(MissingFor(needQq: true, needAi: true, needClassIsland: false));

        OnPropertyChanged(nameof(CanEnableSummon));
        OnPropertyChanged(nameof(CanEnableHomework));
        OnPropertyChanged(nameof(CanEnableExchange));
        OnPropertyChanged(nameof(CanEnableFileArchive));
        OnPropertyChanged(nameof(CanEnableCoursewarePopup));
        OnPropertyChanged(nameof(CanEnableNotice));

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
            if (_featureNotice && !CanEnableNotice) { _featureNotice = false; turnedOff.Add("老师通知转发"); }
            if (turnedOff.Count == 0)
                return;

            OnPropertyChanged(nameof(FeatureSummon));
            OnPropertyChanged(nameof(FeatureHomework));
            OnPropertyChanged(nameof(FeatureExchange));
            OnPropertyChanged(nameof(FeatureFileArchive));
            OnPropertyChanged(nameof(FeatureCoursewarePopup));
            OnPropertyChanged(nameof(FeatureNotice));
            OnPropertyChanged(nameof(FeatureSummary));
            AppendLog($"前置集成未完成，已自动关闭：{string.Join("、", turnedOff)}");
            SaveSettings();
        }
        finally { _enforcingGates = false; }
    }

    private bool _enforcingGates;

    /// <summary>老师通知转发（活动/集合/催交 → ClassIsland 提醒）。默认关。</summary>
    public bool FeatureNotice
    {
        get => _featureNotice;
        set => SetFeature(ref _featureNotice, value, CanEnableNotice, "老师通知转发");
    }

    private bool _featureNotice;

    // ---------- 科目颜色（用户："我希望我能修改作业卡片的颜色"） ----------

    /// <summary>可选颜色（与作业卡片默认色板一致，外加"默认"）。</summary>
    public static IReadOnlyList<string> ColorChoices { get; } =
        ["默认", "#0F6CBD", "#0F7B0F", "#9D5D00", "#B10E1C", "#5C2E91",
         "#00766C", "#8A3707", "#4F6BED", "#6B4E00", "#7A0E4B", "#0078D4", "#C239B3"];

    public ObservableCollection<SubjectColorRow> SubjectColorRows { get; } = new();

    /// <summary>把"当前作业里出现过的科目 + 已配过颜色的科目"列出来，供用户改色。</summary>
    public void RefreshSubjectColors()
    {
        var subjects = Runtime.Homework.All.Select(h => h.Subject.Trim())
            .Concat(Settings.SubjectColors.Keys)
            .Where(s => s.Length > 0)
            .Distinct()
            .OrderBy(s => s, StringComparer.CurrentCulture)
            .ToList();
        SubjectColorRows.Clear();
        foreach (var subject in subjects)
        {
            var row = new SubjectColorRow(subject,
                Settings.SubjectColors.TryGetValue(subject, out var c) ? c : "默认");
            row.Changed += (s, color) =>
            {
                if (color == "默认")
                    Settings.SubjectColors.Remove(s);
                else
                    Settings.SubjectColors[s] = color;
                SaveSettings();
                SubjectColorsChanged?.Invoke();   // 让作业页立刻换色（否则要等切页/定时刷新）
                AppendLog($"科目配色：{s} → {color}");
            };
            SubjectColorRows.Add(row);
        }
        OnPropertyChanged(nameof(HasSubjectColors));
    }

    public bool HasSubjectColors => SubjectColorRows.Count > 0;

    /// <summary>科目配色变化时通知（作业页据此立即重建卡片）。</summary>
    public static event Action? SubjectColorsChanged;

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

    /// <summary>
    /// 缺"监听群号"时，由视图弹窗让用户选群（在线了却开不了开关最让人困惑）。
    /// 视图注入；测试/无窗口时为 null。
    /// </summary>
    public Func<Task<bool>>? GroupPicker { get; set; }

    /// <summary>用户想开、但还差群号的那个开关（选完群后自动打开）。</summary>
    private string? _pendingEnable;

    /// <summary>开启前统一检查前置条件；不允许就直接拒绝并写日志（界面上开关本来就是禁用的）。</summary>
    private void SetFeature(ref bool field, bool value, bool allowed, string name)
    {
        if (value && !allowed)
        {
            // 只差监听群号 → 直接把选群弹窗推给用户，选完自动打开这个开关
            if (ParseGroupIds().Count == 0 && !ListenAllGroups && QqReady == false
                && OneBotHttp.Trim().Length > 0 && GroupPicker is not null)
            {
                _pendingEnable = name;
                _ = GroupPicker();
                return;
            }
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

    /// <summary>
    /// 下拉在"还没点载入目录"时是空的，用户会以为配置丢了。
    /// 用已保存的值做占位文本，等载入目录后再自动选中同名条目。
    /// </summary>
    public string ProviderPlaceholder => AiModelProviderHint.Length > 0 ? AiModelProviderHint : "未选择";

    /// <summary>同上：模型下拉的占位文本显示已保存的模型。</summary>
    public string ModelPlaceholder => AiModel.Length > 0 ? AiModel : "未选择";

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
                OnPropertyChanged(nameof(ProviderPlaceholder));
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
            OnPropertyChanged(nameof(ModelPlaceholder));
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
                Toasts.Warn("AI 返回为空");
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
    /// <summary>归档后额外复制一份到系统"下载"目录（QQ 在那里看到就秒判已接收）。</summary>
    public bool CopyToDownloads
    {
        get => _copyToDownloads;
        set
        {
            if (!Set(ref _copyToDownloads, value))
                return;
            SaveSettings();
        }
    }

    private bool _copyToDownloads = true;

    public string DownloadsDirHint => FileArchive.SystemDownloadsDir() is { Length: > 0 } dir
        ? $"当前会复制到：{dir}"
        : "找不到系统下载目录，已跳过复制。";

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
            Toasts.Success("Node 安装完成");
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
                PluginStatus = "未连接";   // 细节放 Tooltip/日志，别把状态行撑爆
                PluginStatusDetail = "请确认 ClassIsland 已启动、插件已加载、端口与 token 正确";
                PluginSeverity = NoticeSeverity.Error;
                Toasts.Error("ClassIsland 插件未连接");
            }
            else
            {
                PluginStatus = $"已连接 · 插件 v{status.PluginVersion} · 课表{(status.ClassPlanLoaded ? "已加载" : "未加载")}";
                PluginSeverity = status.ClassPlanLoaded ? NoticeSeverity.Success : NoticeSeverity.Warning;
                if (status.ClassPlanLoaded)
                    Toasts.Success("ClassIsland 插件已连接");
                else
                    Toasts.Warn("插件已连接，课表未加载");
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
        Toasts.Success("已找到插件 token");
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

    /// <summary>正在编辑的老师行（null = 新增模式）。</summary>
    private TeacherRow? _editingTeacher;

    public bool IsEditingTeacher => _editingTeacher is not null;
    public string TeacherFormTitle => _editingTeacher is null ? "添加" : "保存修改";
    public string TeacherEditHint => _editingTeacher is null
        ? ""
        : $"正在修改 {_editingTeacher.Name}（QQ {_editingTeacher.Qq}），改完点「保存修改」。";

    /// <summary>点某行的「编辑」：把该行内容填进表单，按钮变成"保存修改"。</summary>
    public void BeginEditTeacher(TeacherRow row)
    {
        _editingTeacher = row;
        NewTeacherQq = row.Qq;
        NewTeacherName = row.Name;
        NewTeacherSubject = row.Subject;
        OnPropertyChanged(nameof(IsEditingTeacher));
        OnPropertyChanged(nameof(TeacherFormTitle));
        OnPropertyChanged(nameof(TeacherEditHint));
    }

    public void CancelEditTeacher()
    {
        _editingTeacher = null;
        NewTeacherQq = NewTeacherName = NewTeacherSubject = "";
        OnPropertyChanged(nameof(IsEditingTeacher));
        OnPropertyChanged(nameof(TeacherFormTitle));
        OnPropertyChanged(nameof(TeacherEditHint));
    }

    public void AddTeacher()
    {
        if (NewTeacherName.Trim().Length == 0 || NewTeacherSubject.Trim().Length == 0)
        {
            AppendLog("老师姓名和科目不能为空。");
            return;
        }
        if (_editingTeacher is { } editing)
        {
            // 改的是同一行对象：列表立刻刷新，保存后老师映射也会热重载
            editing.Qq = NewTeacherQq.Trim();
            editing.Name = NewTeacherName.Trim();
            editing.Subject = NewTeacherSubject.Trim();
            AppendLog($"已修改老师：{editing.Name}（{editing.Subject}）");
            CancelEditTeacher();
            SaveSettings();
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
    public SnowlumaRelease? SelectedRelease
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value))
                return;
            // 选中的版本变了 → "是否有更新"和说明文案要跟着变
            OnPropertyChanged(nameof(LatestSnowlumaVersion));
            OnPropertyChanged(nameof(HasSnowlumaUpdate));
            OnPropertyChanged(nameof(SnowlumaVersionText));
        }
    }

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

    /// <summary>
    /// 展示协议并征得同意的回调（由视图注入；测试/无窗口时为 null，此时不弹窗）。
    /// 和密码弹窗一样，视图模型不直接碰 UI。
    /// </summary>
    public Func<IReadOnlyList<SnowlumaAgreement>, Task<bool>>? ConsentPrompt { get; set; }

    /// <summary>SnowLuma 卡在"等待同意协议"（不同意就不会注入）。</summary>
    private bool _needsWebUiSetup;
    public bool NeedsWebUiSetup { get => _needsWebUiSetup; private set => Set(ref _needsWebUiSetup, value); }

    /// <summary>SnowLuma 的 WebUI 地址（首次设置、看日志都在这里）。</summary>
    public string WebUiUrl => SnowlumaManager.WebUiUrl(InstallDir);

    // ---------- WebUI 登录（初始密码默认只打到 stdout，用户看不到） ----------

    private string _webUiPassword = "";
    /// <summary>我们替它指定的 WebUI 初始密码（启动时通过官方环境变量传进去）。</summary>
    public string WebUiPassword
    {
        get => _webUiPassword;
        private set
        {
            if (!Set(ref _webUiPassword, value))
                return;
            OnPropertyChanged(nameof(WebUiLoginHint));
            OnPropertyChanged(nameof(HasWebUiPassword));
        }
    }

    public bool HasWebUiPassword => WebUiPassword.Length > 0;

    public string WebUiLoginHint
    {
        get
        {
            if (!HasWebUiPassword)
                return "";
            // 用户自己设的密码不在这里回显（那不是临时密码）
            if (_webUiPasswordManual)
                return WebUiPasswordApplied
                    ? "WebUI 密码：已使用你设置的密码"
                    : "WebUI 密码：已使用你设置的密码（下次启动生效）";
            return WebUiPasswordApplied
                ? $"登录 WebUI：用户名 admin，密码 {WebUiPassword}"
                : $"登录 WebUI：用户名 admin，密码 {WebUiPassword}（下次启动生效）";
        }
    }

    /// <summary>密码是用户自己定的（否则就是应用自动生成的）。</summary>
    public bool WebUiPasswordIsManual => _webUiPasswordManual;

    private bool _webUiPasswordManual;

    /// <summary>用户自己设定 WebUI 初始密码（空 = 改回自动生成）。</summary>
    public void SetWebUiPassword(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length > 0 && trimmed.Length < 8)
        {
            // SnowLuma 自己会忽略少于 8 位的值（envBootstrapPassword 里写死）
            Toasts.Error("密码太短", "SnowLuma 要求至少 8 位，否则它会忽略这个密码。");
            return;
        }
        _webUiPasswordManual = trimmed.Length > 0;
        _passwordNeedsSeeding = trimmed.Length > 0;   // 要让它在 SnowLuma 那边真正生效
        WebUiPassword = trimmed;
        SaveSettings();
        AppendLog(_webUiPasswordManual ? "已设置 SnowLuma WebUI 密码（下次启动生效）" : "已改为自动生成 WebUI 密码");
        Toasts.Success(_webUiPasswordManual ? "已设置 WebUI 密码" : "已改为自动生成密码",
            _webUiPasswordManual ? "下次启动 SnowLuma 时生效。" : "");
    }

    /// <summary>
    /// 交给 SnowLuma 的 WebUI 初始密码：
    /// 用户设过就用他的；否则（还在用初始密码时）生成一个——
    /// 它自己随机生成的那个只打到 stdout，GUI 启动的用户根本看不到。
    /// 已经改过密码就不插手。
    /// </summary>
    private string? EnsureWebUiPassword()
    {
        if (_webUiPasswordManual && WebUiPassword.Length > 0)
        {
            WebUiPasswordIsSetFromSettings = true;
            return WebUiPassword;
        }
        if (SnowlumaManager.ReadWebUiMustChangePassword(InstallDir) != true)
            return null;   // 已经改过密码 / 读不出来：不要覆盖用户的设置
        if (!HasWebUiPassword)
        {
            WebUiPassword = GeneratePassword();
            _passwordNeedsSeeding = true;
            SaveSettings();   // 存下来，避免每次启动换一个密码
        }
        return WebUiPassword;
    }

    /// <summary>仅供界面显示"来自设置"。</summary>
    public bool WebUiPasswordIsSetFromSettings { get; private set; }

    /// <summary>需要让 SnowLuma 用我们指定的密码重新播种凭据（备份并删掉 webui.json）。</summary>
    private bool _passwordNeedsSeeding;

    /// <summary>
    /// 让 SnowLuma 用我们的密码重新播种凭据。
    /// 它只在没有 config/webui.json 时才认 SNOWLUMA_WEBUI_BOOTSTRAP_PASSWORD，
    /// 文件在就直接忽略 —— 这是"重启了密码还是不行"的原因。
    /// </summary>
    private void EnsureWebUiCredentials()
    {
        var mustChange = SnowlumaManager.ReadWebUiMustChangePassword(InstallDir);
        if (mustChange != true && !_passwordNeedsSeeding)
            return;   // 用户已在 WebUI 改过密码，且没要求我们改：不要动它
        if (SnowlumaManager.ResetWebUiCredentials(InstallDir))
        {
            AppendLog("已重置 SnowLuma 的 WebUI 凭据（旧文件已备份为 webui.json.bak-*），"
                      + "下次启动会用我们指定的密码播种");
            _passwordNeedsSeeding = false;
        }
        else
        {
            AppendLog("重置 WebUI 凭据失败，密码可能不生效");
        }
    }

    private bool _webUiPasswordApplied;
    /// <summary>
    /// 这个密码是否已经生效。**只有我们自己带密码启动过 SnowLuma 才算**：
    /// 如果它是"之前就在跑"的实例（我们只是接管），那它用的还是启动时随机生成、
    /// 只打在 stdout 的那个密码 —— 拿我们新生成的密码去登录当然是"密码错误"。
    /// </summary>
    public bool WebUiPasswordApplied
    {
        get => _webUiPasswordApplied;
        private set
        {
            if (Set(ref _webUiPasswordApplied, value))
                OnPropertyChanged(nameof(WebUiLoginHint));
        }
    }

    // ---------- 多账号：SnowLuma 给每个登录账号都开一套 OneBot，端口只有一个 ----------

    private string _multiAccountHint = "";
    /// <summary>检测到多个 QQ 登录时的提示（3000/3001 只能给一个账号）。</summary>
    public string MultiAccountHint
    {
        get => _multiAccountHint;
        private set { if (Set(ref _multiAccountHint, value)) OnPropertyChanged(nameof(HasMultiAccountHint)); }
    }

    public bool HasMultiAccountHint => MultiAccountHint.Length > 0;

    /// <summary>
    /// 从 SnowLuma 的配置里把 OneBot 地址与 access token 读过来。
    /// 它默认要求鉴权：不带 token 的请求一律 1401 unauthorized，
    /// 表现就是"检测不到账号 / 看不到在线"——所以必须自动读，别让用户手抄 43 位 token。
    /// </summary>
    public async Task<bool> TryAdoptOneBotEndpointAsync()
    {
        var accounts = SnowlumaManager.ReadOneBotAccounts(InstallDir);
        if (accounts.Count == 0)
            return false;

        // 3000/3001 归哪个账号，取决于 SnowLuma 启动时谁先登录 —— 所以逐个试，
        // 用第一个能应答的（选中的账号优先）。
        var order = accounts.Contains(QqAccount)
            ? new[] { QqAccount }.Concat(accounts.Where(a => a != QqAccount))
            : accounts.AsEnumerable();

        foreach (var uin in order)
        {
            var endpoint = SnowlumaManager.ReadOneBotEndpoint(InstallDir, uin);
            if (endpoint is null || !await OneBotRespondsAsync(endpoint))
                continue;

            var changed = OneBotHttp != endpoint.Http || OneBotWs != endpoint.Ws
                          || OneBotToken != endpoint.Token || OneBotWsToken != endpoint.WsToken;
            OneBotHttp = endpoint.Http;
            OneBotWs = endpoint.Ws;
            OneBotToken = endpoint.Token;
            OneBotWsToken = endpoint.WsToken;   // SnowLuma 的 WS 是另一个 token
            if (QqAccount <= 0)
            {
                QqAccount = endpoint.Uin;   // 没选过就选上这个真能连的
                OnPropertyChanged(nameof(QqAccountLabel));
            }
            if (changed)
            {
                SaveSettings();
                AppendLog($"已从 SnowLuma 读取 OneBot 连接信息（UIN={uin}，含 access token）");
            }
            return true;
        }
        return false;
    }

    /// <summary>这个账号的 OneBot 是否应答（它要求鉴权，token 不对就是 401）。</summary>
    private static async Task<bool> OneBotRespondsAsync(OneBotEndpoint endpoint)
    {
        try
        {
            await using var client = new OneBotClient(endpoint.Http, endpoint.Ws,
                endpoint.Token.Length > 0 ? endpoint.Token : null,
                wsToken: endpoint.WsToken.Length > 0 ? endpoint.WsToken : null);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            await client.GetLoginInfoAsync(cts.Token);
            return true;
        }
        catch { return false; }
    }

    /// <summary>要监听的群（设置里是逗号分隔文本，这里给界面看名字）。</summary>
    public string GroupSummary
    {
        get
        {
            var ids = ParseGroupIds();
            if (ListenAllGroups)
                return "监听全部群（该账号所在的每个群）";
            return ids.Count == 0
                ? "未选择（不会处理任何群的消息）"
                : $"已选 {ids.Count} 个群：{string.Join("、", ids)}";
        }
    }

    /// <summary>
    /// 没命中关键词的消息也交给 AI 分类（默认开启）。关掉可省 AI 调用。
    /// </summary>
    public bool AiDecidesTeacherMessages
    {
        get => _aiDecides;
        set { if (Set(ref _aiDecides, value)) SaveSettings(); }
    }

    private bool _aiDecides = true;

    /// <summary>
    /// 只处理老师名单里的人发的消息（默认开启）。
    /// 关掉它，群里任何人都能触发换课/作业/召唤 —— 换课会真的改课表，慎关。
    /// </summary>
    public bool RequireKnownTeacher
    {
        get => _requireKnownTeacher;
        set
        {
            if (!Set(ref _requireKnownTeacher, value))
                return;
            SaveSettings();
        }
    }

    private bool _requireKnownTeacher = true;

    /// <summary>
    /// 也处理老师私聊（默认关闭）。只认老师映射里的 QQ，陌生人私聊忽略。
    /// </summary>
    public bool ListenTeacherPrivate
    {
        get => _listenTeacherPrivate;
        set
        {
            if (!Set(ref _listenTeacherPrivate, value))
                return;
            RefreshFeatureGates();   // 它也算一种"QQ 连接"配置
            SaveSettings();
        }
    }

    private bool _listenTeacherPrivate;

    /// <summary>true = 监听该账号所在的全部群（默认关闭，避免在无关群里触发）。</summary>
    public bool ListenAllGroups
    {
        get => _listenAllGroups;
        set
        {
            if (!Set(ref _listenAllGroups, value))
                return;
            OnPropertyChanged(nameof(GroupSummary));
            RefreshFeatureGates();
            SaveSettings();
        }
    }

    private bool _listenAllGroups;

    /// <summary>拉取该账号所在的群列表（用于勾选）。</summary>
    public async Task<IReadOnlyList<GroupInfoData>> LoadGroupsAsync()
    {
        try
        {
            await TryAdoptOneBotEndpointAsync();   // 先保证地址/token 是对的
            await using var oneBot = new OneBotClient(OneBotHttp, OneBotWs,
                OneBotToken.Length > 0 ? OneBotToken : null,
                wsToken: OneBotWsToken.Length > 0 ? OneBotWsToken : null);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var groups = await oneBot.GetGroupListAsync(cts.Token) ?? [];
            AppendLog($"读到 {groups.Count} 个群");
            return groups;
        }
        catch (Exception ex)
        {
            AppendLog($"拉取群列表失败：{ex.Message}");
            Toasts.Error("拉取群列表失败", ex.Message);
            return [];
        }
    }

    /// <summary>
    /// 给每个账号自动分配互不冲突的 OneBot 端口并重启 SnowLuma。
    /// 这是"多账号只有一个能连上"的正解：SnowLuma 默认让所有账号都用 3000/3001。
    /// </summary>
    public async Task AutoAssignPortsAsync()
    {
        var assigned = SnowlumaManager.AssignDistinctPorts(InstallDir);
        if (assigned.Count == 0)
        {
            Toasts.Warn("没有可分配的账号", "先让 SnowLuma 至少登录过一次 QQ。");
            return;
        }
        var summary = string.Join("、", assigned.Select(a => $"{a.Uin}→{a.Http}"));
        AppendLog($"已为 {assigned.Count} 个账号分配端口：{summary}（需重启生效）");
        Toasts.Success("已分配端口", $"{summary}；正在重启 SnowLuma");

        if (_manager.FindRunningPid(InstallDir) is not null)
        {
            await StopAsync();      // 端口是启动时读的，不重启不生效
            await StartAsync();
        }
    }

    /// <summary>应用用户勾选的群。</summary>
    public void ApplyGroups(IReadOnlyList<long> ids)
    {
        GroupIdsText = string.Join(",", ids);
        OnPropertyChanged(nameof(GroupSummary));
        SaveSettings();
        AppendLog($"已选择监听 {ids.Count} 个群");
        Toasts.Success("已更新监听群", ids.Count == 0 ? "当前不会处理任何群" : $"共 {ids.Count} 个群");
        EnablePendingFeature();
    }

    /// <summary>选完群后，把用户刚才想开的那个开关打开。</summary>
    private void EnablePendingFeature()
    {
        if (_pendingEnable is null || ParseGroupIds().Count == 0)
            return;
        var name = _pendingEnable;
        _pendingEnable = null;
        switch (name)
        {
            case "召唤通知": FeatureSummon = true; break;
            case "作业自动录入": FeatureHomework = true; break;
            case "换课自动处理": FeatureExchange = true; break;
            case "群文件自动归档": FeatureFileArchive = true; break;
            case "上课课件弹窗": FeatureCoursewarePopup = true; break;
        }
        AppendLog($"已开启「{name}」");
    }

    /// <summary>从 SnowLuma 日志刷新"登录了哪些号"与端口冲突提示。</summary>
    public void RefreshLoggedInAccounts()
    {
        var uins = SnowlumaManager.ReadLoggedInUins(InstallDir);
        var nicknames = SnowlumaManager.ReadAccountNicknames(InstallDir);
        foreach (var uin in uins)
            MergeCandidate(new QqAccount { Uin = uin, Nickname = nicknames.GetValueOrDefault(uin, "") });

        // 每个账号已经各用一组端口时，这个问题就不存在了，别再提示
        var ports = uins
            .Select(u => SnowlumaManager.ReadOneBotEndpoint(InstallDir, u)?.Http)
            .Where(p => p is not null)
            .ToList();
        var distinctPorts = ports.Count > 0 && ports.Distinct().Count() == ports.Count;
        MultiAccountHint = uins.Count <= 1 || distinctPorts
            ? ""
            : $"检测到 {uins.Count} 个 QQ 账号登录（{string.Join("、", uins)}），"
              + "但它们共用同一组 OneBot 端口，只有一个能连上。点「自动分配端口」即可解决。";
    }

    /// <summary>随机初始密码：避开容易看错的 0/O/1/l/I。</summary>
    internal static string GeneratePassword()
    {
        const string alphabet = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(12);
        return new string(bytes.Select(b => alphabet[b % alphabet.Length]).ToArray());
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
        }
    }

    /// <summary>风险提示原文（弹窗与横幅共用一份，避免两处说法不一致）。</summary>
    public const string RiskWarningText =
        "QQ 官方可能检测到第三方登录方式并封禁账号。强烈建议不要使用全新注册的 QQ 号操作；" +
        "班级号请确认可以接受该风险后再启动注入。";

    /// <summary>用户在弹窗里确认风险后调用。</summary>
    public void AcceptRisk()
    {
        if (RiskAccepted)
            return;
        RiskAccepted = true;
        AppendLog("已确认风险，允许启动注入。");
        Toasts.Success("已确认风险");
    }

    // ================= QQ 账号（多账号时选一个） =================

    /// <summary>候选账号（检测到的 + 以前选过的）。</summary>
    public ObservableCollection<QqAccount> QqCandidates { get; } = new();

    /// <summary>已同意的 SnowLuma 协议版本号（空 = 没同意过）。</summary>
    private string _agreementsVersion = "";

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
                return "未选择";
            var nick = QqCandidates.FirstOrDefault(a => a.Uin == QqAccount)?.Nickname;
            return string.IsNullOrWhiteSpace(nick) ? $"已选：{QqAccount}" : $"已选：{QqAccount}（{nick}）";
        }
    }

    private string _onlineQqText = "未检测";
    public string OnlineQqText { get => _onlineQqText; private set => Set(ref _onlineQqText, value); }

    private string _detectionHint = "";
    /// <summary>检测不到账号时的原因（直接显示在账号行下面）。</summary>
    public string DetectionHint
    {
        get => _detectionHint;
        private set { if (Set(ref _detectionHint, value)) OnPropertyChanged(nameof(HasDetectionHint)); }
    }

    public bool HasDetectionHint => DetectionHint.Length > 0;

    /// <summary>探测当前注入实例登录的 QQ，并把它并入候选列表。返回检测到的账号（可能为 null）。</summary>
    /// <summary>
    /// 检测当前在线的 QQ 账号。
    /// 顺带把运行状态刷一遍——"能不能检测到账号"和"OneBot 通不通"本来就是同一件事，
    /// 所以不需要单独的「探测」按钮。
    /// </summary>
    public async Task<QqAccount?> DetectOnlineQqAsync()
    {
        DetectionHint = "";
        await ProbeAsync();
        IsProbing = true;
        try
        {
            await using var oneBot = new OneBotClient(OneBotHttp, OneBotWs,
                OneBotToken.Length > 0 ? OneBotToken : null,
                wsToken: OneBotWsToken.Length > 0 ? OneBotWsToken : null);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var info = await oneBot.GetLoginInfoAsync(cts.Token);
            if (info is { UserId: > 0 })
            {
                var found = new QqAccount { Uin = info.UserId, Nickname = info.Nickname };
                MergeCandidate(found);
                RefreshLoggedInAccounts();   // 用日志里的昵称补全其它账号
                OnlineQqText = string.IsNullOrWhiteSpace(found.Nickname)
                    ? found.Uin.ToString()
                    : $"{found.Uin}（{found.Nickname}）";
                return found;
            }
            DetectionHint = "OneBot 没有返回账号信息。";
        }
        catch (Exception ex)
        {
            AppendLog($"检测 QQ 账号失败：{ex.Message}");
            DetectionHint = ExplainDetectionFailure(ex);
        }
        finally { IsProbing = false; }

        OnlineQqText = "未检测到";
        Toasts.Warn("没检测到 QQ 账号", DetectionHint);
        return null;
    }

    private string _injectionHint = "";
    /// <summary>注入失败的可行原因（直接显示在运行状态下面）。</summary>
    public string InjectionHint
    {
        get => _injectionHint;
        private set { if (Set(ref _injectionHint, value)) OnPropertyChanged(nameof(HasInjectionHint)); }
    }

    public bool HasInjectionHint => InjectionHint.Length > 0;

    /// <summary>
    /// 解释"SnowLuma 起来了但没注入"。
    /// 读它自己的日志找注入失败行；Linux 上再结合 ptrace 限制给出具体做法。
    /// </summary>
    private string ExplainInjectionFailure()
    {
        var failure = SnowlumaManager.LastHookFailure(InstallDir);
        var ptrace = SnowlumaManager.ReadPtraceScope();
        var qq = SnowlumaManager.ReadQqVersion();
        if (failure is not null && ptrace == 1)
            return "注入失败：Linux 的 ptrace 限制（kernel.yama.ptrace_scope=1 只允许跟踪子进程）。"
                 + "执行 sudo sysctl kernel.yama.ptrace_scope=0 后重试，或从 SnowLuma 的 WebUI 里启动 QQ。";
        if (failure is not null && failure.Contains("COMPONENT_LOAD_FAILED", StringComparison.OrdinalIgnoreCase))
        {
            // 这一步已经越过 ptrace（能附加了），失败发生在 QQ 进程内部加载注入组件时
            var ver = qq is null ? "" : $"，当前 QQ {qq}";
            return $"注入组件在 QQ 里加载失败（COMPONENT_LOAD_FAILED{ver}）：这属于 SnowLuma 原生注入的兼容性问题。"
                 + "确认它支持的 QQ 版本，或改用它的 Docker 部署（Linux 上更稳）；也可以带日志去它的 issue 反馈。";
        }
        if (failure is not null)
            return $"注入失败：{Trim(failure)}（详见 SnowLuma 的日志目录）";
        if (ptrace == 1)
            return "还没注入成功。Linux 上注入需要 ptrace 权限（当前 kernel.yama.ptrace_scope=1），"
                 + "可执行 sudo sysctl kernel.yama.ptrace_scope=0 后重试。";
        return "";
    }

    /// <summary>检测不到账号时，说清楚卡在哪一步（否则用户只知道"检测不到"）。
    private string ExplainDetectionFailure(Exception ex)
    {
        if (NeedsWebUiSetup)
            return $"SnowLuma 还没同意用户协议/隐私政策（点「启动」会弹窗，也可打开 {WebUiUrl}），在此之前它不会注入。";
        return QqStatusSeverity switch
        {
            NoticeSeverity.Error when QqStatusText.Contains("未安装") => "尚未安装 SnowLuma。",
            NoticeSeverity.Error when QqStatusText.Contains("QQ 未运行") => "QQ 未运行，先启动并登录班级 QQ。",
            NoticeSeverity.Informational => $"SnowLuma 未启动，先点「启动」（{ex.Message}）。",
            NoticeSeverity.Warning when QqStatusText.Contains("未注入") =>
                InjectionHint.Length > 0
                    ? InjectionHint
                    : $"SnowLuma 已启动但没有注入成功，可打开 WebUI（{WebUiUrl}）查看原因。",
            _ => $"OneBot HTTP 未响应：确认端口与 Token 和 OneBot 端一致（{ex.Message}）。"
        };
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

    /// <summary>
    /// 按账号填入它的 OneBot 地址与 token（config/onebot_&lt;uin&gt;.json）。
    /// 选了账号就直接填好，不需要用户再关心"自动填充"，也不需要靠探测猜。
    /// </summary>
    private void ApplyEndpointForAccount(long uin)
    {
        var endpoint = SnowlumaManager.ReadOneBotEndpoint(InstallDir, uin);
        if (endpoint is null)
            return;
        OneBotHttp = endpoint.Http;
        OneBotWs = endpoint.Ws;
        OneBotToken = endpoint.Token;
        OneBotWsToken = endpoint.WsToken;
        AppendLog($"已按账号 {uin} 填入 OneBot 地址与 token（HTTP/WS 各一个）");
    }

    /// <summary>用户选定了账号。</summary>
    public void ApplyQqAccount(QqAccount account)
    {
        MergeCandidate(account);
        QqAccount = account.Uin;
        ApplyEndpointForAccount(account.Uin);
        _ = ProbeAsync();   // 顺手刷新状态，别让状态栏停在旧的"未注入"
        OnPropertyChanged(nameof(QqAccountLabel));
        SaveSettings();
        var label = string.IsNullOrWhiteSpace(account.Nickname)
            ? account.Uin.ToString()
            : $"{account.Uin}（{account.Nickname}）";
        AppendLog($"已选择 QQ 账号：{label}");
        Toasts.Success("已选择 QQ 账号", label);
    }

    public void NoteQqAccountCanceled()
        => Toasts.Show("未选择 QQ 账号", "", NoticeSeverity.Informational);

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
    /// <summary>
    /// OneBot **WS** token（与 HTTP token 通常不同）。留空则退回用 HTTP 那个。
    /// </summary>
    public string OneBotWsToken
    {
        get => _oneBotWsToken;
        set { if (Set(ref _oneBotWsToken, value)) AutoSaveSoon(); }
    }

    private string _oneBotWsToken = "";

    public string OneBotToken
    {
        get => _oneBotToken;
        set { if (Set(ref _oneBotToken, value)) AutoSaveSoon(); }
    }

    private string _groupIds = "";
    public string GroupIdsText
    {
        get => _groupIds;
        set
        {
            if (!Set(ref _groupIds, value))
                return;
            OnPropertyChanged(nameof(GroupSummary));
            RefreshFeatureGates();
            AutoSaveSoon();
        }
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

    // ================= SnowLuma 更新 =================

    /// <summary>已安装的 SnowLuma 版本（读不到就是空）。</summary>
    public string InstalledSnowlumaVersion =>
        SnowlumaManager.ReadInstalledVersion(InstallDir) ?? "";

    /// <summary>下拉里选中的（默认最新）版本。</summary>
    public string LatestSnowlumaVersion => SelectedRelease?.Tag?.TrimStart('v', 'V') ?? "";

    /// <summary>有新版本可更新：已装 + 有可选版本 + 两者不同。</summary>
    public bool HasSnowlumaUpdate
    {
        get
        {
            var installed = InstalledSnowlumaVersion;
            var latest = LatestSnowlumaVersion;
            return IsInstalled && installed.Length > 0 && latest.Length > 0
                   && !string.Equals(installed, latest, StringComparison.OrdinalIgnoreCase);
        }
    }

    public string SnowlumaVersionText => IsInstalled
        ? (InstalledSnowlumaVersion.Length > 0
            ? $"当前 {InstalledSnowlumaVersion} → 最新 {LatestSnowlumaVersion}"
            : $"已安装（读不到版本）→ 最新 {LatestSnowlumaVersion}")
        : "尚未安装";

    public string SnowlumaUpdateButtonText => Busy ? "更新中…" : "更新";

    /// <summary>
    /// 更新 SnowLuma：停止运行 → 下载最新包 → 覆盖式解压（保留 config/data）→ 原本在跑就重新启动。
    /// </summary>
    public async Task UpdateSnowlumaAsync()
    {
        if (SelectedRelease is null || Busy)
            return;
        var asset = SnowlumaManager.PickAsset(SelectedRelease, SnowlumaManager.CurrentRid());
        if (asset is null)
        {
            AppendLog("当前平台无可用包。");
            return;
        }
        var wasRunning = SnowlumaManager.IsEndpointAliveAsync(OneBotHttp, OneBotToken).GetAwaiter().GetResult();
        Busy = true;
        OnPropertyChanged(nameof(SnowlumaUpdateButtonText));
        try
        {
            if (wasRunning)
            {
                AppendLog("先停止 SnowLuma 再更新…");
                await StopAsync();
            }
            var archive = Path.Combine(InstallDir, "_dl", asset.Name);
            Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
            var prog = new Progress<double>(p => Progress = p * 100);
            AppendLog($"更新：下载 {asset.Name}…");
            await _manager.DownloadAsync(asset.DownloadUrl, archive, prog, CancellationToken.None);
            AppendLog("更新：覆盖文件（保留登录状态与配置）…");
            var copied = await Task.Run(() => SnowlumaManager.ApplyUpdate(archive, InstallDir));
            AppendLog($"更新完成，覆盖 {copied} 个文件。");
            OnPropertyChanged(nameof(InstalledSnowlumaVersion));
            OnPropertyChanged(nameof(HasSnowlumaUpdate));
            OnPropertyChanged(nameof(SnowlumaVersionText));
            Toasts.Success("SnowLuma 已更新", $"现在是最新版本 {LatestSnowlumaVersion}");
            if (wasRunning)
            {
                AppendLog("更新前它在运行，重新启动…");
                await StartAsync();
            }
        }
        catch (Exception ex)
        {
            AppendLog($"更新失败：{ex.Message}");
            Toasts.Error("SnowLuma 更新失败", ex.Message);
        }
        finally
        {
            Busy = false;
            Progress = 0;
            OnPropertyChanged(nameof(SnowlumaUpdateButtonText));
        }
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
            Toasts.Success("SnowLuma 下载完成");
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
            if (!await EnsureAgreementsAcceptedAsync())
            {
                QqStatusText = "未同意协议";
                QqStatusSeverity = NoticeSeverity.Warning;
                return;   // 不同意协议 → 不启动（启动了它也不会注入）
            }
            // SnowLuma 默认 hookAutoLoad=false：只起 WebUI、不自动注入。
            // 用户点的是「启动注入」，所以这里替他把开关打开（可在 WebUI 里改回去）。
            EnsureAutoInjectEnabled();

            RefreshLoggedInAccounts();
            await TryAdoptOneBotEndpointAsync();
            // 启动前先问一句端点是否已经在跑 —— Windows 上没有 /proc，认不出别人启动的进程，
            // 不检查就会再起一个实例抢端口（Linux 上靠 /proc 认出来了，Windows 上不会）。
            if (await SnowlumaManager.IsEndpointAliveAsync(OneBotHttp, OneBotToken))
            {
                QqStatusText = "QQ 已经在跑（未重复启动）";
                QqStatusSeverity = NoticeSeverity.Success;
                AppendLog("检测到 OneBot 端点已有应答，跳过启动。");
                return;
            }
            var webUiPassword = EnsureWebUiPassword();
            if (webUiPassword is not null)
                EnsureWebUiCredentials();   // 先让凭据能被播种，否则环境变量会被忽略
            await _manager.StartAsync(InstallDir, acceptAgreements: true, webUiPassword: webUiPassword);
            NeedsWebUiSetup = false;   // 同意是我们带过去的，不该再显示"卡在等同意"
            if (webUiPassword is not null)
            {
                WebUiPasswordApplied = _manager.StartedByThisApp
                                       || SnowlumaManager.WebUiCredentialsSeededFromEnv(InstallDir);
                AppendLog("已为 SnowLuma WebUI 指定密码");
                // 用户自己设的密码不回显（那不是临时密码）；自动生成的才需要告诉他
                Toasts.Success(_webUiPasswordManual ? "WebUI 密码已生效" : "WebUI 初始密码已设置",
                    _webUiPasswordManual ? "" : $"用户名 admin，密码 {webUiPassword}（设置页可复制）");
            }
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
            await Task.Run(() => _manager.Stop(InstallDir));
            QqDetected = false;
            QqStatusText = "已停止";
            QqStatusSeverity = NoticeSeverity.Informational;
            AppendLog("SnowLuma 已停止。");
            Toasts.Success("SnowLuma 已停止");
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
    /// 确保 SnowLuma 的"发现 QQ 就自动注入"是开着的。
    /// 它默认 false，只起 WebUI 不注入——用户看到的现象就是"启动了但一直未注入"。
    /// </summary>
    private void EnsureAutoInjectEnabled()
    {
        var current = SnowlumaManager.ReadHookAutoLoad(InstallDir);
        if (current is null)
        {
            AppendLog("读不到 SnowLuma 的 runtime.json，跳过自动注入开关");
            return;
        }
        if (current.Value)
            return;
        if (SnowlumaManager.SetHookAutoLoad(InstallDir, true))
        {
            AppendLog("已开启 SnowLuma 的自动注入（hookAutoLoad=true）");
            Toasts.Show("已开启自动注入", "SnowLuma 会在发现 QQ 进程时注入。", NoticeSeverity.Success);
        }
        else
        {
            AppendLog("开启自动注入失败（runtime.json 不可写或格式不对）");
            Toasts.Warn("没能开启自动注入", "请在 SnowLuma 的 WebUI 里手动开启。");
        }
    }

    /// <summary>
    /// 启动前确认 SnowLuma 的协议：读它安装目录里的 EULA.md / PRIVACY.md，
    /// 没同意过（或协议文本变了）就弹窗；同意后由 <see cref="SnowlumaManager.StartAsync"/>
    /// 用它的官方环境变量开关带过去。返回是否可以继续启动。
    /// </summary>
    public async Task<bool> EnsureAgreementsAcceptedAsync()
    {
        var docs = SnowlumaAgreements.ReadFrom(InstallDir);
        if (docs.Count == 0)
        {
            AppendLog("找不到 SnowLuma 的协议文件（EULA.md / PRIVACY.md），无法在应用内征得同意");
            NeedsWebUiSetup = true;
            return false;
        }

        var version = SnowlumaAgreements.ComputeVersion(docs);

        // 已经在 WebUI 里同意过（SnowLuma 自己记着 consent.json）→ 不用再问用户一次
        if (SnowlumaAgreements.AlreadyConsented(InstallDir, docs))
        {
            _agreementsVersion = version;
            SaveSettings();
            NeedsWebUiSetup = false;
            AppendLog("SnowLuma 已记录过协议同意，跳过征询");
            return true;
        }

        if (version == _agreementsVersion)
        {
            NeedsWebUiSetup = false;
            return true;   // 这个版本我们已经征得过同意
        }

        NeedsWebUiSetup = true;
        if (ConsentPrompt is null)
        {
            // 界面没接上（测试/无主窗口）：别静默失败，告诉用户手动出口
            AppendLog("没有可用的协议弹窗，请点「打开 WebUI」同意协议");
            Toasts.Warn("需要同意 SnowLuma 的协议",
                $"这是 SnowLuma 自己的用户协议与隐私政策（与封号风险提示是两回事）：打开 {WebUiUrl} 同意后才会注入。");
            return false;
        }

        if (!await ConsentPrompt(docs))
        {
            AppendLog("未同意 SnowLuma 用户协议/隐私政策，注入不会开始");
            Toasts.Warn("未同意协议", "不同意协议时 SnowLuma 不会注入。");
            return false;
        }

        _agreementsVersion = version;
        SaveSettings();
        NeedsWebUiSetup = false;
        AppendLog("已同意 SnowLuma 用户协议/隐私政策");
        Toasts.Success("已同意 SnowLuma 协议");
        return true;
    }

    /// <summary>
    /// 探测注入状态。
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
                OneBotToken.Length > 0 ? OneBotToken : null,
                wsToken: OneBotWsToken.Length > 0 ? OneBotWsToken : null);
            var s = await _manager.ProbeAsync(InstallDir, oneBot);
            (QqStatusSeverity, QqStatusText) = s switch
            {
                SnowlumaStatus.Online => (NoticeSeverity.Success, "已注入，在线"),
                SnowlumaStatus.InjectedNotLoggedIn =>
                    (NoticeSeverity.Warning, "已注入，但 QQ 未登录"),
                SnowlumaStatus.StartedNotInjected =>
                    (NoticeSeverity.Warning, "已启动，未注入"),
                SnowlumaStatus.NotRunning => (NoticeSeverity.Informational, "未启动（未注入）"),
                SnowlumaStatus.QqNotFound => (NoticeSeverity.Error, "QQ 未运行"),
                SnowlumaStatus.NotInstalled => (NoticeSeverity.Error, "未安装 SnowLuma"),
                _ => (NoticeSeverity.Error, s.ToString())
            };
            Status = QqStatusText;
            QqDetected = s is SnowlumaStatus.Online or SnowlumaStatus.InjectedNotLoggedIn;
            if (s is SnowlumaStatus.Online)
                RefreshLoggedInAccounts();
            if (s is SnowlumaStatus.StartedNotInjected or SnowlumaStatus.NotRunning)
                await TryAdoptOneBotEndpointAsync();   // 状态不对时也顺手把连接信息读对

            // 注入是在 SnowLuma 里做的，失败只写它自己的日志 → 读出来告诉用户卡在哪
            InjectionHint = s is SnowlumaStatus.StartedNotInjected
                ? ExplainInjectionFailure()
                : "";
            switch (s)
            {
                case SnowlumaStatus.Online:
                    Toasts.Success("QQ 在线");
                    break;
                case SnowlumaStatus.InjectedNotLoggedIn:
                    Toasts.Warn("已注入，QQ 未登录", "在 QQ 里登录班级号后重试。");
                    break;
                case SnowlumaStatus.StartedNotInjected when NeedsWebUiSetup:
                    Toasts.Warn("需要同意 SnowLuma 的协议",
                        $"这是 SnowLuma 自己的用户协议与隐私政策（与封号风险提示是两回事）：打开 {WebUiUrl} 同意后才会注入。");
                    break;
                case SnowlumaStatus.StartedNotInjected:
                    Toasts.Warn("SnowLuma 已启动，但未注入", "确认 QQ 已登录；必要时打开 WebUI 查看原因。");
                    break;
                case SnowlumaStatus.NotRunning:
                    Toasts.Show("未启动（未注入）", "点「启动」开始注入。", NoticeSeverity.Informational);
                    break;
                case SnowlumaStatus.QqNotFound:
                    Toasts.Error("QQ 未运行");
                    break;
                case SnowlumaStatus.NotInstalled:
                    Toasts.Error("未安装 SnowLuma");
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

    // ================= 软件更新 =================
    //
    // 只做"查 + 下载"。真正替换文件仅限 Windows（见 UpdateInstaller）；
    // Linux 的应用由包管理器安装，自己替换 /opt 会和 pacman 数据库不一致，故禁用。

    public string CurrentVersionText => $"当前版本 {AppVersion.Current}";

    private string _latestVersionText = "尚未检查";

    /// <summary>裸版本号（不带"最新版本"这类前缀）：给文件名、日志用。</summary>
    private string _latestVersionBare = "";
    public string LatestVersionText { get => _latestVersionText; private set => Set(ref _latestVersionText, value); }

    private NoticeSeverity _updateSeverity = NoticeSeverity.Informational;
    public NoticeSeverity UpdateSeverity { get => _updateSeverity; private set => Set(ref _updateSeverity, value); }

    private string _updateStatusText = "尚未检查更新";
    public string UpdateStatusText { get => _updateStatusText; private set => Set(ref _updateStatusText, value); }

    private bool _hasUpdate;
    public bool HasUpdate
    {
        get => _hasUpdate;
        private set
        {
            if (!Set(ref _hasUpdate, value))
                return;
            OnPropertyChanged(nameof(CanInstallUpdate));
            OnPropertyChanged(nameof(CanInstallLinuxUpdate));
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
            OnPropertyChanged(nameof(CanInstallLinuxUpdate));
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

    /// <summary>按钮状态说明（为什么能点 / 为什么是灰的）。</summary>
    public string UpdatePlatformHint
    {
        get
        {
            if (!CanSelfUpdate)
            {
                if (!CanInstallPackage)
                    return "Linux：本机没有 pacman，请用发行版自带的包管理器更新";
                return HasUpdate
                    ? "Linux：点「下载并安装」会下载安装包并要一次系统密码，用 pacman 装好"
                    : "已是最新版本，无需更新";
            }
            if (IsDownloadingUpdate)
                return "正在下载…";
            if (!HasUpdate)
                return "已是最新版本，无需更新";
            if (_assetUrl is null)
                return "这个 Release 里没有 Windows 安装包（smartclassroom-*-win-x64.zip），"
                       + "请到 Release 页手动下载";
            return "支持应用内自动更新：下载后关掉应用，运行生成的 apply-update.cmd";
        }
    }

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
            // Windows 挑 zip 自更新；Linux 挑 pacman 包（下载后交给 pacman 装）
            var pattern = CanSelfUpdate ? "win-x64.zip" : "x86_64.pkg.tar.zst";
            var info = await _updates.CheckAsync(AppVersion.Current, assetPattern: pattern);
            _assetUrl = info.AssetUrl;
            _releaseUrl = info.ReleaseUrl;
            OnPropertyChanged(nameof(HasReleasePage));

            if (!info.RemoteReachable)
            {
                HasUpdate = false;
                LatestVersionText = "未获取到";
                UpdateSeverity = NoticeSeverity.Warning;
                UpdateStatusText = "检查失败：拿不到远端版本（网络不通或仓库没有 tag/Release）";
                Toasts.Warn("更新检查失败");
            }
            else if (info.HasUpdate)
            {
                HasUpdate = true;
                LatestVersionText = $"最新版本 {info.LatestVersion}";
                _latestVersionBare = info.LatestVersion;
                UpdateSeverity = NoticeSeverity.Warning;
                UpdateStatusText = $"发现新版本 {info.LatestVersion}（当前 {AppVersion.Current}）";
                Toasts.Show("发现新版本", $"{AppVersion.Current} → {info.LatestVersion}", NoticeSeverity.Warning);
            }
            else
            {
                HasUpdate = false;
                LatestVersionText = $"最新版本 {info.LatestVersion}";
                _latestVersionBare = info.LatestVersion;
                // 已是最新：不要再留资产地址，否则按钮还能下载同一个版本
                _assetUrl = null;
                UpdateSeverity = NoticeSeverity.Success;
                UpdateStatusText = "已是最新版本";
                Toasts.Success("已是最新版本");
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
            OnPropertyChanged(nameof(CanInstallLinuxUpdate));
            OnPropertyChanged(nameof(UpdatePlatformHint));
        }
    }

    /// <summary>下载新版本并生成覆盖脚本（仅 Windows 可用）。</summary>
    /// <summary>Linux：本机支持应用内安装（有 pacman）。</summary>
    public bool CanInstallPackage => UpdateInstaller.CanInstallPackage;

    /// <summary>Linux：能点"下载并安装" —— 必须**确实有新版本**（否则按钮灰着，和 Windows 一致）。</summary>
    public bool CanInstallLinuxUpdate => CanInstallPackage && HasUpdate && _assetUrl is not null
                                        && !IsDownloadingUpdate;

    /// <summary>
    /// Linux 更新：下载 pacman 包 → 弹窗要系统密码 → 后台 pacman -U 安装 → 提示重启。
    /// 用户要求的就是这个流程（Arch 系一样在应用内更新）。
    /// </summary>
    public async Task InstallLinuxUpdateAsync(Func<string, Task<string?>> askPassword)
    {
        if (_assetUrl is null)
        {
            Toasts.Warn("没有可下载的安装包");
            return;
        }
        IsDownloadingUpdate = true;
        UpdateProgress = 0;
        try
        {
            UpdateStatusText = "正在下载安装包…";
            var pkg = await UpdateInstaller.DownloadPackageAsync(_assetUrl, _latestVersionBare,
                new Progress<double>(p => UpdateProgress = p));
            UpdateStatusText = "正在安装（需要系统密码）…";
            var password = await askPassword("安装更新需要系统密码（pacman）");
            if (password is null)
            {
                UpdateStatusText = "已取消安装";
                return;
            }
            var (ok, output) = await UpdateInstaller.InstallPackageAsync(pkg, password);
            AppendLog(ok ? "系统包已安装完成" : $"安装失败：{output}");
            if (ok)
            {
                UpdateStatusText = "安装完成，重启应用后生效";
                Toasts.ShowWithActions($"更新 {LatestVersionText} 已安装",
                    "重启应用即可用上新版本",
                    [
                        new ToastAction("立即重启", () => RestartApp(), Accent: true),
                        new ToastAction("稍后重启", () => Toasts.Show("已稍后更新",
                            "下次打开应用时就是新版本"))
                    ],
                    NoticeSeverity.Success);
            }
            else
            {
                UpdateStatusText = "安装失败";
                Toasts.Error("安装失败", output.Length > 200 ? output[..200] : output);
            }
        }
        catch (Exception ex)
        {
            UpdateStatusText = $"安装失败：{ex.Message}";
            AppendLog($"安装更新失败：{ex.Message}");
            Toasts.Error("安装更新失败", ex.Message);
        }
        finally
        {
            IsDownloadingUpdate = false;
            UpdateProgress = 0;
            OnPropertyChanged(nameof(CanInstallUpdate));
            OnPropertyChanged(nameof(CanInstallLinuxUpdate));
            OnPropertyChanged(nameof(UpdatePlatformHint));
        }
    }

    /// <summary>重启本应用：另起一个进程等本进程退出后再启动，然后退出自己。</summary>
    private static void RestartApp()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null)
                return;
            if (OperatingSystem.IsWindows())
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe)
                {
                    UseShellExecute = true
                });
            }
            else
            {
                // 等 1 秒再起，避开单实例锁（旧进程还在退出中）
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/bin/sh")
                {
                    ArgumentList = { "-c", $"sleep 1; exec \"{exe}\"" },
                    UseShellExecute = false
                });
            }
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            Toasts.Error("重启失败", ex.Message);
        }
    }

    /// <summary>下载完成后是否可以直接"重启以更新"（Windows）。</summary>
    public bool CanRestartToUpdate => CanSelfUpdate && UpdateInstaller.ReadPending(_appDir) is not null;

    public string RestartUpdateButtonText => "重启以更新";

    /// <summary>
    /// 重启以更新：记下待完成标记 → 生成并启动覆盖脚本 → 退出应用。
    /// **不需要密码**：这只是重启，不是改设置（用户明确要求免密）。
    /// </summary>
    public bool RestartToUpdate()
    {
        if (!CanSelfUpdate)
            return false;
        if (UpdateInstaller.ReadPending(_appDir) is null)
        {
            Toasts.Warn("没有待安装的更新", "先点「下载并安装」");
            return false;
        }
        if (!UpdateInstaller.LaunchScriptAndExit(_appDir))
        {
            Toasts.Error("启动更新失败", "可以到应用目录手动运行 apply-update.cmd");
            return false;
        }
        Toasts.Success("正在重启以完成更新", "应用会自动关闭并重新打开");
        // 给提示一点时间，然后退出（脚本会等我们退出后覆盖文件并重启）
        _ = Task.Delay(800).ContinueWith(_ => Environment.Exit(0));
        return true;
    }

    public async Task DownloadUpdateAsync()
    {
        if (_assetUrl is null)
        {
            Toasts.Warn("没有可下载的安装包");
            return;
        }
        IsDownloadingUpdate = true;
        UpdateProgress = 0;
        try
        {
            var stage = await UpdateInstaller.StageAsync(_assetUrl, LatestVersionText, _appDir,
                new Progress<double>(p => UpdateProgress = p));
            UpdateInstaller.MarkPending(_appDir, stage, LatestVersionText);
            PendingUpdateScript = UpdateInstaller.WriteScript(stage, _appDir);
            AppendLog($"更新已下载并解压到 {stage}；点「重启以更新」即可完成");
            OnPropertyChanged(nameof(CanRestartToUpdate));
            // 按用户要求：安装完成后弹一条通知，通知上带「重启以更新 / 稍后重启」两个按钮
            Toasts.ShowWithActions($"更新 {LatestVersionText} 已就绪",
                "重启应用即可用上新版本",
                [
                    new ToastAction("重启以更新", () => RestartToUpdate(), Accent: true),
                    new ToastAction("稍后重启", () => Toasts.Show("已稍后更新",
                        "下次启动应用时会自动完成覆盖"))
                ],
                NoticeSeverity.Success);
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
            OnPropertyChanged(nameof(CanInstallUpdate));
            OnPropertyChanged(nameof(CanInstallLinuxUpdate));
            OnPropertyChanged(nameof(UpdatePlatformHint));
        }
    }

    /// <summary>Release 页面地址（视图层负责用浏览器打开）。</summary>
    public string? ReleasePageUrl => _releaseUrl;

    // ================= 调试 =================

    /// <summary>配置文件位置（清空前让用户知道动的是哪个文件）。</summary>
    public string SettingsPathHint => "";

    /// <summary>
    /// 清空所有设置：删掉 settings.json，并把内存里那份一起复位
    /// （设置页与 Runtime 共用对象，只删文件的话退出时又会被写回来）。
    /// 作业/待处理/事件时间线属于数据，不在"设置"范围内，不动。
    /// </summary>
    public void ResetAllSettings()
    {
        // 密码也会被清掉 → 锁定状态必须跟着刷新（否则界面还显示"已锁定"）
        SettingsStore.Reset(SettingsPath);
        _shared?.ResetToDefaults();
        LoadSettings();               // 回到默认值（有共享对象时读的就是刚复位的那份）
        RefreshIntegrationState();    // 开关前置条件重新判定
        RefreshLockState();           // 管理员密码也清了
        OnPropertyChanged(nameof(FeatureSummary));
        AppendLog("已清空所有设置（作业/事件等数据未动）。");
        Toasts.Success("已清空所有设置", "重启后生效");
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
            _copyToDownloads = s.CopyToDownloads;
            OneBotHttp = s.OneBotHttp;
            OneBotWs = s.OneBotWs;
            GroupIdsText = string.Join(",", s.GroupIds);
            OneBotToken = s.OneBotToken;
            OneBotWsToken = s.OneBotWsToken;
            PluginToken = s.PluginToken;
            PluginPort = s.PluginPort;
            _agreementsVersion = s.SnowLumaAgreementsVersion;
            _webUiPasswordManual = s.SnowLumaWebUiPassword.Length > 0;
            WebUiPassword = s.SnowLumaWebUiPassword;
            _listenAllGroups = s.ListenAllGroups;
            _listenTeacherPrivate = s.ListenTeacherPrivate;
            _requireKnownTeacher = s.RequireKnownTeacher;
            _aiDecides = s.AiDecidesTeacherMessages;
            _qqAccount = s.QqAccount;
            QqCandidates.Clear();
            foreach (var a in s.QqAccounts)
                QqCandidates.Add(a);
            _riskAccepted = s.RiskAccepted;
            _adminHash = s.AdminPasswordHash;
            _rememberUnlock = s.RememberUnlock;
            _minimizeToTray = s.MinimizeToTray;
            _uiScale = ContentZoom.Clamp(s.UiScale);
            _cardScale = ContentZoom.Clamp(s.CardScale);
            _autoStart = s.AutoStart;
            _protectFromKill = s.ProtectFromKill;
            _notifyHomework = s.NotifyHomework;
            _notifyExchange = s.NotifyExchange;
            OnPropertyChanged(nameof(CardScale));
            OnPropertyChanged(nameof(CardScaleLabel));
            _theme = AppTheme.Normalize(s.Theme);
            OnPropertyChanged(nameof(ThemeChoice));
            _featureSummon = s.FeatureSummon;
            _featureHomework = s.FeatureHomework;
            _featureExchange = s.FeatureExchange;
            _featureFileArchive = s.FeatureFileArchive;
            _featureCoursewarePopup = s.FeatureCoursewarePopup;
            _featureNotice = s.FeatureNotice;
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
        var s = Settings;
        s.AiEngine = AiEngine.ToStorage();
        s.AiProvider = SelectedProvider?.Id ?? AiModelProviderHint;
        s.AiReasoning = AiReasoning;
        s.ArchiveRoot = ArchiveRoot;
        s.ArchiveDownloadAll = ArchiveDownloadAll;
        s.CopyToDownloads = CopyToDownloads;
        s.AiBaseUrl = AiBaseUrl;
        s.AiApiKey = AiApiKey;
        s.AiModel = AiModel;
        s.OneBotHttp = OneBotHttp;
        s.OneBotWs = OneBotWs;
        s.GroupIds = groups;
        s.OneBotToken = OneBotToken;
        s.OneBotWsToken = OneBotWsToken;
        s.PluginToken = PluginToken;
        s.PluginPort = PluginPort;
        s.SnowLumaAgreementsVersion = _agreementsVersion;
        s.SnowLumaWebUiPassword = _webUiPasswordManual ? WebUiPassword : "";
        s.ListenAllGroups = ListenAllGroups;
        s.ListenTeacherPrivate = ListenTeacherPrivate;
        s.RequireKnownTeacher = RequireKnownTeacher;
        s.AiDecidesTeacherMessages = AiDecidesTeacherMessages;
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
        s.FeatureNotice = FeatureNotice;
        s.AdminPasswordHash = _adminHash;
        s.RememberUnlock = RememberUnlock;
        s.MinimizeToTray = MinimizeToTray;
        s.UiScale = UiScale;
        s.CardScale = CardScale;
        s.AutoStart = AutoStart;
        s.ProtectFromKill = ProtectFromKill;
        s.NotifyHomework = NotifyHomework;
        s.NotifyExchange = NotifyExchange;
        s.Theme = _theme;

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
