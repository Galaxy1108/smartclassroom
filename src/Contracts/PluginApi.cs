// App → 插件 localhost HTTP 接口契约。
// 通道用字符串键，插件内部再映射到提醒渠道 GUID，避免 GUID 散落各处。

namespace SmartClassroom.Contracts;

/// <summary>提醒通道键。</summary>
public static class NotifyChannels
{
    public const string Summon = "summon";     // 召唤通知
    public const string Exchange = "exchange"; // 换课结果
    public const string Manual = "manual";     // 需手动操作请求
}

/// <summary>POST /notify 请求。</summary>
public sealed record NotifyRequest
{
    public required string Channel { get; init; }
    public required string Title { get; init; }
    public required string Body { get; init; }

    /// <summary>
    /// true = 等到提醒**显示完成**再返回（ClassIsland 的 ShowNotificationAsync 语义）。
    /// 排队通知一条一条放就靠它，否则会一次性全糊上去。
    /// </summary>
    public bool Wait { get; init; }
}

/// <summary>POST /exchange 请求体即 <see cref="ExchangeRequest"/>，返回 <see cref="ExchangeVerdict"/>。</summary>
public sealed record PluginStatus
{
    public required string PluginVersion { get; init; }
    public required bool ClassPlanLoaded { get; init; }
}

/// <summary>GET /classplan?date=yyyy-MM-dd 的返回：某天的课表（**临时层优先**）。</summary>
public sealed record ClassPlanDay
{
    /// <summary>日期（yyyy-MM-dd）。</summary>
    public required string Date { get; init; }

    /// <summary>当天有没有课表（false 时 Periods 为空）。</summary>
    public required bool HasPlan { get; init; }

    /// <summary>这份课表是不是临时层（覆盖层）。</summary>
    public bool IsOverlay { get; init; }

    /// <summary>节次列表（Index 从 1 开始，与课表节次序号一致）。</summary>
    public required List<ClassPlanPeriod> Periods { get; init; }

    /// <summary>
    /// ClassIsland 里**已定义的全部科目名**。
    /// 给 AI 用：它必须从中选名字，否则会说"自习课"而课表里其实叫"自习"，
    /// 插件按名字查不到就只能转人工（用户明确提过这个问题）。
    /// </summary>
    public List<string> AllSubjects { get; init; } = [];
}

public sealed record ClassPlanPeriod
{
    public required int Index { get; init; }
    public required string Subject { get; init; }

    /// <summary>该节的起止时间（无时间表则为空），便于 AI 理解"第几节"。</summary>
    public string? Start { get; init; }
    public string? End { get; init; }
}
