using Avalonia;
using Avalonia.Media;
using FluentAvalonia.Styling;

namespace SmartClassroom.App.Tests;

/// <summary>
/// 无头测试需要挂上 FluentAvalonia 主题（生产环境由 App.axaml 加载），
/// 否则 NavigationView 等控件找不到模板部件。
/// FAUI 内嵌 Symbols 图标字体在无头 Skia 下加载失败，这里用系统字体替换
/// （图标在测试里显示为占位符，不影响布局/冒烟；生产环境不受影响）。
/// </summary>
public static class TestSetup
{
    private static bool _ready;

    public static void EnsureApp()
    {
        if (_ready)
            return;
        var app = Application.Current;
        if (app is not null)
        {
            if (!app.Styles.OfType<FluentAvaloniaTheme>().Any())
                app.Styles.Add(new FluentAvaloniaTheme());
            app.Resources["SymbolThemeFontFamily"] = new FontFamily("DejaVu Sans");
        }
        _ready = true;
    }
}
