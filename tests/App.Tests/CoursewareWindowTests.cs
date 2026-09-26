using Avalonia.Headless.XUnit;
using SmartClassroom.App.ViewModels;
using Xunit;

namespace SmartClassroom.App.Tests;

/// <summary>
/// 课件视图模型：缩略图解码、类型标识、大小格式化。
/// 窗口本身（CoursewareWindow）需要 Compositor，本沙箱无法构造，故只测 VM。
/// </summary>
public sealed class CoursewareTests
{
    [AvaloniaFact]
    public void FromFiles_DecodesImageAndLabelsOthers()
    {
        var png = Path.Combine(Path.GetTempPath(), "sc-thumb-" + Guid.NewGuid().ToString("N") + ".png");
        // 最小合法 PNG（1x1），不引入图片库。
        File.WriteAllBytes(png, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="));
        try
        {
            var vm = CoursewareViewModel.FromFiles([
                ("课件.pptx", Path.Combine(Path.GetTempPath(), "no-such-file.pptx"), 12345678),
                ("板书.png", png, 1234),
            ]);

            Assert.Equal("您可能需要的课件", vm.Title);
            Assert.Equal(2, vm.Items.Count);
            Assert.False(vm.Items[0].IsImage);
            Assert.Equal("PPTX", vm.Items[0].TypeLabel);
            Assert.Equal("11.8 MB", vm.Items[0].SizeText);
            Assert.True(vm.Items[1].IsImage);
            Assert.NotNull(vm.Items[1].Thumbnail);
        }
        finally
        {
            File.Delete(png);
        }
    }

    [AvaloniaFact]
    public void EmptyCourseware_HasHintForPreviewWindow()
    {
        // 预览按钮在空列表时也要能打开窗口并说明原因
        // （之前空列表直接 return，用户点了没反应会以为弹窗坏了）
        var vm = new CoursewareViewModel();
        Assert.True(vm.IsEmpty);
        Assert.False(string.IsNullOrWhiteSpace(vm.EmptyHint));
        Assert.Contains("归档", vm.EmptyHint);
    }
}

/// <summary>
/// 课件页：先按科目分类，点进科目后按时间轴（天分组、天内按时间倒序）。
/// </summary>
public sealed class CoursewareTimelineTests
{
    private static (string Subject, string FileName, string LocalPath, long Size, DateTimeOffset? At) F(
        string subject, string name, string at)
        => (subject, name, "/tmp/" + name, 100,
            at.Length == 0 ? null : DateTimeOffset.Parse(at));

    [AvaloniaFact]
    public void Subjects_AreGroupedAndSortedByLatest()
    {
        var vm = new CoursewareViewModel();
        vm.GroupBySubject([
            F("数学", "函数.pptx", "2026-09-26T10:00:00+08:00"),
            F("数学", "数列.pptx", "2026-09-25T09:00:00+08:00"),
            F("语文", "古诗.pptx", "2026-09-26T14:00:00+08:00"),
            F("", "说明.pdf", "2026-09-24T08:00:00+08:00")
        ]);

        Assert.Equal(["语文", "数学", "未分类"], vm.Subjects.Select(s => s.Name));   // 最近有更新的在前
        Assert.Equal(2, vm.Subjects[1].Count);
        Assert.Equal("2 个文件", vm.Subjects[1].CountText);
        Assert.Contains("09-26", vm.Subjects[1].LatestText);
        Assert.False(vm.IsEmpty);
    }

    [AvaloniaFact]
    public void OpenSubject_BuildsTimelineGroupedByDay_NewestFirst()
    {
        var vm = new CoursewareViewModel();
        vm.GroupBySubject([
            F("数学", "函数.pptx", "2026-09-26T10:00:00+08:00"),
            F("数学", "数列.pptx", "2026-09-26T08:30:00+08:00"),
            F("数学", "旧课件.pptx", "2026-09-20T15:00:00+08:00"),
            F("语文", "古诗.pptx", "2026-09-26T14:00:00+08:00")
        ]);

        Assert.True(vm.IsSubjectList);
        vm.OpenSubject("数学");

        Assert.True(vm.IsTimeline);
        Assert.False(vm.IsSubjectList);
        Assert.Equal("数学", vm.SubjectTitle);
        Assert.Equal(2, vm.Timeline.Count);                       // 两天
        Assert.StartsWith("2026-09-26", vm.Timeline[0].DateLabel);
        Assert.Contains("周六", vm.Timeline[0].DateLabel);
        Assert.Equal(["10:00", "08:30"], vm.Timeline[0].Rows.Select(r => r.TimeLabel));   // 天内倒序
        Assert.Equal("函数.pptx", vm.Timeline[0].Rows[0].FileName);
        Assert.Equal("旧课件.pptx", Assert.Single(vm.Timeline[1].Rows).FileName);
    }

    [AvaloniaFact]
    public void Back_ReturnsToSubjectList()
    {
        var vm = new CoursewareViewModel();
        vm.GroupBySubject([F("数学", "函数.pptx", "2026-09-26T10:00:00+08:00")]);
        vm.OpenSubject("数学");

        vm.BackToSubjects();

        Assert.True(vm.IsSubjectList);
        Assert.Empty(vm.Timeline);
    }

    [AvaloniaFact]
    public void NoTimeRecord_FallsIntoOneGroup()
    {
        var vm = new CoursewareViewModel();
        vm.GroupBySubject([F("数学", "无时间.pptx", "")]);
        vm.OpenSubject("数学");

        var day = Assert.Single(vm.Timeline);
        Assert.Equal("无时间记录", day.DateLabel);
        Assert.Equal("--:--", Assert.Single(day.Rows).TimeLabel);
    }

    [AvaloniaFact]
    public void EmptyArchive_ShowsHint()
    {
        var vm = new CoursewareViewModel();
        vm.GroupBySubject([]);

        Assert.True(vm.IsEmpty);
        Assert.Contains("群文件自动归档", vm.EmptyHint);
        Assert.Empty(vm.Subjects);
    }
}

/// <summary>
/// 定时刷新不能把用户从科目时间轴里踢回科目列表 ——
/// 实测 bug：GroupBySubject 每次刷新都置空 SelectedSubject，
/// 用户"点进去具体科目以后又给我自动返回"。
/// </summary>
public sealed class CoursewareNavigationTests
{
    private static (string Subject, string FileName, string LocalPath, long Size, DateTimeOffset? At) F(
        string subject, string name, int day)
        => (subject, name, "/tmp/" + name, 1024, new DateTimeOffset(2026, 9, day, 10, 0, 0, TimeSpan.FromHours(8)));

    [AvaloniaFact]
    public void Refresh_KeepsUserInsideSubjectTimeline()
    {
        var vm = new CoursewareViewModel();
        vm.GroupBySubject([F("数学", "a.pptx", 20), F("语文", "b.docx", 21)]);

        vm.OpenSubject("数学");
        Assert.True(vm.IsTimeline);
        Assert.Single(vm.Timeline);

        // 定时刷新（同样的数据）→ 必须还在数学里
        vm.GroupBySubject([F("数学", "a.pptx", 20), F("语文", "b.docx", 21)]);
        Assert.True(vm.IsTimeline);
        Assert.Equal("数学", vm.SubjectTitle);

        // 数据变了（新增文件）→ 也还在数学里，并且时间轴更新了
        vm.GroupBySubject([F("数学", "a.pptx", 20), F("数学", "c.pdf", 22), F("语文", "b.docx", 21)]);
        Assert.True(vm.IsTimeline);
        Assert.Equal("数学", vm.SubjectTitle);
        Assert.Equal(2, vm.Timeline.Sum(g => g.Rows.Count));

        // 科目没了（文件被删/移走）→ 退回列表，不能卡在空时间轴
        vm.GroupBySubject([F("语文", "b.docx", 21)]);
        Assert.True(vm.IsSubjectList);
    }
}
