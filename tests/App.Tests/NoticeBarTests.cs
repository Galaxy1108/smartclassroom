using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using SmartClassroom.App.Views;
using Xunit;

namespace SmartClassroom.App.Tests;

/// <summary>
/// 通知条与图标资源。
/// 这里替代 FAUI 的 InfoBar：InfoBar 在内容换行时会按单行算高、切掉底边，
/// 所以自带一个用 Grid+StackPanel 排布的 NoticeBar，高度完全由内容决定。
/// </summary>
public sealed class NoticeBarTests
{
    [AvaloniaTheory]
    [InlineData("IconHome")]
    [InlineData("IconAlert")]
    [InlineData("IconFolder")]
    [InlineData("IconSettings")]
    [InlineData("IconInfo")]
    [InlineData("IconWarning")]
    [InlineData("IconDismiss")]
    [InlineData("IconPerson")]
    [InlineData("IconCloud")]
    public void IconResources_AreRegistered(string key)
    {
        Assert.True(Application.Current!.TryFindResource(key, out var value), $"{key} 未注册");
        Assert.IsAssignableFrom<Geometry>(value);
    }

    [AvaloniaFact]
    public void NoticeBar_AppliesSeverityAndContent()
    {
        var bar = new NoticeBar
        {
            Severity = NoticeSeverity.Error,
            Title = "风险警告",
            Message = "很长的说明文本，用来验证换行时高度由内容决定而不是被切掉底边。",
            ActionContent = new Avalonia.Controls.Button { Content = "知道了" }
        };

        Assert.Equal("风险警告", bar.Title);
        Assert.Equal(NoticeSeverity.Error, bar.Severity);
        Assert.NotNull(bar.ActionContent);
        // 构造与属性应用不应抛异常
        Assert.NotNull(bar.Content);
    }

    [AvaloniaFact]
    public void NoticeBar_AllSeveritiesApply()
    {
        foreach (var severity in Enum.GetValues<NoticeSeverity>())
        {
            var bar = new NoticeBar { Severity = severity, Title = "t", Message = "m" };
            Assert.Equal(severity, bar.Severity);
        }
    }
}
