using Avalonia.Headless.XUnit;
using SmartClassroom.App.ViewModels;
using SmartClassroom.Core;
using Xunit;

namespace SmartClassroom.App.Tests;

/// <summary>
/// 拖拽语义：**拖拽期间列表不动**（只显示"虚"卡片 + 目标位指示），松手才落位。
/// 这里在 ViewModel 层复刻视图的事件序列，锁住这个约定——
/// 之前是"边拖边重排"，导致卡片乱跳，且拖拽中反复改集合会丢指针捕获。
/// </summary>
public sealed class DragSemanticsTests
{
    private static HomeworkViewModel VmWith(params string[] subjects)
    {
        var vm = new HomeworkViewModel(new HomeworkStore());
        foreach (var s in subjects)
        {
            vm.BeginAdd();
            vm.FormSubject = s;
            vm.FormItems = "x";
            vm.SubmitAdd();
        }
        return vm;
    }

    [AvaloniaFact]
    public void DragSequence_OrderChangesOnlyOnRelease()
    {
        var vm = VmWith("数学", "语文", "英语");     // 英语, 语文, 数学
        var before = vm.Items.Select(c => c.Subject).ToList();

        // ---- 按下 ----
        vm.SuspendRefresh = true;

        // ---- 拖动中：指针划过若干格子，但列表不应发生任何变化 ----
        for (var i = 0; i < 5; i++)
            vm.Refresh();                            // 期间的定时刷新也不该动它

        Assert.Equal(before, vm.Items.Select(c => c.Subject));

        // ---- 松手：落位（把最后一张移到最前）----
        var target = 0;
        vm.MoveItemLive(2, target);
        vm.SuspendRefresh = false;
        vm.Refresh(force: true);

        Assert.Equal("数学", vm.Items[0].Subject);
        Assert.Equal(3, vm.Items.Count);             // 没有丢条目
    }

    [AvaloniaFact]
    public void DropOnSameSlot_IsNoOp()
    {
        var vm = VmWith("数学", "语文");
        var before = vm.Items.Select(c => c.Subject).ToList();

        vm.SuspendRefresh = true;
        var moved = vm.MoveItemLive(1, 1);           // 原地松手
        vm.SuspendRefresh = false;
        vm.Refresh(force: true);

        Assert.False(moved);
        Assert.Equal(before, vm.Items.Select(c => c.Subject));
    }

    [AvaloniaFact]
    public void CancelDrag_KeepsOriginalOrder()
    {
        var vm = VmWith("数学", "语文", "英语");
        var before = vm.Items.Select(c => c.Subject).ToList();

        // 捕获意外丢失 → 取消：不调用 MoveItemLive
        vm.SuspendRefresh = true;
        vm.SuspendRefresh = false;
        vm.Refresh(force: true);

        Assert.Equal(before, vm.Items.Select(c => c.Subject));
    }

    [AvaloniaFact]
    public void DropOnLastSlot_MovesToEnd()
    {
        var vm = VmWith("数学", "语文", "英语");     // 英语, 语文, 数学

        vm.MoveItemLive(0, 2);                       // 把"英语"拖到最后

        Assert.Equal(["语文", "数学", "英语"], vm.Items.Select(c => c.Subject));
    }
}

/// <summary>
/// 拖拽接线自检。
/// 起因：把卡片外观抽成 DataTemplate 资源时，Border 上的 PointerPressed/Moved/Released/CaptureLost
/// 四个处理器漏抄了 —— 界面正常显示，但拖拽完全没反应、也不报错，很难查。
/// 现在处理器在构造函数里统一挂在列表上，并由这个测试守住"确实挂上了"。
/// </summary>
public sealed class DragWiringTests
{
    [AvaloniaFact]
    public void HomeworkView_AttachesDragHandlers()
    {
        var view = new SmartClassroom.App.Views.HomeworkView
        {
            DataContext = new HomeworkViewModel(new HomeworkStore())
        };
        Assert.True(view.DragHandlersAttached);
    }
}
