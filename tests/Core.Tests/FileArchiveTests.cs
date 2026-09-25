using SmartClassroom.Contracts;
using SmartClassroom.Core.QQ;
using Xunit;

namespace SmartClassroom.Core.Tests;

public sealed class FileArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sc-archive-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    private sealed class StubHandler(byte[] payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken t)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new ByteArrayContent(payload) });
    }

    private static GroupUploadEvent Ev(string fileId = "fid-1", string name = "课件.pptx", long size = 100)
        => new() { GroupId = 1, UserId = 10001, File = new UploadedFile { Id = fileId, Name = name, Size = size } };

    private static SenderInfo Teacher() => new() { UserId = 10001, TeacherName = "张老师" };
    private static SenderInfo Stranger() => new() { UserId = 99999, Card = "陌生人" };

    private FileArchive Archive(bool downloadAll = false, HttpClient? http = null)
        => new(new ArchiveOptions { Root = _root, DownloadAll = downloadAll },
            http ?? new HttpClient(new StubHandler([1, 2, 3])));

    [Fact]
    public async Task TeacherFile_DownloadsToSenderFolder()
    {
        var o = await Archive().HandleAsync(Ev(), Teacher(), (_, _) => Task.FromResult<string?>("http://x/f"));
        Assert.Equal(ArchiveResult.Downloaded, o.Result);
        Assert.NotNull(o.LocalPath);
        Assert.Contains(Path.Combine("张老师", DateTime.Now.ToString("yyyy-MM-dd"), "课件.pptx"), o.LocalPath);
        Assert.True(File.Exists(o.LocalPath));
        Assert.True(File.Exists(o.LocalPath + ".meta.json"));
    }

    [Fact]
    public async Task SameFileId_Dedups()
    {
        var a = Archive();
        var o1 = await a.HandleAsync(Ev(), Teacher(), (_, _) => Task.FromResult<string?>("http://x/f"));
        var o2 = await a.HandleAsync(Ev(), Teacher(), (_, _) => Task.FromResult<string?>("http://x/f"));
        Assert.Equal(ArchiveResult.Downloaded, o1.Result);
        Assert.Equal(ArchiveResult.AlreadyExists, o2.Result);
        Assert.Equal(o1.LocalPath, o2.LocalPath);
    }

    [Fact]
    public async Task SameNameDifferentFile_Renames()
    {
        var a = Archive();
        var o1 = await a.HandleAsync(Ev("fid-1"), Teacher(), (_, _) => Task.FromResult<string?>("http://x/f"));
        var o2 = await a.HandleAsync(Ev("fid-2"), Teacher(), (_, _) => Task.FromResult<string?>("http://x/f"));
        Assert.Equal(ArchiveResult.Downloaded, o1.Result);
        Assert.Equal(ArchiveResult.Downloaded, o2.Result);
        Assert.NotEqual(o1.LocalPath, o2.LocalPath);
        Assert.Contains("(1)", o2.LocalPath);
    }

    [Fact]
    public async Task UnknownSender_SkippedByDefault()
    {
        var o = await Archive().HandleAsync(Ev(), Stranger(), (_, _) => Task.FromResult<string?>("http://x/f"));
        Assert.Equal(ArchiveResult.SkippedUnknownSender, o.Result);
    }

    [Fact]
    public async Task UnknownSender_DownloadsWhenEnabled()
    {
        var o = await Archive(downloadAll: true).HandleAsync(Ev(), Stranger(), (_, _) => Task.FromResult<string?>("http://x/f"));
        Assert.Equal(ArchiveResult.Downloaded, o.Result);
        Assert.Contains("QQ99999", o.LocalPath);
    }

    [Fact]
    public async Task LargeFile_PendingConfirm()
    {
        var a = new FileArchive(new ArchiveOptions { Root = _root, LargeFileConfirmBytes = 10 },
            new HttpClient(new StubHandler([1])));
        var o = await a.HandleAsync(Ev(size: 100), Teacher(), (_, _) => Task.FromResult<string?>("http://x/f"));
        Assert.Equal(ArchiveResult.PendingConfirm, o.Result);
    }
}
