using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
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
    private Point _pressPoint;
    private int _sourceIndex = -1;
    private int _targetIndex = -1;
    private Rect _sourceBounds;
    private Point _grabOffset;

    public HomeworkView()
    {
        InitializeComponent();
        DataContext ??= new HomeworkViewModel();

        AddHandler(PointerWheelChangedEvent, OnWheelZoom, RoutingStrategies.Tunnel);
        ContentZoom.Changed += OnZoomChanged;
        ApplyZoom(ContentZoom.Scale);
        DetachedFromVisualTree += (_, _) => ContentZoom.Changed -= OnZoomChanged;
    }

    private HomeworkViewModel Vm => (HomeworkViewModel)DataContext!;

    // ================= 缩放（仅本页内容区） =================

    private void OnZoomChanged(double scale) => ApplyZoom(scale);

    private void ApplyZoom(double scale)
        => CardScaler.LayoutTransform = new ScaleTransform(scale, scale);

    private void OnWheelZoom(object? sender, PointerWheelEventArgs e) => ContentZoom.HandleWheel(e);

    // ================= 手动添加 =================

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
    //
    // 交互约定（按需求）：
    //   · 拖拽期间列表**不动**，只有一张跟随指针的"虚"卡片；
    //   · 目标位置用一个虚线边框指示（框住将要落位的格子）；
    //   · 松手才真正落位（调用 MoveItemLive 重排一次）。
    // 这样既没有卡片乱跳，也不需要拖拽中反复改集合（那正是之前丢指针捕获/崩溃的根源）。

    private void Card_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        try
        {
            if (sender is not Control card || card.DataContext is not HomeworkCard item)
                return;
            if (!e.GetCurrentPoint(card).Properties.IsLeftButtonPressed)
                return;
            var index = Vm.Items.IndexOf(item);
            if (index < 0)
                return;

            _dragCard = card;
            _sourceIndex = index;
            _targetIndex = -1;
            _dragging = false;
            _pressedAt = DateTime.UtcNow;
            _pressPoint = e.GetPosition(CardLayer);
            // 从"按下"起冻结刷新：定时刷新若重建卡片会销毁被按住的控件、丢失指针捕获
            Vm.SuspendRefresh = true;
            e.Pointer.Capture(card);
        }
        catch (Exception ex)
        {
            SafeReset(ex);
        }
    }

    private void Card_PointerMoved(object? sender, PointerEventArgs e)
    {
        try
        {
            if (_dragCard is null)
                return;

            // PointerMoved 在"只悬停不按键"时也会触发；必须确认左键仍按住
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                EndDrag(commit: _dragging);
                return;
            }

            if (!_dragging)
            {
                if ((DateTime.UtcNow - _pressedAt).TotalMilliseconds < LongPressMs)
                    return;
                if (!BeginDrag())
                    return;
            }

            UpdateDrag(e.GetPosition(CardLayer));
        }
        catch (Exception ex)
        {
            SafeReset(ex);
        }
    }

    private void Card_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        try
        {
            e.Pointer.Capture(null);
        }
        catch (Exception ex)
        {
            SafeReset(ex);
            return;
        }
        EndDrag(commit: _dragging);
    }

    private void Card_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        try
        {
            EndDrag(commit: false);   // 捕获意外丢失 → 取消，不落位
        }
        catch (Exception ex)
        {
            SafeReset(ex);
        }
    }

    /// <summary>进入拖拽：准备"虚"卡片与目标指示。拿不到坐标则放弃本次拖拽。</summary>
    private bool BeginDrag()
    {
        var containers = Containers();
        if (_sourceIndex < 0 || _sourceIndex >= containers.Count)
            return false;
        var topLeft = containers[_sourceIndex].TranslatePoint(new Point(0, 0), CardLayer);
        if (topLeft is null)
            return false;

        _sourceBounds = new Rect(topLeft.Value, containers[_sourceIndex].Bounds.Size);
        _grabOffset = new Point(_pressPoint.X - _sourceBounds.X, _pressPoint.Y - _sourceBounds.Y);
        _targetIndex = _sourceIndex;
        _dragging = true;

        if (_dragCard?.DataContext is HomeworkCard card)
        {
            DragGhost.Content = card;
            DragGhost.Width = _sourceBounds.Width;
            DragGhost.Height = _sourceBounds.Height;
            Canvas.SetLeft(DragGhost, _sourceBounds.X);
            Canvas.SetTop(DragGhost, _sourceBounds.Y);
            DragGhost.IsVisible = true;
        }

        _dragCard?.Classes.Add("dragging");   // 原位卡片压暗，便于看清"虚"卡片
        ShowIndicator(_sourceIndex);
        return true;
    }

    private void UpdateDrag(Point pointer)
    {
        Canvas.SetLeft(DragGhost, pointer.X - _grabOffset.X);
        Canvas.SetTop(DragGhost, pointer.Y - _grabOffset.Y);

        var target = IndexAt(pointer);
        if (target >= 0 && target != _targetIndex)
        {
            _targetIndex = target;
            ShowIndicator(target);
        }
    }

    /// <summary>把虚线指示框移到目标格子。<paramref name="index"/> 越界时隐藏。</summary>
    private void ShowIndicator(int index)
    {
        var containers = Containers();
        if (index < 0 || index >= containers.Count)
        {
            SlotIndicator.IsVisible = false;
            return;
        }
        var topLeft = containers[index].TranslatePoint(new Point(0, 0), CardLayer);
        if (topLeft is null)
        {
            SlotIndicator.IsVisible = false;
            return;
        }
        var size = containers[index].Bounds.Size;
        SlotIndicator.Width = Math.Max(0, size.Width - 2);
        SlotIndicator.Height = Math.Max(0, size.Height - 2);
        Canvas.SetLeft(SlotIndicator, topLeft.Value.X + 1);
        Canvas.SetTop(SlotIndicator, topLeft.Value.Y + 1);
        SlotIndicator.IsVisible = true;
    }

    /// <summary>结束拖拽：撤掉"虚"卡片与指示；commit 为真才真正落位。</summary>
    private void EndDrag(bool commit)
    {
        DragGhost.IsVisible = false;
        DragGhost.Content = null;
        SlotIndicator.IsVisible = false;
        if (_dragCard is not null)
            _dragCard.Classes.Remove("dragging");

        var wasDragging = _dragging;
        var source = _sourceIndex;
        var target = _targetIndex;
        _dragCard = null;
        _dragging = false;
        _sourceIndex = -1;
        _targetIndex = -1;
        Vm.SuspendRefresh = false;

        if (wasDragging && commit && source >= 0 && target >= 0 && target != source)
            Vm.MoveItemLive(source, target);

        Vm.Refresh(force: true);           // 取回期间错过的数据变更
        if (wasDragging && commit)
            Runtime.SaveState();
    }

    /// <summary>兜底：UI 事件里出任何异常都不能让应用崩掉。</summary>
    private void SafeReset(Exception ex)
    {
        try
        {
            DragGhost.IsVisible = false;
            DragGhost.Content = null;
            SlotIndicator.IsVisible = false;
            _dragCard?.Classes.Remove("dragging");
        }
        catch { /* 清理本身失败也要吞掉 */ }

        _dragCard = null;
        _dragging = false;
        _sourceIndex = -1;
        _targetIndex = -1;
        Vm.SuspendRefresh = false;
        Runtime.Feed.Append("ui", "拖拽排序出错，已复位", ex.GetType().Name + ": " + ex.Message);
    }

    /// <summary>当前实际渲染出来的卡片容器（顺序与数据一致）。</summary>
    private List<Control> Containers()
        => HomeworkList.ItemsPanelRoot?.GetVisualChildren().OfType<Control>().ToList() ?? [];

    /// <summary>
    /// 算出指针落在哪个格子上。几何判定交给 <see cref="GridHitTest"/>（纯函数、有单测覆盖，
    /// 因为多行网格下必须按矩形命中，不能只比 Y）。
    /// </summary>
    private int IndexAt(Point p)
        => GridHitTest.IndexAt(Containers().Select(c => c.Bounds).ToList(), p);
}
