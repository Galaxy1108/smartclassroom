using Avalonia.Headless.XUnit;
using SmartClassroom.App.ViewModels;
using SmartClassroom.App.Views;
using SmartClassroom.Contracts;
using SmartClassroom.Core;
using Xunit;

namespace SmartClassroom.App.Tests;

/// <summary>事件页的待处理区：列表、预填、无管线时的降级提示。</summary>
public sealed class PendingViewTests
{
    private static PendingItem Item(string kind, string raw) => new()
    {
        Id = Guid.NewGuid().ToString(),
        Kind = kind,
        Title = "标题",
        RawText = raw,
        Reason = "AI 返回 500",
        Sender = new SenderInfo { UserId = 10001, TeacherName = "张老师", Subject = "数学" },
        Source = new MessageRef { GroupId = 1, MessageId = 7 },
        CreatedAt = DateTimeOffset.Now
    };

    [AvaloniaFact]
    public void EmptyPending_HidesSection()
    {
        var vm = new EventsViewModel(new ActivityFeed(), new PendingStore(), null);
        var view = new EventsView { DataContext = vm };

        Assert.False(vm.HasPending);
        Assert.Empty(vm.Pending);
        Assert.Same(vm, view.DataContext);
    }

    [AvaloniaFact]
    public void PendingItems_AreMappedToRows()
    {
        var pending = new PendingStore();
        pending.Add(Item("homework", "今天数学作业：练习册P10"));
        var vm = new EventsViewModel(new ActivityFeed(), pending, null);

        Assert.True(vm.HasPending);
        var row = Assert.Single(vm.Pending);
        Assert.Equal("homework", row.Kind);
        Assert.Equal("作业", row.KindLabel);
        Assert.Equal("张老师", row.Sender);
        Assert.Contains("练习册P10", row.RawText); // 原文完整保留，不截断
        Assert.Equal("待处理（1）", vm.PendingHeader);  // 计数出现在标题里
    }

    [AvaloniaFact]
    public void BeginEdit_PrefillsTeacherSubjectAndUrgency()
    {
        var pending = new PendingStore();
        pending.Add(Item("summon", "小明现在来一下"));
        var vm = new EventsViewModel(new ActivityFeed(), pending, null);

        vm.BeginEdit(vm.Pending[0]);

        Assert.True(vm.IsEditing);
        Assert.True(vm.IsEditingSummon);
        Assert.True(vm.FormUrgent);              // 原文含"现在" → 预勾选
        Assert.Equal("", vm.FormTarget);          // 被叫人需要人来填
    }

    [AvaloniaFact]
    public void BeginEdit_HomeworkPrefillsSubjectFromTeacherMap()
    {
        var pending = new PendingStore();
        pending.Add(Item("homework", "今天作业：练习册P10"));
        var vm = new EventsViewModel(new ActivityFeed(), pending, null);

        vm.BeginEdit(vm.Pending[0]);

        Assert.True(vm.IsEditingHomework);
        Assert.Equal("数学", vm.FormSubject);     // 来自教师映射的科目
    }

    [AvaloniaFact]
    public async Task WithoutPipeline_ActionsDegradeGracefully()
    {
        var pending = new PendingStore();
        pending.Add(Item("homework", "x"));
        var vm = new EventsViewModel(new ActivityFeed(), pending, null);

        await vm.RetryAsync(vm.Pending[0]);
        Assert.Contains("管线未启动", vm.ActionResult);

        await vm.SubmitEditAsync();
        Assert.Contains("管线未启动", vm.ActionResult);
        Assert.Contains("未配置", vm.PendingHint);
    }

    [AvaloniaFact]
    public void CancelEdit_ClosesForm()
    {
        var pending = new PendingStore();
        pending.Add(Item("exchange", "换课"));
        var vm = new EventsViewModel(new ActivityFeed(), pending, null);

        vm.BeginEdit(vm.Pending[0]);
        Assert.True(vm.IsEditingExchange);

        vm.CancelEdit();
        Assert.False(vm.IsEditing);
    }
}
