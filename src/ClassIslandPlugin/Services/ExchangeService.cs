using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared;
using ClassIsland.Shared.Models.Profile;
using SmartClassroom.Contracts;

namespace SmartClassroom.ClassIslandPlugin.Services;

/// <summary>
/// 换课执行服务（跑在 ClassIsland 进程内，可直接用档案与课表服务）。
/// 流程：按日期取课表 → 建快照 → ExchangeValidator 校验 → 同日 Swap/Replace 落课；
/// 跨天执行暂未启用（supportCrossDayExecution: false），合法解析也转人工。
///
/// 注意：ClassPlan.RefreshClassesList 是 internal，插件不依赖它；
/// 以 Classes 集合顺序对应 TimeType==0 时间点（与主机内部维护的不变量一致）。
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
            var days = new Dictionary<DateOnly, ClassDaySnapshot>();
            foreach (var date in new[] { req.From.Date }.Concat(req.To is null ? [] : [req.To.Date]).Distinct())
            {
                var snap = BuildSnapshot(date);
                if (snap is null)
                    return new ExchangeVerdict
                    {
                        RequestId = req.RequestId,
                        Legal = false,
                        Message = $"{date:MM-dd} 当天没有课表，无法自动换课，请手动确认。"
                    };
                days[date] = snap;
            }

            Func<int, bool>? isFuture = null;
            if (req.From.Date == today)
            {
                var ends = PeriodEndTimes(today);
                if (ends is not null)
                    isFuture = idx => ends.TryGetValue(idx, out var end) && DateTime.Now.TimeOfDay < end;
            }

            var verdict = ExchangeValidator.Validate(req, days, today,
                isPeriodFuture: isFuture, supportCrossDayExecution: false);
            if (!verdict.Legal)
                return verdict;

            // 落课：仅同日 Swap / Replace（PeriodIndex 从 1 开始，对应 Classes 顺序）。
            var plan = Lessons.GetClassPlanByDate(req.From.Date.ToDateTime(TimeOnly.MinValue), out _);
            if (plan is null)
                return Fail(req, "课表解析失败，请手动确认。");
            var count = CountPeriods(plan);

            if (req.Kind == ExchangeKind.Swap && req.To is not null)
            {
                if (!TryClass(plan, req.From.PeriodIndex, count, out var a) ||
                    !TryClass(plan, req.To.PeriodIndex, count, out var b))
                    return Fail(req, "课表节次定位失败，请手动确认。");
                (a.SubjectId, b.SubjectId) = (b.SubjectId, a.SubjectId);
            }
            else if (req.Kind == ExchangeKind.Replace && req.NewSubject is not null)
            {
                if (!TryClass(plan, req.From.PeriodIndex, count, out var target))
                    return Fail(req, "课表节次定位失败，请手动确认。");
                var profile = Profiles.Profile;
                var subject = profile.Subjects.FirstOrDefault(kv => kv.Value.Name == req.NewSubject);
                if (subject.Value is null)
                    return Fail(req, $"课表中没有「{req.NewSubject}」科目，请手动确认。");
                target.SubjectId = subject.Key;
            }
            else
            {
                return Fail(req, "该换课类型暂不支持自动执行，已转人工。");
            }

            Profiles.SaveProfile();
            return verdict with { Message = verdict.Message + "（已写入课表）" };
        }
        catch (Exception ex)
        {
            return Fail(req, $"换课执行异常，已转人工：{ex.GetType().Name}");
        }
    }

    private static int CountPeriods(ClassPlan plan)
        => plan.TimeLayout?.Layouts.Count(l => l.TimeType == 0)
           ?? plan.Classes.Count;

    private static bool TryClass(ClassPlan plan, int periodIndex, int count, out ClassInfo info)
    {
        info = null!;
        if (periodIndex < 1 || periodIndex > count || periodIndex > plan.Classes.Count)
            return false;
        info = plan.Classes[periodIndex - 1];
        return true;
    }

    private ClassDaySnapshot? BuildSnapshot(DateOnly date)
    {
        var plan = Lessons.GetClassPlanByDate(date.ToDateTime(TimeOnly.MinValue), out _);
        if (plan is null)
            return null;
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

    /// <summary>当日各节次结束时间（解析失败返回 null，调用方跳过"已上过"检查）。</summary>
    private Dictionary<int, TimeSpan>? PeriodEndTimes(DateOnly date)
    {
        try
        {
            var plan = Lessons.GetClassPlanByDate(date.ToDateTime(TimeOnly.MinValue), out _);
            var layouts = plan?.TimeLayout?.Layouts.Where(l => l.TimeType == 0).ToList();
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
