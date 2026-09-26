using SmartClassroom.App;
using Xunit;

namespace SmartClassroom.App.Tests;

/// <summary>
/// 瀑布流布局计算。
/// 取代 WrapPanel 的两个理由（也是这组测试要守住的）：
///   1. 卡片宽度拉伸填满整行 → 右侧不留白；
///   2. 每列独立堆叠 → 短卡片不会被同行最高的卡片撑高。
/// </summary>
public sealed class MasonryLayoutTests
{
    private const double Min = 300, Gap = 10;

    [Theory]
    // 宽度, 期望列数（maxColumns=4）
    [InlineData(1200, 3)]      // floor(1210/310)=3
    [InlineData(1185, 3)]
    [InlineData(900, 2)]
    [InlineData(620, 2)]       // floor(630/310)=2
    [InlineData(600, 1)]       // floor(610/310)=1
    [InlineData(300, 1)]
    [InlineData(100, 1)]       // 再窄也至少 1 列
    public void ComputeColumns_AdaptsToWidth(double width, int expectedColumns)
    {
        var (columns, _) = MasonryLayout.ComputeColumns(width, Min, Gap, 4);
        Assert.Equal(expectedColumns, columns);
    }

    [Fact]
    public void ComputeColumns_ItemWidthFillsRowExactly()
    {
        foreach (var width in new[] { 600.0, 900.0, 1185.0, 1200.0, 2000.0 })
        {
            var (columns, itemWidth) = MasonryLayout.ComputeColumns(width, Min, Gap, 4);
            // 列宽 * 列数 + 间隙 = 可用宽度 → 右侧不留白
            Assert.Equal(width, itemWidth * columns + Gap * (columns - 1), 6);
        }
    }

    [Fact]
    public void ComputeColumns_RespectsMaxColumns()
    {
        // 很宽但限制 2 列
        var (columns, itemWidth) = MasonryLayout.ComputeColumns(3000, Min, Gap, 2);
        Assert.Equal(2, columns);
        Assert.Equal((3000 - Gap) / 2, itemWidth, 6);   // 仍然拉伸填满
    }

    [Fact]
    public void ComputeColumns_UnlimitedWhenMaxIsNonPositive()
    {
        var (columns, _) = MasonryLayout.ComputeColumns(2000, Min, Gap, 0);
        Assert.Equal(6, columns);                       // floor(2010/310)=6
    }

    [Theory]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NaN)]
    [InlineData(0)]
    [InlineData(-100)]
    public void ComputeColumns_DegradesToSingleColumn(double width)
    {
        var (columns, itemWidth) = MasonryLayout.ComputeColumns(width, Min, Gap, 4);
        Assert.Equal(1, columns);
        Assert.Equal(Min, itemWidth);                   // 单列时用最小宽度
    }

    [Fact]
    public void ComputeColumns_UsesEffectiveWidth_NotFixedWidth()
    {
        // 这正是之前的毛病：卡片固定 344 宽，1185 只放得下 3 列且右侧留白
        var (columns, itemWidth) = MasonryLayout.ComputeColumns(1185, Min, Gap, 4);
        Assert.Equal(3, columns);
        Assert.True(itemWidth > 344, "卡片应比固定宽度更宽，把空间用满");
        Assert.Equal(1185, itemWidth * 3 + Gap * 2, 6);
    }

    [Fact]
    public void AssignToColumns_BalancesByShortestColumn()
    {
        // 3 个条目、2 列：第 3 个应该去更矮的那列
        var result = MasonryLayout.AssignToColumns([100, 300, 50], 2);
        Assert.Equal([0, 1, 0], result);       // 列0=100, 列1=300 → 第3个进列0（100）
    }

    [Fact]
    public void AssignToColumns_TiesGoLeftmost()
    {
        var result = MasonryLayout.AssignToColumns([100, 100, 100], 2);
        Assert.Equal([0, 1, 0], result);       // 前两个分列两边，第三个回到靠左的列
    }

    [Fact]
    public void AssignToColumns_SingleColumnKeepsOrder()
    {
        var result = MasonryLayout.AssignToColumns([10, 20, 30], 1);
        Assert.Equal([0, 0, 0], result);
    }

    [Fact]
    public void AssignToColumns_EmptyHeights()
        => Assert.Empty(MasonryLayout.AssignToColumns([], 3));

    [Fact]
    public void AssignToColumns_VeryUnevenHeights_FillShortestFirst()
    {
        // 一个超高卡片不应把整行撑高：后续卡片应继续填其它列
        var result = MasonryLayout.AssignToColumns([1000, 50, 50, 50], 3);
        Assert.Equal(0, result[0]);
        Assert.Equal(1, result[1]);
        Assert.Equal(2, result[2]);
        Assert.Equal(1, result[3]);            // 列1 最矮（50）→ 第 4 个继续进列1
    }
}
