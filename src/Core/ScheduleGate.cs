using SmartClassroom.Contracts;

namespace SmartClassroom.Core;

/// <summary>课表状态源（生产实现走 ClassIsland IPC，v0.3 接入；单测用假实现）。</summary>
public interface IClassStatusProvider
{
    /// <summary>当前是否在上课中（含课表未加载返回 false，由调用方标注未知）。</summary>
    Task<bool> IsInClassAsync(CancellationToken cancel = default);
}

/// <summary>
/// 通知调度门：上课中非紧急召唤排队，下课 flush；紧急或空闲立刻发。
/// 同去重键 10 分钟内合并。
/// </summary>
public sealed class ScheduleGate(IClassStatusProvider status)
{
    private readonly Dictionary<string, QueuedNotification> _queue = new();

    public int PendingCount => _queue.Count;

    /// <summary>处理一条召唤，返回发送动作（立刻发 / 已排队）。发送执行由上层注入。</summary>
    public async Task<GateDecision> ProcessSummonAsync(
        SummonEvent summon,
        Func<string, string, CancellationToken, Task> send,
        CancellationToken cancel = default)
    {
        var inClass = await status.IsInClassAsync(cancel).ConfigureAwait(false);
        if (summon.Urgent || !inClass)
        {
            await send($"老师请{(summon.Urgent ? "（现在）" : "")}{summon.Target}过去",
                $"{summon.Sender.TeacherName ?? "老师"}：{summon.Reason}", cancel).ConfigureAwait(false);
            return GateDecision.SentNow;
        }
        var key = SummonGate.DedupKey(summon.Target, summon.ReceivedAt);
        _queue[key] = new QueuedNotification(key, $"请{summon.Target}过去",
            $"{summon.Sender.TeacherName ?? "老师"}：{summon.Reason}", summon.ReceivedAt);
        return GateDecision.Queued;
    }

    /// <summary>下课时调用：把排队通知依次发出并清空。</summary>
    public async Task<int> FlushAsync(Func<string, string, CancellationToken, Task> send, CancellationToken cancel = default)
    {
        var items = _queue.Values.OrderBy(q => q.EnqueuedAt).ToList();
        _queue.Clear();
        foreach (var q in items)
            await send(q.Title, q.Body, cancel).ConfigureAwait(false);
        return items.Count;
    }
}

public enum GateDecision { SentNow, Queued }

public sealed record QueuedNotification(string Key, string Title, string Body, DateTimeOffset EnqueuedAt);
