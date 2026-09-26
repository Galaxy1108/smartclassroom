using Avalonia;
using Avalonia.Media;

namespace SmartClassroom.App.Tests;

/// <summary>
/// headless 渲染辅助。
/// FluentAvalonia 内嵌的 Symbols 图标字体在 headless Skia 下建不出 glyphTypeface，
/// 需要在测试运行时（应用已启动后）把 SymbolThemeFontFamily 换成系统字体；
/// 在 App.Initialize / OnFrameworkInitializationCompleted 里改会被主题资源字典盖掉。
/// 生产环境不受影响。
/// </summary>
public static class TestSetup
{
    private static bool _done;

    public static void OverrideIconFont()
    {
        if (_done)
            return;
        if (Application.Current is { } app)
        {
            app.Resources["SymbolThemeFontFamily"] = new FontFamily("DejaVu Sans");
            _done = true;
        }
    }
}
