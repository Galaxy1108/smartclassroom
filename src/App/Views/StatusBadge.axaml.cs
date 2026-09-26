using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace SmartClassroom.App.Views;

/// <summary>
/// 状态徽标：一个图标 + 一句文案，颜色与图标都由严重级别决定。
///
/// 存在的理由：以前状态只是一行纯文本（例如「已注入 / 服务未就绪」），
/// 用户看不出到底成功没有。现在成功=对钩、警告=感叹号、失败=叉，
/// 颜色取 App.axaml 主题字典里的 Severity*AccentBrush（浅色/深色各一套）。
/// </summary>
public partial class StatusBadge : UserControl
{
    public static readonly StyledProperty<NoticeSeverity> SeverityProperty =
        AvaloniaProperty.Register<StatusBadge, NoticeSeverity>(nameof(Severity));

    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<StatusBadge, string>(nameof(Text), "");

    public NoticeSeverity Severity
    {
        get => GetValue(SeverityProperty);
        set => SetValue(SeverityProperty, value);
    }

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public StatusBadge()
    {
        InitializeComponent();
        ActualThemeVariantChanged += (_, _) => Apply();
        Apply();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SeverityProperty || change.Property == TextProperty)
            Apply();
    }

    private void Apply()
    {
        var key = Severity switch
        {
            NoticeSeverity.Success => "SeveritySuccessAccentBrush",
            NoticeSeverity.Warning => "SeverityWarningAccentBrush",
            NoticeSeverity.Error => "SeverityErrorAccentBrush",
            _ => "SeverityInfoAccentBrush"
        };
        var brush = Application.Current?.TryFindResource(key, out var value) == true
            ? value as IBrush
            : null;

        CheckIcon.IsVisible = Severity == NoticeSeverity.Success;
        WarnIcon.IsVisible = Severity == NoticeSeverity.Warning;
        ErrorIcon.IsVisible = Severity == NoticeSeverity.Error;
        InfoIcon.IsVisible = Severity == NoticeSeverity.Informational;

        if (brush is not null)
        {
            CheckIcon.Stroke = brush;
            WarnIcon.Foreground = brush;
            ErrorIcon.Foreground = brush;
            InfoIcon.Foreground = brush;
            Label.Foreground = brush;
        }

        Label.Text = Text;
    }
}
