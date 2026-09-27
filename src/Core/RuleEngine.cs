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

    /// <summary>通知类线索（AI 不可用时的兜底）。</summary>
    public static readonly string[] NoticeHints =
        ["活动", "集合", "大礼堂", "操场", "记得带", "别忘了", "没交", "交上来", "准时"];

    /// <summary>催交/点名类线索：命中就按"通知"处理，不能当成布置作业。</summary>
    public static readonly string[] ReminderHints =
        ["没交", "交上来", "赶紧交", "快点交", "还没交", "谁没交", "未交"];

    [Flags]
    public enum Kind { None = 0, Summon = 1, Homework = 2, Exchange = 4, Notice = 8 }

    /// <summary>纯本地预分流（无网络，可全单测）。AI 负责精判与抽取。</summary>
    public static Kind ClassifyLocal(string text)
    {
        var kind = Kind.None;
        if (ExchangeHints.Any(text.Contains)) kind |= Kind.Exchange;
        if (HomeworkHints.Any(text.Contains)) kind |= Kind.Homework;
        if (SummonGate.LooksLikeSummon(text)) kind |= Kind.Summon;
        // 催交/点名这类带"作业"二字但不是布置的，本地也要能区分出来
        if (ReminderHints.Any(text.Contains))
            return Kind.Notice;
        if (kind == Kind.None && NoticeHints.Any(text.Contains)) kind |= Kind.Notice;
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
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        // 模型偶尔把数字写成字符串（"period":"3"）；不放开的话反序列化会抛异常，
        // 实测直接把 WS 事件循环掀翻，状态栏变成"QQ 未连接"。
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    /// <summary>
    /// 召唤解析。**必须带上下文**：消息里常只说"老师叫你过去一趟"，
    /// 不给"现在上什么课、谁是科任老师、群里有哪些老师"，AI 根本无从判断是哪个老师，
    /// 通知也就只能写个含糊的"老师"。
    /// </summary>
    public async Task<SummonDraft> AnalyzeSummonAsync(string text, SummonContext? context = null,
        CancellationToken cancel = default, Action<string>? onProgress = null)
    {
        var ctx = context ?? SummonContext.Empty;
        var system = $$"""
            你分析班级QQ群里的消息（**可能带图片：截图里的字也要看**），判断是否为"叫某人过去/上来"类召唤。
            **没点名具体的人也算**（"你们过来一下""都到操场集合""我的儿子们给我滚过来"都是召唤）。
            只输出 JSON：{"is_summon":true/false,"target":"被叫的人名，没点名就空字符串","teacher":"叫人的老师姓名，能确定才填，否则空字符串","urgent":true/false,"title":"通知标题","body":"通知正文","confidence":0-1}
            target 为空时，title/body 用"你们/大家"这类泛指（例：title="老师叫你们过去"）。

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
        var raw = await ai.AskAsync(system, text, cancel, onProgress).ConfigureAwait(false);
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
        CancellationToken cancel = default, Action<string>? onProgress = null,
        IReadOnlyList<AiImage>? images = null)
    {
        var system = $$"""
            你是班级群消息分类器。只输出 JSON：{"category":"homework|exchange|summon|notice|none"}
            **消息可能带图片：图片里的字也要看** —— 老师常发作业/通知截图，
            正文只是"[图片]"占位符时**必须看图**再判断，别直接判 none。

            summon：要求（某）人过去/上来/去某处 —— **即使句子里出现"作业"，只要重点是人过去，就是 summon**
              例："小明现在来一下" → summon
              例："王子诚你上来把作业发一下" → summon（重点是人上来）
              例："张三来办公室" → summon
              **没点名具体的人也算**（叫一群人、口语化喊人）：
              例："你们几个过来一下" → summon（target 留空）
              例："我的儿子们给我滚过来" → summon（target 留空，这是喊人过来）
              例："都到操场集合" → summon
            homework：老师**布置新任务**（不一定出现"作业"二字）
              **只有明确要求完成某个学习任务才算**：
              例："今天数学作业：练习册P10" → homework
              例："把第二章写完，后天交" → homework
              **以下都不算作业**：
              例：课表/排班截图（只有课程名和时间）→ none
              例：表情包、玩笑图、与学习任务无关的图片 → none
              例："喜大普奔，你们拿了第二" → notice
              例："今天数学作业：练习册P10" → homework
              例："把第二章写完，后天交" → homework
              **催交/点名/检查不算布置**：
              例："昨天作业 12,13,14 号没有交，快点交上来" → notice
              例："还有谁没交作业的赶紧交" → notice
            notice：**老师发给全班的信息**（活动、集合、时间地点、催交、提醒带东西、表扬祝贺、结果公布等）
              例："今天你们下午有个活动，2:00 到大礼堂" → notice
              例："明天记得带课本" → notice
              例："喜大普奔哇，你们看看！！！你们拿到了第二哇，哈哈哈哈！！！！" → notice（向全班公布好消息）
              例："这次考试平均分 82，比上次进步了" → notice
              只有**闲聊/提问/与全班无关**才是 none
              例："中午吃什么" → none
            exchange：调课/换课/代课/串课/改到别的节次
              例："第三节和第五节换一下" → exchange
            none：闲聊、提问、通知等
              例："中午吃什么" → none

            发送者科目：{{senderSubject ?? "未知"}}
            """;
        try
        {
            var raw = await ai.AskAsync(system, text, cancel, onProgress).ConfigureAwait(false);
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
                "homework" or "exchange" or "summon" or "notice" or "none" => value,
                _ => null       // 模型答了别的词 → 交给本地关键词兜底
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>把老师的通知整理成一条简短提醒（标题 + 正文）。</summary>
    public async Task<NoticeDraft> AnalyzeNoticeAsync(string text, CancellationToken cancel = default,
        Action<string>? onProgress = null, IReadOnlyList<AiImage>? images = null)
    {
        var system = $$"""
            把老师发的话整理成一条给学生看的提醒。只输出 JSON：{"is_notice":true/false,"title":"标题","body":"正文"}

            **只要老师是在向全班说一件事，就算 notice**（活动、集合、时间地点、催交、提醒带东西、
            表扬祝贺、公布成绩/名次、要求等）。只有闲聊、提问、与全班无关才是 is_notice=false。
            - title 不超过 14 字，一句话概括，例："今天下午 2:00 到大礼堂"、"班级拿了第二名"
            - body 保留关键信息，例："下午有个活动，2:00 到大礼堂"
            - 今天是 {{DateTime.Now:yyyy-MM-dd}}
            例："喜大普奔哇，你们看看！！！你们拿到了第二哇" → {"is_notice":true,"title":"班级拿了第二名","body":"喜大普奔，你们拿到了第二！"}
            例："这次考试平均分 82，比上次进步了" → {"is_notice":true,"title":"平均分 82，有进步","body":"这次考试平均分 82，比上次进步了"}
            例："12,13,14 号没交作业，快点交上来" → {"is_notice":true,"title":"12、13、14 号速交作业","body":"昨天作业 12、13、14 号未交"}
            例："中午吃什么" → {"is_notice":false,"title":"","body":""}
            """;
        var raw = await ai.AskAsync(system, text, cancel, onProgress, images).ConfigureAwait(false);
        var d = JsonSerializer.Deserialize<NoticeDraft>(AiGateway.ExtractJson(raw), Json);
        return d ?? throw new AiException("通知解析为空");
    }

    public async Task<HomeworkDraft> AnalyzeHomeworkAsync(string text, string? senderSubject,
        CancellationToken cancel = default, Action<string>? onProgress = null,
        IReadOnlyList<AiImage>? images = null)
    {
        var system = $$"""
            你整理老师布置的作业。发送者科目为"{{senderSubject ?? "未知"}}"（可作参考，以消息内容为准）。
            只输出 JSON：{"is_homework":true/false,"subject":"科目","date":"yyyy-MM-dd，当日作业则为今天","items":["作业条目1","作业条目2"],"due":"截止说明，无则空字符串","confidence":0-1}
            今天是 {{DateTime.Now:yyyy-MM-dd}}（{{DateTime.Now:dddd}}）。

            due 的三种情况：
              - 消息明确说**不用交/不用提交/自行完成** → due = "无需提交"
              - 提到截止（"后天交""明天上课前交"）→ 换算成具体日期并保留原话
              - **没提**截止 → due = ""（上层会显示"未说明截止"，不要瞎编）
            due 换算示例：
              消息里"后天交"、今天是 2026-09-26 → due = "2026-09-28（后天交）"
              消息里"明天上课前交"、今天是 2026-09-26 → due = "2026-09-27（明天上课前交）"
              没提截止 → due = ""
            """;
        var raw = await ai.AskAsync(system, text, cancel, onProgress, images).ConfigureAwait(false);
        var d = JsonSerializer.Deserialize<HomeworkDraft>(AiGateway.ExtractJson(raw), Json);
        return d ?? throw new AiException("作业解析为空");
    }

    /// <summary>
    /// 换课消息**涉及哪几天**（第一步）。跨周换课只喂"今天/明天"是不够的，
    /// 所以先花一次调用把日期问出来，再按这些日期去取课表。
    /// </summary>
    public async Task<IReadOnlyList<DateOnly>> ResolveExchangeDatesAsync(string text,
        CancellationToken cancel = default, Action<string>? onProgress = null)
    {
        var today = DateTime.Now.Date;
        // 周一为一周之始：把"本周一/下周一"直接写进提示词，模型算跨周日期才不会错
        //（实测不写的话"下周三"会被算成本周三、"这周五"会算成上周五）。
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var nextMonday = monday.AddDays(7);
        var system = $$"""
            判断这条班级消息里提到的"换课/课程变动"涉及哪几天。
            只输出 JSON：{"is_exchange":true/false,"dates":["yyyy-MM-dd", ...]}

            【时间基准】今天是 {{today:yyyy-MM-dd}}（{{today:dddd}}）；
            本周一 = {{monday:yyyy-MM-dd}}，下周一 = {{nextMonday:yyyy-MM-dd}}。

            规则：
            - 把"明天/后天/这周五/下周三/下周第一节"这类说法换算成**具体日期**；
            - **"下X"一律按"下周一 + 偏移"算**（下周三 = 下周一 + 2 天）；
            - "这X"指最近的那个 X，但**所有日期必须在今天或之后**：
              如果最近的那个已经过去，就顺延一周（今天周日说"这周五"→ 指下周五）；
            - 只要消息涉及**某节课的科目/内容变动**（"改成/换成/改上/不上了/周测改成…"）也算 is_exchange；
            - 最多给 4 个日期；完全无关就给空数组。
            """;
        try
        {
            var raw = await ai.AskAsync(system, text, cancel, onProgress).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(AiGateway.ExtractJson(raw));
            if (!doc.RootElement.TryGetProperty("dates", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return [];
            var dates = new List<DateOnly>();
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String
                    && DateOnly.TryParse(item.GetString(), out var d)
                    && !dates.Contains(d))
                    dates.Add(d);
            }
            return dates.Take(4).ToList();
        }
        catch (Exception)
        {
            return [];   // 失败就让上层退回"今天/明天"
        }
    }

    /// <param name="timetables">
    /// 今天/明天的课表（"第1节语文、第2节数学…"）。**必须给**：
    /// 实测"明天的那个周测改成语文了"这类消息根本没说第几节，
    /// 不给课表 AI 只能瞎猜（或干脆给不出节次，整条转人工）。
    /// </param>
    public async Task<ExchangeDraft> AnalyzeExchangeAsync(string text, string? timetables = null,
        CancellationToken cancel = default, Action<string>? onProgress = null)
    {
        var system = $$"""
            你解析老师的换课消息。换课类型 kind：Swap=两节对调(同天)/Replace=某节改为另一科目/CrossDay=涉及不同日期。
            只输出 JSON：{"is_exchange":true/false,"kind":"Swap/Replace/CrossDay","from":{"date":"yyyy-MM-dd","period":某日第几节,"subject":"原科目，可空"},"to":{"date":"yyyy-MM-dd","period":n}或null,"new_subject":"Replace目标科目，否则空字符串","confidence":0-1}
            今天是 {{DateTime.Now:yyyy-MM-dd}}。"明天第三节和今天第五节换"这类要换算成具体日期。

            【课表】没写第几节时**必须**靠它推断，不要留空：
            {{timetables ?? "（取不到课表）"}}
            推断规则：
            - "周测/测验/考试改成X"：先找课表里该科目原本的那一节；找不到就用当天最后一节正课。
            - "某科老师要讲课/某科改成X"：取课表里那一科的节次。
            - 实在无法确定时才留空 period（上层会转人工）。
            """;
        var raw = await ai.AskAsync(system, text, cancel, onProgress).ConfigureAwait(false);
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
public sealed record NoticeDraft(
    [property: JsonPropertyName("is_notice")] bool IsNotice,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("body")] string Body);
public sealed record HomeworkDraft(
    [property: JsonPropertyName("is_homework")] bool IsHomework,
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("items")] List<string> Items,
    [property: JsonPropertyName("due")] string Due,
    [property: JsonPropertyName("confidence")] double Confidence);
public sealed record SlotDraft(
    [property: JsonPropertyName("date")] string? Date,
    [property: JsonPropertyName("period")] int? Period,
    [property: JsonPropertyName("subject")] string? Subject);
public sealed record ExchangeDraft(
    [property: JsonPropertyName("is_exchange")] bool IsExchange,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("from")] SlotDraft? From,
    [property: JsonPropertyName("to")] SlotDraft? To,
    [property: JsonPropertyName("new_subject")] string NewSubject,
    [property: JsonPropertyName("confidence")] double Confidence);
