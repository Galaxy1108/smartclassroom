using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using SmartClassroom.App.ViewModels;

namespace SmartClassroom.App.Views;

public partial class CoursewareListView : UserControl
{
    public CoursewareListView()
    {
        InitializeComponent();
        DataContext ??= new CoursewareViewModel();
    }

    private CoursewareViewModel Vm => (CoursewareViewModel)DataContext!;

    private void Card_DoubleTapped(object? sender, TappedEventArgs e)
    {
        // 时间轴一行是 CoursewareTimelineRow（里面才是 CoursewareItem），老卡片直接是 Item
        var path = (sender as Border)?.DataContext switch
        {
            CoursewareTimelineRow row => row.Item.LocalPath,
            CoursewareItem item => item.LocalPath,
            _ => null
        };
        if (path is not null && File.Exists(path))
        {
            try { CoursewareViewModel.Open(path); }
            catch { }
        }
    }

    /// <summary>点科目卡片进入时间轴（双击也走这里，避免重复打开）。</summary>
    private void Subject_Tapped(object? sender, TappedEventArgs e)
        => OpenSubject(sender);

    private void Subject_DoubleTapped(object? sender, TappedEventArgs e)
        => OpenSubject(sender);

    private void OpenSubject(object? sender)
    {
        if ((sender as Border)?.DataContext is CoursewareSubject subject)
            Vm.OpenSubject(subject.Name);
    }

    private void Back_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Vm.BackToSubjects();

    /// <summary>
    /// 预览上课时的推荐弹窗（真实触发走上课事件 + 功能开关）。
    /// 即使当天没有课件也照常打开并显示空状态——之前空列表直接 return，
    /// 用户点了没反应，会误以为弹窗坏了。
    /// </summary>
    private void Preview_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var preview = new CoursewareViewModel();
        if (DataContext is CoursewareViewModel vm)
        {
            foreach (var item in vm.Items)
                preview.Items.Add(item);
        }
        new CoursewareWindow { DataContext = preview }.Show();
    }
}
