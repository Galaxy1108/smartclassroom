using Avalonia.Headless.XUnit;
using SmartClassroom.App.ViewModels;
using SmartClassroom.Core;
using Xunit;

namespace SmartClassroom.App.Tests;

/// <summary>界面与字体缩放：范围钳制、标签、持久化、立即生效。</summary>
public sealed class UiScaleTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "sc-scale-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
        UiScaling.Scale = UiScaling.Default;
    }

    [Theory]
    [InlineData(0.1, 0.8)]
    [InlineData(0.8, 0.8)]
    [InlineData(1.0, 1.0)]
    [InlineData(1.6, 1.6)]
    [InlineData(9.9, 1.6)]
    [InlineData(double.NaN, 1.0)]
    public void Clamp_KeepsWithinRange(double input, double expected)
        => Assert.Equal(expected, UiScaling.Clamp(input));

    [Fact]
    public void Describe_ShowsPercent()
    {
        Assert.Equal("100%", UiScaling.Describe(1.0));
        Assert.Equal("130%", UiScaling.Describe(1.3));
    }

    [AvaloniaFact]
    public void SettingScale_AppliesImmediatelyAndPersists()
    {
        var vm = new SettingsViewModel(_path);
        Assert.Equal(1.0, vm.UiScale);
        Assert.Equal("100%", vm.UiScaleLabel);

        vm.UiScale = 1.3;

        Assert.Equal(1.3, vm.UiScale);
        Assert.Equal("130%", vm.UiScaleLabel);
        Assert.Equal(1.3, UiScaling.Scale);                       // 立刻生效
        Assert.Equal(1.3, new SettingsViewModel(_path).UiScale);  // 落盘
    }

    [AvaloniaFact]
    public void SettingScale_IsClamped()
    {
        var vm = new SettingsViewModel(_path);
        vm.UiScale = 5.0;
        Assert.Equal(1.6, vm.UiScale);
    }

    [AvaloniaFact]
    public void ChangedEvent_FiresOnScaleChange()
    {
        var seen = new List<double>();
        void Handler(double s) => seen.Add(s);
        UiScaling.Changed += Handler;
        try
        {
            UiScaling.Scale = 1.2;
            UiScaling.Scale = 1.2;   // 同值不重复触发
            Assert.Equal([1.2], seen);
        }
        finally
        {
            UiScaling.Changed -= Handler;
        }
    }
}
