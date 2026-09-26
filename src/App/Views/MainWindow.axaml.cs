using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using SmartClassroom.App.ViewModels;

namespace SmartClassroom.App.Views;

public partial class MainWindow : Window
{
    private readonly HomeworkViewModel _homeVm;
    private readonly EventsViewModel _eventsVm;
    private readonly CoursewareViewModel _coursewareVm = new();
    private readonly SettingsViewModel _settingsVm = new();
    private readonly DispatcherTimer _refreshTimer;

    public MainWindow()
    {
        InitializeComponent();
        _homeVm = new HomeworkViewModel(Runtime.Homework);
        _eventsVm = new EventsViewModel(Runtime.Feed, Runtime.Pending, Runtime.Pipeline);
        Navigate("home");
        NavView.SelectedItem = NavView.MenuItems[0];
        _refreshTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background,
            (_, _) => RefreshActive());
        _refreshTimer.Start();
        // 关闭/退出前把设置页里还没落盘的输入（服务地址、API Key…）存掉。
        // 否则"打完 key 直接关窗口"就会白打——用户看到的现象是"更新后 key 又没了"。
        Closing += (_, _) => _settingsVm.FlushPendingSaves();
        Closed += (_, _) => _refreshTimer.Stop();
    }


    private void NavView_SelectionChanged(object? sender, NavigationViewSelectionChangedEventArgs e)
    {
        if (e.SelectedItem is NavigationViewItem item && item.Tag is string tag)
            Navigate(tag);
    }

    public void Navigate(string tag)
    {
        _settingsVm.FlushPendingSaves();   // 离开设置页也立刻落盘
        switch (tag)
        {
            case "home":
                PageTitle.Text = "作业";
                _homeVm.Refresh();
                PageHost.Content = new HomeworkView { DataContext = _homeVm };
                break;
            case "events":
                PageTitle.Text = "事件";
                _eventsVm.Refresh();
                PageHost.Content = new EventsView { DataContext = _eventsVm };
                break;
            case "courseware":
                PageTitle.Text = "课件";
                RebuildCourseware();
                PageHost.Content = new CoursewareListView { DataContext = _coursewareVm };
                break;
            default:
                PageTitle.Text = "设置";
                PageHost.Content = new SettingsView { DataContext = _settingsVm };
                break;
        }
    }

    private void RefreshActive()
    {
        if (PageHost.Content is HomeworkView)
            _homeVm.Refresh();
        else if (PageHost.Content is EventsView)
            _eventsVm.Refresh();
        else if (PageHost.Content is CoursewareListView)
            RebuildCourseware();
    }

    private void RebuildCourseware()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        _coursewareVm.Items.Clear();
        foreach (var f in Runtime.Courseware.QueryDay(today))
        {
            if (f.LocalPath is null)
                continue;
            _coursewareVm.Items.Add(CoursewareViewModel.CreateItem(f.FileName, f.LocalPath, f.Size));
        }
    }
}
