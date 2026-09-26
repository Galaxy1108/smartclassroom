using Avalonia.Headless.XUnit;
using SmartClassroom.App.ViewModels;
using Xunit;

namespace SmartClassroom.App.Tests;

/// <summary>
/// 主视图模型冒烟。
/// 注意：本沙箱的 headless Skia 无法为 FluentAvalonia 内嵌 Symbols 字体建 glyphTypeface，
/// 而构造任何 Window 都会立刻创建 Compositor 并触发该路径，因此窗口级渲染断言不在此处做。
/// 窗口装配由 views 的构造用例与真机冒烟覆盖。
/// </summary>
public sealed class ShellSmokeTests
{
    [AvaloniaFact]
    public void MainViewModel_HasTitleAndStatus()
    {
        var vm = new MainViewModel();
        Assert.Equal("智慧课堂", vm.Title);
        Assert.False(string.IsNullOrWhiteSpace(vm.StatusText));
        vm.StatusText = "已连接";
        Assert.Equal("已连接", vm.StatusText);
    }
}
