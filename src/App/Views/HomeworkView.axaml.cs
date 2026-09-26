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

    private HomeworkViewModel Vm => (HomeworkViewModel)DataContext!;

    private void BeginAdd_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Vm.BeginAdd();

    private void SubmitAdd_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Vm.SubmitAdd())
            Runtime.SaveState(); // 手动添加后立刻落盘，不等 30 秒定时器
    }

    private void CancelAdd_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Vm.CancelAdd();
}
