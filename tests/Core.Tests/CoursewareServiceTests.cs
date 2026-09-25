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

    private static CoursewareFile MakeFile(string id, string teacher, DateOnly date, string? path)
        => new()
        {
            FileId = id, FileName = id + ".pptx", Size = 10,
            Sender = new SenderInfo { UserId = 1, TeacherName = teacher },
            Source = new MessageRef { GroupId = 1, MessageId = 1 },
            LocalPath = path, ClassDate = date
        };

    [Fact]
    public void Query_FiltersByDateAndTeacher()
    {
        var svc = new CoursewareService();
        var today = DateOnly.FromDateTime(DateTime.Now);
        svc.Register(MakeFile("a", "张老师", today, Touch("a.pptx")));
        svc.Register(MakeFile("b", "李老师", today, Touch("b.pptx")));
        svc.Register(MakeFile("c", "张老师", today.AddDays(-1), Touch("c.pptx")));
        svc.Register(MakeFile("a", "张老师", today, Touch("a2.pptx"))); // 同 file_id 去重

        var q = svc.Query(today, "张老师");
        Assert.Single(q);
        Assert.Equal("a", q[0].FileId);
    }

    [Fact]
    public void Query_ExcludesMissingFiles()
    {
        var svc = new CoursewareService();
        svc.Register(MakeFile("x", "张老师", DateOnly.FromDateTime(DateTime.Now), "/nonexistent/x.pptx"));
        Assert.Empty(svc.Query(DateOnly.FromDateTime(DateTime.Now), "张老师"));
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
