using System.Linq;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using SmartClassroom.App.ViewModels;

namespace SmartClassroom.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        DataContext ??= new SettingsViewModel();
        // SnowLuma 的协议同意弹窗（视图模型不直接碰 UI）
        Vm.ConsentPrompt = async docs => await Dialogs.ConsentAsync(docs);
    }

    private SettingsViewModel Vm => (SettingsViewModel)DataContext!;

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Vm.RefreshLockState();
        _ = Vm.CheckForUpdatesOnceAsync();   // 打开设置页自动查一次更新（进程内只查一次）
    }

    /// <summary>
    /// 退出应用。托盘菜单之外的第二条退出路径：
    /// 某些桌面环境托盘图标可能不显示，不能只留托盘一个出口。
    /// </summary>
    private async void QuitApp_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            return;
        await AppShell.RequestQuitAsync(desktop);
    }

    /// <summary>解锁设置：受管理员密码保护。</summary>
    private async void Unlock_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var ok = await Runtime.Auth.RequireAsync(
            reason => PasswordDialog.PromptAsync("解锁设置", reason),
            "解锁设置需要管理员密码");
        Vm.RefreshLockState();
        if (!ok)
            Vm.RefreshLockState();
    }

    /// <summary>设置/清除管理员密码本身也受保护（已设置密码时需先验证）。</summary>
    private async void ApplyPassword_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var ok = await Runtime.Auth.RequireAsync(
            reason => PasswordDialog.PromptAsync("修改管理员密码", reason),
            "修改管理员密码需要先验证当前密码");
        if (!ok)
            return;
        Vm.ApplyPassword();
    }

    // ---- AI ----
    private async void LoadCatalog_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await Vm.LoadCatalogAsync();

    private async void TestAi_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await Vm.TestAiAsync();

    private void RefreshSidecar_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Vm.RefreshSidecarInfo();

    // ---- Node ----
    private async void LoadNodeVersions_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await Vm.LoadNodeVersionsAsync();

    private async void DownloadNode_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await Vm.DownloadNodeAsync();

    // ---- ClassIsland 集成 ----
    private async void ProbePlugin_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await Vm.ProbePluginAsync();

    private async void LocateToken_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var (found, message) = Vm.LocatePluginToken();
        await Dialogs.ShowAsync(found ? "已找到插件 Token" : "未找到插件 Token", message);
    }

    // ---- 文件归档 ----

    /// <summary>选归档目录（用系统文件夹选择器，避免手打路径出错）。</summary>
    private async void BrowseArchive_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null)
            return;
        var picked = await top.StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
        {
            Title = "选择归档目录",
            AllowMultiple = false
        });
        var path = picked.FirstOrDefault()?.Path.LocalPath;
        if (!string.IsNullOrWhiteSpace(path))
            Vm.ArchiveRoot = path;
    }

    /// <summary>打开归档目录；不存在就先建出来，避免"点了没反应"。</summary>
    private void OpenArchive_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => OpenFolder(Vm.EffectiveArchiveRoot);

    /// <summary>在系统文件管理器里打开目录（不存在则先创建）。</summary>
    private void OpenFolder(string path)
    {
        try
        {
            System.IO.Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Vm.AppendLogFromView($"打开目录失败：{ex.Message}");
            Toasts.Error("打开目录失败", ex.Message);
        }
    }

    // ---- 教师映射 ----
    private async void Save_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var ok = await Runtime.Auth.RequireAsync(
            reason => PasswordDialog.PromptAsync("保存设置", reason),
            "保存设置需要管理员密码");
        if (ok)
        {
            Vm.SaveSettings();
            Toasts.Success("设置已保存", "改动已写入 settings.json。");
        }
    }

    private void AddTeacher_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Vm.AddTeacher();

    private void RemoveTeacher_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is TeacherRow row)
            Vm.RemoveTeacher(row);
    }

    // ---- SnowLuma ----
    private async void Refresh_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await Vm.RefreshReleasesAsync();

    private async void Download_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await Vm.DownloadSelectedAsync();

    private async void Start_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // 没确认过风险就先弹窗（"我已知晓风险"本来就该是弹窗，不是一个随手能勾的小方框）
        if (!Vm.RiskAccepted)
        {
            var ok = await Dialogs.ConfirmAsync("风险警告", SettingsViewModel.RiskWarningText,
                "我已知晓并接受", "取消");
            if (!ok)
                return;
            Vm.AcceptRisk();
        }
        await Vm.StartAsync();
    }

    /// <summary>风险确认按钮（弹窗展示完整警告）。</summary>
    private async void AcceptRisk_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var ok = await Dialogs.ConfirmAsync("风险警告", SettingsViewModel.RiskWarningText,
            "我已知晓并接受", "取消");
        if (ok)
            Vm.AcceptRisk();
    }

    /// <summary>已确认过，再查看一次警告原文。</summary>
    private async void ReviewRisk_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await Dialogs.ShowAsync("风险警告", SettingsViewModel.RiskWarningText, "知道了");

    /// <summary>检测在线 QQ 并弹窗让用户选一个账号。</summary>
    private async void PickQqAccount_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var online = await Vm.DetectOnlineQqAsync();
        var pick = await Dialogs.PickQqAccountAsync(
            Vm.QqCandidates.ToList(), online?.Uin, online?.Nickname);
        if (pick is null)
        {
            Vm.NoteQqAccountCanceled();
            return;
        }
        Vm.ApplyQqAccount(pick);
    }

    private async void Stop_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await Vm.StopAsync();

    /// <summary>打开 SnowLuma 的 WebUI（首次设置、看日志都在那里）。</summary>
    private void OpenWebUi_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Vm.WebUiUrl)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Vm.AppendLogFromView($"打开 WebUI 失败：{ex.Message}");
            Toasts.Error("打开 WebUI 失败", ex.Message);
        }
    }

    private void AutoConnect_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Vm.AutoConnect();

    // ---- 上课课件弹窗：手动测试 ----

    /// <summary>
    /// 手动弹一次课件窗（真实触发走上课事件 + 功能开关）。
    /// 用当天已归档的课件；当天没有就显示空状态——点了必须有反应，否则会以为功能坏了。
    /// </summary>
    private void TestCourseware_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var files = Runtime.Courseware.QueryDay(today);
        var vm = CoursewareViewModel.FromFiles(files
            .Where(f => f.LocalPath is not null)
            .Select(f => (f.FileName, f.LocalPath!, f.Size)));
        new CoursewareWindow { DataContext = vm }.Show();
        Vm.AppendLogFromView($"已打开课件弹窗：{today:yyyy-MM-dd} 共 {files.Count} 个文件。");
    }

    // ---- 软件更新 ----

    private async void CheckUpdate_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await Vm.CheckForUpdatesAsync();

    private async void DownloadUpdate_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await Vm.DownloadUpdateAsync();

    /// <summary>打开 Releases 页（Linux 下就是"去下载新包"的正路）。</summary>
    private void OpenReleasePage_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var url = Vm.ReleasePageUrl;
        if (url is null)
            return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Vm.AppendLogFromView($"打开 Releases 页失败：{ex.Message}");
            Toasts.Error("打开浏览器失败", ex.Message);
        }
    }

    // ---- 调试 ----

    /// <summary>清空所有设置：先确认，再复位（设置页与 Runtime 共用对象，一起清）。</summary>
    private async void ResetSettings_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var ok = await Dialogs.ConfirmAsync("清空所有设置",
            "会把 AI / QQ / 归档 / 教师映射 / 功能开关 / 管理员密码全部恢复默认值，" +
            "并删除 settings.json。\n\n作业、待处理与事件时间线不会被清掉。\n\n确定继续吗？",
            "清空", "取消");
        if (!ok)
            return;
        Vm.ResetAllSettings();
    }

    // ---- 日志 ----

    private async void CopyLog_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
            {
                Vm.AppendLogFromView("当前环境没有剪贴板，无法复制。");
                return;
            }
            await clipboard.SetTextAsync(Vm.Log);
            Vm.AppendLogFromView($"已复制日志（{Vm.Log.Length} 字符）。");
        }
        catch (Exception ex)
        {
            Vm.AppendLogFromView($"复制日志失败：{ex.Message}");
        }
    }

    private void ClearLog_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Vm.ClearLog();
}
