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

    /// <summary>
    /// 作业日期纠偏。模型偶尔会吐出与自己训练数据同期的日期（实测遇到过 3 个月前的日期），
    /// 那会把作业挂到完全错误的一天。只接受 [今天-1, 今天+14]：超出的一律当作"今天的作业"，
    /// 因为这里的 date 语义是"哪一天的作业"，而不是截止日期（截止说明在 due 里）。
    /// </summary>
    public static DateOnly CoerceHomeworkDate(string? raw, DateOnly today)
    {
        if (string.IsNullOrWhiteSpace(raw) || !DateOnly.TryParse(raw.Trim(), out var d))
            return today;
        return d < today.AddDays(-1) || d > today.AddDays(14) ? today : d;
    }
}

/// <summary>AI 结构化调用：三任务专用 prompt + JSON 解析。失败抛 AiException，上层降级。</summary>
public sealed class AiAnalyzer(IAiClient ai)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// 召唤解析。**必须带上下文**：消息里常只说"老师叫你过去一趟"，
    /// 不给"现在上什么课、谁是科任老师、群里有哪些老师"，AI 根本无从判断是哪个老师，
    /// 通知也就只能写个含糊的"老师"。
    /// </summary>
    public async Task<SummonDraft> AnalyzeSummonAsync(string text, SummonContext? context = null,
        CancellationToken cancel = default)
    {
        var ctx = context ?? SummonContext.Empty;
        var system = $$"""
            你分析班级QQ群里的消息，判断是否为"叫某人过去/上来"类召唤。
            只输出 JSON：{"is_summon":true/false,"target":"被叫的人名，无则空字符串","teacher":"叫人的老师姓名，能确定才填，否则空字符串","urgent":true/false,"title":"通知标题","body":"通知正文","confidence":0-1}

            title 是通知里的大字，要求：不超过 12 个字、说清"叫谁做什么"、不要重复人名。
            body 是通知里的小字，格式：老师（科目）：原话摘要。
            例子：
              消息"小明现在来一下" → {"is_summon":true,"target":"小明","urgent":true,"title":"现在请小明过去","body":"张老师（数学）：小明现在来一下"}
              消息"王子诚你上来把作业发一下" → {"is_summon":true,"target":"王子诚","urgent":false,"title":"请王子诚上来发作业","body":"李老师（语文）：王子诚你上来把作业发一下"}
              消息"张三来办公室一趟" → {"is_summon":true,"target":"张三","urgent":false,"title":"请张三去办公室","body":"王老师：张三来办公室一趟"}
            urgent 仅当出现"现在/立刻/马上/立即/赶紧"等要求立即过去的词时为 true。

            【上下文】
            - 发送者：{{ctx.Sender}}（{{ctx.SenderSubject}}）
            - 当前课程：{{ctx.Lesson}}（科任老师：{{ctx.LessonTeacher}}）
            - 班里已知老师：{{ctx.Roster}}
            规则：消息里只写"老师"而没写名字时，优先取**当前课程的科任老师**；
            写的是科目（如"数学老师"）就从已知老师里按科目匹配；都无法确定就留空。
            """;
        var raw = await ai.AskAsync(system, text, cancel).ConfigureAwait(false);
        var d = JsonSerializer.Deserialize<SummonDraft>(AiGateway.ExtractJson(raw), Json);
        return d ?? throw new AiException("召唤解析为空");
    }

    /// <summary>
    /// 轻量分类：老师消息**没命中本地关键词**时用它判断该走哪条流程。
    /// 本地关键词表永远不可能穷举（实测"…第二章全部写完啊，后天交"一条都不命中），
    /// 但直接把每条消息都跑三遍结构化解析又太贵，所以先花一次调用分类。
    /// 返回 "homework" / "exchange" / "summon" / "none"。
    /// </summary>
    public async Task<string?> AnalyzeKindAsync(string text, string? senderSubject,
        CancellationToken cancel = default)
    {
        var system = $$"""
            你是班级群消息分类器。只输出 JSON：{"category":"homework|exchange|summon|none"}

            summon：叫某个人过去/上来/去某处（**即使句子里出现"作业"，只要重点是人过去，就是 summon**）
              例："小明现在来一下" → summon
              例："王子诚你上来把作业发一下" → summon（重点是人上来）
              例："张三来办公室" → summon
            homework：老师布置/要求学生完成的学习任务（不一定出现"作业"二字）
              例："今天数学作业：练习册P10" → homework
              例："把第二章写完，后天交" → homework
            exchange：调课/换课/代课/串课/改到别的节次
              例："第三节和第五节换一下" → exchange
            none：闲聊、提问、通知等
              例："中午吃什么" → none

            发送者科目：{{senderSubject ?? "未知"}}
            """;
        try
        {
            var raw = await ai.AskAsync(system, text, cancel).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(AiGateway.ExtractJson(raw));
            // 拿不到 kind 字段（模型没按格式答 / 调用失败）→ 返回 null，
            // 由上层回退到本地关键词，而不是把消息当"无关"丢掉。
            // 字段名用 category：**不能用 kind** —— 换课请求的 JSON 里也有 kind（Swap/Replace），
            // 撞名会把换课消息误判成"无关"丢掉（实测踩到）。
            if (!doc.RootElement.TryGetProperty("category", out var c))
                return null;
            var value = (c.GetString() ?? "").Trim().ToLowerInvariant();
            return value switch
            {
                "homework" or "exchange" or "summon" or "none" => value,
                _ => null       // 模型答了别的词 → 交给本地关键词兜底
            };
        }
        catch (Exception)
        {
            return null;
        }
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
    [property: JsonPropertyName("teacher")] string? Teacher,
    [property: JsonPropertyName("urgent")] bool Urgent,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("body")] string? Body,
    [property: JsonPropertyName("confidence")] double Confidence);

/// <summary>召唤解析的上下文（消息里常只说"老师"，得靠这些信息落到具体的人）。</summary>
public sealed record SummonContext(
    string Sender,
    string SenderSubject,
    string Lesson,
    string LessonTeacher,
    string Roster)
{
    public static SummonContext Empty { get; } = new("未知", "未知", "未知（课表未加载）", "未知", "未知");
}
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
