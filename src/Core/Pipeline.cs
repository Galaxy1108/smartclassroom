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
        var who = sender.Card ?? sender.Nickname ?? $"QQ{sender.UserId}";
        // 进行中的条目：用户要的是"实时看到处理到哪一步"，而不是只在结束后看到结果
        var id = feed.Begin("qq", "正在处理消息", $"{who}：{Trim(ev.Text)}");
        try
        {
            feed.Update(id, "正在处理消息", $"{who}：{Trim(ev.Text)} · 本地分流…");
            var kind = await ClassifyAsync(ev, sender, cancel, id).ConfigureAwait(false);
            if (kind == RuleEngine.Kind.None)
            {
                feed.Complete(id, "已忽略（与三个功能都无关）", Trim(ev.Text), ActivitySeverity.Info);
                return;
            }
            feed.Update(id, $"正在处理：{FeatureName(kind)}", $"{who}：{Trim(ev.Text)}");
            if (!SenderAllowed(sender, ev, kind))
            {
                feed.Complete(id, "已忽略（发送者不在老师名单里）", Trim(ev.Text), ActivitySeverity.Warning);
                return;
            }
            await DispatchGroupAsync(ev, sender, kind, cancel).ConfigureAwait(false);
            feed.Complete(id, $"已处理：{FeatureName(kind)}", Trim(ev.Text));
        }
        catch (Exception ex)
        {
            feed.Complete(id, "处理失败", ex.Message, ActivitySeverity.Error);
            throw;
        }
    }

    /// <summary>按判定结果分发到对应处理流程（群消息）。</summary>
    private async Task DispatchGroupAsync(GroupMessageEvent ev, SenderInfo sender,
        RuleEngine.Kind kind, CancellationToken cancel)
    {
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

    private static string FeatureName(RuleEngine.Kind kind)
        => kind.HasFlag(RuleEngine.Kind.Exchange) ? "换课"
           : kind.HasFlag(RuleEngine.Kind.Homework) ? "作业"
           : kind.HasFlag(RuleEngine.Kind.Summon) ? "召唤"
           : "未知";

    private static string Trim(string text)
        => text.Length <= 40 ? text : text[..40] + "…";

    /// <summary>
    /// 私聊入口：老师私聊也可能发"来一下"或作业，所以走同一套判定。
    /// 只认老师名单里的人 —— 陌生人私聊一律忽略（不然谁发都触发）。
    /// </summary>
    public async Task OnPrivateMessageAsync(PrivateMessageEvent ev, CancellationToken cancel = default)
    {
        if (teachers.Count > 0 && !teachers.IsKnown(ev.UserId))
        {
            // 把 QQ 号写出来，用户可以直接抄进老师映射 —— 否则只会觉得"我发了怎么没反应"
            feed.Append("private", $"忽略陌生人私聊 {ev.UserId}",
                $"它不在老师映射里。要处理它就在 设置 → 老师映射 里加上 QQ {ev.UserId}，"
                + "或关掉「只处理老师名单里的消息」",
                ActivitySeverity.Warning);
            return;
        }
        var sender = teachers.ToSender(ev.UserId, null, ev.Nickname);
        var who = sender.Card ?? sender.Nickname ?? $"QQ{ev.UserId}";
        var id = feed.Begin("qq", "正在处理私聊", $"{who}：{Trim(ev.Text)}");
        var kind = await ClassifyAsync(ev, sender, cancel, id).ConfigureAwait(false);
        if (kind == RuleEngine.Kind.None)
        {
            feed.Complete(id, "已忽略（与三个功能都无关）", Trim(ev.Text), ActivitySeverity.Info);
            return;
        }
        feed.Update(id, $"正在处理：{FeatureName(kind)}", $"{who}：{Trim(ev.Text)}");

        if (kind.HasFlag(RuleEngine.Kind.Summon) && flags.Summon)
            await HandleSummonAsync(ev, sender, cancel).ConfigureAwait(false);
        else if (kind.HasFlag(RuleEngine.Kind.Summon))
            NoteDisabled("summon", "召唤通知");

        if (kind.HasFlag(RuleEngine.Kind.Homework) && flags.Homework)
            await HandleHomeworkAsync(ev, sender, cancel).ConfigureAwait(false);
        else if (kind.HasFlag(RuleEngine.Kind.Homework))
            NoteDisabled("homework", "作业自动录入");

        if (kind.HasFlag(RuleEngine.Kind.Exchange) && flags.Exchange)
            await HandleExchangeAsync(ev, sender, cancel).ConfigureAwait(false);
        else if (kind.HasFlag(RuleEngine.Kind.Exchange))
            NoteDisabled("exchange", "换课自动处理");
    }

    /// <summary>
    /// 判断这条消息该走哪条流程。
    ///
    /// **不再用关键词把消息挡在 AI 之外**：关键词表永远穷举不完
    /// （实测"你们把那个大培优第二章全部写完啊，后天交"一个词都不命中，
    /// 于是整条消息被丢掉，用户看到的就是"没有被解析"）。
    /// 现在本地关键词只当**快速通道**（命中就省一次 AI 调用），
    /// 其余消息一律交给 AI 分类（只跳过 4 字以下的短句，比如"好的""收到"）。
    /// </summary>
    private async Task<RuleEngine.Kind> ClassifyAsync(IIncomingMessage ev, SenderInfo sender,
        CancellationToken cancel, Guid? progressId = null)
    {
        // 本地关键词只当兜底：它会把"你上来把作业发一下"错判成作业（其实是召唤），
        // 所以能用 AI 就让 AI 分类。
        if (!flags.AiDecidesTeacherMessages || ev.Text.Trim().Length < 4)
            return RuleEngine.ClassifyLocal(ev.Text);

        if (progressId is { } pid)
            feed.Update(pid, "正在处理消息", $"本地关键词未命中 · 交给 AI 分类…");
        var kind = await ai.AnalyzeKindAsync(ev.Text, sender.Subject, cancel).ConfigureAwait(false);
        var mapped = kind switch
        {
            "homework" => RuleEngine.Kind.Homework,
            "exchange" => RuleEngine.Kind.Exchange,
            "summon" => RuleEngine.Kind.Summon,
            "none" => RuleEngine.Kind.None,
            // AI 没给出结论（调用失败/格式不对）→ 回退到本地关键词，别把消息丢掉
            _ => RuleEngine.ClassifyLocal(ev.Text)
        };
        if (mapped != RuleEngine.Kind.None)
            feed.Append("ai", $"AI 判定为{mapped}（未命中关键词）", ev.Text, ActivitySeverity.Info);
        return mapped;
    }

    /// <summary>
    /// 发送者是否允许触发自动动作。
    /// 默认只认**老师名单里**的人：名单外的人发来的换课/作业/召唤一律不自动执行，
    /// 转人工确认 —— 否则群里任何人都能让课表被改掉（这是真实存在的越权面）。
    /// 名单为空时不做过滤（用户还没配名单，否则什么都用不了）。
    /// </summary>
    private bool SenderAllowed(SenderInfo sender, IIncomingMessage ev, RuleEngine.Kind kind)
    {
        if (!flags.RequireKnownTeacher || teachers.Count == 0)
            return true;
        if (sender.TeacherName is not null)
            return true;

        var what = kind switch
        {
            var k when k.HasFlag(RuleEngine.Kind.Exchange) => "换课",
            var k when k.HasFlag(RuleEngine.Kind.Homework) => "作业",
            _ => "召唤"
        };
        var who = sender.Card ?? sender.Nickname ?? $"QQ{sender.UserId}";
        feed.Append("auth", $"忽略{what}请求：{who} 不在老师名单里",
            "设置 → 老师映射 里加上他，或关掉「只处理老师名单里的消息」",
            ActivitySeverity.Warning);
        AddPending("auth", $"{what}请求来自名单外的人（{who}），需人工确认", ev.Text,
            "发送者不在老师名单里", sender,
            new MessageRef { GroupId = ev.GroupId, MessageId = ev.MessageId });
        return false;
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
                    ClassDate = DateOnly.FromDateTime(DateTime.Now),
                    ArchivedAt = DateTimeOffset.Now
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
        IIncomingMessage ev, SenderInfo sender, CancellationToken cancel,
        bool keepOnFailure = false, string? resolvePendingId = null)
    {
        SummonDraft d;
        try
        {
            // 消息里常只说"老师叫你过去"，所以把发送者、当前课程、老师名单一起给 AI
            var lesson = await gate.CurrentLessonAsync(cancel).ConfigureAwait(false);
            var context = new SummonContext(
                Sender: sender.TeacherName ?? sender.Card ?? sender.Nickname ?? $"QQ{sender.UserId}",
                SenderSubject: sender.Subject ?? "未知",
                Lesson: lesson?.Subject ?? "未知（课表未加载）",
                LessonTeacher: lesson?.Teacher ?? "未知",
                Roster: teachers.Describe());
            d = await ai.AnalyzeSummonAsync(ev.Text, context, cancel).ConfigureAwait(false);
        }
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
            Teacher = string.IsNullOrWhiteSpace(d.Teacher) ? null : d.Teacher!.Trim(),
            AiTitle = d.Title,
            AiBody = d.Body,
            Subject = sender.Subject
                      ?? (await gate.CurrentLessonAsync(cancel).ConfigureAwait(false))?.Subject,
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
        IIncomingMessage ev, SenderInfo sender, CancellationToken cancel,
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
        IIncomingMessage ev, SenderInfo sender, CancellationToken cancel,
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
