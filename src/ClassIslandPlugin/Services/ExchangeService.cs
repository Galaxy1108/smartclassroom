using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared;
using ClassIsland.Shared.Models.Profile;
using SmartClassroom.Contracts;

namespace SmartClassroom.ClassIslandPlugin.Services;

/// <summary>
/// 换课执行服务（跑在 ClassIsland 进程内，可直接用档案与课表服务）。
/// 覆盖语义（据 ClassIsland 源码 ProfileService/LessonsService）：
/// 任何一次性改课都必须走临时层——CreateTempClassPlan 深拷贝源课表，
/// 挂到 Profile.OrderedSchedules[date] 做按日期覆盖，原周循环课表不受影响。
/// 同日与跨天统一走此路径；目标日期已有临时层时直接复用，不重复创建。
/// </summary>
public class ExchangeService
{
    private IProfileService Profiles => IAppHost.GetService<IProfileService>();
    private ILessonsService Lessons => IAppHost.GetService<ILessonsService>();

    public ExchangeVerdict Handle(ExchangeRequest req)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        try
        {
            // 1) 按日期解析课表（含临时层覆盖）。
            var plans = new Dictionary<DateOnly, (ClassPlan Plan, Guid Id)>();
            foreach (var date in new[] { req.From.Date }.Concat(req.To is null ? [] : [req.To.Date]).Distinct())
            {
                var plan = Lessons.GetClassPlanByDate(date.ToDateTime(TimeOnly.MinValue), out var guid);
                if (plan is null || guid is null)
                    return Fail(req, $"{date:MM-dd} 当天没有课表，无法自动换课，请手动确认。");
                plans[date] = (plan, guid.Value);
            }

            // 2) 建快照 + 校验。
            var days = plans.ToDictionary(kv => kv.Key, kv => BuildSnapshot(kv.Key, kv.Value.Plan));
            Func<int, bool>? isFuture = null;
            if (req.From.Date == today)
            {
                var ends = PeriodEndTimes(plans[req.From.Date].Plan);
                if (ends is not null)
                    isFuture = idx => ends.TryGetValue(idx, out var end) && DateTime.Now.TimeOfDay < end;
            }
            var verdict = ExchangeValidator.Validate(req, days, today, isPeriodFuture: isFuture);
            if (!verdict.Legal)
                return verdict;

            // 3) 落课：确保临时层后编辑。
            if (req.Kind == ExchangeKind.Swap && req.To is not null)
            {
                var fromTemp = EnsureTemp(plans[req.From.Date], req.From.Date);
                var toTemp = EnsureTemp(plans[req.To.Date], req.To.Date);
                if (fromTemp is null || toTemp is null)
                    return Fail(req, "创建临时层课表失败，请手动确认。");
                // 同日 Swap：同一临时层内对调；跨天：在各自日期临时层内互换科目。
                var fromSide = GetClass(fromTemp, req.From.PeriodIndex);
                var toSide = GetClass(toTemp, req.To.PeriodIndex);
                if (fromSide is null || toSide is null)
                    return Fail(req, "课表节次定位失败，请手动确认。");
                (fromSide.SubjectId, toSide.SubjectId) = (toSide.SubjectId, fromSide.SubjectId);
            }
            else if (req.Kind == ExchangeKind.Replace && req.NewSubject is not null)
            {
                var temp = EnsureTemp(plans[req.From.Date], req.From.Date);
                if (temp is null)
                    return Fail(req, "创建临时层课表失败，请手动确认。");
                var target = GetClass(temp, req.From.PeriodIndex);
                if (target is null)
                    return Fail(req, "课表节次定位失败，请手动确认。");
                var subject = Profiles.Profile.Subjects.FirstOrDefault(kv => kv.Value.Name == req.NewSubject);
                if (subject.Value is null)
                    return Fail(req, $"课表中没有「{req.NewSubject}」科目，请手动确认。");
                target.SubjectId = subject.Key;
            }
            else
            {
                return Fail(req, "该换课类型暂不支持自动执行，已转人工。");
            }

            Profiles.SaveProfile();
            return verdict with { Message = verdict.Message + "（已写入临时层课表）" };
        }
        catch (Exception ex)
        {
            return Fail(req, $"换课执行异常，已转人工：{ex.GetType().Name}");
        }
    }

    /// <summary>
    /// 确保某日期有可编辑的临时层：已是覆盖层直接用；已有临时层复用；否则创建。
    /// </summary>
    private ClassPlan? EnsureTemp((ClassPlan Plan, Guid Id) resolved, DateOnly date)
    {
        var profile = Profiles.Profile;
        if (resolved.Plan.IsOverlay)
            return resolved.Plan;
        var key = date.ToDateTime(TimeOnly.MinValue).Date;
        if (profile.OrderedSchedules.TryGetValue(key, out var ordered)
            && profile.ClassPlans.TryGetValue(ordered.ClassPlanId, out var existing)
            && existing.IsOverlay)
            return existing;
        var tempId = Profiles.CreateTempClassPlan(resolved.Id, enableDateTime: key);
        if (tempId is not null && profile.ClassPlans.TryGetValue(tempId.Value, out var created))
            return created;
        // 并发或已存在时回读。
        return profile.OrderedSchedules.TryGetValue(key, out var retry)
            && profile.ClassPlans.TryGetValue(retry.ClassPlanId, out var plan) ? plan : null;
    }

    private static int CountPeriods(ClassPlan plan)
        => plan.TimeLayout?.Layouts.Count(l => l.TimeType == 0) ?? plan.Classes.Count;

    private static ClassInfo? GetClass(ClassPlan plan, int periodIndex)
    {
        var count = CountPeriods(plan);
        if (periodIndex < 1 || periodIndex > count || periodIndex > plan.Classes.Count)
            return null;
        return plan.Classes[periodIndex - 1];
    }

    private ClassDaySnapshot BuildSnapshot(DateOnly date, ClassPlan plan)
    {
        var subjects = Profiles.Profile.Subjects;
        var periods = plan.Classes
            .Take(CountPeriods(plan))
            .Select((c, i) => new PeriodSlot
            {
                Index = i + 1,
                Subject = subjects.TryGetValue(c.SubjectId, out var s) ? s.Name : "未知科目"
            }).ToList();
        return new ClassDaySnapshot { Date = date, Periods = periods };
    }

    private static Dictionary<int, TimeSpan>? PeriodEndTimes(ClassPlan plan)
    {
        try
        {
            var layouts = plan.TimeLayout?.Layouts.Where(l => l.TimeType == 0).ToList();
            if (layouts is null || layouts.Count == 0)
                return null;
            return layouts.Select((l, i) => (i: i + 1, l.EndTime))
                .ToDictionary(x => x.i, x => x.EndTime);
        }
        catch
        {
            return null;
        }
    }

    private static ExchangeVerdict Fail(ExchangeRequest req, string msg) => new()
    {
        RequestId = req.RequestId,
        Legal = false,
        Message = msg
    };
}
