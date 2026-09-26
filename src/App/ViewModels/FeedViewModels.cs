using System.Collections.ObjectModel;
using SmartClassroom.Contracts;
using SmartClassroom.Core;

namespace SmartClassroom.App.ViewModels;

/// <summary>作业墙：绑定 HomeworkStore。</summary>
public sealed class HomeworkViewModel : ViewModelBase
{
    private readonly HomeworkStore _store;

    public HomeworkViewModel() : this(new HomeworkStore()) { }

    public HomeworkViewModel(HomeworkStore store)
    {
        _store = store;
        Refresh();
    }

    public ObservableCollection<HomeworkItem> Items { get; } = new();

    public void Refresh()
    {
        Items.Clear();
        foreach (var h in _store.All)
            Items.Add(h);
        OnPropertyChanged(nameof(IsEmpty));
    }

    public bool IsEmpty => Items.Count == 0;

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
    public ObservableCollection<ActivityEntry> Entries { get; } = new();

    // ---------- 待处理 ----------
    public ObservableCollection<PendingRow> Pending { get; } = new();

    public bool HasPending => Pending.Count > 0;
    public string PendingHeader => $"待处理（{Pending.Count}）";

    private string _actionResult = "";
    public string ActionResult { get => _actionResult; private set => Set(ref _actionResult, value); }

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
    public void Refresh()
    {
        Entries.Clear();
        foreach (var e in _feed.Entries)
            Entries.Add(e);

        Pending.Clear();
        foreach (var p in _pending.All)
            Pending.Add(new PendingRow(p));
        OnPropertyChanged(nameof(HasPending));
        OnPropertyChanged(nameof(PendingHeader));
        OnPropertyChanged(nameof(PendingHint));
    }

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
