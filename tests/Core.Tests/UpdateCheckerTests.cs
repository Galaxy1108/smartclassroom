using System.Net;
using SmartClassroom.Core;
using SmartClassroom.Core.Updates;
using Xunit;

namespace SmartClassroom.Core.Tests;

public sealed class AppVersionTests
{
    [Theory]
    [InlineData("0.22.0", 0, 22, 0, false)]
    [InlineData("v0.22.0", 0, 22, 0, false)]
    [InlineData("V1.2.3", 1, 2, 3, false)]
    [InlineData("0.22", 0, 22, 0, false)]                 // 少一段按 0 补
    [InlineData("0.23.0-beta.1", 0, 23, 0, true)]         // pre-release 要能认出来
    [InlineData("乱写", 0, 0, 0, false)]
    [InlineData("", 0, 0, 0, false)]
    public void Parse_HandlesRealTagShapes(string raw, int major, int minor, int patch, bool pre)
    {
        var v = AppVersion.Parse(raw);
        Assert.Equal((major, minor, patch), (v.Major, v.Minor, v.Patch));
        Assert.Equal(pre, v.IsPrerelease);
    }

    [Fact]
    public void Compare_OrdersByNumber_AndPrefersReleaseOverPrerelease()
    {
        Assert.True(AppVersion.Parse("0.23.0") > AppVersion.Parse("0.22.9"));
        Assert.True(AppVersion.Parse("0.23.0") > AppVersion.Parse("0.23.0-beta.1"));
        Assert.True(AppVersion.Parse("0.23.0-beta.1") < AppVersion.Parse("0.23.0"));
        Assert.True(AppVersion.Parse("v0.22.0") >= AppVersion.Parse("0.22.0"));
        Assert.False(AppVersion.Parse("0.22.0") > AppVersion.Parse("0.22.0"));
    }

    [Fact]
    public void ToString_RoundTrips()
    {
        Assert.Equal("0.22.0", AppVersion.Parse("v0.22.0").ToString());
        Assert.Equal("0.23.0-beta.1", AppVersion.Parse("0.23.0-beta.1").ToString());
    }
}

public sealed class UpdateCheckerTests
{
    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> fn) : HttpMessageHandler
    {
        public readonly List<string> Urls = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken t)
        {
            Urls.Add(req.RequestUri!.ToString());
            return Task.FromResult(fn(req));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(body) };

    private static UpdateChecker Checker(Stub stub) => new(new HttpClient(stub));

    [Fact]
    public async Task NewerRelease_ReportsUpdateAndPicksAsset()
    {
        var stub = new Stub(_ => Json("""
            {"tag_name":"v0.23.0","name":"v0.23.0","html_url":"https://github.com/x/y/releases/tag/v0.23.0",
             "body":"修了一堆东西",
             "assets":[{"name":"smartclassroom-0.23.0-win-x64.zip","browser_download_url":"https://dl/win.zip"},
                       {"name":"smartclassroom-0.23.0-1-x86_64.pkg.tar.zst","browser_download_url":"https://dl/linux.zst"}]}
            """));
        var info = await Checker(stub).CheckAsync("0.22.0", assetPattern: "win-x64.zip");

        Assert.True(info.HasUpdate);
        Assert.Equal("0.23.0", info.LatestVersion);
        Assert.Equal("release", info.Source);
        Assert.Equal("https://dl/win.zip", info.AssetUrl);
        Assert.Equal("修了一堆东西", info.Notes);
        // GitHub API 必须带 User-Agent，否则 403
        Assert.Contains(stub.Urls, u => u.Contains("/releases/latest"));
    }

    [Fact]
    public async Task SameVersion_NoUpdate()
    {
        var stub = new Stub(_ => Json("""{"tag_name":"v0.22.0","assets":[]}"""));
        var info = await Checker(stub).CheckAsync("0.22.0");
        Assert.False(info.HasUpdate);
        Assert.Equal("0.22.0", info.LatestVersion);
    }

    [Fact]
    public async Task NoAssetForPlatform_LeavesAssetUrlNull()
    {
        var stub = new Stub(_ => Json("""
            {"tag_name":"v0.23.0","assets":[{"name":"something-else.zip","browser_download_url":"https://dl/x.zip"}]}
            """));
        var info = await Checker(stub).CheckAsync("0.22.0", assetPattern: "win-x64.zip");
        Assert.True(info.HasUpdate);
        Assert.Null(info.AssetUrl);
    }

    [Fact]
    public async Task NoRelease_FallsBackToHighestTag_AndSkipsPrerelease()
    {
        var stub = new Stub(req => req.RequestUri!.AbsolutePath.EndsWith("/releases/latest")
            ? Json("""{"message":"Not Found"}""", HttpStatusCode.NotFound)
            : Json("""
                [{"name":"v0.23.0-beta.1"},{"name":"v0.22.0"},{"name":"v0.21.1"},{"name":"v0.20.0"}]
                """));
        var info = await Checker(stub).CheckAsync("0.21.1");

        Assert.Equal("tag", info.Source);
        Assert.Equal("0.22.0", info.LatestVersion);   // 不把 beta 当最新
        Assert.True(info.HasUpdate);
        Assert.Null(info.AssetUrl);                   // tag 兜底没有资产
    }

    [Fact]
    public async Task RemoteUnreachable_ReportsNoVersion()
    {
        var stub = new Stub(_ => Json("{}", HttpStatusCode.InternalServerError));
        var info = await Checker(stub).CheckAsync("0.22.0");
        Assert.False(info.RemoteReachable);
        Assert.False(info.HasUpdate);
        Assert.Equal("none", info.Source);
    }
}

public sealed class SettingsResetTests
{
    [Fact]
    public void ResetToDefaults_ClearsEverything()
    {
        var s = new AppSettings
        {
            AiApiKey = "k", AiModel = "m", AiBaseUrl = "u", OneBotToken = "t",
            GroupIds = [1, 2], QqAccount = 10001, QqAccounts = [new QqAccount { Uin = 10001 }],
            ArchiveRoot = "/x", PluginToken = "p", PluginPort = 1234,
            Teachers = [new Teacher { Qq = 1, Name = "张", Subject = "数学" }],
            FeatureSummon = true, AdminPasswordHash = "hash", UiScale = 1.5, RiskAccepted = true,
            MinimizeToTray = false
        };

        s.ResetToDefaults();

        Assert.Equal("", s.AiApiKey);
        Assert.Equal("", s.AiModel);
        Assert.Empty(s.GroupIds);
        Assert.Equal(0, s.QqAccount);
        Assert.Empty(s.QqAccounts);
        Assert.Empty(s.Teachers);
        Assert.False(s.FeatureSummon);
        Assert.Equal("", s.AdminPasswordHash);
        Assert.Equal("http://127.0.0.1:3000", s.OneBotHttp);   // 回到默认端点
        Assert.True(s.MinimizeToTray);
        Assert.Equal(1.0, s.UiScale);
    }

    [Fact]
    public void StoreReset_DeletesFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "sc-reset-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            SettingsStore.Save(new AppSettings { AiApiKey = "k" }, path);
            Assert.True(File.Exists(path));

            SettingsStore.Reset(path);

            Assert.False(File.Exists(path));
            Assert.Equal("", SettingsStore.Load(path).AiApiKey);   // 读回默认值
            SettingsStore.Reset(path);                             // 幂等
        }
        finally
        {
            foreach (var p in new[] { path, path + ".tmp" })
                if (File.Exists(p)) File.Delete(p);
        }
    }
}
