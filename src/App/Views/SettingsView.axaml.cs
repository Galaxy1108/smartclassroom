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

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Vm.RefreshLockState();
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
        }
    }

    // ---- 教师映射 ----
    private async void Save_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var ok = await Runtime.Auth.RequireAsync(
            reason => PasswordDialog.PromptAsync("保存设置", reason),
            "保存设置需要管理员密码");
        if (ok)
            Vm.SaveSettings();
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
        => await Vm.StartAsync();

    private void Stop_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Vm.Stop();

    private async void Probe_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await Vm.ProbeAsync();

    private void AutoConnect_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Vm.AutoConnect();
}
