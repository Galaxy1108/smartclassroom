using System.Collections.ObjectModel;
using SmartClassroom.Contracts;
using SmartClassroom.Core;

namespace SmartClassroom.App.ViewModels;

/// <summary>作业墙：绑定 HomeworkStore，支持手动添加（可选是否入库由调用方决定）。</summary>
public sealed class HomeworkViewModel : ViewModelBase
{
    private readonly HomeworkStore _store;

    public HomeworkViewModel() : this(new HomeworkStore()) { }

    public HomeworkViewModel(HomeworkStore store, Func<IReadOnlyDictionary<string, string>>? colorSource = null)
    {
        _store = store;
        _colorSource = colorSource;
        Refresh();
    }

    /// <summary>科目配色的来源（运行时读设置；测试里不传就用默认色板）。</summary>
    private readonly Func<IReadOnlyDictionary<string, string>>? _colorSource;

    public ObservableCollection<HomeworkCard> Items { get; } = new();

    /// <summary>
    /// 是否处于锁定状态（设了管理员密码且本次操作未验证）。
    /// 作业页的增删改、拖拽都要先解锁 —— 用户要求"每一次操作都需要管理员密码"。
    /// </summary>
    public bool IsLocked => Runtime.Auth.IsEnabled && !Runtime.Auth.IsUnlocked;

    /// <summary>解锁后刷新界面（各页共用）。</summary>
    public void RefreshLockState() => OnPropertyChanged(nameof(IsLocked));

    /// <summary>当前生效的科目颜色（科目名 → #RRGGBB）。</summary>
    public IReadOnlyDictionary<string, string> SubjectColors
        => _colorSource?.Invoke() ?? new Dictionary<string, string>();

    public bool IsEmpty => Items.Count == 0;

    // ---------- 过期作业 ----------
    //
    // 过期即删除（Runtime.PruneExpiredHomework：启动时 + 每 30 秒一次，连带 state.json）。
    // 这里再挡一道：万一清理还没跑到（例如刚跨过零点），过期卡片也不会闪在墙上。

    /// <summary>可见卡片 → 存储下标。拖拽必须按它换算，否则有卡片被挡掉时会把别的卡片挪走。</summary>
    private readonly List<int> _visibleStoreIndex = new();

    public int TotalCount => _store.All.Count;

    public string EmptyHint => "暂无作业";

    // ---------- 手动添加 ----------
    private bool _isAdding;
    public bool IsAdding
    {
        get => _isAdding;
        set => Set(ref _isAdding, value);
    }

    private string _formSubject = "";
    public string FormSubject { get => _formSubject; set => Set(ref _formSubject, value); }

    private string _formDate = DateTime.Now.ToString("yyyy-MM-dd");
    public string FormDate { get => _formDate; set => Set(ref _formDate, value); }

    private string _formItems = "";
    public string FormItems { get => _formItems; set => Set(ref _formItems, value); }

    private string _formDue = "";
    public string FormDue { get => _formDue; set => Set(ref _formDue, value); }

    private string _addResult = "";
    public string AddResult { get => _addResult; private set => Set(ref _addResult, value); }

    public void BeginAdd()
    {
        IsAdding = true;
        AddResult = "";
        FormSubject = "";
        FormDate = DateTime.Now.ToString("yyyy-MM-dd");
        FormItems = "";
        FormDue = "";
    }

    public void CancelAdd() => IsAdding = false;

    /// <summary>提交手动添加。成功返回 true。校验失败时保留表单内容，方便改。</summary>
    public bool SubmitAdd()
    {
        if (string.IsNullOrWhiteSpace(FormSubject))
        {
            AddResult = "请填写科目。";
            return false;
        }
        var items = FormItems
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (items.Count == 0)
        {
            AddResult = "请至少填写一条作业内容。";
            return false;
        }
        var date = DateOnly.TryParse(FormDate, out var d) ? d : DateOnly.FromDateTime(DateTime.Now);
        _store.AddOrMerge(new HomeworkItem
        {
            HomeworkId = Guid.NewGuid().ToString(),
            Subject = FormSubject.Trim(),
            Date = date,
            Items = items,
            Due = string.IsNullOrWhiteSpace(FormDue) ? null : FormDue.Trim(),
            Sender = new SenderInfo { UserId = 0, TeacherName = "手动添加" },
            Source = new MessageRef { GroupId = 0, MessageId = 0 }
        });
        AddResult = $"已添加：{FormSubject.Trim()}（{date:MM-dd}）";
        IsAdding = false;
        Refresh();
        return true;
    }

    /// <summary>
    /// 拖拽中的实时重排：同时移动存储与 ObservableCollection。
    /// **不能走 Refresh()**——那会 Clear + 重建整串卡片对象，
    /// 正在被拖动的那张控件会被从可视树里摘掉，指针捕获随之失效
    /// （松手事件收不到，就会一直"粘"着鼠标）。
    /// </summary>
    public bool MoveItemLive(int from, int to)
    {
        if (from < 0 || from >= _visibleStoreIndex.Count || from == to)
            return false;
        to = Math.Clamp(to, 0, _visibleStoreIndex.Count - 1);
        // 可见下标 ≠ 存储下标（过期作业可能被收起夹在中间），必须换算
        if (!_store.Move(_visibleStoreIndex[from], _visibleStoreIndex[to]))
            return false;
        Items.Move(from, to);   // 保留现有控件容器，指针捕获不丢
        RecomputeVisibleMap();  // 存储顺序变了，映射跟着重算
        return true;
    }

    /// <summary>重算"可见项 → 存储下标"（过期项被挡掉，下标会错位）。</summary>
    private void RecomputeVisibleMap()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var all = _store.All;
        _visibleStoreIndex.Clear();
        for (var i = 0; i < all.Count; i++)
        {
            if (all[i].Date < today)
                continue;
            _visibleStoreIndex.Add(i);
        }
    }

    /// <summary>拖拽期间挂起定时刷新，避免刷新重建卡片打断拖拽。</summary>
    /// <summary>挂起刷新（按住/拖拽期间），避免重建卡片打断手势。</summary>
    public bool SuspendRefresh { get; set; }

    private string _lastSignature = "";

    public IReadOnlyList<HomeworkItem> Snapshot() => _store.All;

    /// <summary>
    /// 刷新卡片。
    /// **数据没变就不重建** —— 这很关键：定时器每 2 秒调一次，而无条件 Clear+重建
    /// 会销毁正在被按住/拖动的卡片控件，导致指针捕获丢失（拖拽失效），
    /// 在指针事件派发过程中销毁控件还可能直接崩掉应用。
    /// </summary>
    public void Refresh(bool force = false)
    {
        if (SuspendRefresh && !force)
            return;
        var signature = Signature();
        if (!force && signature == _lastSignature && Items.Count == _visibleStoreIndex.Count)
            return;
        _lastSignature = signature;

        var today = DateOnly.FromDateTime(DateTime.Now);
        var now = DateTime.Now;
        Items.Clear();
        _visibleStoreIndex.Clear();
        var all = _store.All;
        for (var i = 0; i < all.Count; i++)
        {
            if (all[i].Date < today)
                continue;   // 过期项（正常情况下已被清理）
            Items.Add(new HomeworkCard(all[i], now, SubjectColors));
            _visibleStoreIndex.Add(i);
        }
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(EmptyHint));
    }

    /// <summary>数据指纹：内容、顺序或过滤条件一变就变。</summary>
    private string Signature()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var h in _store.All)
        {
            sb.Append(h.Subject).Append('|').Append(h.Date).Append('|')
              .Append(string.Join('\u0001', h.Items)).Append('|').Append(h.Due).Append('\u0002');
        }
        // 配色也算进指纹：设置页改了颜色，卡片下一轮刷新就会换色
        foreach (var kv in SubjectColors)
            sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\u0003');
        return sb.ToString();
    }

    public void AddItem(HomeworkItem item)
    {
        _store.AddOrMerge(item);
        Refresh();
    }
}

/// <summary>
/// 事件页：上半是「待处理」（AI 失败等留住的群消息，可重试/补录/忽略），
/// 下半是决策时间线（应用做了什么、为什么没做）。
/// </summary>
public sealed class EventsViewModel : ViewModelBase
{
    private readonly ActivityFeed _feed;
    private readonly PendingStore _pending;
    private readonly PipelineService? _pipeline;

    public EventsViewModel() : this(new ActivityFeed(), new PendingStore(), null) { }

    public EventsViewModel(ActivityFeed feed, PendingStore pending, PipelineService? pipeline)
    {
        _feed = feed;
        _pending = pending;
        _pipeline = pipeline;
        Refresh();
    }

    // ---------- 时间线 ----------
    //
    // 时间线是**跨重启保留**的（存在 state.json 里，见 AppStateStore）。
    // 定时器每 2 秒会调一次 Refresh()，若无条件 Clear+重建，
    // 用户正拖选的一段文字会每 2 秒被清掉一次；所以这里也做指纹比较，没变就不动。

    public ObservableCollection<ActivityRow> Entries { get; } = new();

    public bool HasEntries => Entries.Count > 0;

    /// <summary>还有进行中的条目（界面可以据此显示"正在处理…"）。</summary>
    public bool HasInProgress => _feed.HasInProgress;
    public bool IsEmptyTimeline => Entries.Count == 0;

    private string _entriesSignature = "";

    // ---------- 待处理 ----------
    public ObservableCollection<PendingRow> Pending { get; } = new();

    public bool HasPending => Pending.Count > 0;
    public string PendingHeader => $"待处理（{Pending.Count}）";

    private string _actionResult = "";
    public string ActionResult { get => _actionResult; private set => Set(ref _actionResult, value); }

    /// <summary>锁定状态：待处理的重新解析/补录/忽略、时间线清空都要先解锁。</summary>
    public bool IsLocked => Runtime.Auth.IsEnabled && !Runtime.Auth.IsUnlocked;

    public void RefreshLockState() => OnPropertyChanged(nameof(IsLocked));

    /// <summary>时间线自己的操作反馈（复制/清空），与上方待处理区的 ActionResult 分开显示。</summary>
    private string _timelineResult = "";
    public string TimelineResult { get => _timelineResult; private set => Set(ref _timelineResult, value); }

    /// <summary>视图层（复制到剪贴板等）回报结果用。</summary>
    public void Report(string text) => TimelineResult = text;

    // ---------- 人工录入表单 ----------
    private PendingRow? _editing;
    public PendingRow? Editing
    {
        get => _editing;
        private set
        {
            if (!Set(ref _editing, value))
                return;
            OnPropertyChanged(nameof(IsEditing));
            OnPropertyChanged(nameof(IsEditingHomework));
            OnPropertyChanged(nameof(IsEditingExchange));
            OnPropertyChanged(nameof(IsEditingSummon));
        }
    }

    public bool IsEditing => Editing is not null;
    public bool IsEditingHomework => Editing?.Kind == "homework";
    public bool IsEditingExchange => Editing?.Kind == "exchange";
    public bool IsEditingSummon => Editing?.Kind == "summon";

    private string _formSubject = "";
    public string FormSubject { get => _formSubject; set => Set(ref _formSubject, value); }

    private string _formItems = "";
    public string FormItems { get => _formItems; set => Set(ref _formItems, value); }

    private string _formDate = DateTime.Now.ToString("yyyy-MM-dd");
    public string FormDate { get => _formDate; set => Set(ref _formDate, value); }

    private string _formTarget = "";
    public string FormTarget { get => _formTarget; set => Set(ref _formTarget, value); }

    private bool _formUrgent;
    public bool FormUrgent { get => _formUrgent; set => Set(ref _formUrgent, value); }

    private string _formFromPeriod = "1";
    public string FormFromPeriod { get => _formFromPeriod; set => Set(ref _formFromPeriod, value); }

    private string _formToPeriod = "2";
    public string FormToPeriod { get => _formToPeriod; set => Set(ref _formToPeriod, value); }

    private string _formKind = "Swap";
    public string FormKind { get => _formKind; set => Set(ref _formKind, value); }

    private string _formNewSubject = "";
    public string FormNewSubject { get => _formNewSubject; set => Set(ref _formNewSubject, value); }

    public List<string> ExchangeKinds { get; } = ["Swap", "Replace", "CrossDay"];

    // ---------- 刷新 ----------
    public void Refresh() => Refresh(force: false);

    /// <param name="force">true = 无视指纹重建（清空、切换页面等需要立刻反映的操作）。</param>
    public void Refresh(bool force)
    {
        var signature = EntriesSignature();
        if (force || signature != _entriesSignature || Entries.Count != _feed.Entries.Count)
        {
            _entriesSignature = signature;
            Entries.Clear();
            foreach (var e in _feed.Entries)
                Entries.Add(new ActivityRow(e));
            OnPropertyChanged(nameof(HasEntries));
            OnPropertyChanged(nameof(IsEmptyTimeline));
            OnPropertyChanged(nameof(HasInProgress));
        }

        Pending.Clear();
        foreach (var p in _pending.All)
            Pending.Add(new PendingRow(p));
        OnPropertyChanged(nameof(HasPending));
        OnPropertyChanged(nameof(PendingHeader));
        OnPropertyChanged(nameof(PendingHint));
    }

    private string EntriesSignature()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var e in _feed.Entries)
        {
            sb.Append(e.At.Ticks).Append('|').Append(e.Kind).Append('|').Append(e.Title)
              .Append('|').Append(e.Detail).Append('|').Append((int)e.Severity);
            if (e.InProgress)
                // 进行中的条目把"已用秒数"也放进签名：这样每秒都会重建，时间实时跳
                sb.Append("|run").Append((int)(DateTimeOffset.Now - e.At).TotalSeconds);
            sb.Append('\u0002');
        }
        return sb.ToString();
    }

    /// <summary>单条记录的可复制文本。</summary>
    public string CopyEntryText(ActivityRow row) => row.CopyText;

    /// <summary>
    /// 整条时间线的导出文本：带上导出时间与条数，
    /// 方便用户直接把整段贴进聊天窗口或 issue 里。
    /// </summary>
    public string CopyAllText()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("智慧课堂 · 决策时间线（").Append(Entries.Count).Append(" 条，导出于 ")
          .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("）");
        foreach (var row in Entries)
            sb.Append('\n').Append(row.CopyText);
        return sb.ToString();
    }

    /// <summary>清空时间线（它不会随重启自动清，所以得给个手动出口）。返回清掉的条数。</summary>
    public int ClearTimeline()
    {
        var n = Entries.Count;
        _feed.Clear();
        Entries.Clear();
        _entriesSignature = "";
        OnPropertyChanged(nameof(HasEntries));
        OnPropertyChanged(nameof(IsEmptyTimeline));
        return n;
    }

    /// <summary>启用了管理员密码时，待处理操作需要先验证。</summary>
    public bool RequiresPassword => Runtime.Auth.IsEnabled;

    public string PendingHint => _pipeline is null
        ? "消息管线未启动（未配置 AI / QQ），暂不能处理。"
        : "AI 没能自动处理的群消息会留在这里，可重新解析或人工录入。";

    public void Post(string kind, string title, string detail)
    {
        _feed.Append(kind, title, detail);
        Refresh();
    }

    // ---------- 动作 ----------
    public async Task RetryAsync(PendingRow row)
    {
        if (_pipeline is null) { ActionResult = "管线未启动。"; return; }
        ActionResult = await _pipeline.RetryPendingAsync(row.Id);
        Editing = null;
        Refresh();
    }

    public void Ignore(PendingRow row)
    {
        if (_pipeline is null) { ActionResult = "管线未启动。"; return; }
        ActionResult = _pipeline.IgnorePending(row.Id) ? "已忽略。" : "该事项已不存在。";
        Editing = null;
        Refresh();
    }

    /// <summary>打开人工录入表单，并按 kind 预填能从原文猜到的字段。</summary>
    public void BeginEdit(PendingRow row)
    {
        Editing = row;
        ActionResult = "";
        FormDate = DateTime.Now.ToString("yyyy-MM-dd");
        FormSubject = row.TeacherSubject ?? "";
        FormItems = "";
        FormTarget = "";
        FormUrgent = row.RawText.Contains("现在") || row.RawText.Contains("立刻")
                    || row.RawText.Contains("马上") || row.RawText.Contains("立即");
        FormFromPeriod = "1";
        FormToPeriod = "2";
        FormKind = "Swap";
        FormNewSubject = "";
    }

    public void CancelEdit() => Editing = null;

    public async Task SubmitEditAsync()
    {
        if (_pipeline is null || Editing is null)
        {
            ActionResult = "管线未启动。";
            return;
        }
        var id = Editing.Id;
        ActionResult = Editing.Kind switch
        {
            "homework" => _pipeline.ResolveHomeworkAsync(
                id, FormSubject,
                FormItems.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                DateOnly.TryParse(FormDate, out var d) ? d : DateOnly.FromDateTime(DateTime.Now)),
            "summon" => await _pipeline.ResolveSummonAsync(id, FormTarget, FormUrgent),
            "exchange" => await _pipeline.ResolveExchangeAsync(
                id,
                Enum.TryParse<ExchangeKind>(FormKind, ignoreCase: true, out var k) ? k : ExchangeKind.Swap,
                new ClassSlot
                {
                    Date = DateOnly.TryParse(FormDate, out var fd) ? fd : DateOnly.FromDateTime(DateTime.Now),
                    PeriodIndex = int.TryParse(FormFromPeriod, out var fp) ? fp : 0
                },
                new ClassSlot
                {
                    Date = DateOnly.TryParse(FormDate, out var td) ? td : DateOnly.FromDateTime(DateTime.Now),
                    PeriodIndex = int.TryParse(FormToPeriod, out var tp) ? tp : 0
                },
                string.IsNullOrWhiteSpace(FormNewSubject) ? null : FormNewSubject.Trim()),
            _ => "未知类型。"
        };
        Editing = null;
        Refresh();
    }
}

/// <summary>
/// 时间线行：把 <see cref="ActivitySeverity"/> 翻译成界面语义
/// （配色类名开关 + 图标开关 + 可复制文本），XAML 里不做任何逻辑。
/// </summary>
public sealed class ActivityRow(ActivityEntry entry)
{
    public DateTimeOffset At => entry.At;
    public string Kind => entry.Kind;
    public string Title => entry.Title;
    public string Detail => entry.Detail;
    public ActivitySeverity Severity => entry.Severity;

    public string Time => entry.At.ToString("MM-dd HH:mm:ss");

    /// <summary>正在处理（界面显示转圈 + 已用时间，实时反映处理进度）。</summary>
    public bool InProgress => entry.InProgress;

    /// <summary>已用时间文本（仅进行中的条目显示）。</summary>
    public string ElapsedText
        => entry.InProgress
            ? $"处理中 {(DateTimeOffset.Now - entry.At).TotalSeconds:F1}s"
            : "";

    /// <summary>"已忽略"这类没有发生任何事的结果：灰色，不抢眼也不报警。</summary>
    public bool IsMuted => entry.Severity == ActivitySeverity.Muted;

    public bool IsInfo => entry.Severity == ActivitySeverity.Info;
    public bool IsSuccess => entry.Severity == ActivitySeverity.Success;
    public bool IsWarning => entry.Severity == ActivitySeverity.Warning;
    public bool IsError => entry.Severity == ActivitySeverity.Error;

    public string SeverityLabel => entry.Severity switch
    {
        ActivitySeverity.Muted => "已忽略",
        ActivitySeverity.Success => "成功",
        ActivitySeverity.Warning => "警告",
        ActivitySeverity.Error => "错误",
        _ => "信息"
    };

    /// <summary>复制出去的文本：级别 + 时间 + 来源 + 正文（明细缩进一行）。</summary>
    public string CopyText => string.IsNullOrWhiteSpace(entry.Detail)
        ? $"[{Time}] [{SeverityLabel}] {Kind} · {Title}"
        : $"[{Time}] [{SeverityLabel}] {Kind} · {Title}\n    {entry.Detail.Replace("\n", "\n    ")}";
}

/// <summary>待处理行（包一层，便于绑定显示教师名等派生信息）。</summary>
public sealed class PendingRow(PendingItem item)
{
    public string Id => item.Id;
    public string Kind => item.Kind;
    public string Title => item.Title;
    public string RawText => item.RawText;
    public string Reason => item.Reason;
    public int RetryCount => item.RetryCount;
    public string Sender => item.Sender.TeacherName ?? item.Sender.Card ?? item.Sender.Nickname ?? $"QQ{item.Sender.UserId}";
    public string? TeacherSubject => item.Sender.Subject;
    public string Time => item.CreatedAt.ToString("MM-dd HH:mm");
    public string KindLabel => item.Kind switch
    {
        "homework" => "作业",
        "exchange" => "换课",
        "summon" => "召唤",
        _ => item.Kind
    };
    public string RetryLabel => item.RetryCount > 0 ? $"已重试 {item.RetryCount} 次" : "";
}

/// <summary>
/// 作业卡片视图行：把原始数据整理成卡片要显示的样式信息
/// （主题色、相对日期、条目列表），避免在 XAML 里做逻辑。
/// </summary>
public sealed class HomeworkCard
{
    /// <summary>科目配色板：按科目名稳定取色，同一个科目每次颜色一致。</summary>
    private static readonly string[] Palette =
    [
        "#0F6CBD", "#0F7B0F", "#9D5D00", "#B10E1C", "#5C2E91",
        "#00766C", "#8A3707", "#4F6BED", "#6B4E00", "#7A0E4B"
    ];

    /// <param name="customColors">用户配置的科目颜色（科目名 → #RRGGBB），没配就用默认色板。</param>
    public HomeworkCard(HomeworkItem item, DateTime now,
        IReadOnlyDictionary<string, string>? customColors = null)
    {
        Subject = item.Subject;
        Date = item.Date;
        Due = item.Due;
        Items = item.Items.Select((text, i) => new HomeworkLine(i + 1, text)).ToList();
        Sender = item.Sender.TeacherName ?? "未知来源";
        IsManual = item.Sender.UserId == 0;

        var today = DateOnly.FromDateTime(now);
        RelativeDay = item.Date == today ? "今天"
            : item.Date == today.AddDays(1) ? "明天"
            : item.Date == today.AddDays(-1) ? "昨天"
            : item.Date < today ? "已过期"
            : "还有 " + (item.Date.DayNumber - today.DayNumber) + " 天";

        AccentColor = customColors is not null
                      && customColors.TryGetValue(item.Subject.Trim(), out var custom)
                      && custom.Length > 0
            ? custom
            : Palette[Math.Abs(StableHash(item.Subject)) % Palette.Length];
        ItemCountLabel = item.Items.Count + " 项";
    }

    public string Subject { get; }
    public DateOnly Date { get; }
    public string? Due { get; }
    public IReadOnlyList<HomeworkLine> Items { get; }
    public string Sender { get; }
    public bool IsManual { get; }
    public string RelativeDay { get; }
    public string AccentColor { get; }
    public string ItemCountLabel { get; }
    public string DateLabel => Date.ToString("MM-dd");

    /// <summary>有没有写明截止（AI 会给"无需提交"，或留空表示没提）。</summary>
    public bool HasDue => !string.IsNullOrWhiteSpace(Due);

    /// <summary>
    /// 截止显示文本。**没提截止时不要瞎编**：显示"未说明截止"，
    /// 明确不用交的显示"无需提交"（AI 会在 due 里写"无需提交"）。
    /// </summary>
    public string DueLabel => string.IsNullOrWhiteSpace(Due) ? "未说明截止" : Due!;

    private static int StableHash(string s)
    {
        unchecked
        {
            var h = 17;
            foreach (var c in s)
                h = h * 31 + c;
            return h;
        }
    }
}

/// <summary>作业条目 + 序号（界面上显示为 1. 2. 3.）。</summary>
public sealed record HomeworkLine(int Number, string Text);
