using Avalonia.Headless.XUnit;
using SmartClassroom.App.ViewModels;
using SmartClassroom.Core;
using Xunit;

namespace SmartClassroom.App.Tests;

/// <summary>
/// 刷新策略的回归测试。
/// 起因是一个真 bug：主窗口每 2 秒调一次 Refresh，而无条件 Clear+重建会销毁
/// 正在被按住/拖动的卡片控件 → 指针捕获丢失（拖拽失效），
/// 在指针事件派发过程中销毁控件还可能直接崩溃。
/// 所以"数据没变就不重建"是必须守住的行为。
/// </summary>
public sealed class HomeworkRefreshTests
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
    public void Refresh_DoesNotRebuild_WhenDataUnchanged()
    {
        var vm = VmWith("数学", "语文");
        var before = vm.Items.ToList();

        vm.Refresh();          // 模拟 2 秒一次的定时刷新
        vm.Refresh();

        // 卡片对象必须被复用，否则按住/拖拽中的控件会被销毁
        Assert.Equal(before.Count, vm.Items.Count);
        for (var i = 0; i < before.Count; i++)
            Assert.Same(before[i], vm.Items[i]);
    }

    [AvaloniaFact]
    public void Refresh_Rebuilds_WhenDataChanged()
    {
        var store = new HomeworkStore();
        var vm = new HomeworkViewModel(store);
        vm.BeginAdd();
        vm.FormSubject = "数学";
        vm.FormItems = "x";
        vm.SubmitAdd();
        Assert.Single(vm.Items);

        // 绕过 VM 直接改存储，模拟 AI 录入后触发的刷新
        store.AddOrMerge(new SmartClassroom.Contracts.HomeworkItem
        {
            HomeworkId = "h2",
            Subject = "英语",
            Date = DateOnly.FromDateTime(DateTime.Now),
            Items = ["y"],
            Sender = new SmartClassroom.Contracts.SenderInfo { UserId = 1 },
            Source = new SmartClassroom.Contracts.MessageRef { GroupId = 1, MessageId = 1 }
        });
        vm.Refresh();

        Assert.Equal(2, vm.Items.Count);
        Assert.Contains(vm.Items, c => c.Subject == "英语");
    }

    [AvaloniaFact]
    public void Refresh_DetectsOrderChange()
    {
        var vm = VmWith("数学", "语文", "英语");
        vm.Refresh();
        var first = vm.Items[0].Subject;

        vm.MoveItemLive(2, 0);      // 拖拽重排（已同步 Items）
        vm.Refresh();

        Assert.NotEqual(first, vm.Items[0].Subject);
    }

    [AvaloniaFact]
    public void Refresh_Force_RebuildsEvenWhenUnchanged()
    {
        var vm = VmWith("数学");
        var before = vm.Items[0];

        vm.Refresh(force: true);    // 松手后的强制刷新

        Assert.NotSame(before, vm.Items[0]);
        Assert.Equal("数学", vm.Items[0].Subject);   // 内容仍正确
    }

    [AvaloniaFact]
    public void SuspendRefresh_BlocksEvenChangedData_UntilResumed()
    {
        var store = new HomeworkStore();
        var vm = new HomeworkViewModel(store);
        vm.SuspendRefresh = true;   // 按住期间

        store.AddOrMerge(new SmartClassroom.Contracts.HomeworkItem
        {
            HomeworkId = "h9",
            Subject = "物理",
            Date = DateOnly.FromDateTime(DateTime.Now),
            Items = ["z"],
            Sender = new SmartClassroom.Contracts.SenderInfo { UserId = 1 },
            Source = new SmartClassroom.Contracts.MessageRef { GroupId = 1, MessageId = 1 }
        });
        vm.Refresh();

        Assert.Empty(vm.Items);     // 挂起期间不重建

        vm.SuspendRefresh = false;
        vm.Refresh();
        Assert.Single(vm.Items);    // 恢复后补上
    }

    [AvaloniaFact]
    public void EmptyStore_RefreshIsStable()
    {
        var vm = new HomeworkViewModel(new HomeworkStore());
        vm.Refresh();
        vm.Refresh();
        Assert.True(vm.IsEmpty);
    }
}
