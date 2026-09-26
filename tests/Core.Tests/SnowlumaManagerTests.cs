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

    // ================= 自动注入开关（hookAutoLoad） =================
    //
    // 真实问题：SnowLuma 默认 hookAutoLoad=false，只起 WebUI 不注入，
    // 用户点「启动注入」后一直显示"已启动，未注入"。

    private string WithRuntimeConfig(string json)
    {
        var dir = InstallDir();
        Directory.CreateDirectory(Path.Combine(dir, "config"));
        File.WriteAllText(Path.Combine(dir, "config", "runtime.json"), json);
        return dir;
    }

    [Fact]
    public void ReadHookAutoLoad_ParsesFlag()
    {
        Assert.True(SnowlumaManager.ReadHookAutoLoad(WithRuntimeConfig("""{"webuiPort":5099,"hookAutoLoad":true}""")));
        Assert.False(SnowlumaManager.ReadHookAutoLoad(WithRuntimeConfig("""{"webuiPort":5099,"hookAutoLoad":false}""")));
        Assert.Null(SnowlumaManager.ReadHookAutoLoad(WithRuntimeConfig("""{"webuiPort":5099}""")));   // 没这个字段
        Assert.Null(SnowlumaManager.ReadHookAutoLoad(InstallDir()));                                  // 没这个文件
    }

    [Fact]
    public void SetHookAutoLoad_TurnsItOn_AndKeepsOtherFields()
    {
        var dir = WithRuntimeConfig("""{"webuiPort":5099,"hookAutoLoad":false,"webuiHost":"127.0.0.1"}""");

        Assert.True(SnowlumaManager.SetHookAutoLoad(dir, true));

        Assert.True(SnowlumaManager.ReadHookAutoLoad(dir));
        var text = File.ReadAllText(Path.Combine(dir, "config", "runtime.json"));
        Assert.Contains("5099", text);              // 其它字段不能被写丢
        Assert.Contains("127.0.0.1", text);
        Assert.False(File.Exists(Path.Combine(dir, "config", "runtime.json.tmp")));   // 原子替换，无残留
    }

    [Fact]
    public void SetHookAutoLoad_BrokenJson_DoesNotDestroyTheFile()
    {
        var dir = WithRuntimeConfig("{ 这不是 json");
        Assert.False(SnowlumaManager.SetHookAutoLoad(dir, true));
        Assert.Equal("{ 这不是 json", File.ReadAllText(Path.Combine(dir, "config", "runtime.json")));
    }

    // ================= 已在运行的实例：接管，不要重复开 =================
    //
    // 真实问题：上次应用启动的 SnowLuma 还在跑（pid 文件还在），新一次启动又开一个，
    // 两个实例抢同一个 WebUI 端口；而且「停止」只杀自己启动的那个，等于按了没反应。

    private string InstallDir()
    {
        var dir = Path.Combine(_tmp, "snowluma-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "index.mjs"), "// fake");
        return dir;
    }

    [Fact]
    public void FindRunningPid_ReadsPidFile_AndIgnoresDeadPid()
    {
        var dir = InstallDir();
        using var manager = new SnowlumaManager();

        Assert.Null(manager.FindRunningPid(dir));                 // 什么都没有

        // 写一个活着的 pid（用测试进程自己）
        File.WriteAllText(Path.Combine(dir, SnowlumaManager.PidFileName),
            Environment.ProcessId.ToString());
        Assert.Equal(Environment.ProcessId, manager.FindRunningPid(dir));

        // 写一个早就没了的 pid
        File.WriteAllText(Path.Combine(dir, SnowlumaManager.PidFileName), "999999");
        var found = manager.FindRunningPid(dir);
        Assert.NotEqual(999999, found);                           // 死 pid 不算（Linux 上可能扫到别的）
    }

    [Fact]
    public async Task StartAsync_AdoptsRunningInstance_InsteadOfStartingAnother()
    {
        var dir = InstallDir();
        File.WriteAllText(Path.Combine(dir, SnowlumaManager.PidFileName),
            Environment.ProcessId.ToString());
        using var manager = new SnowlumaManager();
        var logs = new List<string>();
        manager.OnLog += logs.Add;

        await manager.StartAsync(dir);

        Assert.Contains(logs, l => l.Contains("接管现有实例"));
        Assert.False(manager.StartedByThisApp);                   // 没有新起进程
    }

    [Fact]
    public async Task Stop_OnAdoptedInstance_DoesNotThrow()
    {
        var dir = InstallDir();
        // pid 指向一个真实存在但不是 SnowLuma 的进程：这里用"必然不存在的 pid"，
        // 只要保证 Stop 不抛、并把 pid 文件清掉即可（真实 kill 行为无法在单测里安全验证）
        File.WriteAllText(Path.Combine(dir, SnowlumaManager.PidFileName), "999999");
        using var manager = new SnowlumaManager();

        manager.Stop(dir);

        Assert.False(File.Exists(Path.Combine(dir, SnowlumaManager.PidFileName)));
    }
}
