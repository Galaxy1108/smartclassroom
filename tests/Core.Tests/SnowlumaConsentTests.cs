using SmartClassroom.Core.QQ;
using Xunit;

namespace SmartClassroom.Core.Tests;

/// <summary>
/// SnowLuma 的协议同意。
/// 它启动后会停在"等待同意 EULA/隐私政策"，在此之前不注入、OneBot 也不起来，
/// 所以应用要能在启动前把正文给用户看并征得同意，再用它的官方环境变量开关带过去。
/// </summary>
public sealed class SnowlumaConsentTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sc-snowluma-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private void WriteDocs(string eula = "# SnowLuma 用户协议\n\n第一条：别干坏事。", string privacy = "# 隐私政策\n\n只在本机处理。")
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "EULA.md"), eula);
        File.WriteAllText(Path.Combine(_dir, "PRIVACY.md"), privacy);
    }

    [Fact]
    public void ReadFrom_PicksUpBothDocuments_WithTitles()
    {
        WriteDocs();
        var docs = SnowlumaAgreements.ReadFrom(_dir);

        Assert.Equal(2, docs.Count);
        Assert.Equal(["eula", "privacy"], docs.Select(d => d.Id));
        Assert.Equal("SnowLuma 用户协议", docs[0].Title);   // 取正文里的一级标题
        Assert.Equal("隐私政策", docs[1].Title);
        Assert.Contains("别干坏事", docs[0].Text);
    }

    [Fact]
    public void ReadFrom_MissingFiles_YieldsNothing()
    {
        Directory.CreateDirectory(_dir);
        Assert.Empty(SnowlumaAgreements.ReadFrom(_dir));
        Assert.Empty(SnowlumaAgreements.ReadFrom(Path.Combine(_dir, "not-there")));
    }

    [Fact]
    public void Fingerprint_IsStable_AndChangesWithText()
    {
        WriteDocs();
        var a = SnowlumaAgreements.Fingerprint(SnowlumaAgreements.ReadFrom(_dir));
        var again = SnowlumaAgreements.Fingerprint(SnowlumaAgreements.ReadFrom(_dir));
        Assert.Equal(a, again);
        Assert.Equal(16, a.Length);

        // 协议文本一改（SnowLuma 更新条款）→ 指纹必须变，好重新征得同意
        WriteDocs(eula: "# SnowLuma 用户协议\n\n第一条：别干坏事。第二条：新增条款。");
        Assert.NotEqual(a, SnowlumaAgreements.Fingerprint(SnowlumaAgreements.ReadFrom(_dir)));
    }

    [Fact]
    public void Fingerprint_EmptyDocs_IsEmpty()
        => Assert.Equal("", SnowlumaAgreements.Fingerprint([]));

    [Fact]
    public void AcceptanceEnvironment_SetsBothSwitches()
    {
        var env = SnowlumaAgreements.AcceptanceEnvironment();

        // SnowLuma 只认这两个名字，而且**必须同时给**（只给一个会被忽略）
        Assert.Equal("1", env["SNOWLUMA_ACCEPT_EULA"]);
        Assert.Equal("1", env["SNOWLUMA_ACCEPT_PRIVACY"]);
        Assert.Equal(2, env.Count);
    }
}
