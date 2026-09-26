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

    /// <summary>预览上课时的推荐弹窗（真实触发走上课事件，这里看效果）。</summary>
    private void Preview_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not CoursewareViewModel vm || vm.Items.Count == 0)
            return;
        var preview = new CoursewareViewModel();
        foreach (var item in vm.Items)
            preview.Items.Add(item);
        new CoursewareWindow { DataContext = preview }.Show();
    }
}
