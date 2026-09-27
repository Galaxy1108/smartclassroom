using System.Runtime.InteropServices;

namespace SmartClassroom.App;

/// <summary>
/// 应用自身的开机自启。两个平台各用各的标准做法：
///   Linux  → ~/.config/autostart/smartclassroom.desktop（桌面环境通用）
///   Windows→ HKCU\Software\Microsoft\Windows\CurrentVersion\Run
/// 只写当前用户的范围，不需要管理员权限。
/// </summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "SmartClassroom";

    private static string DesktopFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "autostart", "smartclassroom.desktop");

    /// <summary>当前可执行文件路径（自启动要指向它）。</summary>
    private static string ExePath => Environment.ProcessPath ?? "";

    /// <summary>当前是否已开启（以系统里的实际状态为准，不只看设置）。</summary>
    public static bool IsEnabled()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(RunValue) is string s && s.Length > 0;
            }
            return File.Exists(DesktopFile);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>开启/关闭自启动。返回是否成功（失败只记日志，不打断设置保存）。</summary>
    public static bool Apply(bool enabled)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
                if (key is null)
                    return false;
                if (enabled && ExePath.Length > 0)
                    key.SetValue(RunValue, $"\"{ExePath}\" --tray");
                else
                    key.DeleteValue(RunValue, throwOnMissingValue: false);
                return true;
            }

            if (!enabled)
            {
                if (File.Exists(DesktopFile))
                    File.Delete(DesktopFile);
                return true;
            }
            if (ExePath.Length == 0)
                return false;
            Directory.CreateDirectory(Path.GetDirectoryName(DesktopFile)!);
            File.WriteAllText(DesktopFile, $"""
                [Desktop Entry]
                Type=Application
                Name=智慧课堂
                Comment=QQ 消息 → ClassIsland 提醒 / 作业 / 换课 / 课件
                Exec={ExePath} --tray
                Icon=smartclassroom
                Terminal=false
                X-GNOME-Autostart-enabled=true
                """);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
