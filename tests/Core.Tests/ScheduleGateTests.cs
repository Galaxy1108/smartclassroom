using SmartClassroom.Contracts;
using Xunit;

namespace SmartClassroom.Core.Tests;

public sealed class ScheduleGateTests
{
    private sealed class FakeStatus(bool inClass) : IClassStatusProvider
    {
        public Task<bool> IsInClassAsync(CancellationToken cancel = default) => Task.FromResult(inClass);
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
        var d = await gate.ProcessSummonAsync(Summon("小明", false), (t, b, _) => { sent.Add(t); return Task.CompletedTask; });
        Assert.Equal(GateDecision.Queued, d);
        Assert.Empty(sent);
        Assert.Equal(1, gate.PendingCount);
    }

    [Fact]
    public async Task InClass_Urgent_SendsNow()
    {
        var gate = new ScheduleGate(new FakeStatus(true));
        var sent = new List<string>();
        var d = await gate.ProcessSummonAsync(Summon("小明", true), (t, b, _) => { sent.Add(t); return Task.CompletedTask; });
        Assert.Equal(GateDecision.SentNow, d);
        Assert.Single(sent);
    }

    [Fact]
    public async Task Free_Normal_SendsNow()
    {
        var gate = new ScheduleGate(new FakeStatus(false));
        var sent = new List<string>();
        var d = await gate.ProcessSummonAsync(Summon("小明", false), (t, b, _) => { sent.Add(t); return Task.CompletedTask; });
        Assert.Equal(GateDecision.SentNow, d);
        Assert.Single(sent);
    }

    [Fact]
    public async Task Flush_SendsQueuedInOrder()
    {
        var gate = new ScheduleGate(new FakeStatus(true));
        Task noop(string t, string b, CancellationToken c) => Task.CompletedTask;
        await gate.ProcessSummonAsync(Summon("小明", false), noop);
        await gate.ProcessSummonAsync(Summon("小红", false), noop);
        var sent = new List<string>();
        var n = await gate.FlushAsync((t, b, _) => { sent.Add(t); return Task.CompletedTask; });
        Assert.Equal(2, n);
        Assert.Equal(0, gate.PendingCount);
        Assert.Contains("小明", sent[0]);
    }
}
