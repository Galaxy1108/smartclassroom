using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace SmartClassroom.App.Views;

/// <summary>通知条严重级别。</summary>
public enum NoticeSeverity { Informational, Success, Warning, Error }

/// <summary>
/// 自绘通知条（替代 FluentAvalonia 的 InfoBar）。
/// InfoBar 在内容换行为多行时，边框高度按单行计算，会切掉底边；这里用 Grid+StackPanel 自己排布，
/// 高度完全由内容决定。
/// </summary>
public partial class NoticeBar : UserControl
{
    public static readonly StyledProperty<NoticeSeverity> SeverityProperty =
        AvaloniaProperty.Register<NoticeBar, NoticeSeverity>(nameof(Severity));

    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<NoticeBar, string>(nameof(Title), "");

    public static readonly StyledProperty<string> MessageProperty =
        AvaloniaProperty.Register<NoticeBar, string>(nameof(Message), "");

    public static readonly StyledProperty<object?> ActionContentProperty =
        AvaloniaProperty.Register<NoticeBar, object?>(nameof(ActionContent));

    public NoticeSeverity Severity
    {
        get => GetValue(SeverityProperty);
        set => SetValue(SeverityProperty, value);
    }

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Message
    {
        get => GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public object? ActionContent
    {
        get => GetValue(ActionContentProperty);
        set => SetValue(ActionContentProperty, value);
    }

    public NoticeBar()
    {
        InitializeComponent();
        ActualThemeVariantChanged += (_, _) => Apply();
        Apply();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SeverityProperty || change.Property == TitleProperty
            || change.Property == MessageProperty || change.Property == ActionContentProperty)
            Apply();
    }

    private void Apply()
    {
        var dark = ActualThemeVariant == ThemeVariant.Dark;
        // WinUI 的 InfoBar 配色：浅色用深字浅底，深色用亮字深底。
        var (accent, fill, stroke, icon) = (Severity, dark) switch
        {
            (NoticeSeverity.Success, false) => ("#0F7B0F", "#F1FAF1", "#BAD80A", "IconAlert"),
            (NoticeSeverity.Success, true) => ("#6CCB5F", "#1E3A1E", "#3E6B3E", "IconAlert"),
            (NoticeSeverity.Warning, false) => ("#9D5D00", "#FFF9F0", "#FCE100", "IconWarning"),
            (NoticeSeverity.Warning, true) => ("#FCE100", "#433519", "#6B5A2A", "IconWarning"),
            (NoticeSeverity.Error, false) => ("#B10E1C", "#FDF3F4", "#F1707B", "IconAlert"),
            (NoticeSeverity.Error, true) => ("#FF99A4", "#442726", "#6E3A3A", "IconAlert"),
            (_, false) => ("#0F6CBD", "#F3F9FD", "#B3D6F2", "IconInfo"),
            (_, true) => ("#60CDFF", "#1B2A3A", "#2F4A63", "IconInfo"),
        };
        Root.BorderBrush = Brush(stroke);
        Root.Background = Brush(fill);
        Icon.Foreground = Brush(accent);
        TitleText.Foreground = Brush(accent);
        if (Application.Current?.TryFindResource(icon, out var geo) == true)
            Icon.Data = geo as Geometry;

        TitleText.Text = Title;
        TitleText.IsVisible = !string.IsNullOrWhiteSpace(Title);
        MessageText.Text = Message;
        MessageText.IsVisible = !string.IsNullOrWhiteSpace(Message);
        ActionHost.Content = ActionContent;
        ActionHost.IsVisible = ActionContent is not null;
    }

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));
}
