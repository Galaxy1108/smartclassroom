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

    private static double _cardScale = Default;

    /// <summary>
    /// 作业卡片的**独立**缩放（与整窗缩放分开，互不叠乘）。
    /// 用户要求："作业的单独缩放还是需要的" —— 只想让卡片大一点时不用把整个界面放大。
    /// </summary>
    public static double CardScale
    {
        get => _cardScale;
        set
        {
            var clamped = Clamp(value);
            if (Math.Abs(clamped - _cardScale) < 0.001)
                return;
            _cardScale = clamped;
            CardChanged?.Invoke(_cardScale);
        }
    }

    /// <summary>卡片缩放变化时通知（作业页据此刷新）。</summary>
    public static event Action<double>? CardChanged;

    /// <summary>Ctrl+滚轮调的是**卡片**缩放（作业页）。</summary>
    public static bool HandleCardWheel(Avalonia.Input.PointerWheelEventArgs e)
    {
        if ((e.KeyModifiers & Avalonia.Input.KeyModifiers.Control) == 0)
            return false;
        CardScale = Next(CardScale, e.Delta.Y);
        Runtime.PersistCardZoom(CardScale);
        e.Handled = true;
        return true;
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

/// <summary>
/// 界面主题：跟随系统 / 浅色 / 深色。
/// 与缩放一样集中在这里，设置页改完立刻应用到整个应用（含已打开的窗口）。
/// </summary>
public static class AppTheme
{
    public const string System = "default";
    public const string Light = "light";
    public const string Dark = "dark";

    public static event Action<string>? Changed;

    private static string _current = System;

    /// <summary>当前选择（default / light / dark）。</summary>
    public static string Current => _current;

    /// <summary>应用到整个应用。</summary>
    public static void Apply(string theme)
    {
        _current = Normalize(theme);
        var variant = _current switch
        {
            Light => Avalonia.Styling.ThemeVariant.Light,
            Dark => Avalonia.Styling.ThemeVariant.Dark,
            _ => Avalonia.Styling.ThemeVariant.Default
        };
        if (Avalonia.Application.Current is { } app)
            app.RequestedThemeVariant = variant;
        Changed?.Invoke(_current);
    }

    public static string Normalize(string? theme) => theme?.Trim().ToLowerInvariant() switch
    {
        Light => Light,
        Dark => Dark,
        _ => System
    };

    /// <summary>界面显示用（跟随系统 / 浅色 / 深色）。</summary>
    public static string Describe(string theme) => Normalize(theme) switch
    {
        Light => "浅色",
        Dark => "深色",
        _ => "跟随系统"
    };
}
