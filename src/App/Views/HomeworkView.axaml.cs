using Avalonia.Controls;
using SmartClassroom.App.ViewModels;

namespace SmartClassroom.App.Views;

public partial class HomeworkView : UserControl
{
    public HomeworkView()
    {
        InitializeComponent();
        DataContext ??= new HomeworkViewModel();
    }
}
