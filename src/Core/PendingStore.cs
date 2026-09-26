using SmartClassroom.Contracts;

namespace SmartClassroom.Core;

/// <summary>
/// 待确认事项：AI 失败、类型未知等无法自动处理、需要人介入的群消息。
/// 保留原文与来源，因此可以「重新解析」或人工录入，而不是把消息丢掉。
/// </summary>
public sealed record PendingItem
{
    public required string Id { get; init; }

    /// <summary>homework / exchange / summon。</summary>
    public required string Kind { get; init; }

    public required string Title { get; init; }

    /// <summary>群消息原文（去掉 CQ 码）。</summary>
    public required string RawText { get; init; }

    /// <summary>为什么进待确认（AI 超时 / 返回非法 / 类型未知…）。</summary>
    public required string Reason { get; init; }

    public required SenderInfo Sender { get; init; }

    public required MessageRef Source { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>重试次数，UI 可显示"已重试 2 次"。</summary>
    public int RetryCount { get; init; }
}

/// <summary>待确认队列（内存）。作用是把 AI 失败的群消息**留住**，让用户能补录或重试。</summary>
public sealed class PendingStore
{
    private readonly List<PendingItem> _items = new();

    public IReadOnlyList<PendingItem> All => _items.OrderByDescending(i => i.CreatedAt).ToList();

    public int Count => _items.Count;

    public PendingItem Add(PendingItem item)
    {
        _items.Add(item);
        return item;
    }

    /// <summary>按 Id 取；不存在返回 null。</summary>
    public PendingItem? Get(string id) => _items.FirstOrDefault(i => i.Id == id);

    public bool Remove(string id) => _items.RemoveAll(i => i.Id == id) > 0;

    /// <summary>重试计数 +1（保留原条目）。</summary>
    public PendingItem? BumpRetry(string id)
    {
        var item = Get(id);
        if (item is null)
            return null;
        var updated = item with { RetryCount = item.RetryCount + 1 };
        _items[_items.IndexOf(item)] = updated;
        return updated;
    }

    public void Clear() => _items.Clear();
}
