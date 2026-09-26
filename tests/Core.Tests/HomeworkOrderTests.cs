using SmartClassroom.Contracts;
using SmartClassroom.Core;
using Xunit;

namespace SmartClassroom.Core.Tests;

/// <summary>作业顺序：新条目置顶、支持拖拽重排（顺序会被持久化）。</summary>
public sealed class HomeworkOrderTests
{
    private static HomeworkItem Hw(string subject, string date) => new()
    {
        HomeworkId = Guid.NewGuid().ToString(),
        Subject = subject,
        Date = DateOnly.Parse(date),
        Items = ["x"],
        Sender = new SenderInfo { UserId = 1 },
        Source = new MessageRef { GroupId = 1, MessageId = 1 }
    };

    [Fact]
    public void NewItems_GoToTop()
    {
        var s = new HomeworkStore();
        s.AddOrMerge(Hw("数学", "2026-09-25"));
        s.AddOrMerge(Hw("语文", "2026-09-25"));

        Assert.Equal(["语文", "数学"], s.All.Select(h => h.Subject));
    }

    [Fact]
    public void Move_Reorders()
    {
        var s = new HomeworkStore();
        s.AddOrMerge(Hw("数学", "2026-09-25"));
        s.AddOrMerge(Hw("语文", "2026-09-25"));
        s.AddOrMerge(Hw("英语", "2026-09-25"));
        // 当前顺序：英语, 语文, 数学
        Assert.True(s.Move(2, 0));                       // 数学拖到最前
        Assert.Equal(["数学", "英语", "语文"], s.All.Select(h => h.Subject));
    }

    [Fact]
    public void Move_ClampsAndIgnoresNoOps()
    {
        var s = new HomeworkStore();
        s.AddOrMerge(Hw("数学", "2026-09-25"));
        s.AddOrMerge(Hw("语文", "2026-09-25"));

        Assert.False(s.Move(0, 0));      // 原地
        Assert.False(s.Move(5, 0));      // 越界源
        Assert.False(s.Move(-1, 1));
        Assert.True(s.Move(0, 99));      // 目标越界 → 钳到末尾
        Assert.Equal(["数学", "语文"], s.All.Select(h => h.Subject));
    }

    [Fact]
    public void Order_SurvivesPersistence()
    {
        var s = new HomeworkStore();
        s.AddOrMerge(Hw("数学", "2026-09-25"));
        s.AddOrMerge(Hw("语文", "2026-09-25"));
        s.AddOrMerge(Hw("英语", "2026-09-25"));
        s.Move(2, 0);   // 数学置顶

        var restored = new HomeworkStore();
        restored.ReplaceAll(s.All);

        Assert.Equal(s.All.Select(h => h.Subject), restored.All.Select(h => h.Subject));
    }
}
