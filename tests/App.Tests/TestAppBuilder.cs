using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using SmartClassroom.App.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace SmartClassroom.App.Tests;

/// <summary>
/// headless 测试用的应用装配。
/// 用官方 AvaloniaTestApplication 统一初始化会话（不要手动去改 Application.Current，
/// 否则会与 headless 会话生命周期冲突，表现为 fonts:SystemFonts 缺失）。
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<TestApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

/// <summary>
/// 生产 App 的子类：只替换图标字体。
/// FluentAvalonia 内嵌的 Symbols 图标字体在 headless Skia 下无法建 glyphTypeface，
/// 用系统字体顶上（图标显示为占位符，不影响布局与冒烟；生产环境不受影响）。
/// 必须在框架初始化完成后再覆盖，否则会被 FluentAvaloniaTheme 的资源字典盖掉。
/// </summary>
public sealed class TestApp : SmartClassroom.App.App
{
    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        Resources["SymbolThemeFontFamily"] = new FontFamily("DejaVu Sans");
    }
}
