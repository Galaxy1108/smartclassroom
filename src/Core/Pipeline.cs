using SmartClassroom.Contracts;
using SmartClassroom.Core.AI;
using SmartClassroom.Core.QQ;

namespace SmartClassroom.Core;

/// <summary>
/// 运行时总装：QQ 事件 → 教师映射 → 规则分流 → AI → 调度门/存储/插件/归档/课件。
/// 所有外部依赖经构造注入；AI 失败一律保守降级（召唤排队、作业/换课待确认），永不抛给调用方。
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
    ActivityFeed feed)
{
    private ScheduleGate.SendFunc Send => plugin.NotifyAsync;

    public event Action<IReadOnlyList<CoursewareFile>>? CoursewareSuggested;

    /// <summary>群文本消息入口（只处理配置群，群过滤由调用方完成）。</summary>
    public async Task OnGroupMessageAsync(GroupMessageEvent ev, CancellationToken cancel = default)
    {
        var sender = teachers.ToSender(ev.UserId, ev.Card, ev.Nickname);
        var kind = RuleEngine.ClassifyLocal(ev.Text);
        if (kind == RuleEngine.Kind.None)
            return;
        if (kind.HasFlag(RuleEngine.Kind.Summon))
            await HandleSummonAsync(ev, sender, cancel).ConfigureAwait(false);
        if (kind.HasFlag(RuleEngine.Kind.Homework))
            await HandleHomeworkAsync(ev, sender, cancel).ConfigureAwait(false);
        if (kind.HasFlag(RuleEngine.Kind.Exchange))
            await HandleExchangeAsync(ev, sender, cancel).ConfigureAwait(false);
    }

    /// <summary>群文件上传入口。</summary>
    public async Task OnGroupUploadAsync(GroupUploadEvent ev, CancellationToken cancel = default)
    {
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
            feed.Append("file", $"文件归档失败：{ev.File.Name}", ex.Message);
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
                    ClassDate = DateOnly.FromDateTime(DateTime.Now)
                });
                feed.Append("file", $"已归档：{ev.File.Name}", $"来自{sender.TeacherName ?? "未知发送者"}");
                break;
            case ArchiveResult.PendingConfirm:
                feed.Append("file", $"大文件待确认：{ev.File.Name}", $"{ev.File.Size / 1024 / 1024}MB，来自{sender.TeacherName ?? "未知发送者"}");
                break;
            case ArchiveResult.Failed:
                feed.Append("file", $"文件归档失败：{ev.File.Name}", outcome.Error ?? "");
                break;
        }
    }

    /// <summary>上课事件：当天老师有课件则弹推荐（调用方负责弹窗，App 层订阅）。</summary>
    public void OnClassStarted(DateOnly date, string subject, string? teacherName, long? teacherQq = null)
    {
        var files = courseware.Query(date, teacherName, teacherQq);
        if (files.Count == 0 || !courseware.TryMarkShown(date, subject))
            return;
        CoursewareSuggested?.Invoke(files);
    }

    /// <summary>下课/放学事件：flush 排队通知（经原通道发出）。</summary>
    public Task<int> OnClassEndedAsync(CancellationToken cancel = default)
        => gate.FlushAsync(Send, cancel);

    private async Task HandleSummonAsync(GroupMessageEvent ev, SenderInfo sender, CancellationToken cancel)
    {
        SummonDraft d;
        try { d = await ai.AnalyzeSummonAsync(ev.Text, cancel).ConfigureAwait(false); }
        catch (AiException)
        {
            // 降级：本地紧急词 + 原文，保守排队/直发。
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
            feed.Append("summon", $"疑似召唤（AI 失败，已{Desc(decision)}）：{ev.Text}", $"来自{sender.TeacherName ?? "未知"}");
            return;
        }
        if (!d.IsSummon || string.IsNullOrWhiteSpace(d.Target))
        {
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
        feed.Append("summon", $"召唤{d.Target}（{(d.Urgent ? "立刻" : "排队")}，{Desc(result)}）", ev.Text);
    }

    private async Task HandleHomeworkAsync(GroupMessageEvent ev, SenderInfo sender, CancellationToken cancel)
    {
        HomeworkDraft d;
        try { d = await ai.AnalyzeHomeworkAsync(ev.Text, sender.Subject, cancel).ConfigureAwait(false); }
        catch (AiException ex)
        {
            feed.Append("homework", "作业解析失败，待确认", $"{ev.Text}（{ex.Message}）");
            return;
        }
        if (!d.IsHomework)
            return;
        if (!DateOnly.TryParse(d.Date, out var date))
            date = DateOnly.FromDateTime(DateTime.Now);
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
        feed.Append("homework", $"作业已上墙：{d.Subject}", string.Join("；", d.Items));
    }

    private async Task HandleExchangeAsync(GroupMessageEvent ev, SenderInfo sender, CancellationToken cancel)
    {
        ExchangeDraft d;
        try { d = await ai.AnalyzeExchangeAsync(ev.Text, cancel).ConfigureAwait(false); }
        catch (AiException ex)
        {
            feed.Append("exchange", "换课解析失败，待确认", $"{ev.Text}（{ex.Message}）");
            return;
        }
        if (!d.IsExchange)
            return;
        if (!Enum.TryParse<ExchangeKind>(d.Kind, ignoreCase: true, out var kind))
        {
            feed.Append("exchange", "换课类型未知，待确认", ev.Text);
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
            feed.Append("exchange", "换课请求发送失败，待确认", $"{ev.Text}（{ex.Message}）");
            return;
        }
        feed.Append("exchange", verdict.Legal ? $"换课已执行：{verdict.Message}" : $"换课非法，已排队下课通知手动：{verdict.Message}", ev.Text);
        if (!verdict.Legal)
            gate.EnqueueManual("需手动换课", verdict.Message);
    }

    private static DateOnly ParseDate(string s)
        => DateOnly.TryParse(s, out var d) ? d : DateOnly.FromDateTime(DateTime.Now);

    private static string Desc(GateDecision d) => d == GateDecision.SentNow ? "立刻发送" : "已排队";
}
