using SmartClassroom.Contracts;
using Xunit;

namespace SmartClassroom.Core.Tests;

public sealed class CoursewareServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sc-courseware-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    private string Touch(string name)
    {
        Directory.CreateDirectory(_root);
        var p = Path.Combine(_root, name);
        File.WriteAllBytes(p, [1, 2, 3]);
        return p;
    }

    private static CoursewareFile MakeFile(string id, string teacher, DateOnly date, string? path,
        string? subject = null)
        => new()
        {
            FileId = id, FileName = id + ".pptx", Size = 10,
            Sender = new SenderInfo { UserId = 1, TeacherName = teacher },
            Source = new MessageRef { GroupId = 1, MessageId = 1 },
            LocalPath = path, Subject = subject, ClassDate = date
        };

    // ================= 「课件」页：当天全部 =================

    [Fact]
    public void QueryDay_FiltersByDate()
    {
        var svc = new CoursewareService();
        var today = DateOnly.FromDateTime(DateTime.Now);
        svc.Register(MakeFile("a", "张老师", today, Touch("a.pptx")));
        svc.Register(MakeFile("b", "李老师", today, Touch("b.pptx")));
        svc.Register(MakeFile("c", "张老师", today.AddDays(-1), Touch("c.pptx")));
        svc.Register(MakeFile("a", "张老师", today, Touch("a2.pptx"))); // 同 file_id 去重

        var q = svc.QueryDay(today);
        Assert.Equal(2, q.Count);
        Assert.Equal(["a", "b"], q.Select(f => f.FileId).OrderBy(x => x));
    }

    [Fact]
    public void QueryDay_ExcludesMissingFiles()
    {
        var svc = new CoursewareService();
        svc.Register(MakeFile("x", "张老师", DateOnly.FromDateTime(DateTime.Now), "/nonexistent/x.pptx"));
        Assert.Empty(svc.QueryDay(DateOnly.FromDateTime(DateTime.Now)));
    }

    // ================= 上课弹窗：当天 + 当科 =================

    [Fact]
    public void QueryForLesson_SameDaySameSubject_Only()
    {
        var svc = new CoursewareService();
        var today = DateOnly.FromDateTime(DateTime.Now);
        svc.Register(MakeFile("math", "张老师", today, Touch("math.pptx"), "数学"));
        svc.Register(MakeFile("chinese", "李老师", today, Touch("chinese.pptx"), "语文"));
        svc.Register(MakeFile("math-old", "张老师", today.AddDays(-1), Touch("old.pptx"), "数学"));

        var q = svc.QueryForLesson(today, "数学", "张老师");
        Assert.Single(q);
        Assert.Equal("math", q[0].FileId);   // 不弹语文、不弹昨天的
    }

    [Fact]
    public void QueryForLesson_EmptySubject_NeverPops()
    {
        var svc = new CoursewareService();
        var today = DateOnly.FromDateTime(DateTime.Now);
        svc.Register(MakeFile("math", "张老师", today, Touch("math.pptx"), "数学"));

        // 连科目都不知道（课表没给）就不弹，避免"上数学弹语文"
        Assert.Empty(svc.QueryForLesson(today, null));
        Assert.Empty(svc.QueryForLesson(today, "  "));
    }

    [Fact]
    public void QueryForLesson_UnknownSubject_FallsBackToTeacherIdentity()
    {
        var svc = new CoursewareService();
        var today = DateOnly.FromDateTime(DateTime.Now);
        // 认不出科目（未分类），但能认出是这位老师发的
        svc.Register(MakeFile("unknown", "张老师", today, Touch("unknown.pptx")));

        Assert.Single(svc.QueryForLesson(today, "数学", "张老师"));
        Assert.Single(svc.QueryForLesson(today, "数学", null, teacherQq: 1));
        // 换成别的老师就不该看别人的"未分类"文件
        Assert.Empty(svc.QueryForLesson(today, "数学", "李老师"));
    }

    [Fact]
    public void QueryForLesson_SubjectMatchWinsEvenWithoutTeacher()
    {
        var svc = new CoursewareService();
        var today = DateOnly.FromDateTime(DateTime.Now);
        svc.Register(MakeFile("math", "张老师", today, Touch("math.pptx"), "数学"));

        // 科目对上就够（同一科目的老师共享）；科目首尾空格不敏感
        Assert.Single(svc.QueryForLesson(today, " 数学 "));
        Assert.Single(svc.QueryForLesson(today, "数学", "别的老师"));
    }

    [Fact]
    public void TryMarkShown_OncePerLesson()
    {
        var svc = new CoursewareService();
        var today = DateOnly.FromDateTime(DateTime.Now);
        Assert.True(svc.TryMarkShown(today, "数学"));
        Assert.False(svc.TryMarkShown(today, "数学"));
        Assert.True(svc.TryMarkShown(today, "语文"));
    }
}
