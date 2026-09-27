using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using Avalonia.Media.Imaging;

namespace SmartClassroom.App.ViewModels;

public sealed record CoursewareItem
{
    public required string FileName { get; init; }
    public required string LocalPath { get; init; }
    public required string SizeText { get; init; }
    public required bool IsImage { get; init; }
    public required string TypeLabel { get; init; }
    public Bitmap? Thumbnail { get; init; }
}

/// <summary>科目分组（课件页第一层）。</summary>
public sealed class CoursewareSubject
{
    public required string Name { get; init; }
    public required int Count { get; init; }
    public required DateTimeOffset? Latest { get; init; }

    public string CountText => $"{Count} 个文件";
    public string LatestText => Latest is { } t ? $"最近 {t.LocalDateTime:MM-dd HH:mm}" : "无时间记录";
}

/// <summary>时间轴里的一天。</summary>
public sealed class CoursewareDayGroup
{
    public required string DateLabel { get; init; }
    public required List<CoursewareTimelineRow> Rows { get; init; }
}

/// <summary>时间轴一行（时间 + 文件）。</summary>
public sealed class CoursewareTimelineRow
{
    public required string TimeLabel { get; init; }
    public required CoursewareItem Item { get; init; }
    public string FileName => Item.FileName;
    public string SizeText => Item.SizeText;
    public string TypeLabel => Item.TypeLabel;
    public bool IsImage => Item.IsImage;
    public Bitmap? Thumbnail => Item.Thumbnail;
}

/// <summary>课件弹窗视图模型：缩略图加载 + 双击打开。</summary>
public sealed class CoursewareViewModel : ViewModelBase
{
    private static readonly string[] ImageExts = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp"];

    /// <summary>弹窗标题（「您可能需要的课件」）。</summary>
    public string Title => "您可能需要的课件";

    /// <summary>弹窗/预览用的扁平列表。</summary>
    public ObservableCollection<CoursewareItem> Items { get; } = new();

    /// <summary>科目分组（第一层）。</summary>
    public ObservableCollection<CoursewareSubject> Subjects { get; } = new();

    /// <summary>选中科目后的时间轴（第二层，按天分组、天内按时间倒序）。</summary>
    public ObservableCollection<CoursewareDayGroup> Timeline { get; } = new();

    private string? _selectedSubject;
    public string? SelectedSubject
    {
        get => _selectedSubject;
        private set
        {
            if (!Set(ref _selectedSubject, value))
                return;
            OnPropertyChanged(nameof(IsSubjectList));
            OnPropertyChanged(nameof(IsTimeline));
            OnPropertyChanged(nameof(SubjectTitle));
        }
    }

    public bool IsSubjectList => SelectedSubject is null;
    public bool IsTimeline => SelectedSubject is not null;
    public string SubjectTitle => SelectedSubject ?? "";

    /// <summary>
    /// 有没有内容。**必须同时看扁平列表（Items，上课弹窗用）和科目列表（Subjects，课件页用）** ——
    /// 只看 Subjects 的话，上课弹窗里明明列出了文件，下面还挂着"还没有归档到课件"的空提示
    ///（实测反馈："这不是有课件吗"）。
    /// </summary>
    public bool IsEmpty => Subjects.Count == 0 && Items.Count == 0;

    public string EmptyHint => "还没有归档到课件。老师往群里发文件后，"
                             + "开启「群文件自动归档」即可在这里看到。";

    private string _signature = "";

    /// <summary>
    /// 按科目分组：&lt;科目&gt; → 该科目的文件（按时间倒序）。
    ///
    /// **必须保住用户当前所在的科目**：这个方法会被定时刷新反复调用，
    /// 以前每次都会把 SelectedSubject 置空 —— 用户点进某个科目的时间轴后立刻被踢回列表
    ///（用户反馈："课件点进去具体科目以后又给我自动返回"）。
    /// 顺带做了指纹比较：内容没变就直接返回，避免每次刷新都重建列表（闪烁）。
    /// </summary>
    public void GroupBySubject(IEnumerable<(string Subject, string FileName, string LocalPath, long Size, DateTimeOffset? At)> files)
    {
        var all = files.ToList();
        var signature = string.Join('\u0002', all
            .Select(f => $"{f.Subject}|{f.FileName}|{f.LocalPath}|{f.Size}|{f.At?.UtcTicks}"));
        if (signature == _signature && Subjects.Count > 0)
            return;                       // 没变化：别动界面（也保住当前科目）
        _signature = signature;

        var previous = SelectedSubject;    // 刷新前用户在看哪个科目
        Subjects.Clear();
        foreach (var group in all
                     .GroupBy(f => string.IsNullOrWhiteSpace(f.Subject) ? "未分类" : f.Subject.Trim())
                     .OrderByDescending(g => g.Max(f => f.At ?? DateTimeOffset.MinValue)))
        {
            Subjects.Add(new CoursewareSubject
            {
                Name = group.Key,
                Count = group.Count(),
                Latest = group.Max(f => f.At)
            });
        }
        _bySubject = all.GroupBy(f => string.IsNullOrWhiteSpace(f.Subject) ? "未分类" : f.Subject.Trim())
            .ToDictionary(g => g.Key, g => g.OrderByDescending(f => f.At ?? DateTimeOffset.MinValue).ToList());
        SelectedSubject = null;
        Timeline.Clear();
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyHint));

        // 还在原来那个科目里就继续待着（数据变了也要留在原地）
        if (previous is not null && _bySubject.ContainsKey(previous))
            OpenSubject(previous);
    }

    private Dictionary<string, List<(string Subject, string FileName, string LocalPath, long Size, DateTimeOffset? At)>> _bySubject = new();

    /// <summary>进入某个科目的时间轴。</summary>
    public void OpenSubject(string subject)
    {
        if (!_bySubject.TryGetValue(subject, out var files))
            return;
        Timeline.Clear();
        foreach (var day in files.GroupBy(f => (f.At?.LocalDateTime ?? DateTime.MinValue).Date)
                     .OrderByDescending(g => g.Key))
        {
            var label = day.Key == DateTime.MinValue.Date
                ? "无时间记录"
                : day.Key.ToString("yyyy-MM-dd") + "　" + Weekday(day.Key);
            Timeline.Add(new CoursewareDayGroup
            {
                DateLabel = label,
                Rows = day.Select(f => new CoursewareTimelineRow
                {
                    TimeLabel = f.At is { } t ? t.LocalDateTime.ToString("HH:mm") : "--:--",
                    Item = CreateItem(f.FileName, f.LocalPath, f.Size)
                }).ToList()
            });
        }
        SelectedSubject = subject;
    }

    /// <summary>回到科目列表。</summary>
    public void BackToSubjects()
    {
        SelectedSubject = null;
        Timeline.Clear();
    }

    private static string Weekday(DateTime date) => date.DayOfWeek switch
    {
        DayOfWeek.Monday => "周一",
        DayOfWeek.Tuesday => "周二",
        DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四",
        DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六",
        _ => "周日"
    };

    public static CoursewareViewModel FromFiles(IEnumerable<(string FileName, string LocalPath, long Size)> files)
    {
        var vm = new CoursewareViewModel();
        foreach (var (name, path, size) in files)
            vm.Items.Add(CreateItem(name, path, size));
        vm.OnPropertyChanged(nameof(IsEmpty));   // 空提示要跟着扁平列表走
        return vm;
    }

    internal static CoursewareItem CreateItem(string fileName, string localPath, long size)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        var isImage = ImageExts.Contains(ext) && File.Exists(localPath);
        Bitmap? thumb = null;
        if (isImage)
        {
            try
            {
                using var fs = File.OpenRead(localPath);
                thumb = Bitmap.DecodeToWidth(fs, 160);
            }
            catch { isImage = false; }
        }
        return new CoursewareItem
        {
            FileName = fileName,
            LocalPath = localPath,
            SizeText = FormatSize(size),
            IsImage = isImage,
            TypeLabel = ext.TrimStart('.').ToUpperInvariant(),
            Thumbnail = thumb
        };
    }

    public static void Open(string localPath)
    {
        Process.Start(new ProcessStartInfo(localPath) { UseShellExecute = true });
    }

    internal static string FormatSize(long bytes)
        => bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:F1} MB",
            _ => $"{bytes / 1024.0 / 1024 / 1024:F1} GB"
        };
}
