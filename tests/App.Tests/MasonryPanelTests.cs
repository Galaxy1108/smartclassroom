using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using SmartClassroom.App;
using Xunit;

namespace SmartClassroom.App.Tests;

/// <summary>
/// 面板级验证：真的跑一遍 Measure/Arrange，检查实际排布几何。
/// （只测数学函数不够——面板本身把宽度传错、或者 arrange 用错列，同样出问题。）
/// </summary>
public sealed class MasonryPanelTests
{
    /// <summary>
    /// 几何断言容差。
    /// Avalonia 会把子元素的 Bounds **取整到整像素**（itemWidth 388.33 → 实际 389、x 796.67 → 797），
    /// 所以边缘会有约 1px 的舍入差。精确到小数的断言放在 MasonryLayout 的纯数学测试里。
    /// </summary>
    private const double Pixel = 1.5;
    private static MasonryPanel Build(double[] heights, double width,
        double minItemWidth = 300, double gap = 10, int maxColumns = 4)
    {
        var panel = new MasonryPanel { MinItemWidth = minItemWidth, Gap = gap, MaxColumns = maxColumns };
        foreach (var h in heights)
            panel.Children.Add(new Border { Height = h });
        panel.Measure(new Size(width, double.PositiveInfinity));
        panel.Arrange(new Rect(0, 0, width, panel.DesiredSize.Height));
        return panel;
    }

    [AvaloniaFact]
    public void CardsFillAvailableWidth_NoRightGap()
    {
        var panel = Build([100, 100, 100, 100], 1185);

        // 3 列，最后一列的右边缘应当正好贴住可用宽度
        var rightmost = panel.Children.Max(c => c.Bounds.Right);
        // 取整后允许 ~1px 误差，关键是"没有成片的右侧留白"
        Assert.True(Math.Abs(rightmost - 1185) <= Pixel, $"右边缘 {rightmost}，期望 ≈1185");
    }

    [AvaloniaFact]
    public void CardsAreWiderThanFixedWidth_BecauseTheyStretch()
    {
        var panel = Build([100, 100, 100], 1185);
        var width = panel.Children[0].Bounds.Width;
        Assert.True(width > 344, $"卡片应拉伸填满（实际 {width:F1}）");
        Assert.True(Math.Abs(width - (1185 - 20) / 3.0) <= Pixel, $"列宽 {width}，期望 ≈388.3");
    }

    [AvaloniaFact]
    public void ShortCardIsNotStretchedByTallNeighbour()
    {
        // 第一列超高、第二列很矮：矮卡片不应被撑高
        var panel = Build([1000, 50], 620);   // floor(630/310)=2 列
        Assert.Equal(2, panel.Children.Count);
        Assert.True(Math.Abs(panel.Children[0].Bounds.Height - 1000) <= Pixel);
        Assert.True(Math.Abs(panel.Children[1].Bounds.Height - 50) <= Pixel);
        Assert.True(Math.Abs(panel.Children[1].Bounds.Top) <= Pixel);   // 矮卡片仍从顶部开始
    }

    [AvaloniaFact]
    public void TallCardDoesNotPushOthersDown_TheyContinueInShortColumn()
    {
        // WrapPanel 的行为是整行下移；瀑布流应当让后续卡片继续填矮列
        var panel = Build([1000, 100, 100, 100], 940);   // 3 列
        var tops = panel.Children.Select(c => c.Bounds.Top).ToArray();
        Assert.True(tops[0] <= Pixel && tops[1] <= Pixel && tops[2] <= Pixel);
        // 第 4 张进列 1（在 100 高的卡片下面），而不是被 1000 高的卡片推到 1010
        Assert.True(Math.Abs(panel.Children[3].Bounds.Top - 110) <= Pixel);
        Assert.True(panel.Children[3].Bounds.Top < 1000, "不应被超高卡片推到后面");
    }

    [AvaloniaFact]
    public void NarrowWidth_SingleColumnStacksInOrder()
    {
        var panel = Build([100, 50, 70], 250);   // 放不下两列
        Assert.True(Math.Abs(panel.Children[0].Bounds.Top) <= Pixel);
        Assert.True(Math.Abs(panel.Children[1].Bounds.Top - 110) <= Pixel);
        Assert.True(Math.Abs(panel.Children[2].Bounds.Top - 170) <= Pixel);
        Assert.True(Math.Abs(panel.Children[0].Bounds.Width - 250) <= Pixel);
    }

    [AvaloniaFact]
    public void DesiredHeight_IsTallestColumn()
    {
        var panel = Build([100, 100, 100, 100], 620);   // 2 列 → 每列 2 张
        // 列高 = 100 + 10 + 100 = 210（末尾不再加 gap）
        Assert.True(Math.Abs(panel.DesiredSize.Height - 210) <= Pixel);
    }

    [AvaloniaFact]
    public void EmptyPanel_MeasuresToZero()
    {
        var panel = new MasonryPanel();
        panel.Measure(new Size(1000, double.PositiveInfinity));
        Assert.True(panel.DesiredSize.Width <= Pixel);
        Assert.True(panel.DesiredSize.Height <= Pixel);
    }

    /// <summary>按实际排布反推列数（X 坐标有几个不同取值就是几列）。</summary>
    private static int ColumnCount(MasonryPanel panel)
        => panel.Children.Select(c => Math.Round(c.Bounds.X)).Distinct().Count();

    [AvaloniaFact]
    public void ColumnsAdaptWhenWidthChanges()
    {
        var panel = Build([100, 100, 100, 100], 1185);
        Assert.Equal(3, ColumnCount(panel));                 // 宽 → 3 列
        var wide = panel.Children[0].Bounds.Width;

        // 窗口变窄 → 列数减少；每列会变窄（宽度也跟着变小），但依然填满
        panel.Measure(new Size(620, double.PositiveInfinity));
        panel.Arrange(new Rect(0, 0, 620, panel.DesiredSize.Height));

        Assert.Equal(2, ColumnCount(panel));                 // 窄 → 2 列
        var narrow = panel.Children[0].Bounds.Width;
        Assert.True(narrow < wide, $"列数变少但总宽也小了，每列应变窄（{wide:F1} → {narrow:F1}）");

        var right = panel.Children.Max(c => c.Bounds.Right);
        Assert.True(Math.Abs(right - 620) <= Pixel, $"右边缘 {right}，期望 ≈620");
    }
}
