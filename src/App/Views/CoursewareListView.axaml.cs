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
}
