using SmartClassroom.Contracts;

namespace SmartClassroom.Core;

/// <summary>课表状态源（生产实现走 ClassIsland IPC；单测用假实现）。</summary>
public interface IClassStatusProvider
{
    /// <summary>当前是否在上课中（含课表未加载返回 false，由调用方标注未知）。</summary>
    Task<bool> IsInClassAsync(CancellationToken cancel = default);

    /// <summary>当前课程（上课中才有；未连接/无课表返回 null）。</summary>
    Task<CurrentLesson?> GetCurrentLessonAsync(CancellationToken cancel = default);
}

/// <summary>当前课程快照。</summary>
public sealed record CurrentLesson(string Subject, string? Teacher);

/// <summary>
/// 通知调度门：上课中非紧急召唤排队，下课 flush；紧急或空闲立刻发。
/// 队列项自带通道（summon/manual），flush 时原通道发出；手动事项按标题小时级去重。
/// </summary>
public sealed class ScheduleGate(IClassStatusProvider status)
{
    private readonly Dictionary<string, QueuedNotification> _queue = new();

    public int PendingCount => _queue.Count;

    /// <summary>当前课程（召唤解析要用它判断"老师"是谁）。未连接/无课表返回 null。</summary>
    public Task<CurrentLesson?> CurrentLessonAsync(CancellationToken cancel = default)
        => status.GetCurrentLessonAsync(cancel);

    /// <summary>发送函数：(通道, 标题, 正文)。</summary>
    public delegate Task SendFunc(string channel, string title, string body, CancellationToken cancel);

    /// <summary>处理一条召唤，返回发送动作（立刻发 / 已排队）。发送执行由上层注入。</summary>
    public async Task<GateDecision> ProcessSummonAsync(
        SummonEvent summon,
        SendFunc send,
        CancellationToken cancel = default)
    {
        var inClass = await status.IsInClassAsync(cancel).ConfigureAwait(false);
        if (summon.Urgent || !inClass)
        {
            // 通知里写清"哪个老师、哪一科" —— 原消息常只说"老师叫你过去"，
            // 不带上科任信息，看通知的人根本不知道是谁叫的。
            await send(NotifyChannels.Summon, summon.MaskText, summon.OverlayText, cancel).ConfigureAwait(false);
            return GateDecision.SentNow;
        }
        var key = SummonGate.DedupKey(summon.Target, summon.ReceivedAt);
        _queue[key] = new QueuedNotification(key, NotifyChannels.Summon,
            summon.MaskText, summon.OverlayText, summon.ReceivedAt);
        return GateDecision.Queued;
    }

    /// <summary>排一条手动事项（如非法换课），下课 flush 时经 manual 通道发出。</summary>
    public void EnqueueManual(string title, string body)
    {
        var key = $"manual:{title}:{DateTimeOffset.Now.ToUnixTimeSeconds() / 3600}";
        _queue[key] = new QueuedNotification(key, NotifyChannels.Manual, title, body, DateTimeOffset.Now);
    }

    /// <summary>
    /// 发一条"需要人工介入"类的提醒。**不在上课就立刻发**，上课才排队等下课。
    ///
    /// 以前这里无条件下排队、只有下课事件才 flush —— 周末/假期没有下课事件，
    /// 通知就永远卡在队列里（实测：事件页写着"已转发（已排队）"，ClassIsland 一条都没收到）。
    /// </summary>
    public async Task<GateDecision> SendManualAsync(string title, string body, SendFunc send,
        CancellationToken cancel = default)
    {
        var inClass = await status.IsInClassAsync(cancel).ConfigureAwait(false);
        if (!inClass)
        {
            await send(NotifyChannels.Manual, title, body, cancel).ConfigureAwait(false);
            return GateDecision.SentNow;
        }
        EnqueueManual(title, body);
        return GateDecision.Queued;
    }

    /// <summary>下课时调用：把排队通知按原通道依次发出并清空。</summary>
    public async Task<int> FlushAsync(SendFunc send, CancellationToken cancel = default)
    {
        var items = _queue.Values.OrderBy(q => q.EnqueuedAt).ToList();
        _queue.Clear();
        foreach (var q in items)
            await send(q.Channel, q.Title, q.Body, cancel).ConfigureAwait(false);
        return items.Count;
    }
}

public enum GateDecision { SentNow, Queued }

public sealed record QueuedNotification(string Key, string Channel, string Title, string Body, DateTimeOffset EnqueuedAt);
