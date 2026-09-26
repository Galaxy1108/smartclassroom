using System.Diagnostics;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SmartClassroom.App.ViewModels;

namespace SmartClassroom.App.Views;

public partial class CoursewareWindow : Window
{
    public CoursewareWindow()
    {
        InitializeComponent();
        AddHandler(PointerWheelChangedEvent, OnWheelZoom, RoutingStrategies.Tunnel);
    }

    /// <summary>课件弹窗同样支持 Ctrl+滚轮缩放（与主界面共用同一档位）。</summary>
    private void OnWheelZoom(object? sender, PointerWheelEventArgs e) => UiScaleGesture.Handle(e);

    private void Card_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Border)?.DataContext is CoursewareItem item
            && File.Exists(item.LocalPath))
        {
            try { CoursewareViewModel.Open(item.LocalPath); }
            catch { /* 打开失败静默，文件缺失由上层过滤 */ }
        }
    }

    private void OpenFolder_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is CoursewareViewModel vm && vm.Items.Count > 0)
        {
            var dir = Path.GetDirectoryName(vm.Items[0].LocalPath);
            if (dir is not null)
                try
                {
                    Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
                }
                catch { }
        }
    }

    private void Close_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();
}
