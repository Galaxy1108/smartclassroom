using Avalonia.Headless.XUnit;
using SmartClassroom.App.ViewModels;
using SmartClassroom.Contracts;
using SmartClassroom.Core;
using Xunit;

namespace SmartClassroom.App.Tests;

/// <summary>
/// 过期作业：**直接删除**（date &lt; 今天），不再只是收起。
/// 作业的 date 语义是"哪一天的作业"，过了那天就不再是"今天的作业"。
/// </summary>
public sealed class HomeworkPruneTests
{
    private static HomeworkItem Hw(string subject, DateOnly date, string item = "P10") => new()
    {
        HomeworkId = Guid.NewGuid().ToString(),
        Subject = subject, Date = date, Items = [item],
        Sender = new SenderInfo { UserId = 1, TeacherName = "张老师" },
        Source = new MessageRef { GroupId = 1, MessageId = 1 }
    };

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    [Fact]
    public void PruneExpired_RemovesPast_KeepsTodayAndFuture()
    {
        var store = new HomeworkStore();
        store.ReplaceAll([
            Hw("英语", Today.AddDays(1)),
            Hw("数学", Today),
            Hw("语文", Today.AddDays(-1)),
            Hw("物理", Today.AddDays(-30))
        ]);

        var removed = store.PruneExpired(Today);

        Assert.Equal(2, removed.Count);
        Assert.Equal(["语文", "物理"], removed.Select(h => h.Subject));
        Assert.Equal(["英语", "数学"], store.All.Select(h => h.Subject));   // 顺序不变
    }

    [Fact]
    public void PruneExpired_NothingToDo_ReturnsEmpty()
    {
        var store = new HomeworkStore();
        store.ReplaceAll([Hw("数学", Today)]);
        Assert.Empty(store.PruneExpired(Today));
        Assert.Single(store.All);
    }

    [Fact]
    public void PruneExpired_IsIdempotent()
    {
        var store = new HomeworkStore();
        store.ReplaceAll([Hw("语文", Today.AddDays(-3))]);
        Assert.Single(store.PruneExpired(Today));
        Assert.Empty(store.PruneExpired(Today));
        Assert.Empty(store.All);
    }

    [AvaloniaFact]
    public void Runtime_Prune_LogsToTimeline()
    {
        Runtime.Homework.ReplaceAll([Hw("语文", Today.AddDays(-2)), Hw("数学", Today)]);
        var before = Runtime.Feed.Entries.Count;

        var removed = Runtime.PruneExpiredHomework();

        Assert.Equal(1, removed);
        Assert.Single(Runtime.Homework.All);                       // 过期那条没了
        Assert.Equal("数学", Runtime.Homework.All[0].Subject);
        Assert.Equal(before + 1, Runtime.Feed.Entries.Count);      // 记一条，别让作业"莫名消失"
        Assert.Contains(Runtime.Feed.Entries, e => e.Title.Contains("已删除 1 条过期作业"));
    }

    [AvaloniaFact]
    public void Wall_NeverShowsExpired_EvenIfPruneHasNotRunYet()
    {
        // 清理每 30 秒才跑一次；刚跨过零点时，墙上也不能闪过期卡片
        var store = new HomeworkStore();
        store.ReplaceAll([Hw("语文", Today.AddDays(-1)), Hw("数学", Today)]);
        var vm = new HomeworkViewModel(store);

        Assert.Single(vm.Items);
        Assert.Equal("数学", vm.Items[0].Subject);
        Assert.Equal(2, vm.TotalCount);                            // 存储里还在（等 Runtime 清）
    }

    [AvaloniaFact]
    public void EmptyHint_IsSimplyNoHomework()
    {
        var vm = new HomeworkViewModel(new HomeworkStore());
        Assert.True(vm.IsEmpty);
        Assert.Equal("暂无作业", vm.EmptyHint);
    }

    /// <summary>
    /// 有卡片被挡掉时"可见下标 ≠ 存储下标"，拖拽必须按映射换算，
    /// 否则会把没显示出来的那条挪走、用户看到的顺序也不对。
    /// </summary>
    [AvaloniaFact]
    public void Drag_WithAHiddenExpiredItem_MovesTheRightCard()
    {
        // 显示顺序：英语(明天) / 语文(过期) / 数学(今天)
        var store = new HomeworkStore();
        store.ReplaceAll([Hw("英语", Today.AddDays(1)), Hw("语文", Today.AddDays(-5)), Hw("数学", Today)]);
        var vm = new HomeworkViewModel(store);
        Assert.Equal(["英语", "数学"], vm.Items.Select(c => c.Subject));

        Assert.True(vm.MoveItemLive(0, 1));                        // 把英语拖到后面

        Assert.Equal(["数学", "英语"], vm.Items.Select(c => c.Subject));
        Assert.Equal(["语文", "数学", "英语"], vm.Snapshot().Select(h => h.Subject));
    }
}
