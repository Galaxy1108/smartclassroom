using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using SmartClassroom.App.ViewModels;

namespace SmartClassroom.App.Views;

public partial class HomeworkView : UserControl
{
    private int _dragFrom = -1;
    private bool _dragging;

    public HomeworkView()
    {
        InitializeComponent();
        DataContext ??= new HomeworkViewModel();
    }

    private HomeworkViewModel Vm => (HomeworkViewModel)DataContext!;

    private void BeginAdd_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Vm.BeginAdd();
    }

    private async void SubmitAdd_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var ok = await Runtime.Auth.RequireAsync(
            reason => PasswordDialog.PromptAsync("添加作业", reason),
            "手动添加作业需要管理员密码");
        if (!ok)
            return;
        if (Vm.SubmitAdd())
            Runtime.SaveState();   // 手动改动立刻落盘
    }

    private void CancelAdd_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Vm.CancelAdd();

    // ---------- 拖拽重排 ----------

    private void Card_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control card || card.DataContext is not HomeworkCard item)
            return;
        var index = Vm.Items.IndexOf(item);
        if (index < 0)
            return;
        _dragFrom = index;
        _dragging = false;
        e.Pointer.Capture(card);
    }

    private void Card_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragFrom < 0)
            return;
        _dragging = true;
        Visual reference = HomeworkList.ItemsPanelRoot is { } panel ? panel : HomeworkList;
        var target = IndexAt(e.GetPosition(reference));
        if (target >= 0 && target != _dragFrom && Vm.MoveItem(_dragFrom, target))
            _dragFrom = target;   // 跟随移动，形成"实时跟手"的重排
    }

    private void Card_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
        if (_dragging && _dragFrom >= 0)
            Runtime.SaveState();   // 松手后落盘新顺序
        _dragFrom = -1;
        _dragging = false;
    }

    private void Card_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _dragFrom = -1;
        _dragging = false;
    }

    /// <summary>
    /// 按各卡片实际位置算出指针落在哪张卡片上。
    /// 用矩形命中（X 和 Y 都比）而不是只看 Y——卡片是自适应多列网格，
    /// 只比 Y 会把不同列的卡片混在一起。都不命中时回退到最近的卡片。
    /// </summary>
    private int IndexAt(Point p)
    {
        var panel = HomeworkList.ItemsPanelRoot;
        if (panel is null)
            return -1;
        var containers = panel.GetVisualChildren().OfType<Control>().ToList();
        if (containers.Count == 0)
            return -1;

        for (var i = 0; i < containers.Count; i++)
            if (containers[i].Bounds.Contains(p))
                return i;

        // 落在卡片之间的空隙：取中心点最近的那张
        var nearest = -1;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < containers.Count; i++)
        {
            var c = containers[i].Bounds.Center;
            var d = (c.X - p.X) * (c.X - p.X) + (c.Y - p.Y) * (c.Y - p.Y);
            if (d < bestDistance)
            {
                bestDistance = d;
                nearest = i;
            }
        }
        return nearest;
    }
}
