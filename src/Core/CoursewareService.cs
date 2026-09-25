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
                    ClassDate = DateOnly.FromDateTime(meta.Time.LocalDateTime)
                });
            }
            catch { /* 坏 meta 跳过 */ }
        }
    }

    /// <summary>查询某日某老师（按姓名或 QQ）的课件。返回前先过滤本地文件仍存在的。</summary>
    public IReadOnlyList<CoursewareFile> Query(DateOnly date, string? teacherName, long? teacherQq = null)
        => _files.Where(f => f.ClassDate == date
            && f.LocalPath is not null && File.Exists(f.LocalPath)
            && (teacherName is not null && f.Sender.TeacherName == teacherName
                || teacherQq is not null && f.Sender.UserId == teacherQq))
            .OrderBy(f => f.FileName).ToList();

    /// <summary>本节课是否已弹过（去重键：日期+科目）。未弹过则标记并返回 true。</summary>
    public bool TryMarkShown(DateOnly date, string subject)
        => _shownKeys.Add($"{date:yyyy-MM-dd}@{subject}");
}
