

namespace SmartClassroom.Contracts;

/// <summary>某日课表快照（插件从 ClassIsland 档案读取后传入，供纯逻辑校验）。</summary>
public sealed record ClassDaySnapshot
{
    public required DateOnly Date { get; init; }
    public required List<PeriodSlot> Periods { get; init; }
}

public sealed record PeriodSlot
{
    public required int Index { get; init; }
    public required string Subject { get; init; }
}

/// <summary>
/// 换课合法性校验（纯逻辑，可全单测；插件端用真实课表快照调用）。
/// 保守原则：置信度不足、目标不存在、时间已过、科目未知 → 非法，转人工。
/// </summary>
public static class ExchangeValidator
{
    public static ExchangeVerdict Validate(
        ExchangeRequest req,
        IReadOnlyDictionary<DateOnly, ClassDaySnapshot> days,
        DateOnly today,
        double minConfidence = 0.5,
        Func<int, bool>? isPeriodFuture = null)
    {
        ExchangeVerdict No(string msg) => new()
        {
            RequestId = req.RequestId, Legal = false, Message = msg
        };

        if (req.Confidence < minConfidence)
            return No($"AI 置信度 {req.Confidence:F2} 过低，已转人工确认，请在 ClassIsland 中手动换课。");
        if (req.From.Date < today)
            return No("原课程日期已过去，无法换课，请手动确认。");
        if (!days.TryGetValue(req.From.Date, out var fromDay))
            return No($"{req.From.Date:MM-dd} 当天没有课表，无法自动换课，请手动确认。");

        var from = fromDay.Periods.FirstOrDefault(p => p.Index == req.From.PeriodIndex);
        if (from is null)
            return No($"{req.From.Date:MM-dd} 第{req.From.PeriodIndex}节不存在，无法换课，请手动确认。");

        switch (req.Kind)
        {
            case ExchangeKind.Swap:
                if (req.To is null)
                    return No("调换缺少目标节次，请手动确认。");
                if (req.To.Date != req.From.Date)
                    return No("跨天调换请走换课流程描述，当前请求已转人工。");
                var swapTarget = fromDay.Periods.FirstOrDefault(p => p.Index == req.To.PeriodIndex);
                if (swapTarget is null)
                    return No($"{req.To.Date:MM-dd} 第{req.To.PeriodIndex}节不存在，请手动确认。");
                if (req.From.Date == today && isPeriodFuture is not null && !isPeriodFuture(req.From.PeriodIndex))
                    return No("要调换的课程已上过，请手动确认。");
                return Yes(req, $"已将{req.From.Date:MM-dd}第{req.From.PeriodIndex}节（{from.Subject}）与第{req.To.PeriodIndex}节（{swapTarget.Subject}）对调。");

            case ExchangeKind.Replace:
                if (string.IsNullOrWhiteSpace(req.NewSubject))
                    return No("替换缺少目标科目，请手动确认。");
                return Yes(req, $"已将{req.From.Date:MM-dd}第{req.From.PeriodIndex}节（{from.Subject}）替换为{req.NewSubject}。");

            case ExchangeKind.CrossDay:
                if (req.To is null)
                    return No("跨天换课缺少目标日期节次，请手动确认。");
                if (req.To.Date < today)
                    return No("跨天换课的目标日期已过去，请手动确认。");
                if (!days.TryGetValue(req.To.Date, out var toDay))
                    return No($"{req.To.Date:MM-dd} 当天没有课表，跨天换课已转人工。");
                if (toDay.Periods.All(p => p.Index != req.To.PeriodIndex))
                    return No($"{req.To.Date:MM-dd} 第{req.To.PeriodIndex}节不存在，请手动确认。");
                // 执行层在涉及的两个日期各建临时层后互换科目，原周循环课表不受影响。
                return Yes(req, $"已通过临时层课表处理跨天换课：{req.From.Date:MM-dd}第{req.From.PeriodIndex}节 ↔ {req.To.Date:MM-dd}第{req.To.PeriodIndex}节。");

            default:
                return No("未知换课类型，已转人工。");
        }
    }

    private static ExchangeVerdict Yes(ExchangeRequest req, string msg) => new()
    {
        RequestId = req.RequestId, Legal = true, Message = msg
    };
}
