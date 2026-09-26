using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using SmartClassroom.Core.AI;
using Xunit;

namespace SmartClassroom.Core.Tests;

public sealed class NodeManagerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sc-node-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void ArchiveName_And_Url_FollowNodeDistLayout()
    {
        Assert.Equal("node-v22.20.0-linux-x64.tar.gz",
            NodeManager.ArchiveName("v22.20.0", "linux-x64"));
        Assert.Equal("node-v22.20.0-win-x64.zip",
            NodeManager.ArchiveName("v22.20.0", "win-x64"));
        Assert.Equal("https://nodejs.org/dist/v22.20.0/node-v22.20.0-linux-x64.tar.gz",
            NodeManager.DownloadUrl("v22.20.0", "linux-x64"));
    }

    [Fact]
    public async Task ListLts_SkipsNonLtsEntries()
    {
        const string payload = """
        [
          {"version":"v26.1.0","lts":false},
          {"version":"v24.9.0","lts":"Krypton"},
          {"version":"v22.20.0","lts":"Jod"}
        ]
        """;
        var mgr = new NodeManager(new HttpClient(new Stub(payload)));
        var list = await mgr.ListLtsAsync();
        Assert.Equal(2, list.Count);
        Assert.Equal("v24.9.0", list[0].Version);
        Assert.Equal("Krypton", list[0].LtsName);
    }

    [Fact]
    public void ExtractFlattened_TarGz_StripsRootFolder()
    {
        var archive = BuildTarGz("node-v22.20.0-linux-x64", ("bin/node", "#!/bin/sh\n"));
        var root = Path.Combine(_dir, "node");
        NodeManager.ExtractFlattened(archive, root);
        Assert.True(File.Exists(Path.Combine(root, "bin", "node")), "bin/node 应被平铺到安装根");
    }

    [Fact]
    public void ExtractFlattened_Zip_StripsRootFolder()
    {
        var zip = Path.Combine(_dir, "n.zip");
        Directory.CreateDirectory(_dir);
        using (var fs = File.Create(zip))
        using (var z = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var e = z.CreateEntry("node-v22.20.0-win-x64/node.exe");
            using var w = new StreamWriter(e.Open());
            w.Write("MZ");
        }
        var root = Path.Combine(_dir, "node");
        NodeManager.ExtractFlattened(zip, root);
        Assert.True(File.Exists(Path.Combine(root, "node.exe")));
    }

    [Fact]
    public void FlattenSource_FindsNestedNodeBin()
    {
        var baseDir = Path.Combine(_dir, "x");
        Directory.CreateDirectory(Path.Combine(baseDir, "sub", "bin"));
        File.WriteAllText(Path.Combine(baseDir, "sub", "bin", "node"), "x");
        Assert.Equal(Path.Combine(baseDir, "sub"), NodeManager.FlattenSource(baseDir));
    }

    private string BuildTarGz(string rootName, params (string Path, string Content)[] files)
    {
        Directory.CreateDirectory(_dir);
        var stage = Path.Combine(_dir, "stage", rootName);
        Directory.CreateDirectory(stage);
        foreach (var (rel, content) in files)
        {
            var full = Path.Combine(stage, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        var archive = Path.Combine(_dir, "node.tar.gz");
        using var fs = File.Create(archive);
        using var gz = new GZipStream(fs, CompressionLevel.Fastest);
        TarFile.CreateFromDirectory(Path.Combine(_dir, "stage"), gz, includeBaseDirectory: true);
        return archive;
    }

    private sealed class Stub(string payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken t)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new StringContent(payload) });
    }
}
