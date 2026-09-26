using Avalonia.Headless.XUnit;
using SmartClassroom.App.ViewModels;
using SmartClassroom.App.Views;
using SmartClassroom.Contracts;
using SmartClassroom.Core;
using Xunit;

namespace SmartClassroom.App.Tests;

/// <summary>作业/事件页：XAML 可构造、绑定到真实存储后数据正确。</summary>
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
    public void HomeworkView_BindsToStore()
    {
        var store = new HomeworkStore();
        store.AddOrMerge(Hw());
        var vm = new HomeworkViewModel(store);
        var view = new HomeworkView { DataContext = vm };

        Assert.False(vm.IsEmpty);
        Assert.Single(vm.Items);
        Assert.Equal("数学", vm.Items[0].Subject);
        Assert.Same(vm, view.DataContext);
    }

    [AvaloniaFact]
    public void EventsView_BindsToFeed()
    {
        var feed = new ActivityFeed();
        feed.Append("summon", "请小明过去", "张老师：来一下");
        var vm = new EventsViewModel(feed);
        var view = new EventsView { DataContext = vm };

        var entry = Assert.Single(vm.Entries);
        Assert.Equal("请小明过去", entry.Title);
        Assert.Same(vm, view.DataContext);
    }
}
