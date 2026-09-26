using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using SmartClassroom.App.ViewModels;
using SmartClassroom.App.Views;
using SmartClassroom.Contracts;
using SmartClassroom.Core;
using Xunit;

namespace SmartClassroom.App.Tests;

public sealed class FeedViewsTests
{
    private static HomeworkItem Hw() => new()
    {
        HomeworkId = "h1", Subject = "数学", Date = new DateOnly(2026, 9, 25),
        Items = ["练习册P10"],
        Sender = new SenderInfo { UserId = 1 },
        Source = new MessageRef { GroupId = 1, MessageId = 1 }
    };

    [AvaloniaFact]
    public void HomeworkView_RendersItems()
    {
        TestSetup.EnsureApp();
        var store = new HomeworkStore();
        store.AddOrMerge(Hw());
        var view = new HomeworkView { DataContext = new HomeworkViewModel(store) };
        var window = new Window { Content = view };
        window.Show();
        Assert.False(((HomeworkViewModel)view.DataContext!).IsEmpty);
        Assert.Single(((HomeworkViewModel)view.DataContext!).Items);
        window.Close();
    }

    [AvaloniaFact]
    public void EventsView_RendersEntries()
    {
        TestSetup.EnsureApp();
        var feed = new ActivityFeed();
        feed.Append("summon", "请小明过去", "张老师：来一下");
        var view = new EventsView { DataContext = new EventsViewModel(feed) };
        var window = new Window { Content = view };
        window.Show();
        Assert.Single(((EventsViewModel)view.DataContext!).Entries);
        window.Close();
    }
}
