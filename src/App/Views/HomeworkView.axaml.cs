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

    /// <summary>按各卡片实际位置（不是等高的假设）算出指针落在第几张卡片上。</summary>
    private int IndexAt(Point p)
    {
        var panel = HomeworkList.ItemsPanelRoot;
        if (panel is null)
            return -1;
        var containers = panel.GetVisualChildren().OfType<Control>().ToList();
        for (var i = 0; i < containers.Count; i++)
        {
            var bounds = containers[i].Bounds;
            if (p.Y >= bounds.Top && p.Y <= bounds.Bottom)
                return i;
        }
        return p.Y < 0 ? 0 : Math.Max(0, containers.Count - 1);
    }
}
