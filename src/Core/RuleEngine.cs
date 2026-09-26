using System.Text.Json;
using System.Text.Json.Serialization;
using SmartClassroom.Core.AI;

namespace SmartClassroom.Core;

/// <summary>
/// 群文本消息的规则引擎：本地关键词预分流 → 各任务走 AI 结构化。
/// 换课/作业/召唤可多命中；File 事件走 notice 通道，不在这里处理。
/// </summary>
public static class RuleEngine
{
    public static readonly string[] ExchangeHints =
        ["换课", "调课", "对调", "调换", "对换", "换一下", "换一节", "改到", "串课", "代课", "顶课"];

    public static readonly string[] HomeworkHints =
        ["作业", "练习", "背诵", "默写", "预习", "抄写", "试卷", "习题"];

    [Flags]
    public enum Kind { None = 0, Summon = 1, Homework = 2, Exchange = 4 }

    /// <summary>纯本地预分流（无网络，可全单测）。AI 负责精判与抽取。</summary>
    public static Kind ClassifyLocal(string text)
    {
        var kind = Kind.None;
        if (ExchangeHints.Any(text.Contains)) kind |= Kind.Exchange;
        if (HomeworkHints.Any(text.Contains)) kind |= Kind.Homework;
        if (SummonGate.LooksLikeSummon(text)) kind |= Kind.Summon;
        return kind;
    }
}

/// <summary>AI 结构化调用：三任务专用 prompt + JSON 解析。失败抛 AiException，上层降级。</summary>
public sealed class AiAnalyzer(IAiClient ai)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public async Task<SummonDraft> AnalyzeSummonAsync(string text, CancellationToken cancel = default)
    {
        const string system = """
            你分析班级QQ群里老师的消息，判断是否为"叫某人过去"类召唤。
            只输出 JSON：{"is_summon":true/false,"target":"被叫的人名，无则空字符串","urgent":true/false,"confidence":0-1}
            urgent 仅当出现"现在/立刻/马上/立即/赶紧"等要求立即过去的词时为 true。
            """;
        var raw = await ai.AskAsync(system, text, cancel).ConfigureAwait(false);
        var d = JsonSerializer.Deserialize<SummonDraft>(AiGateway.ExtractJson(raw), Json);
        return d ?? throw new AiException("召唤解析为空");
    }

    public async Task<HomeworkDraft> AnalyzeHomeworkAsync(string text, string? senderSubject, CancellationToken cancel = default)
    {
        var system = $$"""
            你整理老师布置的作业。发送者科目为"{{senderSubject ?? "未知"}}"（可作参考，以消息内容为准）。
            只输出 JSON：{"is_homework":true/false,"subject":"科目","date":"yyyy-MM-dd，当日作业则为今天","items":["作业条目1","作业条目2"],"due":"截止说明，无则空字符串","confidence":0-1}
            今天是 {{DateTime.Now:yyyy-MM-dd}}。
            """;
        var raw = await ai.AskAsync(system, text, cancel).ConfigureAwait(false);
        var d = JsonSerializer.Deserialize<HomeworkDraft>(AiGateway.ExtractJson(raw), Json);
        return d ?? throw new AiException("作业解析为空");
    }

    public async Task<ExchangeDraft> AnalyzeExchangeAsync(string text, CancellationToken cancel = default)
    {
        var system = $$"""
            你解析老师的换课消息。换课类型 kind：Swap=两节对调(同天)/Replace=某节改为另一科目/CrossDay=涉及不同日期。
            只输出 JSON：{"is_exchange":true/false,"kind":"Swap/Replace/CrossDay","from":{"date":"yyyy-MM-dd","period":某日第几节,"subject":"原科目，可空"},"to":{"date":"yyyy-MM-dd","period":n}或null,"new_subject":"Replace目标科目，否则空字符串","confidence":0-1}
            今天是 {{DateTime.Now:yyyy-MM-dd}}。"明天第三节和今天第五节换"这类要换算成具体日期。
            """;
        var raw = await ai.AskAsync(system, text, cancel).ConfigureAwait(false);
        var d = JsonSerializer.Deserialize<ExchangeDraft>(AiGateway.ExtractJson(raw), Json);
        return d ?? throw new AiException("换课解析为空");
    }
}

public sealed record SummonDraft(
    [property: JsonPropertyName("is_summon")] bool IsSummon,
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("urgent")] bool Urgent,
    [property: JsonPropertyName("confidence")] double Confidence);
public sealed record HomeworkDraft(
    [property: JsonPropertyName("is_homework")] bool IsHomework,
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("items")] List<string> Items,
    [property: JsonPropertyName("due")] string Due,
    [property: JsonPropertyName("confidence")] double Confidence);
public sealed record SlotDraft(
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("period")] int Period,
    [property: JsonPropertyName("subject")] string? Subject);
public sealed record ExchangeDraft(
    [property: JsonPropertyName("is_exchange")] bool IsExchange,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("from")] SlotDraft From,
    [property: JsonPropertyName("to")] SlotDraft? To,
    [property: JsonPropertyName("new_subject")] string NewSubject,
    [property: JsonPropertyName("confidence")] double Confidence);
