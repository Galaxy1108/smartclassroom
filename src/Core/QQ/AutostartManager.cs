using System.Runtime.InteropServices;

namespace SmartClassroom.Core.QQ;

/// <summary>
/// SnowLuma 开机自启：Windows 写 Startup 文件夹 bat，Linux 写 ~/.config/autostart desktop。
/// 纯文件操作，无需管理员权限；目录可覆盖以便单测。
/// </summary>
public static class AutostartManager
{
    public const string EntryName = "smartclassroom-snowluma";

    public static string EntryPath(
        string? windowsDir = null, string? linuxDir = null, OSPlatform? platform = null)
    {
        var os = platform ?? CurrentOs();
        if (os == OSPlatform.Windows)
            return Path.Combine(windowsDir ?? DefaultWindowsDir(), EntryName + ".bat");
        return Path.Combine(linuxDir ?? DefaultLinuxDir(), EntryName + ".desktop");
    }

    public static bool IsEnabled(string installDir,
        string? windowsDir = null, string? linuxDir = null, OSPlatform? platform = null)
        => File.Exists(EntryPath(windowsDir, linuxDir, platform));

    public static void SetEnabled(string installDir, string nodeExe, bool enabled,
        string? windowsDir = null, string? linuxDir = null, OSPlatform? platform = null)
    {
        var path = EntryPath(windowsDir, linuxDir, platform);
        if (!enabled)
        {
            if (File.Exists(path))
                File.Delete(path);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var os = platform ?? CurrentOs();
        File.WriteAllText(path, os == OSPlatform.Windows
            ? "@echo off\r\n" + $"cd /d \"{installDir}\"\r\n" + $"start \"\" \"{nodeExe}\" \"index.mjs\"\r\n"
            : "[Desktop Entry]\nType=Application\nName=SmartClassroom SnowLuma\n"
              + $"Exec={nodeExe} \"{Path.Combine(installDir, "index.mjs")}\"\n"
              + $"Path={installDir}\nX-GNOME-Autostart-enabled=true\n");
    }

    internal static OSPlatform CurrentOs()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return OSPlatform.Windows;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return OSPlatform.OSX;
        return OSPlatform.Linux;
    }

    private static string DefaultWindowsDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "Windows", "Start Menu", "Programs", "Startup");

    private static string DefaultLinuxDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "autostart");
}
