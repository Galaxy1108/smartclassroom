using Avalonia;
using Avalonia.Media;
using System;

namespace SmartClassroom.App;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            // 中文优先的字体回退链：Windows 用微软雅黑，Linux 用 Noto/思源/鸿蒙。
            // 不设这个的话，中文会落到丑陋的默认回退字体上。
            .With(new FontManagerOptions
            {
                DefaultFamilyName =
                    "Microsoft YaHei UI, Noto Sans CJK SC, Source Han Sans SC, HarmonyOS Sans SC, sans-serif"
            })
            .LogToTrace();
}
