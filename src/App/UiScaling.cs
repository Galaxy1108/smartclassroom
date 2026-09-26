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

/// <summary>Ctrl+滚轮缩放的支持（View 挂载用，放在这里方便复用与测试）。</summary>
public static class UiScaleGesture
{
    /// <summary>
    /// 由当前缩放与滚轮方向算出下一档。
    /// 单独抽出来是为了能测：滚轮事件本身在无头环境不好造。
    /// </summary>
    public static double Next(double current, double deltaY)
    {
        if (Math.Abs(deltaY) < 0.001)
            return UiScaling.Clamp(current);
        var step = deltaY > 0 ? UiScaling.Step : -UiScaling.Step;
        return UiScaling.Clamp(current + step);
    }

    /// <summary>给元素挂上 Ctrl+滚轮缩放；不按 Ctrl 时返回 false，交给正常滚动。</summary>
    public static bool Handle(Avalonia.Input.PointerWheelEventArgs e)
    {
        if ((e.KeyModifiers & Avalonia.Input.KeyModifiers.Control) == 0)
            return false;
        var next = Next(UiScaling.Scale, e.Delta.Y);
        if (Math.Abs(next - UiScaling.Scale) > 0.001)
        {
            UiScaling.Scale = next;
            Runtime.PersistUiScale(next);
        }
        e.Handled = true;   // 缩放时不要再滚动列表
        return true;
    }
}
