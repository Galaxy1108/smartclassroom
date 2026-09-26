using Avalonia.Headless.XUnit;
using SmartClassroom.App.ViewModels;
using SmartClassroom.Core;
using Xunit;

namespace SmartClassroom.App.Tests;

/// <summary>
/// 归档设置。
/// 之前「指定文件夹」这条需求其实没做完——根目录在设置页没有任何入口，
/// 只能用写死的默认路径，用户根本找不到文件去了哪。
/// </summary>
public sealed class ArchiveSettingsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "sc-archive-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [AvaloniaFact]
    public void EmptyRoot_FallsBackToDefault_AndShowsIt()
    {
        var vm = new SettingsViewModel(_path);
        Assert.Equal("", vm.ArchiveRoot);
        // 界面要显示真正生效的目录，否则用户不知道文件去哪了
        Assert.Equal(Runtime.DefaultArchiveRoot, vm.EffectiveArchiveRoot);
        Assert.Contains("archive", vm.EffectiveArchiveRoot);
    }

    [AvaloniaFact]
    public void CustomRoot_PersistsAndBecomesEffective()
    {
        var custom = Path.Combine(Path.GetTempPath(), "sc-my-archive");
        var vm = new SettingsViewModel(_path) { ArchiveRoot = custom };

        Assert.Equal(custom, vm.EffectiveArchiveRoot);
        Assert.Equal(custom, new SettingsViewModel(_path).ArchiveRoot);
    }

    [AvaloniaFact]
    public void DownloadAll_DefaultsOff_AndPersists()
    {
        var vm = new SettingsViewModel(_path);
        Assert.False(vm.ArchiveDownloadAll);      // 默认只下载教师

        vm.ArchiveDownloadAll = true;
        Assert.True(new SettingsViewModel(_path).ArchiveDownloadAll);
    }

    [Fact]
    public void ArchiveOptions_DefaultDownloadAllIsFalse()
        => Assert.False(new ArchiveOptions { Root = "/tmp" }.DownloadAll);
}
