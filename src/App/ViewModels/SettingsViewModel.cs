using System.Collections.ObjectModel;
using System.IO;
using SmartClassroom.Core.QQ;

namespace SmartClassroom.App.ViewModels;

/// <summary>设置页：AI / OneBot / SnowLuma 下载器。网络操作均为异步，失败只写日志行。</summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private readonly SnowlumaManager _manager = new();
    private string _log = "";
    private string _status = "未检测";
    private double _progress;
    private bool _busy;

    public SettingsViewModel()
    {
        _manager.OnLog += line => Log += line + "\n";
        InstallDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SmartClassroom", "snowluma");
    }

    // ---- OneBot / AI（文本配置，存本地，v0.4 不做加密） ----
    private string _oneBotHttp = "http://127.0.0.1:3000";
    public string OneBotHttp { get => _oneBotHttp; set => Set(ref _oneBotHttp, value); }

    private string _oneBotWs = "ws://127.0.0.1:3001";
    public string OneBotWs { get => _oneBotWs; set => Set(ref _oneBotWs, value); }

    private string _aiBaseUrl = "";
    public string AiBaseUrl { get => _aiBaseUrl; set => Set(ref _aiBaseUrl, value); }

    private string _aiModel = "";
    public string AiModel { get => _aiModel; set => Set(ref _aiModel, value); }

    // ---- SnowLuma 下载器 ----
    public string InstallDir { get; set; }
    public ObservableCollection<SnowlumaRelease> Releases { get; } = new();

    private SnowlumaRelease? _selected;
    public SnowlumaRelease? SelectedRelease { get => _selected; set => Set(ref _selected, value); }

    public string Status { get => _status; private set => Set(ref _status, value); }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public bool Busy { get => _busy; private set => Set(ref _busy, value); }
    public string Log { get => _log; private set => Set(ref _log, value); }

    public bool IsInstalled => File.Exists(Path.Combine(InstallDir, "index.mjs"));

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
            Log += "SnowLuma 就绪。\n";
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
            Log += "SnowLuma 已启动，观察注入日志。\n";
        }
        catch (Exception ex) { Log += $"启动失败：{ex.Message}\n"; }
    }

    public void Stop()
    {
        _manager.Stop();
        Log += "SnowLuma 已停止。\n";
    }

    public async Task ProbeAsync()
    {
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
        }
        catch (Exception ex) { Status = $"探测失败：{ex.Message}"; }
    }
}
