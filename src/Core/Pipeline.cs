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

    /// <summary>
    /// 热更新功能开关：用户在设置页打开/关闭功能后**立刻生效**，
    /// 不用重启应用（以前 flags 是启动时的快照，改了要重启，很反直觉）。
    /// </summary>
    public void UpdateFlags(FeatureFlags next) => flags = next;

    private ScheduleGate.SendFunc Send => plugin.NotifyAsync;

    public PendingStore Pending => pending;

    public event Action<IReadOnlyList<CoursewareFile>>? CoursewareSuggested;

    /// <summary>群文本消息入口（只处理配置群，群过滤由调用方完成）。</summary>
    public async Task OnGroupMessageAsync(GroupMessageEvent ev, CancellationToken cancel = default,
        Guid? rowId = null)
    {
        var sender = teachers.ToSender(ev.UserId, ev.Card, ev.Nickname);
        var who = sender.Card ?? sender.Nickname ?? $"QQ{sender.UserId}";
        // 进行中的条目：用户要的是"实时看到处理到哪一步"，而不是只在结束后看到结果。
        // 行可以由 Runtime 先建好（那样"未监听的群"也能给出已忽略的结果），这里复用。
        var id = rowId ?? feed.Begin("qq", "正在处理消息", $"{who}：{Trim(ev.Text)}");
        try
        {
            feed.Update(id, "正在处理消息", $"{who}：{Trim(ev.Text)} · 本地分流…");
            var kind = await ClassifyAsync(ev, sender, cancel, id).ConfigureAwait(false);
            if (kind == RuleEngine.Kind.None)
            {
                feed.Complete(id, "已忽略（与三个功能都无关）", Trim(ev.Text), ActivitySeverity.Muted);
                return;
            }
            feed.Update(id, $"正在处理：{FeatureName(kind)}", $"{who}：{Trim(ev.Text)}");
            if (!SenderAllowed(sender, ev, kind))
            {
                feed.Complete(id, "已忽略（发送者不在老师名单里）", Trim(ev.Text), ActivitySeverity.Muted);
                return;
            }
            await DispatchGroupAsync(ev, sender, kind, cancel, id).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            feed.Complete(id, "出现错误", ex.Message, ActivitySeverity.Error);
            throw;
        }
    }

    /// <summary>按判定结果分发到对应处理流程（群消息）。</summary>
    private async Task DispatchGroupAsync(GroupMessageEvent ev, SenderInfo sender,
        RuleEngine.Kind kind, CancellationToken cancel, Guid? rowId = null)
    {
        if (kind.HasFlag(RuleEngine.Kind.Summon))
        {
            if (flags.Summon)
                await HandleSummonAsync(ev, sender, cancel, rowId: rowId).ConfigureAwait(false);
            else
                NoteDisabled("summon", "召唤通知");
        }
        if (kind.HasFlag(RuleEngine.Kind.Homework))
        {
            if (flags.Homework)
                await HandleHomeworkAsync(ev, sender, cancel, rowId: rowId).ConfigureAwait(false);
            else
                NoteDisabled("homework", "作业自动录入");
        }
        if (kind.HasFlag(RuleEngine.Kind.Exchange))
        {
            if (flags.Exchange)
                await HandleExchangeAsync(ev, sender, cancel, rowId: rowId).ConfigureAwait(false);
            else
                NoteDisabled("exchange", "换课自动处理");
        }
        if (kind.HasFlag(RuleEngine.Kind.Notice))
        {
            if (flags.Notice)
                await HandleNoticeAsync(ev, sender, cancel, rowId: rowId).ConfigureAwait(false);
            else
                NoteDisabled("notice", "老师通知转发");
        }
    }

    private static string FeatureName(RuleEngine.Kind kind)
        => kind.HasFlag(RuleEngine.Kind.Exchange) ? "换课"
           : kind.HasFlag(RuleEngine.Kind.Homework) ? "作业"
           : kind.HasFlag(RuleEngine.Kind.Summon) ? "召唤"
           : kind.HasFlag(RuleEngine.Kind.Notice) ? "通知"
           : "未知";

    private static string Weekday(DateOnly date) => date.DayOfWeek switch
    {
        DayOfWeek.Monday => "周一",
        DayOfWeek.Tuesday => "周二",
        DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四",
        DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六",
        _ => "周日"
    };

    private static string Trim(string text)
        => text.Length <= 40 ? text : text[..40] + "…";

    /// <summary>更新进行中行的细节（AI 步骤、失败与重试都走这里）。</summary>
    private void UpdateRow(Guid? rowId, string title, string detail)
    {
        if (rowId is { } id)
            feed.Update(id, title, detail);
    }

    /// <summary>
    /// 收尾：有进行中的行就把它就地变成结果行（已忽略 / 已执行 / 出现错误），
    /// 没有行（人工补录等场景）才新加一条。
    /// </summary>
    private void Finish(Guid? rowId, string title, string detail,
        ActivitySeverity severity = ActivitySeverity.Success)
    {
        if (rowId is { } id)
            feed.Complete(id, title, detail, severity);
        else
            feed.Append("qq", title, detail, severity);
    }

    /// <summary>
    /// 私聊入口：老师私聊也可能发"来一下"或作业，所以走同一套判定。
    /// 只认老师名单里的人 —— 陌生人私聊一律忽略（不然谁发都触发）。
    /// </summary>
    public async Task OnPrivateMessageAsync(PrivateMessageEvent ev, CancellationToken cancel = default,
        Guid? rowId = null)
    {
        var sender = teachers.ToSender(ev.UserId, null, ev.Nickname);
        var who = sender.Card ?? sender.Nickname ?? $"QQ{ev.UserId}";
        var id = rowId ?? feed.Begin("qq", "正在处理私聊", $"{who}：{Trim(ev.Text)}");

        if (teachers.Count > 0 && !teachers.IsKnown(ev.UserId))
        {
            // 陌生人私聊：**必须结束这条进行中的记录**，否则界面上的转圈和秒表一直跑
            //（实测出现"收到私聊 处理中 164.8s"）。同时把 QQ 号写出来，用户可以直接抄进老师映射。
            Finish(id, "已忽略（发送者不在老师名单里）",
                $"{Trim(ev.Text)} · 要处理它就在 设置 → 老师映射 里加上 QQ {ev.UserId}，"
                + "或关掉「只处理老师名单里的消息」",
                ActivitySeverity.Muted);
            return;
        }
        var kind = await ClassifyAsync(ev, sender, cancel, id).ConfigureAwait(false);
        if (kind == RuleEngine.Kind.None)
        {
            feed.Complete(id, "已忽略（与三个功能都无关）", Trim(ev.Text), ActivitySeverity.Muted);
            return;
        }
        feed.Update(id, $"正在处理：{FeatureName(kind)}", $"{who}：{Trim(ev.Text)}");

        if (kind.HasFlag(RuleEngine.Kind.Summon) && flags.Summon)
            await HandleSummonAsync(ev, sender, cancel, rowId: id).ConfigureAwait(false);
        else if (kind.HasFlag(RuleEngine.Kind.Summon))
            NoteDisabled("summon", "召唤通知");

        if (kind.HasFlag(RuleEngine.Kind.Homework) && flags.Homework)
            await HandleHomeworkAsync(ev, sender, cancel, rowId: id).ConfigureAwait(false);
        else if (kind.HasFlag(RuleEngine.Kind.Homework))
            NoteDisabled("homework", "作业自动录入");

        if (kind.HasFlag(RuleEngine.Kind.Exchange) && flags.Exchange)
            await HandleExchangeAsync(ev, sender, cancel, rowId: id).ConfigureAwait(false);
        else if (kind.HasFlag(RuleEngine.Kind.Exchange))
            NoteDisabled("exchange", "换课自动处理");

        if (kind.HasFlag(RuleEngine.Kind.Notice) && flags.Notice)
            await HandleNoticeAsync(ev, sender, cancel, rowId: id).ConfigureAwait(false);
        else if (kind.HasFlag(RuleEngine.Kind.Notice))
            NoteDisabled("notice", "老师通知转发");
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
            feed.Update(pid, "正在处理消息", "交给 AI 分类…");
        // AI 的失败与重试也实时写在进度下面（用户要求：失败也要可见，并且要重试）
        var kind = await ai.AnalyzeKindAsync(ev.Text, sender.Subject, cancel,
            msg => { if (progressId is { } p2) feed.Update(p2, "正在处理消息", msg); })
            .ConfigureAwait(false);
        var mapped = kind switch
        {
            "homework" => RuleEngine.Kind.Homework,
            "exchange" => RuleEngine.Kind.Exchange,
            "summon" => RuleEngine.Kind.Summon,
            "notice" => RuleEngine.Kind.Notice,
            "none" => RuleEngine.Kind.None,
            // AI 没给出结论（调用失败/格式不对）→ 回退到本地关键词，别把消息丢掉
            _ => RuleEngine.ClassifyLocal(ev.Text)
        };
        if (mapped != RuleEngine.Kind.None && progressId is { } id2)
            feed.Update(id2, "正在处理消息", $"AI 判定为 {FeatureName(mapped)} · 正在处理…");
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
        // 结果行由调用方写成"已忽略（发送者不在老师名单里）"，这里只负责进待处理 + 下课通知
        AddPending("auth", $"{what}请求来自名单外的人（{who}），需人工确认", ev.Text,
            $"发送者不在老师名单里（设置 → 老师映射 里加上 QQ {sender.UserId}，或关掉「只处理老师名单里的消息」）",
            sender, new MessageRef { GroupId = ev.GroupId, MessageId = ev.MessageId }, rowId: null);
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
    public async Task OnGroupUploadAsync(GroupUploadEvent ev, CancellationToken cancel = default,
        Guid? rowId = null)
    {
        var fileName = string.IsNullOrWhiteSpace(ev.File.Name) ? "(未命名文件)" : ev.File.Name;
        if (!flags.FileArchive)
        {
            Finish(rowId, "已忽略（群文件自动归档未开启）", fileName, ActivitySeverity.Muted);
            NoteDisabled("file", "群文件自动归档");
            return;
        }
        var sender = teachers.ToSender(ev.UserId, null, null);
        if (!SenderAllowed(sender, ev, RuleEngine.Kind.None))
        {
            Finish(rowId, "已忽略（发送者不在老师名单里）", fileName, ActivitySeverity.Muted);
            return;
        }
        ArchiveOutcome outcome;
        try
        {
            outcome = await archive.HandleAsync(ev, sender, async (e, ct) =>
            {
                // 文件段自带直链（私聊文件就是这种情况）就直接用；
                // 群文件没带 url 时才去调 get_group_file_url。
                if (e.File.HasUrl)
                    return e.File.Url;
                try { return (await oneBot.GetGroupFileUrlAsync(e.GroupId, e.File.Id, e.File.Busid, ct))?.Url; }
                catch { return null; }
            }, cancel).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Finish(rowId, "出现错误：文件归档失败", $"{fileName} · {ex.Message}", ActivitySeverity.Error);
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
                Finish(rowId, "已执行：已归档", $"{fileName} · 来自{sender.TeacherName ?? "未知发送者"}");
                break;
            case ArchiveResult.PendingConfirm:
                Finish(rowId, "已忽略：大文件待确认",
                    $"{fileName}（{ev.File.Size / 1024 / 1024}MB，来自{sender.TeacherName ?? "未知发送者"}）",
                    ActivitySeverity.Muted);
                break;
            case ArchiveResult.Failed:
                Finish(rowId, "出现错误：文件归档失败", $"{fileName} · {outcome.Error}",
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
        feed.Append(item.Kind, $"已忽略：{item.Title}", item.RawText, ActivitySeverity.Muted);
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

    /// <summary>
    /// 转人工：进待处理列表 + **下课时通过 ClassIsland 通知**（用户要求：
    /// "记得在下课时通过 classisland 发送通知"），并把它作为那条消息的结果（需要人工介入），
    /// 而不是另起一条"待确认"的时间线记录。
    /// </summary>
    private PendingItem AddPending(string kind, string title, string rawText, string reason,
        SenderInfo sender, MessageRef source, Guid? rowId = null)
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
        Finish(rowId, $"需要人工介入：{title}", $"原因：{reason} · {Trim(rawText)}",
            ActivitySeverity.Warning);
        // 上课时排队、下课时发；不在上课就立刻发（与召唤通知同一套调度门）
        _ = gate.SendManualAsync($"需要人工介入：{title}", reason, Send);
        return item;
    }

    // ================= 各类消息处理 =================

    private async Task HandleSummonAsync(
        IIncomingMessage ev, SenderInfo sender, CancellationToken cancel,
        bool keepOnFailure = false, string? resolvePendingId = null, Guid? rowId = null)
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
            d = await ai.AnalyzeSummonAsync(ev.Text, context, cancel,
                msg => UpdateRow(rowId, "正在解析召唤", msg)).ConfigureAwait(false);
        }
        catch (AiException ex)
        {
            if (!keepOnFailure)
                AddPending("summon", "召唤解析失败，需人工确认被叫的人", ev.Text, ex.Message, sender,
                    new MessageRef { GroupId = ev.GroupId, MessageId = ev.MessageId }, rowId: rowId);
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
            Finish(rowId, $"出现错误：召唤解析失败（已{Desc(decision)}）", ev.Text + " · " + ex.Message,
                ActivitySeverity.Warning);
            feed.Append("summon", $"疑似召唤（AI 失败，已{Desc(decision)}）", ev.Text,
                ActivitySeverity.Warning);
            return;
        }
        if (!d.IsSummon || string.IsNullOrWhiteSpace(d.Target))
        {
            if (resolvePendingId is not null)
                pending.Remove(resolvePendingId);
            Finish(rowId, "已忽略（AI 判定不是召唤）", ev.Text, ActivitySeverity.Muted);
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
        Finish(rowId, $"已执行：召唤（{Desc(result)}）", $"{d.Target} · {ev.Text}");
    }

    /// <summary>
    /// 老师通知转发：活动/集合/催交这类消息整理成一条提醒，
    /// 走调度门（上课排队、下课时发）经 ClassIsland 通知出来。
    /// </summary>
    private async Task HandleNoticeAsync(IIncomingMessage ev, SenderInfo sender,
        CancellationToken cancel, Guid? rowId = null)
    {
        NoticeDraft d;
        try
        {
            d = await ai.AnalyzeNoticeAsync(ev.Text, cancel,
                msg => UpdateRow(rowId, "正在整理通知", msg)).ConfigureAwait(false);
        }
        catch (AiException ex)
        {
            AddPending("notice", "通知解析失败，需人工确认", ev.Text, ex.Message, sender,
                new MessageRef { GroupId = ev.GroupId, MessageId = ev.MessageId }, rowId: rowId);
            return;
        }
        if (!d.IsNotice || d.Title.Trim().Length == 0)
        {
            Finish(rowId, "已忽略（AI 判定不是通知）", ev.Text, ActivitySeverity.Muted);
            return;
        }
        var who = sender.TeacherName ?? sender.Card ?? sender.Nickname ?? "老师";
        var body = d.Body.Trim().Length > 0 ? d.Body.Trim() : ev.Text;
        // 不在上课就立刻发（否则周末/假期没有下课事件，通知会永远卡在队列里）
        var decision = await gate.SendManualAsync(d.Title.Trim(), $"{who}：{body}", Send, cancel)
            .ConfigureAwait(false);
        Finish(rowId, $"已执行：通知已转发（{Desc(decision)}）",
            $"{d.Title.Trim()} · {body}");
    }

    private async Task HandleHomeworkAsync(
        IIncomingMessage ev, SenderInfo sender, CancellationToken cancel,
        bool keepOnFailure = false, string? resolvePendingId = null, Guid? rowId = null)
    {
        HomeworkDraft d;
        try
        {
            d = await ai.AnalyzeHomeworkAsync(ev.Text, sender.Subject, cancel,
                msg => UpdateRow(rowId, "正在整理作业", msg)).ConfigureAwait(false);
        }
        catch (AiException ex)
        {
            if (!keepOnFailure)
                AddPending("homework", "作业解析失败，需人工补录", ev.Text, ex.Message, sender,
                    new MessageRef { GroupId = ev.GroupId, MessageId = ev.MessageId }, rowId: rowId);
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
        Finish(rowId, $"已执行：作业已上墙（{d.Subject}）", string.Join("；", d.Items));
    }

    private async Task HandleExchangeAsync(
        IIncomingMessage ev, SenderInfo sender, CancellationToken cancel,
        bool keepOnFailure = false, string? resolvePendingId = null, Guid? rowId = null)
    {
        ExchangeDraft d;
        try
        {
            // 两步走：先问 AI 这条消息涉及哪几天（跨周也能算出来），再按那些日期取课表。
            // 只喂"今天/明天"是不够的 —— 实测"下周三的数学和周五的语文换一下"就取不到。
            var today = DateOnly.FromDateTime(DateTime.Now);
            var wanted = (await ai.ResolveExchangeDatesAsync(ev.Text, cancel,
                    msg => UpdateRow(rowId, "正在解析换课", msg)).ConfigureAwait(false)).ToList();
            if (wanted.Count == 0)
                wanted = [today, today.AddDays(1)];   // 没解析出来就退回今天/明天

            UpdateRow(rowId, "正在解析换课", $"取课表：{string.Join("、", wanted.Select(d => d.ToString("MM-dd")))}…");
            var plans = await Task.WhenAll(wanted.Select(async d =>
                (Date: d, Plan: await plugin.GetClassPlanAsync(d, cancel).ConfigureAwait(false))));
            var timetableText = string.Join("\n", plans.Select(p =>
                $"{p.Date:yyyy-MM-dd}（{Weekday(p.Date)}）：{PluginLink.DescribeClassPlan(p.Plan)}"));
            UpdateRow(rowId, "正在解析换课", "已取到课表，交给 AI 解析…");
            d = await ai.AnalyzeExchangeAsync(ev.Text, timetableText, cancel,
                msg => UpdateRow(rowId, "正在解析换课", msg)).ConfigureAwait(false);
        }
        catch (AiException ex)
        {
            if (!keepOnFailure)
                AddPending("exchange", "换课解析失败，需人工补录", ev.Text, ex.Message, sender,
                    new MessageRef { GroupId = ev.GroupId, MessageId = ev.MessageId }, rowId: rowId);
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
                    new MessageRef { GroupId = ev.GroupId, MessageId = ev.MessageId }, rowId: rowId);
            return;
        }
        // 节次缺失（模型没给 / 给了非数字）就转人工，别拿 0 去查课表
        var fromPeriod = d.From?.Period ?? 0;
        if (d.From is null || fromPeriod <= 0)
        {
            if (!keepOnFailure)
                AddPending("exchange", "换课缺少节次，需人工补录", ev.Text, "AI 没给出具体第几节", sender,
                    new MessageRef { GroupId = ev.GroupId, MessageId = ev.MessageId }, rowId: rowId);
            return;
        }
        var req = new ExchangeRequest
        {
            RequestId = Guid.NewGuid().ToString(),
            Kind = kind,
            From = new ClassSlot { Date = ParseDate(d.From.Date), PeriodIndex = fromPeriod },
            To = d.To?.Period is { } toPeriod and > 0
                ? new ClassSlot { Date = ParseDate(d.To.Date), PeriodIndex = toPeriod }
                : null,
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
                    new MessageRef { GroupId = ev.GroupId, MessageId = ev.MessageId }, rowId: rowId);
            return;
        }
        if (resolvePendingId is not null)
            pending.Remove(resolvePendingId);
        Finish(rowId,
            verdict.Legal ? "已执行：换课" : "已忽略：换课非法（已转人工）",
            $"{verdict.Message} · {ev.Text}",
            verdict.Legal ? ActivitySeverity.Success : ActivitySeverity.Muted);
        if (!verdict.Legal)
            gate.EnqueueManual("需手动换课", verdict.Message);
    }

    private static DateOnly ParseDate(string s)
        => DateOnly.TryParse(s, out var d) ? d : DateOnly.FromDateTime(DateTime.Now);

    private static string Desc(GateDecision d) => d == GateDecision.SentNow ? "立刻发送" : "已排队";
}
