using Avalonia;

namespace SmartClassroom.App;

/// <summary>
/// 界面与字体缩放。
/// 用 LayoutTransformControl 做整体缩放（字体、图标、卡片一起放大），
/// 这样能保住主题本身的字号层级（标题/正文/说明的相对大小），
/// 比"逐个覆盖 FontSize"更不容易把界面搞乱；卡片网格也会随缩放重新排列。
/// </summary>
public static class UiScaling
{
    public const double Min = 0.8;
    public const double Max = 1.6;
    public const double Step = 0.1;
    public const double Default = 1.0;

    private static double _scale = Default;

    /// <summary>缩放变化时通知（已打开的窗口据此刷新）。</summary>
    public static event Action<double>? Changed;

    public static double Scale
    {
        get => _scale;
        set
        {
            var clamped = Clamp(value);
            if (Math.Abs(clamped - _scale) < 0.001)
                return;
            _scale = clamped;
            Changed?.Invoke(_scale);
        }
    }

    public static double Clamp(double value)
        => Math.Round(Math.Clamp(double.IsFinite(value) ? value : Default, Min, Max), 2);

    /// <summary>供 XAML 绑定的显示文本。</summary>
    public static string Describe(double scale) => $"{scale * 100:F0}%";
}
