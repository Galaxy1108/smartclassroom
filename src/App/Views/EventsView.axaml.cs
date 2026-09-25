using Avalonia.Controls;
using SmartClassroom.App.ViewModels;

namespace SmartClassroom.App.Views;

public partial class EventsView : UserControl
{
    public EventsView()
    {
        InitializeComponent();
        DataContext ??= new EventsViewModel();
    }
}
