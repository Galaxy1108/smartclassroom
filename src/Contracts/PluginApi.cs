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
}

/// <summary>POST /exchange 请求体即 <see cref="ExchangeRequest"/>，返回 <see cref="ExchangeVerdict"/>。</summary>
public sealed record PluginStatus
{
    public required string PluginVersion { get; init; }
    public required bool ClassPlanLoaded { get; init; }
}
