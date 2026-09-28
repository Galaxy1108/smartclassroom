using SmartClassroom.Contracts;

namespace SmartClassroom.Core;

/// <summary>作业存储：同科目同日合并条目，按日期倒序列出。纯内存，持久化由 App 层决定。</summary>
public sealed class HomeworkStore
{
    private readonly List<HomeworkItem> _items = new();

    /// <summary>按当前显示顺序返回（新添加的在前，可被手动拖拽重排）。</summary>
    public IReadOnlyList<HomeworkItem> All => _items.ToList();

    /// <summary>按 HomeworkId 删除（作业页的「删除」）。返回是否删掉了。</summary>
    public bool Remove(string homeworkId)
    {
        var index = _items.FindIndex(h => h.HomeworkId == homeworkId);
        if (index < 0)
            return false;
        _items.RemoveAt(index);
        return true;
    }

    /// <summary>按 HomeworkId 取一条。</summary>
    public HomeworkItem? Get(string homeworkId)
        => _items.FirstOrDefault(h => h.HomeworkId == homeworkId);

    /// <summary>按 HomeworkId 就地替换（作业页的「编辑」）。</summary>
    public bool Replace(HomeworkItem item)
    {
        var index = _items.FindIndex(h => h.HomeworkId == item.HomeworkId);
        if (index < 0)
            return false;
        _items[index] = item;
        return true;
    }

    /// <summary>拖拽重排：把 <paramref name="from"/> 位置的条目移动到 <paramref name="to"/>。</summary>
    public bool Move(int from, int to)
    {
        if (from < 0 || from >= _items.Count)
            return false;
        to = Math.Clamp(to, 0, _items.Count - 1);
        if (from == to)
            return false;
        var item = _items[from];
        _items.RemoveAt(from);
        _items.Insert(to, item);
        return true;
    }

    /// <summary>整体替换（从磁盘恢复时用）。</summary>
    public void ReplaceAll(IEnumerable<HomeworkItem> items)
    {
        _items.Clear();
        _items.AddRange(items);
    }

    /// <summary>
    /// 删除已过期的作业（date &lt; today），返回被删掉的条目。
    /// 作业的 date 语义是"哪一天的作业"：过了那天就不再是"今天的作业"，留着只会越积越多。
    /// </summary>
    public IReadOnlyList<HomeworkItem> PruneExpired(DateOnly today)
    {
        var removed = _items.Where(h => h.Date < today).ToList();
        if (removed.Count > 0)
            _items.RemoveAll(h => h.Date < today);
        return removed;
    }

    public IReadOnlyList<HomeworkItem> ForDate(DateOnly date)
        => _items.Where(h => h.Date == date).OrderBy(h => h.Subject).ToList();

    /// <summary>加入作业；同科目同日存在则合并条目（去重）并返回合并后的。</summary>
    public HomeworkItem AddOrMerge(HomeworkItem item)
    {
        var existing = _items.FirstOrDefault(h => h.Subject == item.Subject && h.Date == item.Date);
        if (existing is null)
        {
            _items.Insert(0, item); // 新条目置顶
            return item;
        }
        var merged = existing.Items.Concat(item.Items).Distinct().ToList();
        var updated = existing with { Items = merged };
        _items[_items.IndexOf(existing)] = updated;
        return updated;
    }
}

/// <summary>动态流：召唤/换课/归档等事件的统一时间线（UI 绑定源，保留最近 N 条）。</summary>
public sealed class ActivityFeed(int capacity = 200)
{
    private readonly LinkedList<ActivityEntry> _entries = new();
    private readonly Dictionary<Guid, LinkedListNode<ActivityEntry>> _byId = new();

    public IReadOnlyList<ActivityEntry> Entries => _entries.ToList();

    /// <summary>是否还有"进行中"的条目（界面据此加快刷新，实时显示处理状态）。</summary>
    public bool HasInProgress => _entries.Any(e => e.InProgress);

    public void Append(string kind, string title, string detail,
        ActivitySeverity severity = ActivitySeverity.Info)
    {
        _entries.AddFirst(new ActivityEntry(DateTimeOffset.Now, kind, title, detail, severity));
        while (_entries.Count > capacity)
        {
            var last = _entries.Last!;
            _byId.Remove(last.Value.Id);
            _entries.RemoveLast();
        }
    }

    /// <summary>
    /// 开一条"进行中"的条目（例如"正在处理 QQ 消息"），返回它的 id。
    /// 处理过程中用 <see cref="Update"/> 改状态，结束时用 <see cref="Complete"/> 收尾 ——
    /// 用户要的是"实时看到处理到哪一步了"，而不是只在结束后看到一条结果。
    /// </summary>
    public Guid Begin(string kind, string title, string detail = "")
    {
        var entry = new ActivityEntry(DateTimeOffset.Now, kind, title, detail, ActivitySeverity.Info)
        {
            InProgress = true
        };
        var node = _entries.AddFirst(entry);
        _byId[entry.Id] = node;
        while (_entries.Count > capacity)
        {
            var last = _entries.Last!;
            _byId.Remove(last.Value.Id);
            _entries.RemoveLast();
        }
        return entry.Id;
    }

    /// <summary>更新进行中条目的状态（标题/细节/级别）。找不到就忽略（可能已被清空）。</summary>
    public void Update(Guid id, string? title = null, string? detail = null,
        ActivitySeverity? severity = null)
    {
        if (!_byId.TryGetValue(id, out var node))
            return;
        var e = node.Value;
        node.Value = e with
        {
            Title = title ?? e.Title,
            Detail = detail ?? e.Detail,
            Severity = severity ?? e.Severity
        };
    }

    /// <summary>结束一条进行中条目（就地变成结果行，不再转圈）。</summary>
    /// <summary>
    /// 把这条记录的计时起点重置为"现在"。
    /// 用于排队通知：排队等下课的时间不该算进耗时里（用户明确要求），
    /// 所以真正发出前先重置起点，之后 Complete 得到的耗时才是"发送本身"的耗时。
    /// </summary>
    public void ResetTimer(Guid id)
    {
        if (!_byId.TryGetValue(id, out var node))
            return;
        node.Value = node.Value with { At = DateTimeOffset.Now };
    }

    public void Complete(Guid id, string title, string detail,
        ActivitySeverity severity = ActivitySeverity.Success)
    {
        if (!_byId.TryGetValue(id, out var node))
            return;
        var e = node.Value;
        // 关键：把 InProgress 置回 false —— 否则界面上的转圈与秒表永远不停
        //（实测"已执行：作业已上墙 处理中 81.0s"这种鬼东西）。
        node.Value = e with
        {
            Title = title,
            Detail = detail,
            Severity = severity,
            InProgress = false,
            ElapsedMs = (long)(DateTimeOffset.Now - e.At).TotalMilliseconds
        };
    }

    /// <summary>
    /// 把残留的"进行中"记录收尾。启动时调用：
    /// 上次运行如果被强杀/异常退出，那些行会永远停在"处理中 xx.xs"
    ///（实测用户机器上就有 66.8s、23.7s 这种卡片）。
    /// </summary>
    public int CloseDangling(string reason = "上次运行中断")
    {
        var count = 0;
        for (var node = _entries.First; node is not null; node = node.Next)
        {
            if (!node.Value.InProgress)
                continue;
            node.Value = node.Value with
            {
                Title = "已中断（" + reason + "）",
                InProgress = false,
                Severity = ActivitySeverity.Warning,
                ElapsedMs = (long)(DateTimeOffset.Now - node.Value.At).TotalMilliseconds
            };
            count++;
        }
        return count;
    }

    /// <summary>清空时间线（界面上的「清空」按钮；持久化由 App 层的下一次落盘完成）。</summary>
    public void Clear() => _entries.Clear();

    /// <summary>整体替换（从磁盘恢复时用，按时间倒序重放）。</summary>
    public void ReplaceAll(IEnumerable<ActivityEntry> entries)
    {
        _entries.Clear();
        _byId.Clear();
        foreach (var e in entries.OrderByDescending(x => x.At))
            _entries.AddLast(e);
        while (_entries.Count > capacity)
            _entries.RemoveLast();
    }
}

/// <summary>
/// 时间线条目的严重级别。它只表达"这条消息对用户意味着什么"，
/// 用来决定界面配色（信息=中性蓝、成功=绿、警告=橙、错误=红）。
/// </summary>
public enum ActivitySeverity
{
    /// <summary>中性记录（正常流程、恢复状态、AI 判定后跳过）。</summary>
    Info,

    /// <summary>
    /// "已忽略"这类**没有发生任何事**的结果：界面用灰色，
    /// 不抢眼也不报警（用户明确要求：已忽略应该是灰色）。
    /// </summary>
    Muted,

    /// <summary>按预期完成的副作用（已归档、已上墙、已发通知）。</summary>
    Success,

    /// <summary>需要留意但没坏（功能未开启、连接断开重连中、非法换课转人工、AI 降级）。</summary>
    Warning,

    /// <summary>出错（异常、归档失败、解析失败）。</summary>
    Error
}

public sealed record ActivityEntry(
    DateTimeOffset At,
    string Kind,
    string Title,
    string Detail,
    ActivitySeverity Severity = ActivitySeverity.Info)
{
    /// <summary>条目标识（进行中的条目靠它就地更新）。</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>true = 正在处理（界面显示转圈 + 已用时间）。</summary>
    public bool InProgress { get; init; }

    /// <summary>
    /// 处理耗时（毫秒，完成时填）。用户问过"这个已忽略经过了 AI 吗，怎么这么快" ——
    /// 有耗时就能自证：走 AI 至少一两秒，几百毫秒说明走的是本地判定。
    /// </summary>
    public long? ElapsedMs { get; init; }

    public string ElapsedLabel => ElapsedMs is { } ms
        ? ms < 1000 ? $"耗时 {ms}ms" : $"耗时 {ms / 1000.0:0.#}s"
        : "";
}
