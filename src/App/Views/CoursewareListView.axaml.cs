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

    private void Card_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Border)?.DataContext is CoursewareItem item
            && File.Exists(item.LocalPath))
        {
            try { CoursewareViewModel.Open(item.LocalPath); }
            catch { }
        }
    }

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
