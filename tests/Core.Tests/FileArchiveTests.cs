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

/// <summary>
/// 归档后往系统"下载"目录再放一份（用户："你得往 ~/Downloads 里面写，
/// 要不然有的老师喜欢在 QQ 里打开，你写了以后 QQ 检测到有了就是秒下"）。
/// 目录解析必须分平台：Windows = %USERPROFILE%\Downloads，Linux = XDG_DOWNLOAD_DIR。
/// </summary>
public sealed class ArchiveMirrorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sc-mirror-" + Guid.NewGuid().ToString("N"));
    private readonly string _mirror = Path.Combine(Path.GetTempPath(), "sc-dl-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        foreach (var d in new[] { _root, _mirror })
            if (Directory.Exists(d)) Directory.Delete(d, true);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken t)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent([9, 8, 7, 6]) });
    }

    [Fact]
    public async Task DownloadedFile_IsMirroredToDownloadsDir()
    {
        Directory.CreateDirectory(_mirror);
        var archive = new FileArchive(new ArchiveOptions
        {
            Root = _root, DownloadAll = true, MirrorDir = _mirror
        }, new HttpClient(new StubHandler()));
        var sender = new SenderInfo { UserId = 10001, TeacherName = "张老师", Subject = "信息技术" };

        var outcome = await archive.HandleAsync(
            new GroupUploadEvent
            {
                PostType = "notice", NoticeType = "group_upload", GroupId = 100200300, UserId = 10001,
                File = new UploadedFile { Id = "f1", Name = "blue_search.py", Size = 4 }
            },
            sender, (_, _) => Task.FromResult<string?>("http://x/f"));

        Assert.Equal(ArchiveResult.Downloaded, outcome.Result);
        var mirrored = Path.Combine(_mirror, "blue_search.py");
        Assert.True(File.Exists(mirrored), "下载目录里应该有这个文件（QQ 靠它判断已接收）");
        Assert.Equal(new byte[] { 9, 8, 7, 6 }, await File.ReadAllBytesAsync(mirrored));
    }

    [Fact]
    public void SystemDownloadsDir_MatchesPlatformConvention()
    {
        var dir = FileArchive.SystemDownloadsDir();

        if (OperatingSystem.IsWindows())
            Assert.EndsWith("Downloads", dir);          // %USERPROFILE%\Downloads
        else
            Assert.Contains("Down", dir, StringComparison.OrdinalIgnoreCase);   // ~/Downloads 或本地化的"下载"
        Assert.True(Path.IsPathRooted(dir));
    }
}

/// <summary>
/// 重启后课件不能消失。踩到的坑：元数据在 0.36.2 起搬到了
/// &lt;Root&gt;/.smartclassroom-meta/**/*.json，而 RebuildFromArchive 只扫旧的 *.meta.json
/// → 新位置归档的文件重启就恢复不出来（用户："每次启动以后，我的信息技术课件怎么消失了"）。
/// </summary>
public sealed class CoursewareRebuildTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sc-cw-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private string WriteFile(string subject, string name, string fileId)
    {
        var dir = Path.Combine(_root, subject);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, "x");
        var metaDir = Path.Combine(_root, ".smartclassroom-meta", subject);
        Directory.CreateDirectory(metaDir);
        File.WriteAllText(Path.Combine(metaDir, name + ".json"),
            System.Text.Json.JsonSerializer.Serialize(new ArchiveMeta
            {
                FileId = fileId, FileName = name, Size = 1, GroupId = 0,
                SenderQq = 10001, SenderName = "张老师", Subject = subject,
                Time = DateTimeOffset.Now, LocalPath = path
            }));
        return path;
    }

    [Fact]
    public void NewMetaLocation_IsPickedUp()
    {
        WriteFile("信息技术", "blue_search.py", "f1");
        WriteFile("信息技术", "brute.py", "f2");

        var svc = new CoursewareService();
        svc.RebuildFromArchive(_root);

        var subjects = svc.QueryAll().Select(f => f.Subject).Distinct().ToList();
        Assert.Contains("信息技术", subjects);
        Assert.Equal(2, svc.QueryAll().Count);
    }

    [Fact]
    public void FilesWithoutMeta_StillShowUpByFolderName()
    {
        // 元数据丢了（写入失败/手工放进来）也不能凭空消失
        var dir = Path.Combine(_root, "数学");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "handout.pdf"), "x");

        var svc = new CoursewareService();
        svc.RebuildFromArchive(_root);

        var item = Assert.Single(svc.QueryAll());
        Assert.Equal("handout.pdf", item.FileName);
        Assert.Equal("数学", item.Subject);
    }

    [Fact]
    public void HiddenMetaDir_IsNotTreatedAsSubject()
    {
        WriteFile("数学", "a.pdf", "f1");

        var svc = new CoursewareService();
        svc.RebuildFromArchive(_root);

        Assert.DoesNotContain(svc.QueryAll(), f => f.Subject == ".smartclassroom-meta");
    }
}
