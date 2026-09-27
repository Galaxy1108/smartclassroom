using SmartClassroom.Contracts;
using SmartClassroom.Core.QQ;

namespace SmartClassroom.Core;

/// <summary>文件归档配置。</summary>
public sealed record ArchiveOptions
{
    public required string Root { get; init; }

    /// <summary>false = 仅下载教师映射命中的发送者（默认）；true = 下载所有群文件。</summary>
    public bool DownloadAll { get; init; } = false;

    /// <summary>超过此大小先登记不下载，等用户确认（默认 100MB）。</summary>
    public long LargeFileConfirmBytes { get; init; } = 100 * 1024 * 1024;
}

/// <summary>
/// 群文件自动归档：**按科目分类** → &lt;Root&gt;/&lt;科目名&gt;/&lt;文件名&gt; + 同名 .meta.json。
/// 目录只到科目一层（不按日期再分层）；发送时间、群号、发送者都记在 meta.json 里。
/// 同 file_id 不重复下载；重名自动加 (1)(2)；非老师文件按配置决定。
/// </summary>
public sealed class FileArchive(ArchiveOptions options, HttpClient? http = null)
{
    private readonly HttpClient _http = http ?? new HttpClient();

    /// <summary>
    /// 归档子目录名：优先用教师映射里的**科目**；
    /// 没配科目但认得出发送者时退化为老师姓名，都不认识则归入「未分类」。
    /// </summary>
    public static string SubjectFolder(SenderInfo sender)
    {
        if (!string.IsNullOrWhiteSpace(sender.Subject))
            return Sanitize(sender.Subject);
        if (!string.IsNullOrWhiteSpace(sender.TeacherName))
            return Sanitize(sender.TeacherName);
        return "未分类";
    }

    /// <summary>处理一条群上传事件。返回归档结果（下载/跳过/待确认）。</summary>
    public async Task<ArchiveOutcome> HandleAsync(
        GroupUploadEvent ev,
        SenderInfo sender,
        Func<GroupUploadEvent, CancellationToken, Task<string?>> resolveUrl,
        CancellationToken cancel = default)
    {
        var dir = Path.Combine(options.Root, SubjectFolder(sender));

        // 去重：扫描该科目目录下的 meta，同 file_id 且本地文件仍在 → 已归档。
        if (Directory.Exists(dir))
        {
            // 新位置：<Root>/.smartclassroom-meta/**/*.json；旧位置：科目目录里的 *.meta.json（兼容）
            // ⚠️ 元数据目录可能还不存在（第一次归档前），EnumerateFiles 会抛
            // DirectoryNotFoundException —— 实测表现为"文件归档失败：Could not find a part of the path
            // '<root>/.smartclassroom-meta'"，明明文件能下却报失败。
            var metaFiles = Directory.Exists(MetaDir(options.Root))
                ? Directory.EnumerateFiles(MetaDir(options.Root), "*.json", SearchOption.AllDirectories)
                : [];
            foreach (var metaFile in metaFiles
                         .Concat(Directory.EnumerateFiles(dir, "*.meta.json", SearchOption.AllDirectories)))
            {
                try
                {
                    var meta = System.Text.Json.JsonSerializer.Deserialize<ArchiveMeta>(await File.ReadAllTextAsync(metaFile, cancel));
                    if (meta?.FileId == ev.File.Id && meta.LocalPath is not null && File.Exists(meta.LocalPath))
                        return new ArchiveOutcome(ArchiveResult.AlreadyExists, meta.LocalPath);
                }
                catch { /* 单个 meta 损坏就跳过 */ }
            }
        }

        var knownTeacher = sender.TeacherName is not null;
        if (!knownTeacher && !options.DownloadAll)
            return new ArchiveOutcome(ArchiveResult.SkippedUnknownSender, null);

        if (ev.File.Size >= options.LargeFileConfirmBytes)
            return new ArchiveOutcome(ArchiveResult.PendingConfirm, null);

        var url = await resolveUrl(ev, cancel).ConfigureAwait(false);
        if (url is null)
            return new ArchiveOutcome(ArchiveResult.Failed, null, "取下载链接失败");

        Directory.CreateDirectory(dir);
        var localPath = UniquePath(dir, ev.File.Name);
        try
        {
            using var res = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
            res.EnsureSuccessStatusCode();
            await using var src = await res.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
            await using var dst = File.Create(localPath);
            await src.CopyToAsync(dst, cancel).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return new ArchiveOutcome(ArchiveResult.Failed, null, ex.Message);
        }

        var record = new ArchiveMeta
        {
            FileId = ev.File.Id,
            FileName = ev.File.Name,
            Size = ev.File.Size,
            GroupId = ev.GroupId,
            SenderQq = sender.UserId,
            SenderName = sender.TeacherName ?? sender.Card ?? sender.Nickname ?? "",
            Subject = sender.Subject ?? "",
            Time = DateTimeOffset.Now,
            LocalPath = localPath
        };
        await WriteMetaAsync(options.Root, record, cancel).ConfigureAwait(false);
        return new ArchiveOutcome(ArchiveResult.Downloaded, localPath);
    }

    /// <summary>
    /// 元数据目录（<c>&lt;Root&gt;/.smartclassroom-meta/&lt;科目&gt;/</c>）。
    /// 以前写成"同名 .meta.json"放在科目文件夹里，用户会以为归档了一堆垃圾文件；
    /// 现在统一收到隐藏目录里，科目文件夹里只有真正的文件。
    /// </summary>
    internal static string MetaDir(string root) => Path.Combine(root, ".smartclassroom-meta");

    private static async Task WriteMetaAsync(string root, ArchiveMeta record, CancellationToken cancel)
    {
        try
        {
            var dir = Path.Combine(MetaDir(root), Sanitize(record.Subject.Length > 0 ? record.Subject : "未分类"));
            Directory.CreateDirectory(dir);
            var name = Sanitize(Path.GetFileName(record.LocalPath)) + ".json";
            await File.WriteAllTextAsync(Path.Combine(dir, name),
                System.Text.Json.JsonSerializer.Serialize(record), cancel).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 元数据写不进去不影响文件已经归档这件事
        }
    }

    internal static string UniquePath(string dir, string fileName)
    {
        var path = Path.Combine(dir, Sanitize(fileName));
        if (!File.Exists(path))
            return path;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 1; ; i++)
        {
            var p = Path.Combine(dir, $"{Sanitize(stem)}({i}){ext}");
            if (!File.Exists(p))
                return p;
        }
    }

    internal static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Trim().Length == 0 ? "unnamed" : name.Trim();
    }
}

public enum ArchiveResult
{
    Downloaded,
    AlreadyExists,
    SkippedUnknownSender,
    PendingConfirm,
    Failed
}

public sealed record ArchiveOutcome(ArchiveResult Result, string? LocalPath, string? Error = null);

public sealed record ArchiveMeta
{
    public string FileId { get; init; } = "";
    public string FileName { get; init; } = "";
    public long Size { get; init; }
    public long GroupId { get; init; }
    public long SenderQq { get; init; }
    public string SenderName { get; init; } = "";

    /// <summary>教师映射命中的科目；空 = 认不出（旧的 meta 也没有这个字段）。</summary>
    public string Subject { get; init; } = "";

    public DateTimeOffset Time { get; init; }
    public string? LocalPath { get; init; }
}
