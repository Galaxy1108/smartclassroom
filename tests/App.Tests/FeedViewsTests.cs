using Avalonia.Threading;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Controls;
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
        // 用今天：过期作业默认被收起（见 HomeworkExpiryTests），这里测的是绑定
        HomeworkId = "h1", Subject = "数学", Date = DateOnly.FromDateTime(DateTime.Now),
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
        var vm = new EventsViewModel(feed, new PendingStore(), null);
        var view = new EventsView { DataContext = vm };

        var entry = Assert.Single(vm.Entries);
        Assert.Equal("请小明过去", entry.Title);
        Assert.Same(vm, view.DataContext);
    }
}

/// <summary>
/// 科目颜色可自定义（用户："我希望我能修改作业卡片的颜色"）。
/// 没配的科目仍按科目名稳定取默认色板里的颜色（同一科目每次颜色一致）。
/// </summary>
public sealed class SubjectColorTests
{
    private static HomeworkItem Item(string subject) => new()
    {
        HomeworkId = Guid.NewGuid().ToString(),
        Subject = subject,
        Date = DateOnly.FromDateTime(DateTime.Now),
        Items = ["练习册P10"],
        Sender = new SmartClassroom.Contracts.SenderInfo { UserId = 1, TeacherName = "张老师", Subject = subject },
        Source = new SmartClassroom.Contracts.MessageRef { GroupId = 1, MessageId = 1 }
    };

    [AvaloniaFact]
    public void CustomColor_Wins_OverPalette()
    {
        var store = new HomeworkStore();
        store.AddOrMerge(Item("数学"));
        var colors = new Dictionary<string, string> { ["数学"] = "#0078D4" };

        var vm = new HomeworkViewModel(store, () => colors);

        Assert.Equal("#0078D4", vm.Items[0].AccentColor);
    }

    [AvaloniaFact]
    public void UnconfiguredSubject_UsesStablePaletteColor()
    {
        var store = new HomeworkStore();
        store.AddOrMerge(Item("语文"));

        var a = new HomeworkViewModel(store, () => new Dictionary<string, string>());
        var b = new HomeworkViewModel(store, () => new Dictionary<string, string>());

        Assert.StartsWith("#", a.Items[0].AccentColor);
        Assert.Equal(a.Items[0].AccentColor, b.Items[0].AccentColor);   // 稳定：同一科目同一颜色
    }

    [AvaloniaFact]
    public void ChangingColor_RefreshesCards()
    {
        var store = new HomeworkStore();
        store.AddOrMerge(Item("数学"));
        var colors = new Dictionary<string, string>();
        var vm = new HomeworkViewModel(store, () => colors);
        var before = vm.Items[0].AccentColor;

        colors["数学"] = "#123456";   // 故意用色板里没有的颜色
        vm.Refresh();

        Assert.NotEqual(before, vm.Items[0].AccentColor);
        Assert.Equal("#123456", vm.Items[0].AccentColor);
    }
}

/// <summary>
/// 科目颜色：改色必须落到设置里并反映到卡片。
/// 踩过的坑：选色盘的 Color 绑定默认是单向，拖完色盘等于没改（用户实测"选色没有生效"），
/// 所以 XAML 里显式写了 Mode=TwoWay；这里把"改了要生效"这条链锁住。
/// </summary>
public sealed class SubjectColorPickerTests
{
    [AvaloniaFact]
    public void PickerColor_WritesThroughToSettingsAndCards()
    {
        var colors = new Dictionary<string, string>();
        var changed = 0;
        var row = new SubjectColorRow("英语", "默认");
        row.Changed += (subject, color) => { colors[subject] = color; changed++; };

        row.PickerColor = Avalonia.Media.Color.Parse("#FF8800");

        Assert.Equal(1, changed);
        Assert.Equal("#FF8800", colors["英语"]);
        Assert.Equal("#FF8800", row.Color);
    }

    [AvaloniaFact]
    public void PickerColor_ReflectsCurrentValue()
    {
        var row = new SubjectColorRow("数学", "#0F7B0F");
        Assert.Equal(Avalonia.Media.Color.Parse("#0F7B0F"), row.PickerColor);
    }
}

/// <summary>
/// 右键卡片要能弹出编辑/删除菜单。
/// 踩过的坑：用 Control.Parent（逻辑树）找卡片，模板子控件的逻辑父级是 null
/// → 找不到卡片 → 菜单怎么点都不弹（用户连报两次）。
/// 这里真的往卡片上发一次右键 PointerPressed，断言菜单弹出。
/// </summary>
public sealed class HomeworkContextMenuTests
{
    [AvaloniaFact]
    public void RightClickOnCard_OpensMenu()
    {
        var store = new HomeworkStore();
        store.AddOrMerge(new HomeworkItem
        {
            HomeworkId = "h1",
            Subject = "英语",
            Date = DateOnly.FromDateTime(DateTime.Now),
            Items = ["背单词"],
            Sender = new SmartClassroom.Contracts.SenderInfo { UserId = 0, TeacherName = "手动添加" },
            Source = new SmartClassroom.Contracts.MessageRef { GroupId = 0, MessageId = 0 }
        });

        var view = new SmartClassroom.App.Views.HomeworkView
        {
            DataContext = new HomeworkViewModel(store, () => new Dictionary<string, string>())
        };
        var window = new Window { Width = 900, Height = 600, Content = view };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // 找到卡片上的任意一个可见控件（就是用户右键点到的东西）
        var target = view.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(t => t.Text == "英语");
        Assert.NotNull(target);

        var args = new Avalonia.Input.PointerPressedEventArgs(
            target!, new Avalonia.Input.Pointer(0, Avalonia.Input.PointerType.Mouse, true), target!, default,
            0, new Avalonia.Input.PointerPointProperties(Avalonia.Input.RawInputModifiers.RightMouseButton, Avalonia.Input.PointerUpdateKind.RightButtonPressed),
            Avalonia.Input.KeyModifiers.None);
        target!.RaiseEvent(args);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, view.MenuOpenCount);
        window.Close();
    }

    /// <summary>
    /// 卡片右上角的 ⋯ 按钮在他环境里收不到点击，所以改成了**工具栏按钮**：
    /// 点卡片选中 → 工具栏「编辑/删除」可用。这条路径和「手动添加作业」同一层，
    /// 不经过卡片上的指针处理，一定能点到。
    /// </summary>
    [AvaloniaFact]
    public void SelectingCard_EnablesToolbarActions()
    {
        var store = new HomeworkStore();
        store.AddOrMerge(new HomeworkItem
        {
            HomeworkId = "h1", Subject = "英语", Date = DateOnly.FromDateTime(DateTime.Now),
            Items = ["背单词"],
            Sender = new SmartClassroom.Contracts.SenderInfo { UserId = 0, TeacherName = "手动添加" },
            Source = new SmartClassroom.Contracts.MessageRef { GroupId = 0, MessageId = 0 }
        });
        var vm = new HomeworkViewModel(store, () => new Dictionary<string, string>());
        var view = new SmartClassroom.App.Views.HomeworkView { DataContext = vm };
        var window = new Window { Width = 900, Height = 600, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.HasSelection);
        vm.Select(vm.Items[0]);
        Assert.True(vm.HasSelection);
        Assert.True(vm.Items[0].IsSelected);
        Assert.Contains("编辑", view.GetVisualDescendants().OfType<Button>()
            .Select(b => b.Content as string));
        window.Close();
    }
}
