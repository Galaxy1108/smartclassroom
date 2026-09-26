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
        foreach (var metaFile in Directory.EnumerateFiles(archiveRoot, "*.meta.json", SearchOption.AllDirectories))
        {
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
