using Avalonia;
using Avalonia.Controls;

namespace SmartClassroom.App;

/// <summary>
/// 瀑布流面板：列数随宽度自适应，卡片宽度拉伸填满，每列独立堆叠。
/// 适合高度差异大的卡片（作业条目 1~10 条都有），避免 WrapPanel 那种
/// "同行等高、短卡片下方一片空白"的浪费。
/// </summary>
public class MasonryPanel : Panel
{
    public static readonly StyledProperty<double> MinItemWidthProperty =
        AvaloniaProperty.Register<MasonryPanel, double>(nameof(MinItemWidth), 300);

    public static readonly StyledProperty<double> GapProperty =
        AvaloniaProperty.Register<MasonryPanel, double>(nameof(Gap), 10);

    /// <summary>最大列数；≤0 表示不限制。</summary>
    public static readonly StyledProperty<int> MaxColumnsProperty =
        AvaloniaProperty.Register<MasonryPanel, int>(nameof(MaxColumns), 4);

    private int[] _columnOf = [];

    static MasonryPanel()
    {
        AffectsMeasure<MasonryPanel>(MinItemWidthProperty, GapProperty, MaxColumnsProperty);
        AffectsArrange<MasonryPanel>(MinItemWidthProperty, GapProperty, MaxColumnsProperty);
    }

    public double MinItemWidth
    {
        get => GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    public double Gap
    {
        get => GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    public int MaxColumns
    {
        get => GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = Children;
        if (children.Count == 0)
        {
            _columnOf = [];
            return new Size(0, 0);
        }

        var (columns, itemWidth) = MasonryLayout.ComputeColumns(
            availableSize.Width, MinItemWidth, Gap, MaxColumns);

        var heights = new double[children.Count];
        for (var i = 0; i < children.Count; i++)
        {
            // 用确定的宽度测量，卡片才会按列宽换行、算出真实高度
            children[i].Measure(new Size(itemWidth, double.PositiveInfinity));
            heights[i] = children[i].DesiredSize.Height;
        }

        _columnOf = MasonryLayout.AssignToColumns(heights, columns);

        var columnTops = new double[columns];
        for (var i = 0; i < children.Count; i++)
            columnTops[_columnOf[i]] += heights[i] + Gap;

        var tallest = 0d;
        foreach (var t in columnTops)
            tallest = Math.Max(tallest, t);

        var width = double.IsInfinity(availableSize.Width) ? itemWidth : availableSize.Width;
        return new Size(width, Math.Max(0, tallest - Gap));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = Children;
        if (children.Count == 0)
            return finalSize;

        var (columns, itemWidth) = MasonryLayout.ComputeColumns(
            finalSize.Width, MinItemWidth, Gap, MaxColumns);

        if (_columnOf.Length != children.Count)
        {
            var heights = new double[children.Count];
            for (var i = 0; i < children.Count; i++)
                heights[i] = children[i].DesiredSize.Height;
            _columnOf = MasonryLayout.AssignToColumns(heights, columns);
        }

        var columnTops = new double[columns];
        for (var i = 0; i < children.Count; i++)
        {
            var column = _columnOf[i];
            var x = column * (itemWidth + Gap);
            var y = columnTops[column];
            var h = children[i].DesiredSize.Height;
            children[i].Arrange(new Rect(x, y, itemWidth, h));
            columnTops[column] = y + h + Gap;
        }
        return finalSize;
    }
}
