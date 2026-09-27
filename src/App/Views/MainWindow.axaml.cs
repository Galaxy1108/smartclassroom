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

    private double _appliedScale = 1.0;

    /// <summary>
    /// 整窗缩放（含导航与页面）。
    ///
    /// 同时按比例调整窗口尺寸：缩放后逻辑可视区域会变小，窗口不跟着变大
    /// 就会把右侧内容裁掉（实测 130% 时右上角的主题按钮直接被切没了）。
    /// 未最大化时按比例放大，并限制在工作区内。
    /// </summary>
    private void ApplyZoom(double scale)
    {
        ZoomHost.LayoutTransform = new Avalonia.Media.ScaleTransform(scale, scale);

        if (Math.Abs(scale - _appliedScale) < 0.001)
            return;
        var ratio = scale / _appliedScale;
        _appliedScale = scale;

        if (WindowState != WindowState.Normal)
            return;   // 最大化/全屏时不用动，屏幕就那么大

        // ⚠️ 取屏幕信息必须包起来：窗口还没挂到屏幕上时 ScreenFromWindow 会抛异常，
        // 而这是在启动路径上（设置里存了缩放值就会调用），一抛就是启动崩溃。
        try
        {
            var screen = Screens?.ScreenFromWindow(this) ?? Screens?.Primary;
            var area = screen?.WorkingArea ?? default;
            var maxWidth = area.Width / (screen?.Scaling ?? 1) - 40;
            var maxHeight = area.Height / (screen?.Scaling ?? 1) - 80;
            Width = maxWidth > 400 ? Math.Min(Width * ratio, maxWidth) : Width * ratio;
            Height = maxHeight > 300 ? Math.Min(Height * ratio, maxHeight) : Height * ratio;
        }
        catch (Exception)
        {
            // 拿不到屏幕信息就不调窗口尺寸，缩放本身已经生效
        }
    }

    /// <summary>
    /// 主题按钮的图标与文字跟着当前主题走。
    ///
    /// ⚠️ 这里**绝不能抛异常**：`FindResource` 找不到键时返回 UnsetValue，
    /// 直接强转会 InvalidCastException —— 而这是在**主窗口构造函数**里调用的，
    /// 一抛就是"应用启动即崩溃"（实测：窗口变成一块黑框 + core dump）。
    /// 所以用 TryFindResource，取不到就只是没图标。
    /// </summary>
    private void ApplyTheme(string theme)
    {
        ThemeLabel.Text = AppTheme.Describe(theme);
        ThemeIcon.Data = theme switch
        {
            AppTheme.Light => LookupGeometry("IconSun"),
            AppTheme.Dark => LookupGeometry("IconMoon"),
            _ => LookupGeometry("IconThemeAuto")
        };
    }

    private static Geometry? LookupGeometry(string key)
    {
        try
        {
            // TryFindResource 需要 out 参数；找到就返回，找不到返回 null（不抛）
            return Application.Current is { } app
                   && app.TryFindResource(key, out var value)
                   && value is Geometry geometry
                ? geometry
                : null;
        }
        catch (Exception)
        {
            return null;
        }
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
