using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using SmartClassroom.App.ViewModels;

namespace SmartClassroom.App.Views;

public partial class HomeworkView : UserControl
{
    /// <summary>长按多久才进入拖拽（毫秒）。</summary>
    private const int LongPressMs = 300;

    private Control? _dragCard;
    private bool _dragging;
    private DateTime _pressedAt;

    public HomeworkView()
    {
        InitializeComponent();
        DataContext ??= new HomeworkViewModel();
    }

    private HomeworkViewModel Vm => (HomeworkViewModel)DataContext!;

    private void BeginAdd_Click(object? sender, RoutedEventArgs e) => Vm.BeginAdd();

    private async void SubmitAdd_Click(object? sender, RoutedEventArgs e)
    {
        var ok = await Runtime.Auth.RequireAsync(
            reason => PasswordDialog.PromptAsync("添加作业", reason),
            "手动添加作业需要管理员密码");
        if (!ok)
            return;
        if (Vm.SubmitAdd())
            Runtime.SaveState();
    }

    private void CancelAdd_Click(object? sender, RoutedEventArgs e) => Vm.CancelAdd();

    // ================= 长按拖拽排序 =================

    private void Card_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control card || card.DataContext is not HomeworkCard)
            return;
        if (!e.GetCurrentPoint(card).Properties.IsLeftButtonPressed)
            return;

        _dragCard = card;
        _dragging = false;
        _pressedAt = DateTime.UtcNow;
        e.Pointer.Capture(card);
    }

    private void Card_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragCard is null)
            return;

        // 关键修复：PointerMoved 在"只悬停不按键"时也会触发。
        // 必须确认左键仍按住，否则松手后会继续跟着鼠标重排。
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            EndDrag(save: _dragging);
            return;
        }

        // 长按才进入拖拽（按住不放约 300ms）
        if (!_dragging)
        {
            if ((DateTime.UtcNow - _pressedAt).TotalMilliseconds < LongPressMs)
                return;
            BeginDrag();
        }

        if (_dragCard.DataContext is not HomeworkCard card)
            return;
        var current = Vm.Items.IndexOf(card);
        if (current < 0)
            return;

        Visual reference = HomeworkList.ItemsPanelRoot is { } panel ? panel : HomeworkList;
        var target = IndexAt(e.GetPosition(reference));
        if (target >= 0 && target != current)
            Vm.MoveItemLive(current, target);
    }

    private void Card_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
        EndDrag(save: _dragging);
    }

    private void Card_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        => EndDrag(save: _dragging);

    /// <summary>进入拖拽态：给卡片加"抬起"样式（缩放 + 阴影 + 强调边）。</summary>
    private void BeginDrag()
    {
        _dragging = true;
        Vm.SuspendRefresh = true;
        _dragCard?.Classes.Add("dragging");
    }

    /// <summary>结束拖拽：复位状态。松手后落盘一次，并补一次刷新取回期间错过的数据变更。</summary>
    private void EndDrag(bool save)
    {
        if (_dragCard is not null)
            _dragCard.Classes.Remove("dragging");

        var wasDragging = _dragging;
        _dragCard = null;
        _dragging = false;
        Vm.SuspendRefresh = false;

        if (wasDragging)
        {
            Vm.Refresh();
            if (save)
                Runtime.SaveState();
        }
    }

    /// <summary>
    /// 按各卡片实际位置算出指针落在哪张卡片上。
    /// 用矩形命中（X 和 Y 都比）而不是只看 Y——卡片是自适应多列网格，
    /// 只比 Y 会把不同列的卡片混在一起。都不命中时回退到中心点最近的那张。
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

        var nearest = -1;
        var best = double.MaxValue;
        for (var i = 0; i < containers.Count; i++)
        {
            var c = containers[i].Bounds.Center;
            var d = (c.X - p.X) * (c.X - p.X) + (c.Y - p.Y) * (c.Y - p.Y);
            if (d < best)
            {
                best = d;
                nearest = i;
            }
        }
        return nearest;
    }
}
