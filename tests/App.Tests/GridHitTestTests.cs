using Avalonia;
using SmartClassroom.App;
using Xunit;

namespace SmartClassroom.App.Tests;

/// <summary>
/// 自适应网格的落位判定。
/// 重点覆盖**多行**：卡片按 WrapPanel 排成多行时，只比 Y 坐标会把不同行的卡片算成同一张，
/// 必须用矩形命中。
/// </summary>
public sealed class GridHitTestTests
{
    private const double W = 344, H = 132, GX = 10, GY = 10;

    /// <summary>按列数生成一个网格（每格 344x132，间距 10）。</summary>
    private static List<Rect> Grid(int count, int columns)
    {
        var cells = new List<Rect>();
        for (var i = 0; i < count; i++)
        {
            var row = i / columns;
            var col = i % columns;
            cells.Add(new Rect(col * (W + GX), row * (H + GY), W, H));
        }
        return cells;
    }

    [Fact]
    public void EmptyGrid_ReturnsMinusOne()
        => Assert.Equal(-1, GridHitTest.IndexAt([], new Point(10, 10)));

    [Fact]
    public void SingleRow_HitsCorrectCell()
    {
        var cells = Grid(3, 3);
        Assert.Equal(0, GridHitTest.IndexAt(cells, new Point(100, 60)));
        Assert.Equal(1, GridHitTest.IndexAt(cells, new Point(400, 60)));
        Assert.Equal(2, GridHitTest.IndexAt(cells, new Point(760, 60)));
    }

    [Fact]
    public void MultiRow_SecondRowIsNotConfusedWithFirst()
    {
        // 2 列 4 张 → 两行
        var cells = Grid(4, 2);
        // 第 1 行
        Assert.Equal(0, GridHitTest.IndexAt(cells, new Point(100, 60)));
        Assert.Equal(1, GridHitTest.IndexAt(cells, new Point(400, 60)));
        // 第 2 行：X 相同但 Y 不同 —— 只比 Y 的实现会在这里出错
        Assert.Equal(2, GridHitTest.IndexAt(cells, new Point(100, 200)));
        Assert.Equal(3, GridHitTest.IndexAt(cells, new Point(400, 200)));
    }

    [Fact]
    public void MultiRow_ThreeRowsAndUnevenLast()
    {
        // 3 列 7 张 → 3 行，最后一行只有 1 张
        var cells = Grid(7, 3);
        Assert.Equal(4, GridHitTest.IndexAt(cells, new Point(400, 200)));
        Assert.Equal(6, GridHitTest.IndexAt(cells, new Point(100, 350)));
    }

    [Fact]
    public void GapBetweenCards_FallsBackToNearestCenter()
    {
        var cells = Grid(4, 2);   // 0/1 第一行，2/3 第二行
        // 列间空隙（x 344~354）：左右两格中心恰好等距 → 取靠前的格子（实现里严格更近才更新）
        Assert.Equal(0, GridHitTest.IndexAt(cells, new Point(340, 60)));
        Assert.Equal(0, GridHitTest.IndexAt(cells, new Point(349, 60)));
        Assert.Equal(1, GridHitTest.IndexAt(cells, new Point(360, 60)));
        // 行间空隙（y 132~142）：偏上 → 上一行；偏下 → 下一行
        Assert.Equal(0, GridHitTest.IndexAt(cells, new Point(100, 135)));
        Assert.Equal(2, GridHitTest.IndexAt(cells, new Point(100, 140)));
        // 任何空隙都不能返回 -1（否则指示框会闪一下消失）
        for (var y = 128.0; y <= 146.0; y += 2)
            Assert.InRange(GridHitTest.IndexAt(cells, new Point(100, y)), 0, 3);
    }

    [Fact]
    public void OutsideGrid_FallsBackToNearest()
    {
        var cells = Grid(4, 2);
        Assert.Equal(0, GridHitTest.IndexAt(cells, new Point(-50, -50)));      // 左上角外
        Assert.Equal(3, GridHitTest.IndexAt(cells, new Point(900, 500)));      // 右下角外
    }

    [Fact]
    public void BoundaryOfCell_CountsAsInside()
    {
        var cells = Grid(1, 1);
        Assert.Equal(0, GridHitTest.IndexAt(cells, new Point(0, 0)));
        Assert.Equal(0, GridHitTest.IndexAt(cells, new Point(W, H)));
    }

    [Fact]
    public void VariableHeightCells_StillHitByRectangle()
    {
        // 同一行里高度不同的卡片（内容多的会长高）
        var cells = new List<Rect>
        {
            new(0, 0, 344, 132),      // 高 132
            new(354, 0, 344, 200),    // 同一行但更高
            new(0, 210, 344, 132),    // 第二行
        };
        Assert.Equal(1, GridHitTest.IndexAt(cells, new Point(400, 180)));   // 落在高卡片的延伸区
        Assert.Equal(2, GridHitTest.IndexAt(cells, new Point(100, 260)));
    }
}
