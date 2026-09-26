using SmartClassroom.Contracts;

namespace SmartClassroom.Core;

/// <summary>作业存储：同科目同日合并条目，按日期倒序列出。纯内存，持久化由 App 层决定。</summary>
public sealed class HomeworkStore
{
    private readonly List<HomeworkItem> _items = new();

    /// <summary>按当前显示顺序返回（新添加的在前，可被手动拖拽重排）。</summary>
    public IReadOnlyList<HomeworkItem> All => _items.ToList();

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

    public IReadOnlyList<ActivityEntry> Entries => _entries.ToList();

    public void Append(string kind, string title, string detail,
        ActivitySeverity severity = ActivitySeverity.Info)
    {
        _entries.AddFirst(new ActivityEntry(DateTimeOffset.Now, kind, title, detail, severity));
        while (_entries.Count > capacity)
            _entries.RemoveLast();
    }

    /// <summary>清空时间线（界面上的「清空」按钮；持久化由 App 层的下一次落盘完成）。</summary>
    public void Clear() => _entries.Clear();

    /// <summary>整体替换（从磁盘恢复时用，按时间倒序重放）。</summary>
    public void ReplaceAll(IEnumerable<ActivityEntry> entries)
    {
        _entries.Clear();
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
    ActivitySeverity Severity = ActivitySeverity.Info);
