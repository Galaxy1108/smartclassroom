using Avalonia.Headless.XUnit;
using SmartClassroom.App.ViewModels;
using Xunit;

namespace SmartClassroom.App.Tests;

/// <summary>
/// 课件视图模型：缩略图解码、类型标识、大小格式化。
/// 窗口本身（CoursewareWindow）需要 Compositor，本沙箱无法构造，故只测 VM。
/// </summary>
public sealed class CoursewareTests
{
    [AvaloniaFact]
    public void FromFiles_DecodesImageAndLabelsOthers()
    {
        var png = Path.Combine(Path.GetTempPath(), "sc-thumb-" + Guid.NewGuid().ToString("N") + ".png");
        // 最小合法 PNG（1x1），不引入图片库。
        File.WriteAllBytes(png, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="));
        try
        {
            var vm = CoursewareViewModel.FromFiles([
                ("课件.pptx", Path.Combine(Path.GetTempPath(), "no-such-file.pptx"), 12345678),
                ("板书.png", png, 1234),
            ]);

            Assert.Equal("您可能需要的课件", vm.Title);
            Assert.Equal(2, vm.Items.Count);
            Assert.False(vm.Items[0].IsImage);
            Assert.Equal("PPTX", vm.Items[0].TypeLabel);
            Assert.Equal("11.8 MB", vm.Items[0].SizeText);
            Assert.True(vm.Items[1].IsImage);
            Assert.NotNull(vm.Items[1].Thumbnail);
        }
        finally
        {
            File.Delete(png);
        }
    }
}
