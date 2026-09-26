using Avalonia.Headless.XUnit;
using SmartClassroom.App.ViewModels;
using SmartClassroom.Contracts;
using SmartClassroom.Core;
using Xunit;

namespace SmartClassroom.App.Tests;

/// <summary>
/// 过期作业：默认收起、不删除，可勾选查看。
///
/// 之前的做法是"永久留在墙上、只把日期标成已过期"，越积越多；
/// 现在数据照旧保留（state.json 里不动），只是默认不占位置。
/// </summary>
public sealed class HomeworkExpiryTests : IDisposable
{
    private readonly bool _original = Runtime.Settings.ShowExpiredHomework;

    public void Dispose() => Runtime.Settings.ShowExpiredHomework = _original;

    private static HomeworkItem Hw(string subject, DateOnly date, string item = "P10") => new()
    {
        HomeworkId = Guid.NewGuid().ToString(),
        Subject = subject, Date = date, Items = [item],
        Sender = new SenderInfo { UserId = 1, TeacherName = "张老师" },
        Source = new MessageRef { GroupId = 1, MessageId = 1 }
    };

    /// <summary>按给定顺序摆放（AddOrMerge 是"新加的在前"，这里要能明确指定顺序）。</summary>
    private static HomeworkViewModel Vm(params HomeworkItem[] displayOrder)
    {
        var store = new HomeworkStore();
        store.ReplaceAll(displayOrder);
        return new HomeworkViewModel(store);
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    [AvaloniaFact]
    public void ExpiredHiddenByDefault_ButStillStored()
    {
        var vm = Vm(Hw("语文", Today.AddDays(-5)), Hw("数学", Today), Hw("英语", Today.AddDays(1)));

        Assert.Equal(2, vm.Items.Count);                       // 昨天以前的不显示
        Assert.Equal(["数学", "英语"], vm.Items.Select(c => c.Subject));
        Assert.Equal(1, vm.HiddenExpiredCount);
        Assert.True(vm.HasHiddenExpired);
        Assert.Equal(3, vm.TotalCount);                        // 数据一条都没少
        Assert.Equal(3, vm.Snapshot().Count);
    }

    [AvaloniaFact]
    public void YesterdayCountsAsExpired()
    {
        var vm = Vm(Hw("数学", Today.AddDays(-1)));
        Assert.Empty(vm.Items);
        Assert.Equal(1, vm.HiddenExpiredCount);
    }

    [AvaloniaFact]
    public void ShowExpired_RevealsThemAgain()
    {
        var vm = Vm(Hw("语文", Today.AddDays(-5)), Hw("数学", Today));
        Assert.Single(vm.Items);

        vm.ShowExpired = true;

        Assert.Equal(2, vm.Items.Count);
        Assert.Equal(0, vm.HiddenExpiredCount);
        Assert.False(vm.HasHiddenExpired);

        vm.ShowExpired = false;
        Assert.Single(vm.Items);
    }

    [AvaloniaFact]
    public void ShowExpiredPreference_IsRememberedInSettings()
    {
        var vm = Vm(Hw("数学", Today));
        vm.ShowExpired = true;
        Assert.True(Runtime.Settings.ShowExpiredHomework);     // 存进共享设置对象

        var again = Vm(Hw("数学", Today));                      // 新开的视图模型读同一份偏好
        Assert.True(again.ShowExpired);
    }

    [AvaloniaFact]
    public void EmptyHint_DistinguishesNoHomeworkFromAllExpired()
    {
        var fresh = Vm(Hw("数学", Today));
        Assert.False(fresh.IsEmpty);
        Assert.Equal("暂无作业", fresh.EmptyHint);

        var allExpired = Vm(Hw("语文", Today.AddDays(-9)));
        Assert.True(allExpired.IsEmpty);
        Assert.Contains("今天没有作业", allExpired.EmptyHint);
        Assert.Contains("1 条", allExpired.EmptyHint);
        Assert.Contains("显示已过期", allExpired.EmptyHint);
    }

    /// <summary>
    /// 收起过期项后，可见下标 ≠ 存储下标。拖拽必须按映射换算，
    /// 否则会把夹在中间的过期作业挪走、用户看到的顺序也不对。
    /// </summary>
    [AvaloniaFact]
    public void Drag_WithHiddenExpiredItems_MovesTheRightCard()
    {
        // 存储顺序（新的在前）：英语(明天) / 语文(5天前) / 数学(今天)
        var vm = Vm(Hw("英语", Today.AddDays(1)), Hw("语文", Today.AddDays(-5)), Hw("数学", Today));
        Assert.Equal(["英语", "数学"], vm.Items.Select(c => c.Subject));   // 语文被收起

        // 把可见的第 0 张（英语）拖到第 1 个位置
        Assert.True(vm.MoveItemLive(0, 1));

        Assert.Equal(["数学", "英语"], vm.Items.Select(c => c.Subject));
        // 存储里也真的换了位；被收起的语文仍在，且没有挪动别的卡片
        var all = vm.Snapshot().Select(h => h.Subject).ToList();
        Assert.Equal(["语文", "数学", "英语"], all);
    }

    [AvaloniaFact]
    public void Drag_ThenShowExpired_KeepsOrderConsistent()
    {
        var vm = Vm(Hw("英语", Today.AddDays(1)), Hw("语文", Today.AddDays(-5)), Hw("数学", Today));
        vm.MoveItemLive(0, 1);            // 英语 ↔ 数学

        vm.ShowExpired = true;

        Assert.Equal(3, vm.Items.Count);
        Assert.Equal(["语文", "数学", "英语"], vm.Items.Select(c => c.Subject));
    }
}
