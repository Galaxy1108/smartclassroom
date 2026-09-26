using Avalonia;

namespace SmartClassroom.App;

/// <summary>
/// 自适应网格的命中判定（拖拽落位用）。
/// 抽成纯函数是为了可测——**多行布局下"只看 Y 坐标"会把不同行的卡片混在一起**，
/// 这是必须用矩形判断的地方。
/// </summary>
public static class GridHitTest
{
    /// <summary>
    /// 严格命中：只有指针真的落在某个格子矩形内才返回该下标，否则 -1。
    /// 用于"按下"判定——这里**不能**用"最近的格子"兜底，
    /// 否则点击卡片之间的空白也会误判成拖拽某张卡片。
    /// </summary>
    public static int IndexAtStrict(IReadOnlyList<Rect> cells, Point p)
    {
        for (var i = 0; i < cells.Count; i++)
            if (cells[i].Contains(p))
                return i;
        return -1;
    }

    /// <summary>
    /// 返回指针所在的格子下标；都不命中时返回中心点最近的格子；空集合返回 -1。
    /// 用于拖拽过程中的落位指示——允许"最近"兜底，指示框才不会在间隙里闪掉。
    /// </summary>
    public static int IndexAt(IReadOnlyList<Rect> cells, Point p)
    {
        if (cells.Count == 0)
            return -1;

        for (var i = 0; i < cells.Count; i++)
            if (cells[i].Contains(p))
                return i;

        var nearest = -1;
        var best = double.MaxValue;
        for (var i = 0; i < cells.Count; i++)
        {
            var c = cells[i].Center;
            var dx = c.X - p.X;
            var dy = c.Y - p.Y;
            var d = dx * dx + dy * dy;
            if (d < best)
            {
                best = d;
                nearest = i;
            }
        }
        return nearest;
    }
}
