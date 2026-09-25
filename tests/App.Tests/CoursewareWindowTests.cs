using Avalonia.Headless.XUnit;
using SmartClassroom.App.ViewModels;
using SmartClassroom.App.Views;
using Xunit;

namespace SmartClassroom.App.Tests;

public sealed class CoursewareWindowTests
{
    [AvaloniaFact]
    public void CoursewareWindow_ShowsItemsWithTitle()
    {
        var png = Path.Combine(Path.GetTempPath(), "sc-thumb-test.png");
        // 最小合法 PNG（1x1），避免依赖外部图片库。
        File.WriteAllBytes(png, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="));
        try
        {
            var vm = CoursewareViewModel.FromFiles([
                ("课件.pptx", Path.Combine(Path.GetTempPath(), "no-such-file.pptx"), 12345678),
                ("板书.png", png, 1234),
            ]);
            var window = new CoursewareWindow { DataContext = vm };
            window.Show();
            Assert.Equal("您可能需要的课件", window.Title);
            Assert.Equal(2, vm.Items.Count);
            Assert.False(vm.Items[0].IsImage);
            Assert.Equal("PPTX", vm.Items[0].TypeLabel);
            Assert.True(vm.Items[1].IsImage);
            Assert.NotNull(vm.Items[1].Thumbnail);
            window.Close();
        }
        finally
        {
            File.Delete(png);
        }
    }
}
