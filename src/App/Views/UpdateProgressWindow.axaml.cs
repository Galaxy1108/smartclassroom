using Avalonia.Controls;
using Avalonia.Threading;

namespace SmartClassroom.App.Views;

/// <summary>
/// "正在更新"窗口：启动时如果检测到上次留下了待完成的更新，
/// 先弹这个窗口，在后台把新版本文件覆盖进应用目录（带进度），完成后再进主界面。
///
/// 用户要求的就是这个：不要让人手动跑脚本，重启即更新，而且要有进度。
/// </summary>
public partial class UpdateProgressWindow : Window
{
    public UpdateProgressWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 执行待完成的更新；返回 (覆盖数, 跳过数)。进度与文案实时更新。
    /// </summary>
    public async Task<(int Copied, int Skipped)> RunAsync(string appDir, string version)
    {
        StatusText.Text = $"正在覆盖文件（{version}）…";
        var progress = new Progress<double>(p =>
        {
            Bar.Value = p;
            PercentText.Text = $"{p:F0}%";
        });
        var result = await Task.Run(() => UpdateInstaller.ApplyPending(appDir, progress));

        StatusText.Text = result.Skipped > 0
            ? $"已覆盖 {result.Copied} 个文件，{result.Skipped} 个正在使用中（重启后完成）"
            : $"已覆盖 {result.Copied} 个文件，正在启动…";
        Bar.Value = 100;
        PercentText.Text = "100%";
        await Task.Delay(600);
        return result;
    }

    /// <summary>给调用方一个"至少显示 0.6 秒"的收尾，避免闪一下就没了。</summary>
    public static void CloseSoon(Window window) => Dispatcher.UIThread.Post(window.Close);
}
