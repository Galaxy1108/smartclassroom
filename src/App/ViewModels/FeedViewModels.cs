using System.Collections.ObjectModel;
using SmartClassroom.Contracts;
using SmartClassroom.Core;

namespace SmartClassroom.App.ViewModels;

/// <summary>作业墙：绑定 HomeworkStore，emit 事件供 UI 刷新。</summary>
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

/// <summary>事件流：绑定 ActivityFeed。</summary>
public sealed class EventsViewModel : ViewModelBase
{
    private readonly ActivityFeed _feed;

    public EventsViewModel() : this(new ActivityFeed()) { }

    public EventsViewModel(ActivityFeed feed)
    {
        _feed = feed;
        Refresh();
    }

    public ObservableCollection<ActivityEntry> Entries { get; } = new();

    public void Refresh()
    {
        Entries.Clear();
        foreach (var e in _feed.Entries)
            Entries.Add(e);
    }

    public void Post(string kind, string title, string detail)
    {
        _feed.Append(kind, title, detail);
        Refresh();
    }
}
