using Avalonia.Headless.XUnit;
using SmartClassroom.App.ViewModels;
using SmartClassroom.App.Views;
using SmartClassroom.Core;
using Xunit;

namespace SmartClassroom.App.Tests;

/// <summary>作业页的手动添加（不依赖 AI、不依赖功能开关）。</summary>
public sealed class HomeworkManualAddTests
{
    [AvaloniaFact]
    public void SubmitAdd_RequiresSubjectAndItems()
    {
        var vm = new HomeworkViewModel(new HomeworkStore());
        vm.BeginAdd();

        vm.FormSubject = "";
        Assert.False(vm.SubmitAdd());
        Assert.Contains("科目", vm.AddResult);
        Assert.True(vm.IsAdding);            // 校验失败保留表单

        vm.FormSubject = "数学";
        vm.FormItems = "   \n  ";
        Assert.False(vm.SubmitAdd());
        Assert.Contains("至少", vm.AddResult);
    }

    [AvaloniaFact]
    public void SubmitAdd_AddsToWall()
    {
        var store = new HomeworkStore();
        var vm = new HomeworkViewModel(store);
        vm.BeginAdd();
        vm.FormSubject = "数学";
        var today = DateOnly.FromDateTime(DateTime.Now);
        vm.FormDate = today.ToString("yyyy-MM-dd");
        vm.FormItems = "练习册P10\n试卷一张";
        vm.FormDue = "明天";

        Assert.True(vm.SubmitAdd());

        Assert.False(vm.IsAdding);
        Assert.False(vm.IsEmpty);
        var item = Assert.Single(vm.Items);
        Assert.Equal("数学", item.Subject);
        Assert.Equal(today, item.Date);
        Assert.Equal(2, item.Items.Count);
        Assert.Equal("明天", item.Due);
        Assert.Equal(today.ToString("MM-dd"), item.DateLabel);
        Assert.Equal("手动添加", item.Sender);
        Assert.True(item.IsManual);
        Assert.Equal("今天", item.RelativeDay);
    }

    [AvaloniaFact]
    public void SubmitAdd_MergesWithSameSubjectAndDay()
    {
        var store = new HomeworkStore();
        var vm = new HomeworkViewModel(store);

        vm.BeginAdd();
        vm.FormSubject = "数学";
        vm.FormDate = "2026-09-25";
        vm.FormItems = "练习册P10";
        vm.SubmitAdd();

        vm.BeginAdd();
        vm.FormSubject = "数学";
        vm.FormDate = "2026-09-25";
        vm.FormItems = "练习册P10\n试卷一张";   // 含重复项
        vm.SubmitAdd();

        var item = Assert.Single(vm.Items);      // 同科目同日合并
        Assert.Equal(2, item.Items.Count);       // 重复项被去重
    }

    [AvaloniaFact]
    public void InvalidDate_FallsBackToToday()
    {
        var vm = new HomeworkViewModel(new HomeworkStore());
        vm.BeginAdd();
        vm.FormSubject = "语文";
        vm.FormDate = "不是日期";
        vm.FormItems = "背诵课文";

        Assert.True(vm.SubmitAdd());
        Assert.Equal(DateOnly.FromDateTime(DateTime.Now), Assert.Single(vm.Items).Date);
    }

    [AvaloniaFact]
    public void CancelAdd_ClosesFormWithoutAdding()
    {
        var vm = new HomeworkViewModel(new HomeworkStore());
        vm.BeginAdd();
        vm.FormSubject = "数学";
        vm.CancelAdd();

        Assert.False(vm.IsAdding);
        Assert.True(vm.IsEmpty);
    }

    [AvaloniaFact]
    public void HomeworkView_Builds()
    {
        var vm = new HomeworkViewModel(new HomeworkStore());
        var view = new HomeworkView { DataContext = vm };
        Assert.Same(vm, view.DataContext);
        Assert.True(vm.IsEmpty);
    }

    [AvaloniaFact]
    public void Items_AreNumberedFromOne()
    {
        var vm = new HomeworkViewModel(new HomeworkStore());
        vm.BeginAdd();
        vm.FormSubject = "数学";
        vm.FormItems = "练习册P10\n试卷一张\n预习第三节";
        Assert.True(vm.SubmitAdd());

        var card = Assert.Single(vm.Items);
        Assert.Equal(3, card.Items.Count);
        Assert.Equal([1, 2, 3], card.Items.Select(i => i.Number));
        Assert.Equal("练习册P10", card.Items[0].Text);
        Assert.Equal("预习第三节", card.Items[2].Text);
    }

    [AvaloniaFact]
    public void AccentColor_IsStablePerSubject()
    {
        var a = new HomeworkCard(ManualHw("数学"), DateTime.Now);
        var b = new HomeworkCard(ManualHw("数学"), DateTime.Now);
        var c = new HomeworkCard(ManualHw("语文"), DateTime.Now);
        Assert.Equal(a.AccentColor, b.AccentColor);   // 同科目同色
        Assert.NotNull(c.AccentColor);
    }

    private static SmartClassroom.Contracts.HomeworkItem ManualHw(string subject) => new()
    {
        HomeworkId = System.Guid.NewGuid().ToString(),
        Subject = subject,
        Date = DateOnly.FromDateTime(DateTime.Now),
        Items = ["x"],
        Sender = new SmartClassroom.Contracts.SenderInfo { UserId = 0 },
        Source = new SmartClassroom.Contracts.MessageRef { GroupId = 0, MessageId = 0 }
    };

    [AvaloniaFact]
    public void MoveItemLive_ReordersWithoutRebuildingCards()
    {
        var store = new HomeworkStore();
        var vm = new HomeworkViewModel(store);
        foreach (var s in new[] { "数学", "语文", "英语" })
        {
            vm.BeginAdd();
            vm.FormSubject = s;
            vm.FormItems = "x";
            vm.SubmitAdd();
        }
        // 新条目置顶 → 英语, 语文, 数学
        var cardToMove = vm.Items[2];              // 数学
        var firstCard = vm.Items[0];

        Assert.True(vm.MoveItemLive(2, 0));

        Assert.Equal("数学", vm.Items[0].Subject);
        // 关键：现有卡片对象被复用（不是 Clear + 重建），否则拖拽中的指针捕获会丢
        Assert.Same(cardToMove, vm.Items[0]);
        Assert.Same(firstCard, vm.Items[1]);
    }

    [AvaloniaFact]
    public void MoveItemLive_IgnoresNoOpAndOutOfRange()
    {
        var store = new HomeworkStore();
        var vm = new HomeworkViewModel(store);
        vm.BeginAdd();
        vm.FormSubject = "数学";
        vm.FormItems = "x";
        vm.SubmitAdd();

        Assert.False(vm.MoveItemLive(0, 0));
        Assert.False(vm.MoveItemLive(5, 0));
        Assert.False(vm.MoveItemLive(-1, 0));
    }

    [AvaloniaFact]
    public void SuspendRefresh_PreventsRebuildDuringDrag()
    {
        var store = new HomeworkStore();
        var vm = new HomeworkViewModel(store);
        vm.BeginAdd();
        vm.FormSubject = "数学";
        vm.FormItems = "x";
        vm.SubmitAdd();
        var card = vm.Items[0];

        vm.SuspendRefresh = true;
        vm.Refresh();                              // 拖拽期间定时器触发的刷新

        Assert.Same(card, vm.Items[0]);            // 没有被重建

        vm.SuspendRefresh = false;
        vm.Refresh();
        Assert.Equal(1, vm.Items.Count);
    }
}
