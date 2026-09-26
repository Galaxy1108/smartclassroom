namespace SmartClassroom.App;

/// <summary>
/// 瀑布流布局的纯计算部分（抽出来是为了可单测）。
/// 与 WrapPanel 的区别：
///   · 列数按可用宽度算，卡片宽度**拉伸填满**整行，右侧不留白；
///   · 每列独立向下堆叠，**不会被同一行最高的卡片撑高**（消除短卡片下方的大片空白）。
/// </summary>
public static class MasonryLayout
{
    /// <summary>
    /// 由可用宽度算出列数与每列宽度。
    /// 宽度无限（例如放在横向 ScrollViewer 里）或非法时退化为单列。
    /// </summary>
    public static (int Columns, double ItemWidth) ComputeColumns(
        double availableWidth, double minItemWidth, double gap, int maxColumns)
    {
        if (minItemWidth <= 0)
            minItemWidth = 1;
        if (gap < 0)
            gap = 0;
        var columnCap = maxColumns <= 0 ? int.MaxValue : maxColumns;

        if (double.IsNaN(availableWidth) || double.IsInfinity(availableWidth) || availableWidth <= 0)
            return (1, minItemWidth);

        // 能放下几列：n 列需要 n*min + (n-1)*gap ≤ width
        var columns = (int)Math.Floor((availableWidth + gap) / (minItemWidth + gap));
        columns = Math.Clamp(columns, 1, columnCap);

        var itemWidth = (availableWidth - gap * (columns - 1)) / columns;
        return (columns, Math.Max(1, itemWidth));
    }

    /// <summary>
    /// 把每个条目分配到当前最矮的一列（平局取靠左），返回每个条目的列号。
    /// </summary>
    public static int[] AssignToColumns(IReadOnlyList<double> heights, int columns)
    {
        var result = new int[heights.Count];
        if (columns <= 1)
            return result;   // 全 0：单列时顺序堆叠

        var tops = new double[columns];
        for (var i = 0; i < heights.Count; i++)
        {
            var best = 0;
            for (var c = 1; c < columns; c++)
                if (tops[c] < tops[best] - 1e-9)   // 严格更矮才换 → 平局取靠左，结果稳定
                    best = c;
            result[i] = best;
            tops[best] += Math.Max(0, heights[i]);
        }
        return result;
    }
}
