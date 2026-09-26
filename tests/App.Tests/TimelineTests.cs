using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using SmartClassroom.App.ViewModels;
using SmartClassroom.App.Views;
using SmartClassroom.Core;
using Xunit;

namespace SmartClassroom.App.Tests;

/// <summary>
/// 决策时间线：按严重级别配色、可复制、以及"跨重启保留"带来的刷新约束。
/// </summary>
public sealed class TimelineTests
{
    private static ActivityEntry Entry(ActivitySeverity severity, string detail = "细节")
        => new(new DateTimeOffset(2026, 9, 26, 12, 51, 3, TimeSpan.FromHours(8)),
            "crash", "后台任务异常", detail, severity);

    private static EventsViewModel Vm(params ActivityEntry[] entries)
    {
        var feed = new ActivityFeed();
        foreach (var e in entries.Reverse())
            feed.Append(e.Kind, e.Title, e.Detail, e.Severity);
        return new EventsViewModel(feed, new PendingStore(), null);
    }

    [AvaloniaFact]
    public void Row_MapsSeverityToFlagsAndLabel()
    {
        var error = new ActivityRow(Entry(ActivitySeverity.Error));
        Assert.True(error.IsError);
        Assert.False(error.IsWarning);
        Assert.False(error.IsSuccess);
        Assert.False(error.IsInfo);
        Assert.Equal("错误", error.SeverityLabel);

        var warning = new ActivityRow(Entry(ActivitySeverity.Warning));
        Assert.True(warning.IsWarning);
        Assert.Equal("警告", warning.SeverityLabel);

        var success = new ActivityRow(Entry(ActivitySeverity.Success));
        Assert.True(success.IsSuccess);
        Assert.Equal("成功", success.SeverityLabel);

        var info = new ActivityRow(Entry(ActivitySeverity.Info));
        Assert.True(info.IsInfo);
        Assert.Equal("信息", info.SeverityLabel);
    }

    [AvaloniaFact]
    public void CopyText_CarriesLevelTimeAndIndentedDetail()
    {
        var text = new ActivityRow(Entry(ActivitySeverity.Error,
            "ServiceUnknown: The name is not activatable")).CopyText;

        Assert.StartsWith("[09-26 12:51:03] [错误] crash · 后台任务异常", text);
        Assert.Contains("\n    ServiceUnknown", text);
    }

    [AvaloniaFact]
    public void CopyText_WithoutDetail_IsSingleLine()
        => Assert.DoesNotContain("\n", new ActivityRow(Entry(ActivitySeverity.Info, "")).CopyText);

    [AvaloniaFact]
    public void CopyAll_IncludesHeaderCountAndEveryRow()
    {
        var vm = Vm(Entry(ActivitySeverity.Warning), Entry(ActivitySeverity.Error));

        var text = vm.CopyAllText();
        Assert.Contains("决策时间线", text);
        Assert.Contains("2 条", text);
        Assert.Equal(2, text.Split("后台任务异常").Length - 1);
        Assert.Contains("[错误]", text);
        Assert.Contains("[警告]", text);
    }

    [AvaloniaFact]
    public void ClearTimeline_EmptiesBothViewModelAndFeed()
    {
        var feed = new ActivityFeed();
        feed.Append("a", "第一条", "d");
        feed.Append("b", "第二条", "d");
        var vm = new EventsViewModel(feed, new PendingStore(), null);

        Assert.Equal(2, vm.ClearTimeline());

        Assert.Empty(vm.Entries);
        Assert.Empty(feed.Entries);          // 存储也清掉，否则落盘后又回来了
        Assert.True(vm.IsEmptyTimeline);
        Assert.False(vm.HasEntries);
    }

    /// <summary>
    /// 数据没变时 Refresh() 不能重建行对象。
    /// 主窗口每 2 秒刷新一次，而重建会把用户正拖选的一段文字清掉——
    /// 「可以复制」的前提是选中状态不会被定时器打断。
    /// </summary>
    [AvaloniaFact]
    public void Refresh_WithUnchangedFeed_KeepsRowInstances()
    {
        var feed = new ActivityFeed();
        feed.Append("a", "t", "d");
        var vm = new EventsViewModel(feed, new PendingStore(), null);
        var first = vm.Entries[0];

        vm.Refresh();
        vm.Refresh();
        Assert.Same(first, vm.Entries[0]);

        feed.Append("b", "t2", "d2");
        vm.Refresh();
        Assert.Equal(2, vm.Entries.Count);
    }

    /// <summary>错误级别的行必须真的拿到"错误"配色（验证样式类绑定 + 主题字典都没写错）。</summary>
    [AvaloniaFact]
    public void ErrorRow_UsesErrorBrushesFromTheme()
    {
        var vm = Vm(Entry(ActivitySeverity.Error), Entry(ActivitySeverity.Info));
        var view = new EventsView { DataContext = vm };

        var window = new Window { Width = 900, Height = 700, Content = view };
        try
        {
            window.Show();

            var rows = view.GetVisualDescendants().OfType<Border>()
                .Where(b => b.Classes.Contains("tlrow")).ToList();
            Assert.Equal(2, rows.Count);

            var errorRow = rows[0];
            Assert.Contains("error", errorRow.Classes);
            Assert.DoesNotContain("error", rows[1].Classes);

            // 浅色 #FDF3F4 / 深色 #442726：错误就是红的
            var light = view.ActualThemeVariant != ThemeVariant.Dark;
            var fill = Color.Parse(light ? "#FDF3F4" : "#442726");
            var stroke = Color.Parse(light ? "#F1707B" : "#6E3A3A");
            var accent = Color.Parse(light ? "#B10E1C" : "#FF99A4");

            Assert.Equal(fill, ColorOf(errorRow.Background));
            Assert.Equal(stroke, ColorOf(errorRow.BorderBrush));

            // 左侧色条与级别文字用的是同一个强调色
            var strip = errorRow.GetVisualDescendants().OfType<Border>()
                .First(b => b.Classes.Contains("tlstrip"));
            Assert.Equal(accent, ColorOf(strip.Background));

            // 信息级别的行是另一套色（蓝色），说明配色确实按级别分开了
            Assert.Equal(Color.Parse(light ? "#F3F9FD" : "#1B2A3A"), ColorOf(rows[1].Background));
        }
        finally
        {
            window.Close();
        }
    }

    private static Color ColorOf(IBrush? brush)
        => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;
}
