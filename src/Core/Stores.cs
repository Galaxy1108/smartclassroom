using SmartClassroom.Contracts;

namespace SmartClassroom.Core;

/// <summary>作业存储：同科目同日合并条目，按日期倒序列出。纯内存，持久化由 App 层决定。</summary>
public sealed class HomeworkStore
{
    private readonly List<HomeworkItem> _items = new();

    public IReadOnlyList<HomeworkItem> All => _items.OrderByDescending(h => h.Date).ToList();

    /// <summary>整体替换（从磁盘恢复时用）。</summary>
    public void ReplaceAll(IEnumerable<HomeworkItem> items)
    {
        _items.Clear();
        _items.AddRange(items);
    }

    public IReadOnlyList<HomeworkItem> ForDate(DateOnly date)
        => _items.Where(h => h.Date == date).OrderBy(h => h.Subject).ToList();

    /// <summary>加入作业；同科目同日存在则合并条目（去重）并返回合并后的。</summary>
    public HomeworkItem AddOrMerge(HomeworkItem item)
    {
        var existing = _items.FirstOrDefault(h => h.Subject == item.Subject && h.Date == item.Date);
        if (existing is null)
        {
            _items.Add(item);
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

    public void Append(string kind, string title, string detail)
    {
        _entries.AddFirst(new ActivityEntry(DateTimeOffset.Now, kind, title, detail));
        while (_entries.Count > capacity)
            _entries.RemoveLast();
    }

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

public sealed record ActivityEntry(DateTimeOffset At, string Kind, string Title, string Detail);
