using System.Net;
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

    private static SenderInfo Teacher() => new() { UserId = 10001, TeacherName = "张老师", Subject = "数学" };
    private static SenderInfo Stranger() => new() { UserId = 99999, Card = "陌生人" };

    private FileArchive Archive(bool downloadAll = false, HttpClient? http = null)
        => new(new ArchiveOptions { Root = _root, DownloadAll = downloadAll },
            http ?? new HttpClient(new StubHandler([1, 2, 3])));


    /// <summary>元数据现在写在 &lt;Root&gt;/.smartclassroom-meta/&lt;科目&gt;/&lt;文件&gt;.json，
    /// 科目文件夹里只留真正的文件（用户反馈过"你还归档了一个 .meta.json"）。</summary>
    private static string MetaOf(string root, string localPath)
    {
        var name = Path.GetFileName(localPath) + ".json";
        return Directory.EnumerateFiles(Path.Combine(root, ".smartclassroom-meta"), name,
            SearchOption.AllDirectories).First();
    }

    [Fact]
    public async Task TeacherFile_GoesIntoSubjectFolder()
    {
        var o = await Archive().HandleAsync(Ev(), Teacher(), (_, _) => Task.FromResult<string?>("http://x/f"));
        Assert.Equal(ArchiveResult.Downloaded, o.Result);
        Assert.NotNull(o.LocalPath);
        // 结构：<根>/<科目名>/<文件名>（不再按日期分层）
        Assert.Equal(Path.Combine(_root, "数学", "课件.pptx"), o.LocalPath);
        Assert.True(File.Exists(o.LocalPath));
        Assert.True(File.Exists(MetaOf(_root, o.LocalPath!)));   // 发送时间等仍在 meta 里（隐藏目录）
    }

    [Fact]
    public async Task Meta_RecordsSubject_SoCoursewarePopupCanFilterBySubject()
    {
        var o = await Archive().HandleAsync(Ev(), Teacher(), (_, _) => Task.FromResult<string?>("http://x/f"));

        var meta = System.Text.Json.JsonSerializer.Deserialize<ArchiveMeta>(
            await File.ReadAllTextAsync(MetaOf(_root, o.LocalPath!)));

        Assert.NotNull(meta);
        Assert.Equal("数学", meta!.Subject);       // 上课弹窗靠它判断"当科"
        Assert.Equal("张老师", meta.SenderName);
        Assert.Equal(10001, meta.SenderQq);
    }

    [Fact]
    public async Task Meta_UnknownSubject_StaysEmpty()
    {
        var o = await Archive(downloadAll: true)
            .HandleAsync(Ev(), Stranger(), (_, _) => Task.FromResult<string?>("http://x/f"));

        var meta = System.Text.Json.JsonSerializer.Deserialize<ArchiveMeta>(
            await File.ReadAllTextAsync(MetaOf(_root, o.LocalPath!)));
        Assert.Equal("", meta!.Subject);           // 认不出科目就不能瞎猜
    }

    [Fact]
    public void SubjectFolder_PrefersSubject_ThenTeacher_ThenUnclassified()
    {
        Assert.Equal("数学", FileArchive.SubjectFolder(new SenderInfo { UserId = 1, TeacherName = "张老师", Subject = "数学" }));
        Assert.Equal("张老师", FileArchive.SubjectFolder(new SenderInfo { UserId = 1, TeacherName = "张老师" }));
        Assert.Equal("未分类", FileArchive.SubjectFolder(new SenderInfo { UserId = 999 }));
        // 科目名里的非法文件名字符要被替换，避免建目录失败
        Assert.Equal("数学_物理", FileArchive.SubjectFolder(new SenderInfo { UserId = 1, Subject = "数学/物理" }));
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
        Assert.Contains(Path.Combine("未分类", "课件.pptx"), o.LocalPath);
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

/// <summary>
/// 元数据目录还不存在时，去重扫描不能抛异常。
/// 实测 bug：`Directory.EnumerateFiles(<root>/.smartclassroom-meta)` 在目录不存在时抛
/// DirectoryNotFoundException，被当成"文件归档失败：Could not find a part of the path …"，
/// 明明文件能下却报失败（用户："文件保存失败"）。
/// </summary>
public sealed class ArchiveMetaDirMissingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sc-meta-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken t)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent([1, 2, 3]) });
    }

    private static GroupUploadEvent Upload(string id, string name) => new()
    {
        PostType = "notice", NoticeType = "group_upload", GroupId = 100200300, UserId = 10001,
        File = new UploadedFile { Id = id, Name = name, Size = 3 }
    };

    [Fact]
    public async Task SecondArchive_WhenMetaDirMissing_StillSucceeds()
    {
        var archive = new FileArchive(new ArchiveOptions { Root = _root, DownloadAll = true },
            new HttpClient(new StubHandler()));
        var sender = new SenderInfo { UserId = 10001, TeacherName = "张老师", Subject = "数学" };

        // 第一次：科目目录被创建；元数据目录此时也建好了
        var first = await archive.HandleAsync(Upload("f1", "a.pdf"), sender, (_, _) => Task.FromResult<string?>("http://x/a"));
        Assert.Equal(ArchiveResult.Downloaded, first.Result);

        // 人为删掉元数据目录，模拟"科目目录在、元数据目录不在"（老版本升级上来的真实状态）
        Directory.Delete(Path.Combine(_root, ".smartclassroom-meta"), true);

        var second = await archive.HandleAsync(Upload("f2", "b.pdf"), sender, (_, _) => Task.FromResult<string?>("http://x/b"));

        Assert.Equal(ArchiveResult.Downloaded, second.Result);   // 不能再报"归档失败"
        Assert.True(File.Exists(second.LocalPath));
    }
}
