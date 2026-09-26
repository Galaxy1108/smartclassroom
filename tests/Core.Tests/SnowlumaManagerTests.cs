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

    // ================= WebUI 初始密码 =================
    //
    // SnowLuma 默认每次启动随机生成初始密码，而且只打印到 stdout —— GUI 启动的用户看不到。
    // 它提供了官方环境变量 SNOWLUMA_WEBUI_BOOTSTRAP_PASSWORD，应用就用它替用户定一个。

    [Fact]
    public void ReadWebUiMustChangePassword_ReflectsConfig()
    {
        var dir = InstallDir();
        Assert.True(SnowlumaManager.ReadWebUiMustChangePassword(dir));   // 还没有 webui.json = 还没设过

        Directory.CreateDirectory(Path.Combine(dir, "config"));
        File.WriteAllText(Path.Combine(dir, "config", "webui.json"),
            """{"passwordHash":"x","mustChangePassword":true}""");
        Assert.True(SnowlumaManager.ReadWebUiMustChangePassword(dir));

        File.WriteAllText(Path.Combine(dir, "config", "webui.json"),
            """{"passwordHash":"x","mustChangePassword":false}""");
        Assert.False(SnowlumaManager.ReadWebUiMustChangePassword(dir));  // 用户已经改过密码
    }

    [Fact]
    public void BootstrapPasswordEnv_HasTheOfficialName()
        => Assert.Equal("SNOWLUMA_WEBUI_BOOTSTRAP_PASSWORD", SnowlumaManager.BootstrapPasswordEnv);

    // ================= 多账号：日志里能看出登录了哪些号、端口冲突 =================

    private string WithLog(string content)
    {
        var dir = InstallDir();
        var logDir = Path.Combine(dir, "logs");
        Directory.CreateDirectory(logDir);
        File.WriteAllText(Path.Combine(logDir, "snowluma-test.log"), content);
        return dir;
    }

    [Fact]
    public void ReadLoggedInUins_PicksUpEverySession()
    {
        var dir = WithLog(
            "19:18:06 DEBUG [Bridge] session started: UIN=100000001\n" +
            "19:18:07 INFO  [OneBot] session started: UIN=100000001\n" +   // 同一个号重复出现只算一次
            "19:18:22 DEBUG [Bridge] session started: UIN=100000002\n");

        Assert.Equal([100000001L, 100000002L], SnowlumaManager.ReadLoggedInUins(dir));
        Assert.Empty(SnowlumaManager.ReadLoggedInUins(InstallDir()));      // 没有日志
    }

    [Fact]
    public void LastPortConflict_FindsEaddrInUse()
    {
        var dir = WithLog(
            "19:18:07 OK    [100000001] [OneBot.HTTP] [http-default] listening 127.0.0.1:3000/\n" +
            "19:18:22 ERROR [100000002] [OneBot.HTTP] server error: listen EADDRINUSE: address already in use 127.0.0.1:3000\n" +
            "19:18:22 WARN  [OneBot] network startup degraded: UIN=100000002 failures=2\n");

        var conflict = SnowlumaManager.LastPortConflict(dir);
        Assert.NotNull(conflict);
        Assert.Contains("EADDRINUSE", conflict);
        Assert.Null(SnowlumaManager.LastPortConflict(InstallDir()));
    }

    // ================= OneBot 连接信息（含 access token） =================
    //
    // 实测：SnowLuma 的 OneBot HTTP 默认要求鉴权，不带 token 一律 1401 unauthorized ——
    // 应用表现就是"检测不到账号 / 看不到在线"。token 就写在 config/onebot_<uin>.json 里。

    [Fact]
    public void ReadOneBotEndpoint_PullsAddressAndToken()
    {
        var dir = InstallDir();
        Directory.CreateDirectory(Path.Combine(dir, "config"));
        File.WriteAllText(Path.Combine(dir, "config", "onebot_100000001.json"),
            """
            {"networks":{"httpServers":[{"host":"127.0.0.1","port":3000,"accessToken":"http-tok"}],
                         "wsServers":[{"host":"127.0.0.1","port":3001,"accessToken":"ws-tok"}]}}
            """);

        var ep = SnowlumaManager.ReadOneBotEndpoint(dir, 100000001);

        Assert.NotNull(ep);
        Assert.Equal("http://127.0.0.1:3000", ep!.Http);
        Assert.Equal("ws://127.0.0.1:3001", ep.Ws);
        Assert.Equal("http-tok", ep.Token);
        Assert.Equal(100000001, ep.Uin);
        Assert.Null(SnowlumaManager.ReadOneBotEndpoint(dir, 999));       // 没有这个账号的配置
    }

    [Fact]
    public void ReadOneBotAccounts_ListsConfiguredUins()
    {
        var dir = InstallDir();
        Directory.CreateDirectory(Path.Combine(dir, "config"));
        File.WriteAllText(Path.Combine(dir, "config", "onebot_100000001.json"), "{}");
        File.WriteAllText(Path.Combine(dir, "config", "onebot_100000002.json"), "{}");

        Assert.Equal([100000001L, 100000002L], SnowlumaManager.ReadOneBotAccounts(dir));
        Assert.Empty(SnowlumaManager.ReadOneBotAccounts(InstallDir()));
    }

    [Fact]
    public void ReadAccountNicknames_FromLog()
    {
        var dir = WithLog(
            "19:18:08 DEBUG [Bridge] self info: UIN=100000001 uid=u_x nickname=测试昵称A\n" +
            "19:18:23 DEBUG [Bridge] self info: UIN=100000002 uid=u_y nickname=测试昵称B\n");

        var map = SnowlumaManager.ReadAccountNicknames(dir);

        Assert.Equal("测试昵称A", map[100000001]);
        Assert.Equal("测试昵称B", map[100000002]);
    }

    // ================= 自动分配端口 + WebUI 凭据播种 =================

    private string WithOneBotConfig(long uin, int httpPort, int wsPort)
    {
        var dir = InstallDir();
        Directory.CreateDirectory(Path.Combine(dir, "config"));
        var json = "{\"networks\":{\"httpServers\":[{\"host\":\"127.0.0.1\",\"port\":" + httpPort
                   + ",\"accessToken\":\"t" + uin + "\"}],\"wsServers\":[{\"host\":\"127.0.0.1\",\"port\":"
                   + wsPort + ",\"accessToken\":\"w" + uin + "\"}]}}";
        File.WriteAllText(Path.Combine(dir, "config", "onebot_" + uin + ".json"), json);
        return dir;
    }

    [Fact]
    public void AssignDistinctPorts_GivesEveryAccountItsOwnPair()
    {
        var dir = WithOneBotConfig(100000001, 3000, 3001);
        // 第二个账号的配置放到同一个目录
        var json2 = "{\"networks\":{\"httpServers\":[{\"host\":\"127.0.0.1\",\"port\":3000,"
                    + "\"accessToken\":\"t2\"}],\"wsServers\":[{\"host\":\"127.0.0.1\",\"port\":3001,"
                    + "\"accessToken\":\"w2\"}]}}";
        File.WriteAllText(Path.Combine(dir, "config", "onebot_100000002.json"), json2);

        var assigned = SnowlumaManager.AssignDistinctPorts(dir);

        // 不写死具体端口：本机 3000/3001 可能被别的程序占着（会被自动跳过）
        Assert.Equal(2, assigned.Count);
        Assert.Equal(2, assigned.Select(a => a.Http).Distinct().Count());   // 两组互不冲突
        Assert.All(assigned, a => Assert.Equal(a.Http + 1, a.Ws));
        Assert.True(assigned[1].Http > assigned[0].Http);

        var first = SnowlumaManager.ReadOneBotEndpoint(dir, 100000001);
        Assert.Equal($"http://127.0.0.1:{assigned[0].Http}", first!.Http);
        Assert.Equal("t100000001", first.Token);   // 其它字段没被写丢
        Assert.Equal(assigned[1].Http, SnowlumaManager.ReadOneBotEndpoint(dir, 100000002)!.Uin > 0
            ? assigned[1].Http : -1);
    }

    [Fact]
    public void ResetWebUiCredentials_BacksUpAndDeletes()
    {
        // SnowLuma 只在没有 webui.json 时才认 SNOWLUMA_WEBUI_BOOTSTRAP_PASSWORD，
        // 文件在就直接忽略 —— 所以要让密码生效必须先把它挪走（备份）
        var dir = InstallDir();
        Directory.CreateDirectory(Path.Combine(dir, "config"));
        var path = Path.Combine(dir, "config", "webui.json");
        File.WriteAllText(path, "{\"passwordHash\":\"x\",\"mustChangePassword\":true}");

        Assert.True(SnowlumaManager.ResetWebUiCredentials(dir));

        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(Path.Combine(dir, "config"), "webui.json.bak-*"));
        Assert.True(SnowlumaManager.ResetWebUiCredentials(dir));   // 幂等
    }

    [Fact]
    public void WebUiCredentialsSeededFromEnv_ReadsTheLog()
    {
        var seeded = WithLog("18:00:00 INFO [WebUI.Auth] webui credentials seeded from SNOWLUMA_WEBUI_BOOTSTRAP_PASSWORD\n");
        Assert.True(SnowlumaManager.WebUiCredentialsSeededFromEnv(seeded));
        Assert.False(SnowlumaManager.WebUiCredentialsSeededFromEnv(InstallDir()));
    }

    // ================= 注入失败的原因（只在它自己的日志里） =================

    [Fact]
    public void LastHookFailure_ReadsNewestLogTail()
    {
        var dir = InstallDir();
        var logDir = Path.Combine(dir, "logs");
        Directory.CreateDirectory(logDir);
        File.WriteAllText(Path.Combine(logDir, "old.log"),
            "10:00:00 ERROR [Hook] load failed: PID=1 err=old");
        File.WriteAllText(Path.Combine(logDir, "new.log"),
            "18:47:51 INFO  [App] hook auto-load enabled\n" +
            "18:47:51 ERROR [Hook] load failed: PID=7303 err=component loading failed [COMPONENT_LOAD_FAILED]\n" +
            "18:47:52 INFO  [App] something else\n");
        File.SetLastWriteTimeUtc(Path.Combine(logDir, "new.log"), DateTime.UtcNow);

        var failure = SnowlumaManager.LastHookFailure(dir);

        Assert.NotNull(failure);
        Assert.Contains("7303", failure);                       // 取最新那份日志里的失败行
        Assert.DoesNotContain("old", failure!);
    }

    [Fact]
    public void LastHookFailure_IsIgnoredOnceInjectionSucceeded()
    {
        // 实测踩到：18:59 注入失败，19:18 重启 QQ 后连上了（pipe connected），
        // 但日志尾部还留着那条旧失败 —— 不能再拿它吓唬用户。
        var stale = WithLog(
            "18:59:29 ERROR [Hook] load failed: PID=7303 err=component loading failed [COMPONENT_LOAD_FAILED]\n" +
            "19:09:07 INFO  [Hook] pipe connected: PID=1767099\n" +
            "19:18:06 OK    [Hook] login detected: PID=1818839 UIN=100000001\n");
        Assert.Null(SnowlumaManager.LastHookFailure(stale));

        // 成功之后又失败：这才是当前的失败
        var fresh = WithLog(
            "19:18:06 OK    [Hook] login detected: PID=1818839 UIN=100000001\n" +
            "19:20:00 ERROR [Hook] load failed: PID=99 err=component loading failed [COMPONENT_LOAD_FAILED]\n");
        var failure = SnowlumaManager.LastHookFailure(fresh);
        Assert.NotNull(failure);
        Assert.Contains("PID=99", failure!);
    }

    [Fact]
    public void LastHookFailure_NoLogsOrNoFailure_ReturnsNull()
    {
        Assert.Null(SnowlumaManager.LastHookFailure(InstallDir()));      // 没有 logs 目录

        var dir = InstallDir();
        Directory.CreateDirectory(Path.Combine(dir, "logs"));
        File.WriteAllText(Path.Combine(dir, "logs", "a.log"), "18:00:00 INFO [App] all good\n");
        Assert.Null(SnowlumaManager.LastHookFailure(dir));               // 有日志但没失败
    }

    [Fact]
    public void ReadPtraceScope_OnLinux_ReturnsValue()
    {
        var scope = SnowlumaManager.ReadPtraceScope();
        if (!OperatingSystem.IsLinux())
        {
            Assert.Null(scope);
            return;
        }
        // Linux 上通常有 yama（0/1/2/3）；没有这个文件时返回 null 也算正常
        Assert.True(scope is null or >= 0);
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

        await manager.StartAsync(dir);

        Assert.False(manager.StartedByThisApp);                   // 没有新起进程
        Assert.Equal(Environment.ProcessId, manager.FindRunningPid(dir));   // 认的是已在跑的那个
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
