using SmartClassroom.Contracts;

namespace SmartClassroom.Core;

/// <summary>
/// 当天课件索引：登记已归档文件，供上课事件触发"您可能需要的课件"弹窗。
/// 内存索引 + 归档目录扫描双来源（重启后可重建）。
/// </summary>
public sealed class CoursewareService
{
    private readonly List<CoursewareFile> _files = new();
    private readonly HashSet<string> _shownKeys = new();

    public void Register(CoursewareFile file)
    {
        if (_files.Any(f => f.FileId == file.FileId))
            return;
        _files.Add(file);
    }

    /// <summary>扫描归档根目录重建索引（meta sidecar）。</summary>
    public void RebuildFromArchive(string archiveRoot)
    {
        if (!Directory.Exists(archiveRoot))
            return;

        // ⚠️ 元数据有两个位置：0.36.2 起写在 <Root>/.smartclassroom-meta/**/*.json，
        // 更早的版本写在各科目目录里的 *.meta.json。**两个都要扫** ——
        // 只扫旧位置的话，元数据搬家之后归档的文件重启就恢复不出来
        //（实测反馈："每次启动以后，我的信息技术课件怎么消失了"）。
        var metaDir = FileArchive.MetaDir(archiveRoot);
        var metaFiles = Directory.Exists(metaDir)
            ? Directory.EnumerateFiles(metaDir, "*.json", SearchOption.AllDirectories)
            : [];
        var legacyFiles = Directory.EnumerateFiles(archiveRoot, "*.meta.json", SearchOption.AllDirectories);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var metaFile in metaFiles.Concat(legacyFiles))
        {
            if (!seen.Add(metaFile))
                continue;
            try
            {
                var meta = System.Text.Json.JsonSerializer.Deserialize<ArchiveMeta>(File.ReadAllText(metaFile));
                if (meta?.LocalPath is null || !File.Exists(meta.LocalPath))
                    continue;
                Register(new CoursewareFile
                {
                    FileId = meta.FileId,
                    FileName = meta.FileName,
                    Size = meta.Size,
                    Sender = new SenderInfo { UserId = meta.SenderQq, TeacherName = meta.SenderName.Length > 0 ? meta.SenderName : null },
                    Source = new MessageRef { GroupId = meta.GroupId, MessageId = 0 },
                    LocalPath = meta.LocalPath,
                    Subject = meta.Subject.Length > 0 ? meta.Subject : null,
                    ClassDate = DateOnly.FromDateTime(meta.Time.LocalDateTime),
                    ArchivedAt = meta.Time
                });
            }
            catch { /* 坏 meta 跳过 */ }
        }

        // 兜底：没有任何 meta 的文件（例如元数据写入失败/手工放进来的），
        // 按"科目目录名"登记，至少不会在课件页里凭空消失。
        foreach (var dir in Directory.EnumerateDirectories(archiveRoot))
        {
            var subject = Path.GetFileName(dir);
            if (subject.StartsWith(".", StringComparison.Ordinal))
                continue;   // .smartclassroom-meta 之类的隐藏目录不算科目
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (file.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (_files.Any(f => string.Equals(f.LocalPath, file, StringComparison.Ordinal)))
                    continue;
                var info = new FileInfo(file);
                Register(new CoursewareFile
                {
                    FileId = file,
                    FileName = info.Name,
                    Size = info.Length,
                    Sender = new SenderInfo { UserId = 0 },
                    Source = new MessageRef { GroupId = 0, MessageId = 0 },
                    LocalPath = file,
                    Subject = subject,
                    ClassDate = DateOnly.FromDateTime(info.LastWriteTime),
                    ArchivedAt = new DateTimeOffset(info.LastWriteTime)
                });
            }
        }
    }

    /// <summary>全部课件（「课件」页按科目分组用），按归档时间倒序。</summary>
    public IReadOnlyList<CoursewareFile> QueryAll()
        => _files.Where(f => f.LocalPath is not null && File.Exists(f.LocalPath))
            .OrderByDescending(f => f.ArchivedAt ?? DateTimeOffset.MinValue)
            .ThenBy(f => f.FileName)
            .ToList();

    /// <summary>某日全部课件（上课弹窗的预览用）。</summary>
    public IReadOnlyList<CoursewareFile> QueryDay(DateOnly date)
        => _files.Where(f => f.ClassDate == date && f.LocalPath is not null && File.Exists(f.LocalPath))
            .OrderBy(f => f.FileName).ToList();

    /// <summary>
    /// 上课弹窗用的严格查询：**只取当天的、当科的**。
    /// 规则：
    /// 1) 必须是同一天且本地文件还在；
    /// 2) 科目必须匹配（AI/映射认不出科目的文件不算"当科"，除非能确认就是这位老师发的——
    ///    那种情况属于"老师的文件但分不出科目"，仍然给他看，总比漏掉强）；
    /// 3) 科目为空时一律不弹（无从判断）。
    /// </summary>
    public IReadOnlyList<CoursewareFile> QueryForLesson(
        DateOnly date, string? subject, string? teacherName = null, long? teacherQq = null)
    {
        if (string.IsNullOrWhiteSpace(subject))
            return [];
        return _files.Where(f => f.ClassDate == date
                && f.LocalPath is not null && File.Exists(f.LocalPath)
                && LessonMatch(f, subject, teacherName, teacherQq))
            .OrderBy(f => f.FileName).ToList();
    }

    private static bool LessonMatch(CoursewareFile f, string subject, string? teacherName, long? teacherQq)
    {
        var subjectKnown = !string.IsNullOrWhiteSpace(f.Subject);
        if (subjectKnown
            && string.Equals(f.Subject!.Trim(), subject.Trim(), StringComparison.OrdinalIgnoreCase))
            return true;

        // 科目未知：只有确认是这位老师的文件才放行（同一老师只有一门课，误弹概率低）。
        var identityMatch = teacherQq is not null && f.Sender.UserId == teacherQq
            || !string.IsNullOrWhiteSpace(teacherName)
               && string.Equals(f.Sender.TeacherName ?? "", teacherName.Trim(), StringComparison.OrdinalIgnoreCase);
        return !subjectKnown && identityMatch;
    }

    /// <summary>本节课是否已弹过（去重键：日期+科目）。未弹过则标记并返回 true。</summary>
    public bool TryMarkShown(DateOnly date, string subject)
        => _shownKeys.Add($"{date:yyyy-MM-dd}@{subject}");
}
