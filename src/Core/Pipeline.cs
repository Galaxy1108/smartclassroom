using SmartClassroom.Contracts;
using SmartClassroom.Core.AI;
using SmartClassroom.Core.QQ;

namespace SmartClassroom.Core;

/// <summary>
/// 运行时总装：QQ 事件 → 教师映射 → 规则分流 → AI → 调度门/存储/插件/归档/课件。
/// AI 失败或类型未知时不丢消息：进 <see cref="PendingStore"/>，由用户在事件页重试或人工补录。
/// 所有外部依赖经构造注入。
/// </summary>
public sealed class PipelineService(
    TeacherMap teachers,
    AiAnalyzer ai,
    ScheduleGate gate,
    PluginLink plugin,
    OneBotClient oneBot,
    FileArchive archive,
    CoursewareService courseware,
    HomeworkStore homework,
    ActivityFeed feed,
    PendingStore pending,
    FeatureFlags flags)
{
    private readonly HashSet<string> _disabledNotified = [];

    private ScheduleGate.SendFunc Send => plugin.NotifyAsync;

    public PendingStore Pending => pending;

    public event Action<IReadOnlyList<CoursewareFile>>? CoursewareSuggested;

    /// <summary>群文本消息入口（只处理配置群，群过滤由调用方完成）。</summary>
    public async Task OnGroupMessageAsync(GroupMessageEvent ev, CancellationToken cancel = default)
    {
        var sender = teachers.ToSender(ev.UserId, ev.Card, ev.Nickname);
        var kind = RuleEngine.ClassifyLocal(ev.Text);
        if (kind == RuleEngine.Kind.None)
            return;

        if (kind.HasFlag(RuleEngine.Kind.Summon))
        {
            if (flags.Summon)
                await HandleSummonAsync(ev, sender, cancel).ConfigureAwait(false);
            else
                NoteDisabled("summon", "召唤通知");
        }
        if (kind.HasFlag(RuleEngine.Kind.Homework))
        {
            if (flags.Homework)
                await HandleHomeworkAsync(ev, sender, cancel).ConfigureAwait(false);
            else
                NoteDisabled("homework", "作业自动录入");
        }
        if (kind.HasFlag(RuleEngine.Kind.Exchange))
        {
            if (flags.Exchange)
                await HandleExchangeAsync(ev, sender, cancel).ConfigureAwait(false);
            else
                NoteDisabled("exchange", "换课自动处理");
        }
    }

    /// <summary>
    /// 功能未开启时，每类只提示一次（避免每条命中关键词的消息都刷屏）。
    /// 这样"老师说了话但没反应"在事件页里能直接看出原因。
    /// </summary>
    private void NoteDisabled(string kind, string featureName)
    {
        if (_disabledNotified.Add(kind))
            feed.Append(kind, $"「{featureName}」未启用，已跳过", "可在 设置 → 功能开关 中开启",
                ActivitySeverity.Warning);
    }

    /// <summary>群文件上传入口。</summary>
    public async Task OnGroupUploadAsync(GroupUploadEvent ev, CancellationToken cancel = default)
    {
        if (!flags.FileArchive)
        {
            NoteDisabled("file", "群文件自动归档");
            return;
        }
        var sender = teachers.ToSender(ev.UserId, null, null);
        ArchiveOutcome outcome;
        try
        {
            outcome = await archive.HandleAsync(ev, sender, async (e, ct) =>
            {
                try { return (await oneBot.GetGroupFileUrlAsync(e.GroupId, e.File.Id, e.File.Busid, ct))?.Url; }
                catch { return null; }
            }, cancel).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            feed.Append("file", $"文件归档失败：{ev.File.Name}", ex.Message, ActivitySeverity.Error);
            return;
        }
        switch (outcome.Result)
        {
            case ArchiveResult.Downloaded when outcome.LocalPath is not null:
                courseware.Register(new CoursewareFile
                {
                    FileId = ev.File.Id,
                    FileName = ev.File.Name,
                    Size = ev.File.Size,
                    Sender = sender,
                    Source = new MessageRef { GroupId = ev.GroupId, MessageId = 0 },
                    LocalPath = outcome.LocalPath,
                    Subject = sender.Subject,
                    ClassDate = DateOnly.FromDateTime(DateTime.Now)
                });
                feed.Append("file", $"已归档：{ev.File.Name}", $"来自{sender.TeacherName ?? "未知发送者"}",
                    ActivitySeverity.Success);
                break;
            case ArchiveResult.PendingConfirm:
                feed.Append("file", $"大文件待确认：{ev.File.Name}",
                    $"{ev.File.Size / 1024 / 1024}MB，来自{sender.TeacherName ?? "未知发送者"}",
                    ActivitySeverity.Warning);
                break;
            case ArchiveResult.Failed:
                feed.Append("file", $"文件归档失败：{ev.File.Name}", outcome.Error ?? "",
                    ActivitySeverity.Error);
                break;
        }
    }

    /// <summary>
    /// 上课事件：**当天 + 当科**有课件才弹（没有就完全不弹），App 层订阅弹窗。
    /// 科目缺失时用教师映射里的科目补；都补不出来就不弹——宁可少弹，也不要上数学课弹语文课件。
    /// </summary>
    public void OnClassStarted(DateOnly date, string? subject, string? teacherName, long? teacherQq = null)
    {
        if (!flags.CoursewarePopup)
        {
            NoteDisabled("courseware", "课件弹窗");
            return;
        }
        var known = teacherQq is not null
            ? teachers.Resolve(teacherQq.Value, null, null)
            : teachers.Resolve(0, teacherName, teacherName);
        var effectiveSubject = !string.IsNullOrWhiteSpace(subject) ? subject : known?.Subject;
        if (string.IsNullOrWhiteSpace(effectiveSubject))
        {
            feed.Append("courseware", "本节课没有科目信息，已跳过课件弹窗", "可在教师映射里补上该老师的科目",
                ActivitySeverity.Warning);
            return;
        }
        var effectiveName = teacherName ?? known?.Name;
        var effectiveQq = teacherQq ?? (known is { Qq: > 0 } ? known.Qq : null);
        var files = courseware.QueryForLesson(date, effectiveSubject, effectiveName, effectiveQq);
        if (files.Count == 0)
            return;   // 没有当天该科的课件：不弹
        if (!courseware.TryMarkShown(date, effectiveSubject))
            return;   // 本节课已经弹过
        feed.Append("courseware", $"上课弹窗：{effectiveSubject}", $"当天该科课件 {files.Count} 个",
            ActivitySeverity.Success);
        CoursewareSuggested?.Invoke(files);
    }

    /// <summary>下课/放学事件：flush 排队通知（经原通道发出）。</summary>
    public Task<int> OnClassEndedAsync(CancellationToken cancel = default)
        => gate.FlushAsync(Send, cancel);

    // ================= 待确认处置（事件页的出口） =================

    /// <summary>重新解析：拿原消息再跑一次 AI。成功则移除待确认项。</summary>
    public async Task<string> RetryPendingAsync(string id, CancellationToken cancel = default)
    {
        var item = pending.Get(id);
        if (item is null)
            return "该事项已不存在。";
        pending.BumpRetry(id);
        var ev = ToEvent(item);

        var before = pending.Count;
        switch (item.Kind)
        {
            case "homework":
                await HandleHomeworkAsync(ev, item.Sender, cancel, keepOnFailure: true, resolvePendingId: id).ConfigureAwait(false);
                break;
            case "exchange":
                await HandleExchangeAsync(ev, item.Sender, cancel, keepOnFailure: true, resolvePendingId: id).ConfigureAwait(false);
                break;
            case "summon":
                await HandleSummonAsync(ev, item.Sender, cancel, keepOnFailure: true, resolvePendingId: id).ConfigureAwait(false);
                break;
            default:
                return $"未知类型：{item.Kind}";
        }
        var resolved = pending.Get(id) is null;
        return resolved ? "重新解析成功，已处理。" : "重新解析仍未成功，已保留在待确认列表。";
    }

    /// <summary>忽略：从待确认列表移除，不再处理。</summary>
    public bool IgnorePending(string id)
    {
        var item = pending.Get(id);
        if (item is null || !pending.Remove(id))
            return false;
        feed.Append(item.Kind, $"已忽略：{item.Title}", item.RawText, ActivitySeverity.Warning);
        return true;
    }

    /// <summary>人工补录召唤。</summary>
    public async Task<string> ResolveSummonAsync(string id, string target, bool urgent, CancellationToken cancel = default)
    {
        var item = pending.Get(id);
        if (item is null)
            return "该事项已不存在。";
        if (string.IsNullOrWhiteSpace(target))
            return "请填写被叫的人。";
        var summon = new SummonEvent
        {
            EventId = Guid.NewGuid().ToString(),
            ReceivedAt = DateTimeOffset.Now,
            Target = target.Trim(),
            Urgent = urgent,
            Reason = item.RawText,
            Sender = item.Sender,
            Source = item.Source,
            Confidence = 1.0 // 人工确认
        };
        var decision = await gate.ProcessSummonAsync(summon, Send, cancel).ConfigureAwait(false);
        pending.Remove(id);
        feed.Append("summon", $"人工补录召唤 {summon.Target}（{(urgent ? "立刻" : "排队")}，{Desc(decision)}）",
            item.RawText, ActivitySeverity.Success);
        return "已按人工录入处理。";
    }

    /// <summary>人工补录作业。</summary>
    public string ResolveHomeworkAsync(string id, string subject, IReadOnlyList<string> items, DateOnly date)
    {
        var item = pending.Get(id);
        if (item is null)
            return "该事项已不存在。";
        if (string.IsNullOrWhiteSpace(subject) || items.Count == 0)
            return "请至少填写科目和一条作业内容。";

        var merged = homework.AddOrMerge(new HomeworkItem
        {
            HomeworkId = Guid.NewGuid().ToString(),
            Subject = subject.Trim(),
            Date = date,
            Items = items.Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i.Trim()).ToList(),
            Sender = item.Sender,
            Source = item.Source
        });
        pending.Remove(id);
        feed.Append("homework", $"人工补录作业：{merged.Subject}", string.Join("；", merged.Items),
            ActivitySeverity.Success);
        return "已按人工录入上墙。";
    }

    /// <summary>人工补录换课（仍走插件校验与落课）。</summary>
    public async Task<string> ResolveExchangeAsync(
        string id, ExchangeKind kind, ClassSlot from, ClassSlot? to, string? newSubject,
        CancellationToken cancel = default)
    {
        var item = pending.Get(id);
        if (item is null)
            return "该事项已不存在。";
        if (from.PeriodIndex <= 0)
            return "请填写原节次。";

        var req = new ExchangeRequest
        {
            RequestId = Guid.NewGuid().ToString(),
            Kind = kind,
            From = from,
            To = to,
            NewSubject = newSubject,
            RawText = item.RawText,
            Sender = item.Sender,
            Source = item.Source,
            Confidence = 1.0 // 人工确认，不受 AI 置信度门槛限制
        };
        ExchangeVerdict verdict;
        try { verdict = await plugin.ExchangeAsync(req, cancel).ConfigureAwait(false); }
        catch (Exception ex)
        {
            feed.Append("exchange", "人工换课提交失败", $"{item.RawText}（{ex.Message}）",
                ActivitySeverity.Error);
            return $"提交失败：{ex.Message}";
        }
        pending.Remove(id);
        feed.Append("exchange",
            verdict.Legal ? $"人工换课已执行：{verdict.Message}" : $"人工换课被驳回：{verdict.Message}",
            item.RawText,
            verdict.Legal ? ActivitySeverity.Success : ActivitySeverity.Warning);
        if (!verdict.Legal)
            gate.EnqueueManual("需手动换课", verdict.Message);
        return verdict.Message;
    }

    private static GroupMessageEvent ToEvent(PendingItem item) => new()
    {
        GroupId = item.Source.GroupId,
        UserId = item.Sender.UserId,
        MessageId = item.Source.MessageId,
        RawMessage = item.RawText,
        Text = item.RawText,
        Card = item.Sender.Card,
        Nickname = item.Sender.Nickname
    };

    private PendingItem AddPending(string kind, string title, string rawText, string reason, SenderInfo sender, MessageRef source)
    {
        var item = pending.Add(new PendingItem
        {
            Id = Guid.NewGuid().ToString(),
            Kind = kind,
            Title = title,
            RawText = rawText,
            Reason = reason,
            Sender = sender,
            Source = source,
            CreatedAt = DateTimeOffset.Now
        });
        feed.Append(kind, $"待确认：{title}", $"原因：{reason}", ActivitySeverity.Warning);
        return item;
    }

    // ================= 各类消息处理 =================

    private async Task HandleSummonAsync(
        GroupMessageEvent ev, SenderInfo sender, CancellationToken cancel,
        bool keepOnFailure = false, string? resolvePendingId = null)
    {
        SummonDraft d;
        try { d = await ai.AnalyzeSummonAsync(ev.Text, cancel).ConfigureAwait(false); }
        catch (AiException ex)
        {
            if (!keepOnFailure)
                AddPending("summon", "召唤解析失败，需人工确认被叫的人", ev.Text, ex.Message, sender,
                    new MessageRef { GroupId = ev.GroupId, MessageId = ev.MessageId });
            // 保守降级：仍按紧急词排队/直发，不阻塞（通知里标"有人"，人工补录可纠正）
            var urgent = SummonGate.IsUrgent(ev.Text);
            var fallback = new SummonEvent
            {
                EventId = Guid.NewGuid().ToString(),
                ReceivedAt = DateTimeOffset.Now,
                Target = "有人",
                Urgent = urgent,
                Reason = ev.Text,
                Sender = sender,
                Source = new MessageRef { GroupId = ev.GroupId, MessageId = ev.MessageId },
                Confidence = 0.3
            };
            var decision = await gate.ProcessSummonAsync(fallback, Send, cancel).ConfigureAwait(false);
            feed.Append("summon", $"疑似召唤（AI 失败，已{Desc(decision)}）", ev.Text,
                ActivitySeverity.Warning);
            return;
        }
        if (!d.IsSummon || string.IsNullOrWhiteSpace(d.Target))
        {
            if (resolvePendingId is not null)
                pending.Remove(resolvePendingId);
            feed.Append("summon", "AI 判定非召唤，已忽略", ev.Text);
            return;
        }
        var summon = new SummonEvent
        {
            EventId = Guid.NewGuid().ToString(),
            ReceivedAt = DateTimeOffset.Now,
            Target = d.Target,
            Urgent = d.Urgent,
            Reason = ev.Text,
            Sender = sender,
            Source = new MessageRef { GroupId = ev.GroupId, MessageId = ev.MessageId },
            Confidence = d.Confidence
        };
        var result = await gate.ProcessSummonAsync(summon, Send, cancel).ConfigureAwait(false);
        if (resolvePendingId is not null)
            pending.Remove(resolvePendingId);
        feed.Append("summon", $"召唤{d.Target}（{(d.Urgent ? "立刻" : "排队")}，{Desc(result)}）", ev.Text,
            ActivitySeverity.Success);
    }

    private async Task HandleHomeworkAsync(
        GroupMessageEvent ev, SenderInfo sender, CancellationToken cancel,
        bool keepOnFailure = false, string? resolvePendingId = null)
    {
        HomeworkDraft d;
        try { d = await ai.AnalyzeHomeworkAsync(ev.Text, sender.Subject, cancel).ConfigureAwait(false); }
        catch (AiException ex)
        {
            if (!keepOnFailure)
                AddPending("homework", "作业解析失败，需人工补录", ev.Text, ex.Message, sender,
                    new MessageRef { GroupId = ev.GroupId, MessageId = ev.MessageId });
            return;
        }
        if (!d.IsHomework)
            return;
        // 日期纠偏：模型给的日期离今天太远时按今天算，避免作业挂到错误的一天
        var today = DateOnly.FromDateTime(DateTime.Now);
        var date = RuleEngine.CoerceHomeworkDate(d.Date, today);
        if (DateOnly.TryParse(d.Date, out var modelDate) && modelDate != date)
            feed.Append("homework", $"作业日期已纠偏：{modelDate:yyyy-MM-dd} → {date:yyyy-MM-dd}",
                "模型给出的日期与今天相差过大，已按今天处理", ActivitySeverity.Warning);
        homework.AddOrMerge(new HomeworkItem
        {
            HomeworkId = Guid.NewGuid().ToString(),
            Subject = d.Subject.Length > 0 ? d.Subject : (sender.Subject ?? "未知科目"),
            Date = date,
            Items = d.Items,
            Due = d.Due,
            Sender = sender,
            Source = new MessageRef { GroupId = ev.GroupId, MessageId = ev.MessageId }
        });
        if (resolvePendingId is not null)
            pending.Remove(resolvePendingId);
        feed.Append("homework", $"作业已上墙：{d.Subject}", string.Join("；", d.Items),
            ActivitySeverity.Success);
    }

    private async Task HandleExchangeAsync(
        GroupMessageEvent ev, SenderInfo sender, CancellationToken cancel,
        bool keepOnFailure = false, string? resolvePendingId = null)
    {
        ExchangeDraft d;
        try { d = await ai.AnalyzeExchangeAsync(ev.Text, cancel).ConfigureAwait(false); }
        catch (AiException ex)
        {
            if (!keepOnFailure)
                AddPending("exchange", "换课解析失败，需人工补录", ev.Text, ex.Message, sender,
                    new MessageRef { GroupId = ev.GroupId, MessageId = ev.MessageId });
            return;
        }
        if (!d.IsExchange)
        {
            if (resolvePendingId is not null)
                pending.Remove(resolvePendingId);
            return;
        }
        if (!Enum.TryParse<ExchangeKind>(d.Kind, ignoreCase: true, out var kind))
        {
            if (!keepOnFailure)
                AddPending("exchange", $"换课类型未知（{d.Kind}），需人工补录", ev.Text, "AI 返回的类型无法识别", sender,
                    new MessageRef { GroupId = ev.GroupId, MessageId = ev.MessageId });
            return;
        }
        var req = new ExchangeRequest
        {
            RequestId = Guid.NewGuid().ToString(),
            Kind = kind,
            From = new ClassSlot { Date = ParseDate(d.From.Date), PeriodIndex = d.From.Period },
            To = d.To is null ? null : new ClassSlot { Date = ParseDate(d.To.Date), PeriodIndex = d.To.Period },
            NewSubject = d.NewSubject,
            RawText = ev.Text,
            Sender = sender,
            Source = new MessageRef { GroupId = ev.GroupId, MessageId = ev.MessageId },
            Confidence = d.Confidence
        };
        ExchangeVerdict verdict;
        try { verdict = await plugin.ExchangeAsync(req, cancel).ConfigureAwait(false); }
        catch (Exception ex)
        {
            if (!keepOnFailure)
                AddPending("exchange", "换课请求发送失败，需人工确认", ev.Text, ex.Message, sender,
                    new MessageRef { GroupId = ev.GroupId, MessageId = ev.MessageId });
            return;
        }
        if (resolvePendingId is not null)
            pending.Remove(resolvePendingId);
        feed.Append("exchange",
            verdict.Legal ? $"换课已执行：{verdict.Message}" : $"换课非法，已排队下课通知手动：{verdict.Message}",
            ev.Text, verdict.Legal ? ActivitySeverity.Success : ActivitySeverity.Warning);
        if (!verdict.Legal)
            gate.EnqueueManual("需手动换课", verdict.Message);
    }

    private static DateOnly ParseDate(string s)
        => DateOnly.TryParse(s, out var d) ? d : DateOnly.FromDateTime(DateTime.Now);

    private static string Desc(GateDecision d) => d == GateDecision.SentNow ? "立刻发送" : "已排队";
}
