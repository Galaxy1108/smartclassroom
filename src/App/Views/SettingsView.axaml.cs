using Avalonia.Interactivity;
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
    }

    private SettingsViewModel Vm => (SettingsViewModel)DataContext!;

    /// <summary>
    /// 接线 SnowLuma 的协议同意弹窗。
    /// **必须放在这里**：外部是 `new SettingsView { DataContext = vm }`，
    /// 构造期间 DataContext 还是 null（会被兜底成另一个临时视图模型），
    /// 那时接线等于接到了一个马上被丢掉的实例上——真实例永远拿不到弹窗，
    /// 表现就是"点启动没看到弹窗，只显示未同意协议"。
    /// </summary>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is SettingsViewModel vm)
        {
            vm.RefreshSubjectColors();
            vm.ConsentPrompt = async docs => await Dialogs.ConsentAsync(docs);
            // 缺群号时由视图弹窗选群（在线了却开不了开关最让人困惑）
            vm.GroupPicker = async () =>
            {
                var groups = await vm.LoadGroupsAsync();
                if (groups.Count == 0)
                {
                    Toasts.Warn("没读到群列表", "确认 OneBot 已在线（可先点「检测并选择账号」）。");
                    return false;
                }
                var picked = await Dialogs.PickGroupsAsync(groups, vm.ParseGroupIds());
                if (picked is null)
                    return false;
                vm.ApplyGroups(picked);
                return true;
            };
        }
    }

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
            reason => PasswordDialog.PromptAsync("解锁设置", reason), "解锁设置需要管理员密码",
            onWrongPassword: attempt => Toasts.Error("密码不正确",
                attempt >= 3 ? "已连续输错 3 次，请稍后再试。" : "请重新输入管理员密码。"));
        Vm.RefreshLockState();
        if (!ok)
            Vm.RefreshLockState();
    }

    /// <summary>设置/清除管理员密码本身也受保护（已设置密码时需先验证）。</summary>
    private async void ApplyPassword_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // 改密码**永远**要重新输一次当前密码，即使还在免输冷却期内（用户要求）：
        // 这是唯一一处不能靠"刚才解锁过"蒙过去的操作。
        var ok = await Runtime.Auth.RequireAsync(
            reason => PasswordDialog.PromptAsync("修改管理员密码", reason),
            "修改管理员密码需要先验证当前密码", forcePrompt: true);
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

    /// <summary>把应用自带的 ClassIsland 插件装进 ClassIsland。</summary>
    private void InstallPlugin_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Vm.InstallClassIslandPlugin();

    /// <summary>给每个账号自动分配互不冲突的 OneBot 端口并重启 SnowLuma。</summary>
    private async void AutoAssignPorts_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await Vm.AutoAssignPortsAsync();

    /// <summary>从 OneBot 拉群列表并让用户勾选要监听的群。</summary>
    private async void PickGroups_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var groups = await Vm.LoadGroupsAsync();
        if (groups.Count == 0)
        {
            Toasts.Warn("没读到群列表", "确认 OneBot 已在线（可先点「检测并选择账号」）。");
            return;
        }
        var picked = await Dialogs.PickGroupsAsync(groups, Vm.ParseGroupIds());
        if (picked is null)
            return;   // 取消
        Vm.ApplyGroups(picked);
    }

    /// <summary>复制 WebUI 初始密码（它默认只打到 stdout，用户看不到）。</summary>
    private async void CopyWebUiPassword_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
                return;
            await clipboard.SetTextAsync(Vm.WebUiPassword);
            Toasts.Success("已复制 WebUI 初始密码");
        }
        catch (Exception ex)
        {
            Toasts.Error("复制失败", ex.Message);
        }
    }

    /// <summary>让用户自己设定 SnowLuma WebUI 的初始密码（留空 = 改回自动生成）。</summary>
    private async void SetWebUiPassword_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var value = await Dialogs.PromptAsync("SnowLuma WebUI 密码",
            "设置 WebUI（http://127.0.0.1:5099，用户名 admin）的初始密码。留空则改回由应用自动生成。下次启动 SnowLuma 时生效。",
            watermark: "留空 = 自动生成", password: true);
        if (value is null)
            return;   // 取消
        Vm.SetWebUiPassword(value);
    }

    /// <summary>打开 SnowLuma 的日志目录（注入失败的原因只写在那里）。</summary>
    private void OpenQqLog_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => OpenFolder(System.IO.Path.Combine(Vm.InstallDir, "logs"));

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

    private void EditTeacher_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is TeacherRow row)
            Vm.BeginEditTeacher(row);
    }

    private void CancelEditTeacher_Click(object? sender, RoutedEventArgs e) => Vm.CancelEditTeacher();

    /// <summary>把某个科目的颜色恢复成"默认"（按科目名自动取色）。</summary>
    private void ResetSubjectColor_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is SubjectColorRow row)
            row.Color = "默认";
    }

    /// <summary>重启以更新（免密码：只是重启）。</summary>
    private void RestartUpdate_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Vm.RestartToUpdate();

    /// <summary>Linux 应用内更新：下载安装包并要一次系统密码，交给 pacman 安装。</summary>
    private async void InstallLinuxUpdate_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await Vm.InstallLinuxUpdateAsync(reason =>
            PasswordDialog.PromptAsync("安装更新", reason));
}
