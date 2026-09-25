using SmartClassroom.Core.QQ;
using Xunit;

namespace SmartClassroom.Core.Tests;

public sealed class SnowlumaManagerTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "sc-snow-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tmp))
            Directory.Delete(_tmp, true);
    }

    private static SnowlumaRelease Release() => new("v1.14.19", [
        new SnowlumaAsset("SnowLuma-v1.14.19-win-x64.zip", "http://x/full.zip", 36),
        new SnowlumaAsset("SnowLuma-v1.14.19-win-x64-lite.zip", "http://x/lite.zip", 4),
        new SnowlumaAsset("SnowLuma-v1.14.19-linux-x64.tar.gz", "http://x/full.tgz", 44),
        new SnowlumaAsset("SnowLuma-v1.14.19-linux-x64-lite.tar.gz", "http://x/lite.tgz", 3),
    ]);

    [Fact]
    public void PickAsset_WindowsFull()
    {
        var a = SnowlumaManager.PickAsset(Release(), "win-x64");
        Assert.NotNull(a);
        Assert.EndsWith(".zip", a.Name);
        Assert.DoesNotContain("-lite", a.Name);
    }

    [Fact]
    public void PickAsset_LinuxLiteFallback()
    {
        var liteOnly = new SnowlumaRelease("v1", [new SnowlumaAsset("SnowLuma-v1-linux-arm64-lite.tar.gz", "u", 1)]);
        var a = SnowlumaManager.PickAsset(liteOnly, "linux-arm64");
        Assert.NotNull(a);
        Assert.Contains("-lite", a.Name);
    }

    [Fact]
    public void PickAsset_NoMatch_Null()
    {
        Assert.Null(SnowlumaManager.PickAsset(Release(), "osx-arm64"));
    }

    [Fact]
    public void Extract_ZipRoundtrip()
    {
        Directory.CreateDirectory(_tmp);
        var src = Path.Combine(_tmp, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "index.mjs"), "hello");
        var zip = Path.Combine(_tmp, "s.zip");
        System.IO.Compression.ZipFile.CreateFromDirectory(src, zip);
        var dst = Path.Combine(_tmp, "dst");
        SnowlumaManager.Extract(zip, dst);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(dst, "index.mjs")));
    }

    [Fact]
    public void Extract_TarGzRoundtrip()
    {
        Directory.CreateDirectory(_tmp);
        var src = Path.Combine(_tmp, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "index.mjs"), "hello");
        var tgz = Path.Combine(_tmp, "s.tar.gz");
        using (var fs = File.Create(tgz))
        using (var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionLevel.Fastest))
            System.Formats.Tar.TarFile.CreateFromDirectory(src, gz, includeBaseDirectory: false);
        var dst = Path.Combine(_tmp, "dst");
        SnowlumaManager.Extract(tgz, dst);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(dst, "index.mjs")));
    }
}
