using System.Runtime.InteropServices;
using SmartClassroom.Core.QQ;
using Xunit;

namespace SmartClassroom.Core.Tests;

public sealed class AutostartManagerTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "sc-auto-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tmp))
            Directory.Delete(_tmp, true);
    }

    [Fact]
    public void Linux_Roundtrip()
    {
        var dir = Path.Combine(_tmp, "autostart");
        Assert.False(AutostartManager.IsEnabled("/opt/snow", linuxDir: dir, platform: OSPlatform.Linux));
        AutostartManager.SetEnabled("/opt/snow", "node", true, linuxDir: dir, platform: OSPlatform.Linux);
        Assert.True(AutostartManager.IsEnabled("/opt/snow", linuxDir: dir, platform: OSPlatform.Linux));
        var text = File.ReadAllText(Path.Combine(dir, "smartclassroom-snowluma.desktop"));
        Assert.Contains("index.mjs", text);
        AutostartManager.SetEnabled("/opt/snow", "node", false, linuxDir: dir, platform: OSPlatform.Linux);
        Assert.False(AutostartManager.IsEnabled("/opt/snow", linuxDir: dir, platform: OSPlatform.Linux));
    }

    [Fact]
    public void Windows_EntryIsBat()
    {
        var dir = Path.Combine(_tmp, "startup");
        AutostartManager.SetEnabled(@"C:\SnowLuma", "node.exe", true, windowsDir: dir, platform: OSPlatform.Windows);
        var path = Path.Combine(dir, "smartclassroom-snowluma.bat");
        Assert.True(File.Exists(path));
        Assert.Contains("index.mjs", File.ReadAllText(path));
    }
}
