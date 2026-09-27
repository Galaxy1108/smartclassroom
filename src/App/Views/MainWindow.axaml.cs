using Avalonia.Interactivity;
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
        // 全局缩放 + 主题：启动就应用一次，之后跟着设置变
        ApplyZoom(ContentZoom.Scale);
        ContentZoom.Changed += ApplyZoom;
        ApplyTheme(AppTheme.Current);
        AppTheme.Changed += ApplyTheme;
        _homeVm = new HomeworkViewModel(Runtime.Homework, () => Runtime.Settings.SubjectColors);
        _eventsVm = new EventsViewModel(Runtime.Feed, Runtime.Pending, Runtime.Pipeline);
        ToastList.ItemsSource = Toasts.Items;   // 右下角的应用内通知
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
        // 内置的「设置」项固定在左下角（WinUI 的惯例），它没有 Tag
        if (e.IsSettingsSelected)
        {
            Navigate("settings");
            return;
        }
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

    /// <summary>课件页：全部归档文件按科目分组（第一层），点进科目看时间轴。</summary>
    private void RebuildCourseware()
    {
        _coursewareVm.GroupBySubject(Runtime.Courseware.QueryAll()
            .Where(f => f.LocalPath is not null)
            .Select(f => (f.Subject ?? "", f.FileName, f.LocalPath!, f.Size, f.ArchivedAt)));
    }

    /// <summary>关掉一条通知（不等它自动消失）。</summary>
    private void ToastClose_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is ToastItem item)
            Toasts.Remove(item);
    }

    /// <summary>整窗缩放（含导航与页面）。</summary>
    private void ApplyZoom(double scale)
        => ZoomHost.LayoutTransform = new Avalonia.Media.ScaleTransform(scale, scale);

    /// <summary>主题按钮的图标与文字跟着当前主题走。</summary>
    private void ApplyTheme(string theme)
    {
        ThemeLabel.Text = AppTheme.Describe(theme);
        ThemeIcon.Data = theme switch
        {
            AppTheme.Light => (Avalonia.Media.Geometry)Application.Current!.FindResource("IconSun"),
            AppTheme.Dark => (Avalonia.Media.Geometry)Application.Current!.FindResource("IconMoon"),
            _ => (Avalonia.Media.Geometry)Application.Current!.FindResource("IconTheme")
        };
    }

    /// <summary>点击循环：跟随系统 → 浅色 → 深色 → 跟随系统。</summary>
    private void Theme_Click(object? sender, RoutedEventArgs e)
    {
        var next = AppTheme.Current switch
        {
            AppTheme.System => AppTheme.Light,
            AppTheme.Light => AppTheme.Dark,
            _ => AppTheme.System
        };
        AppTheme.Apply(next);
        Runtime.Settings.Theme = next;
        SmartClassroom.Core.SettingsStore.Save(Runtime.Settings);
    }
}
