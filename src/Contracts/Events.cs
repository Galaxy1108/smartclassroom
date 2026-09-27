// App 与 ClassIsland 插件之间的共享契约。
// 序列化：System.Text.Json，命名策略 camelCase，时间统一 DateTimeOffset (ISO 8601)。

namespace SmartClassroom.Contracts;

/// <summary>召唤事件：老师在群里叫某人过去。</summary>
public sealed record SummonEvent
{
    public required string EventId { get; init; }
    public required DateTimeOffset ReceivedAt { get; init; }

    /// <summary>被叫的人（群名片/昵称原文）。</summary>
    public required string Target { get; init; }

    /// <summary>是否要求立刻过去（出现"现在/立刻/马上/立即"等词）。</summary>
    public required bool Urgent { get; init; }

    /// <summary>叫人的老师姓名（AI 结合当前课程/老师名单解析；未知则空）。</summary>
    public string? Teacher { get; init; }

    /// <summary>该老师/当前课程的科目（用于通知里写清是哪一科）。</summary>
    public string? Subject { get; init; }

    /// <summary>通知里显示的老师称呼：解析出的老师 → 发送者 → "老师"。</summary>
    public string TeacherLabel =>
        !string.IsNullOrWhiteSpace(Teacher) ? Teacher!
        : !string.IsNullOrWhiteSpace(Sender.TeacherName) ? Sender.TeacherName!
        : "老师";

    /// <summary>AI 生成的通知标题（大字）。为空则用本地模板。</summary>
    public string? AiTitle { get; init; }

    /// <summary>AI 生成的通知正文（小字）。为空则用本地模板。</summary>
    public string? AiBody { get; init; }

    /// <summary>
    /// 通知的遮罩大字。发送者与被叫的人**同名**时不写两遍
    /// （实测出现过"张老师请（现在）张老师过去"这种蠢话）。
    /// </summary>
    public string MaskText
        => !string.IsNullOrWhiteSpace(AiTitle) ? AiTitle!.Trim()
        : TeacherLabel == Target
            ? $"{(Urgent ? "现在" : "")}请{Target}过去"
            : $"{TeacherLabel}请{(Urgent ? "（现在）" : "")}{Target}过去";

    /// <summary>通知的正文（谁、哪一科、原话）。</summary>
    public string OverlayText
        => !string.IsNullOrWhiteSpace(AiBody) ? AiBody!.Trim()
        : TeacherLabel == Target
            ? $"{Target}{SubjectLabel}：{Reason}"
            : $"{TeacherLabel}{SubjectLabel}：{Reason}";

    /// <summary>通知里显示的科目后缀（如"（数学）"；未知则空）。</summary>
    public string SubjectLabel
    {
        get
        {
            var subject = !string.IsNullOrWhiteSpace(Subject) ? Subject
                : Sender.Subject;
            return string.IsNullOrWhiteSpace(subject) ? "" : $"（{subject}）";
        }
    }

    /// <summary>原消息摘录（去图去 at 后的纯文本）。</summary>
    public required string Reason { get; init; }

    /// <summary>发送者（老师）解析结果。</summary>
    public required SenderInfo Sender { get; init; }

    public required MessageRef Source { get; init; }

    /// <summary>AI 置信度 0~1；低于阈值走保守排队 + 待确认。</summary>
    public double Confidence { get; init; } = 1.0;
}

/// <summary>作业条目：某科目某日的一批作业。</summary>
public sealed record HomeworkItem
{
    public required string HomeworkId { get; init; }
    public required string Subject { get; init; }
    public required DateOnly Date { get; init; }
    public required List<string> Items { get; init; }
    public string? Due { get; init; }
    public required SenderInfo Sender { get; init; }
    public required MessageRef Source { get; init; }
}

/// <summary>换课请求类型。</summary>
public enum ExchangeKind
{
    /// <summary>调换：A 节 ↔ B 节互换（同天）。</summary>
    Swap,
    /// <summary>替换：某节改为另一科目。</summary>
    Replace,
    /// <summary>跨天：涉及不同日期的节次变动。</summary>
    CrossDay
}

/// <summary>换课请求中的一端（某日某节）。</summary>
public sealed record ClassSlot
{
    public required DateOnly Date { get; init; }

    /// <summary>当天第几节（从 1 开始，对应课表节次序号）。</summary>
    public required int PeriodIndex { get; init; }

    public string? Subject { get; init; }
}

/// <summary>结构化换课请求（AI 输出 + 插件输入）。</summary>
public sealed record ExchangeRequest
{
    public required string RequestId { get; init; }
    public required ExchangeKind Kind { get; init; }
    public required ClassSlot From { get; init; }
    public ClassSlot? To { get; init; }

    /// <summary>Replace 时的目标科目；Swap/CrossDay 时一般为空。</summary>
    public string? NewSubject { get; init; }

    public required string RawText { get; init; }
    public required SenderInfo Sender { get; init; }
    public required MessageRef Source { get; init; }
    public double Confidence { get; init; } = 1.0;
}

/// <summary>插件对换课请求的裁决回执。</summary>
public sealed record ExchangeVerdict
{
    public required string RequestId { get; init; }
    public required bool Legal { get; init; }

    /// <summary>合法时：已落课说明；非法时：原因 + 期望的手动操作。</summary>
    public required string Message { get; init; }

    public DateTimeOffset DecidedAt { get; init; } = DateTimeOffset.Now;
}

/// <summary>归档/课件文件记录。</summary>
public sealed record CoursewareFile
{
    public required string FileId { get; init; }
    public required string FileName { get; init; }
    public required long Size { get; init; }
    public required SenderInfo Sender { get; init; }
    public required MessageRef Source { get; init; }

    /// <summary>本地归档绝对路径（下载完成后填写）。</summary>
    public string? LocalPath { get; init; }

    /// <summary>
    /// 科目（教师映射命中时）。为空 = 认不出科目；
    /// 上课弹窗要求"当科"，此时只能退化为按老师身份匹配。
    /// </summary>
    public string? Subject { get; init; }

    public required DateOnly ClassDate { get; init; }

    /// <summary>归档时间（时间轴排序用）。老的记录可能没有。</summary>
    public DateTimeOffset? ArchivedAt { get; init; }
}

/// <summary>QQ 发送者信息（教师映射命中结果）。</summary>
public sealed record SenderInfo
{
    public required long UserId { get; init; }
    public string? Card { get; init; }
    public string? Nickname { get; init; }

    /// <summary>映射命中的老师姓名；未命中为 null（未知发送者）。</summary>
    public string? TeacherName { get; init; }

    /// <summary>映射命中的科目；未命中为 null。</summary>
    public string? Subject { get; init; }
}

/// <summary>QQ 消息定位。</summary>
public sealed record MessageRef
{
    public required long GroupId { get; init; }
    public required long MessageId { get; init; }
}
