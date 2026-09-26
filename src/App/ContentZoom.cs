using Avalonia;
using Avalonia.Input;

namespace SmartClassroom.App;

/// <summary>
/// 作业内容区的缩放（Ctrl+滚轮 / 设置页滑块）。
/// **只作用于作业卡片区域**，不影响导航栏、设置页等其余界面。
/// 用 LayoutTransformControl 整体缩放该区域：字号、图标、卡片一起等比放大，
/// 保住主题原本的字号层级；卡片网格也会随缩放重新排列。
/// </summary>
public static class ContentZoom
{
    public const double Min = 0.8;
    public const double Max = 1.8;
    public const double Step = 0.1;
    public const double Default = 1.0;

    private static double _scale = Default;

    /// <summary>缩放变化时通知（作业视图据此刷新）。</summary>
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

    /// <summary>界面显示用（如 130%）。</summary>
    public static string Describe(double scale) => $"{scale * 100:F0}%";

    /// <summary>
    /// 由当前缩放与滚轮方向算出下一档。
    /// 单独抽出来是为了能测：滚轮事件本身在无头环境不好造。
    /// </summary>
    public static double Next(double current, double deltaY)
    {
        if (Math.Abs(deltaY) < 0.001)
            return Clamp(current);
        return Clamp(current + (deltaY > 0 ? Step : -Step));
    }

    /// <summary>处理 Ctrl+滚轮。不按 Ctrl 时返回 false，交给正常滚动。</summary>
    public static bool HandleWheel(PointerWheelEventArgs e)
    {
        if ((e.KeyModifiers & KeyModifiers.Control) == 0)
            return false;
        var next = Next(Scale, e.Delta.Y);
        if (Math.Abs(next - Scale) > 0.001)
        {
            Scale = next;
            Runtime.PersistZoom(next);
        }
        e.Handled = true;   // 缩放时不要再滚动列表
        return true;
    }
}
