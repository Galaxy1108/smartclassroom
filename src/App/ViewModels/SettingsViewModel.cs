using System.Collections.ObjectModel;
using System.IO;
using SmartClassroom.Core;
using SmartClassroom.Core.QQ;

namespace SmartClassroom.App.ViewModels;

public sealed record AiProviderPreset(string Name, string BaseUrl);
public sealed record TeacherRow(string Qq, string Name, string Subject);

/// <summary>设置页：AI 预设 / OneBot / 下载器总开关 / 教师映射。改动即落盘 settings.json。</summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private readonly SnowlumaManager _manager = new();
    private string _log = "";
    private string _status = "未检测";
    private double _progress;
    private bool _busy;
    private bool _riskAccepted;
    private bool _qqDetected;

    public SettingsViewModel() : this(SettingsStore.DefaultPath) { }

    public SettingsViewModel(string settingsPath)
    {
        SettingsPath = settingsPath;
        _manager.OnLog += line => Log += line + "\n";
        InstallDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SmartClassroom", "snowluma");
        LoadSettings();
        AutostartEnabled = AutostartManager.IsEnabled(InstallDir);
    }

    public string SettingsPath { get; }

    // ---- AI（含预设；手写 HttpClient 直调 chat/completions，单测覆盖） ----
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

    private string _aiBaseUrl = "";
    public string AiBaseUrl { get => _aiBaseUrl; set => Set(ref _aiBaseUrl, value); }

    private string _aiApiKey = "";
    public string AiApiKey { get => _aiApiKey; set => Set(ref _aiApiKey, value); }

    private string _aiModel = "";
    public string AiModel { get => _aiModel; set => Set(ref _aiModel, value); }

    // ---- OneBot ----
    private string _oneBotHttp = "http://127.0.0.1:3000";
    public string OneBotHttp { get => _oneBotHttp; set => Set(ref _oneBotHttp, value); }

    private string _oneBotWs = "ws://127.0.0.1:3001";
    public string OneBotWs { get => _oneBotWs; set => Set(ref _oneBotWs, value); }

    private string _groupIds = "";
    public string GroupIdsText { get => _groupIds; set => Set(ref _groupIds, value); }

    // ---- 下载器总开关 ----
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
                var node = SnowlumaManager.FindNode(InstallDir);
                AutostartManager.SetEnabled(InstallDir, node, value);
                Log += value ? "已开启 SnowLuma 开机自启。\n" : "已关闭 SnowLuma 开机自启。\n";
            }
            catch (Exception ex) { Log += $"自启设置失败：{ex.Message}\n"; }
        }
    }

    // ---- 教师映射 ----
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
            Log += "老师姓名和科目不能为空。\n";
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

    // ---- 流程 ----
    public async Task RefreshReleasesAsync()
    {
        Busy = true;
        try
        {
            Releases.Clear();
            foreach (var r in await _manager.ListReleasesAsync())
                Releases.Add(r);
            SelectedRelease = Releases.Count > 0 ? Releases[0] : null;
            Log += $"找到 {Releases.Count} 个版本。\n";
        }
        catch (Exception ex) { Log += $"拉取版本失败：{ex.Message}\n"; }
        finally { Busy = false; }
    }

    public async Task DownloadSelectedAsync()
    {
        if (SelectedRelease is null || Busy)
            return;
        var asset = SnowlumaManager.PickAsset(SelectedRelease, SnowlumaManager.CurrentRid());
        if (asset is null)
        {
            Log += "当前平台无可用包。\n";
            return;
        }
        Busy = true;
        try
        {
            var archive = Path.Combine(InstallDir, "_dl", asset.Name);
            var prog = new Progress<double>(p => Progress = p * 100);
            Log += $"下载 {asset.Name}（{asset.Size / 1024 / 1024} MB）…\n";
            await _manager.DownloadAsync(asset.DownloadUrl, archive, prog, CancellationToken.None);
            Log += "解压中…\n";
            await Task.Run(() => SnowlumaManager.Extract(archive, InstallDir));
            Log += "SnowLuma 就绪。请阅读封号警告并勾选知晓后启动。\n";
            OnPropertyChanged(nameof(IsInstalled));
        }
        catch (Exception ex) { Log += $"下载失败：{ex.Message}\n"; }
        finally { Busy = false; Progress = 0; }
    }

    public async Task StartAsync()
    {
        try
        {
            await _manager.StartAsync(InstallDir);
            Log += "SnowLuma 已启动，5 秒后自动检测 QQ…\n";
            await Task.Delay(5000);
            await ProbeAsync();
        }
        catch (Exception ex) { Log += $"启动失败：{ex.Message}\n"; }
    }

    public void Stop()
    {
        _manager.Stop();
        QqDetected = false;
        Log += "SnowLuma 已停止。\n";
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
        Log += "已填入默认 OneBot 地址并保存，重启 App 后管线自动连接。\n";
    }

    public void LoadSettings()
    {
        var s = SettingsStore.Load(SettingsPath);
        AiBaseUrl = s.AiBaseUrl;
        AiApiKey = s.AiApiKey;
        AiModel = s.AiModel;
        OneBotHttp = s.OneBotHttp;
        OneBotWs = s.OneBotWs;
        GroupIdsText = string.Join(",", s.GroupIds);
        _riskAccepted = s.RiskAccepted;
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
            AiBaseUrl = AiBaseUrl,
            AiApiKey = AiApiKey,
            AiModel = AiModel,
            OneBotHttp = OneBotHttp,
            OneBotWs = OneBotWs,
            GroupIds = groups,
            Teachers = Teachers.Select(t => new Teacher
            {
                Qq = long.TryParse(t.Qq, out var n) ? n : 0,
                Name = t.Name,
                Subject = t.Subject
            }).ToList(),
            RiskAccepted = RiskAccepted
        }, SettingsPath);
        Log += "设置已保存。\n";
    }
}
