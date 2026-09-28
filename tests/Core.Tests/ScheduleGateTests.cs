using SmartClassroom.Contracts;
using Xunit;

namespace SmartClassroom.Core.Tests;

public sealed class ScheduleGateTests
{
    private sealed class FakeStatus(bool inClass) : IClassStatusProvider
    {
        public Task<bool> IsInClassAsync(CancellationToken cancel = default) => Task.FromResult(inClass);
        public Task<CurrentLesson?> GetCurrentLessonAsync(CancellationToken cancel = default) => Task.FromResult<CurrentLesson?>(null);
    }

    private static SummonEvent Summon(string target, bool urgent) => new()
    {
        EventId = Guid.NewGuid().ToString(),
        ReceivedAt = DateTimeOffset.Now,
        Target = target,
        Urgent = urgent,
        Reason = "来一下",
        Sender = new SenderInfo { UserId = 1, TeacherName = "张老师" },
        Source = new MessageRef { GroupId = 1, MessageId = 1 }
    };

    [Fact]
    public async Task InClass_Normal_Queues()
    {
        var gate = new ScheduleGate(new FakeStatus(true));
        var sent = new List<string>();
        var d = await gate.ProcessSummonAsync(Summon("小明", false), (c, t, b, _) => { sent.Add(t); return Task.CompletedTask; });
        Assert.Equal(GateDecision.Queued, d);
        Assert.Empty(sent);
        Assert.Equal(1, gate.PendingCount);
    }

    [Fact]
    public async Task InClass_Urgent_SendsNow()
    {
        var gate = new ScheduleGate(new FakeStatus(true));
        var sent = new List<string>();
        var d = await gate.ProcessSummonAsync(Summon("小明", true), (c, t, b, _) => { sent.Add(t); return Task.CompletedTask; });
        Assert.Equal(GateDecision.SentNow, d);
        Assert.Single(sent);
    }

    [Fact]
    public async Task Free_Normal_SendsNow()
    {
        var gate = new ScheduleGate(new FakeStatus(false));
        var sent = new List<string>();
        var d = await gate.ProcessSummonAsync(Summon("小明", false), (c, t, b, _) => { sent.Add(t); return Task.CompletedTask; });
        Assert.Equal(GateDecision.SentNow, d);
        Assert.Single(sent);
    }

    [Fact]
    public async Task Flush_SendsQueuedInOrder()
    {
        var gate = new ScheduleGate(new FakeStatus(true));
        Task noop(string ch, string t, string b, CancellationToken c) => Task.CompletedTask;
        await gate.ProcessSummonAsync(Summon("小明", false), noop);
        await gate.ProcessSummonAsync(Summon("小红", false), noop);
        var sent = new List<string>();
        var n = await gate.FlushAsync((c, t, b, _) => { sent.Add(t); return Task.CompletedTask; });
        Assert.Equal(2, n);
        Assert.Equal(0, gate.PendingCount);
        Assert.Contains("小明", sent[0]);
    }
}

/// <summary>
/// 排队通知与事件行的联动：排队时标"正在等待下课"，真发出去了才算完成；
/// 排队时间不计入耗时（发出前重置计时起点）。
/// </summary>
public sealed class QueueRowTests
{
    private sealed class InClass : IClassStatusProvider
    {
        public Task<bool> IsInClassAsync(CancellationToken cancel = default) => Task.FromResult(true);
        public Task<CurrentLesson?> GetCurrentLessonAsync(CancellationToken cancel = default)
            => Task.FromResult<CurrentLesson?>(null);
    }

    [Fact]
    public async Task QueuedThenSent_FiresBothEvents()
    {
        ScheduleGate.Spacing = TimeSpan.Zero;
        var gate = new ScheduleGate(new InClass());
        var feed = new ActivityFeed();
        var rowId = feed.Begin("summon", "正在处理消息", "x");
        var queued = new List<Guid>();
        var sent = new List<Guid>();
        gate.Queued += id => { queued.Add(id); feed.Update(id, "正在等待下课", "排队中"); };
        gate.Sent += id => { sent.Add(id); feed.ResetTimer(id); feed.Complete(id, "已执行：通知已发出", ""); };

        var decision = await gate.NotifyAsync("summon", "标题", "正文",
            (_, _, _, _) => Task.CompletedTask, default, rowId);

        Assert.Equal(GateDecision.Queued, decision);
        Assert.Equal([rowId], queued);
        Assert.Empty(sent);                                   // 还没下课，不算发出
        Assert.True(feed.Entries.First(e => e.Id == rowId).InProgress);
        Assert.Equal("正在等待下课", feed.Entries.First(e => e.Id == rowId).Title);

        await gate.FlushAsync((_, _, _, _) => Task.CompletedTask);

        Assert.Equal([rowId], sent);
        var row = feed.Entries.First(e => e.Id == rowId);
        Assert.False(row.InProgress);                         // 真发出去了才完成
        Assert.Equal("已执行：通知已发出", row.Title);
    }

    [Fact]
    public async Task Urgent_SendsImmediatelyEvenInClass()
    {
        var gate = new ScheduleGate(new InClass());
        var feed = new ActivityFeed();
        var rowId = feed.Begin("summon", "正在处理消息", "x");
        var sent = new List<Guid>();
        gate.Sent += id => sent.Add(id);

        var decision = await gate.NotifyAsync("summon", "立刻来", "现在来一下",
            (_, _, _, _) => Task.CompletedTask, default, rowId, urgent: true);

        Assert.Equal(GateDecision.SentNow, decision);
        Assert.Equal([rowId], sent);                          // 紧急：上课也直接发
    }
}
