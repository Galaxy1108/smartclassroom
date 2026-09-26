using System.Net;
using System.Text.Json;
using SmartClassroom.Contracts;
using SmartClassroom.Core.AI;
using SmartClassroom.Core.QQ;
using Xunit;

namespace SmartClassroom.Core.Tests;

/// <summary>
/// 功能开关：默认全关，未开启时不得对群消息产生任何副作用。
/// 这是"装完不会偷偷动课表/下文件"的安全底线。
/// </summary>
public sealed class FeatureGateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sc-gate-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }

    private sealed class Harness
    {
        public readonly List<(string Channel, string Title)> Sent = [];
        public readonly List<string> ExchangeBodies = [];
        public readonly List<string> UploadCalls = [];
        public readonly HomeworkStore Homework = new();
        public readonly PendingStore Pending = new();
        public readonly ActivityFeed Feed = new();
        public readonly CoursewareService Courseware = new();

        public PipelineService Build(FeatureFlags flags, string archiveRoot)
        {
            var gate = new ScheduleGate(new Free());
            var ai = new AiGateway(new AiOptions { BaseUrl = "http://ai", Model = "m" },
                new HttpClient(new Stub(_ => new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(Chat("""{"is_homework":true,"subject":"数学","date":"2026-09-25","items":["P10"],"due":"","confidence":0.9}""")) })));
            var plugin = new PluginLink("http://p", "t", new HttpClient(new Stub(req =>
            {
                var body = req.Content!.ReadAsStringAsync().Result;
                if (req.RequestUri!.AbsolutePath.EndsWith("/exchange"))
                    ExchangeBodies.Add(body);
                return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("""{"requestId":"r","legal":true,"message":"ok"}""") };
            })));
            var oneBot = new OneBotClient("http://q", "ws://q", null, new HttpClient(new Stub(req =>
            {
                UploadCalls.Add(req.RequestUri!.AbsolutePath);
                return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("{\"status\":\"ok\",\"retcode\":0,\"data\":{\"url\":\"http://x/f\"}}") };
            })));
            return new PipelineService(
                new TeacherMap([new Teacher { Qq = 10001, Name = "张老师", Subject = "数学" }]),
                new AiAnalyzer(ai), gate, plugin, oneBot,
                new FileArchive(new ArchiveOptions { Root = archiveRoot },
                    new HttpClient(new Stub(_ => new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new ByteArrayContent([1]) }))),
                Courseware, Homework, Feed, Pending, flags);
        }
    }

    private sealed class Free : IClassStatusProvider
    {
        public Task<bool> IsInClassAsync(CancellationToken cancel = default) => Task.FromResult(false);
        public Task<CurrentLesson?> GetCurrentLessonAsync(CancellationToken cancel = default)
            => Task.FromResult<CurrentLesson?>(null);
    }

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> fn) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken t)
            => Task.FromResult(fn(req));
    }

    private static string Chat(string content)
        => "{\"choices\":[{\"message\":{\"content\":" + JsonSerializer.Serialize(content) + "}}]}";

    private static GroupMessageEvent Msg(string text) => new()
    { GroupId = 1, UserId = 10001, MessageId = 1, RawMessage = text, Text = text, Card = "张老师" };

    [Fact]
    public void DefaultFlags_AllOff()
    {
        var f = FeatureFlags.AllDisabled;
        Assert.False(f.AnyEnabled);
        Assert.False(f.Summon);
        Assert.False(f.Homework);
        Assert.False(f.Exchange);
        Assert.False(f.FileArchive);
        Assert.False(f.CoursewarePopup);
        Assert.Contains("全部关闭", f.Describe());
    }

    [Fact]
    public async Task HomeworkDisabled_NoWallEntry_AndNoticeLoggedOnce()
    {
        var h = new Harness();
        var pipe = h.Build(FeatureFlags.AllDisabled, Path.Combine(_dir, "a"));

        await pipe.OnGroupMessageAsync(Msg("今天数学作业：练习册P10"));
        await pipe.OnGroupMessageAsync(Msg("今天数学作业：试卷一张"));

        Assert.Empty(h.Homework.All);                  // 关键：没有副作用
        Assert.Empty(h.Pending.All);                   // 也不进待确认（是没开启，不是失败）
        var notices = h.Feed.Entries.Where(e => e.Title.Contains("未启用")).ToList();
        Assert.Single(notices);                        // 同类只提示一次，不刷屏
        Assert.Contains("作业自动录入", notices[0].Title);
    }

    [Fact]
    public async Task HomeworkEnabled_GoesOnWall()
    {
        var h = new Harness();
        var pipe = h.Build(FeatureFlags.AllDisabled with { Homework = true }, Path.Combine(_dir, "a"));

        await pipe.OnGroupMessageAsync(Msg("今天数学作业：练习册P10"));

        Assert.Single(h.Homework.All);
        Assert.DoesNotContain(h.Feed.Entries, e => e.Title.Contains("未启用"));
    }

    [Fact]
    public async Task ExchangeDisabled_NeverCallsPlugin()
    {
        var h = new Harness();
        var pipe = h.Build(FeatureFlags.AllDisabled, Path.Combine(_dir, "a"));

        await pipe.OnGroupMessageAsync(Msg("明天第三节和今天第五节换一下"));

        Assert.Empty(h.ExchangeBodies);                // 绝不自动落课
    }

    [Fact]
    public async Task FileArchiveDisabled_DoesNotResolveUrlOrDownload()
    {
        var h = new Harness();
        var pipe = h.Build(FeatureFlags.AllDisabled, Path.Combine(_dir, "a"));

        await pipe.OnGroupUploadAsync(new GroupUploadEvent
        {
            GroupId = 1, UserId = 10001,
            File = new UploadedFile { Id = "f1", Name = "课件.pptx", Size = 10 }
        });

        Assert.Empty(h.UploadCalls);                   // 连取直链都没做
        Assert.False(Directory.Exists(Path.Combine(_dir, "a")));
    }

    [Fact]
    public void CoursewarePopupDisabled_DoesNotRaise()
    {
        var h = new Harness();
        var pipe = h.Build(FeatureFlags.AllDisabled, Path.Combine(_dir, "a"));
        var raised = false;
        pipe.CoursewareSuggested += _ => raised = true;

        pipe.OnClassStarted(DateOnly.FromDateTime(DateTime.Now), "数学", "张老师");

        Assert.False(raised);
        Assert.Contains(h.Feed.Entries, e => e.Title.Contains("课件弹窗"));
    }

    [Theory]
    [InlineData(true, true, true, true, true, "召唤")]
    [InlineData(false, false, true, false, false, "换课")]
    public void Describe_ListsEnabledFeatures(
        bool summon, bool homework, bool exchange, bool files, bool popup, string expected)
    {
        var f = new FeatureFlags
        {
            Summon = summon, Homework = homework, Exchange = exchange,
            FileArchive = files, CoursewarePopup = popup
        };
        Assert.Contains(expected, f.Describe());
    }

    // ================= 上课课件弹窗：没有当天该科的课件就不弹 =================

    /// <summary>造一个当天真实存在的课件文件（Query 会检查本地文件是否还在）。</summary>
    private CoursewareFile Register(string name, string subject, string teacher, DateOnly date)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, [1]);
        var file = new CoursewareFile
        {
            FileId = name, FileName = name, Size = 3,
            Sender = new SenderInfo { UserId = 10001, TeacherName = teacher },
            Source = new MessageRef { GroupId = 1, MessageId = 1 },
            LocalPath = path, Subject = subject, ClassDate = date
        };
        return file;
    }

    private (Harness H, PipelineService Pipe, List<int> Raised) PopupHarness()
    {
        var h = new Harness();
        var pipe = h.Build(FeatureFlags.AllDisabled with { CoursewarePopup = true }, Path.Combine(_dir, "a"));
        var raised = new List<int>();
        pipe.CoursewareSuggested += files => raised.Add(files.Count);
        return (h, pipe, raised);
    }

    [Fact]
    public void CoursewarePopup_NoFileForLesson_DoesNotRaise()
    {
        var (h, pipe, raised) = PopupHarness();
        var today = DateOnly.FromDateTime(DateTime.Now);
        h.Courseware.Register(Register("语文.pptx", "语文", "李老师", today));

        pipe.OnClassStarted(today, "数学", "张老师");   // 当天只有语文课件

        Assert.Empty(raised);
    }

    [Fact]
    public void CoursewarePopup_SameDaySameSubject_RaisesOncePerLesson()
    {
        var (h, pipe, raised) = PopupHarness();
        var today = DateOnly.FromDateTime(DateTime.Now);
        h.Courseware.Register(Register("数学.pptx", "数学", "张老师", today));

        pipe.OnClassStarted(today, "数学", "张老师");
        pipe.OnClassStarted(today, "数学", "张老师");   // 同一节课重复触发不重复弹

        Assert.Equal([1], raised);
    }

    [Fact]
    public void CoursewarePopup_NoSubjectAtAll_DoesNotRaise()
    {
        var (h, pipe, raised) = PopupHarness();
        var today = DateOnly.FromDateTime(DateTime.Now);
        h.Courseware.Register(Register("数学.pptx", "数学", "张老师", today));

        pipe.OnClassStarted(today, null, null);   // 课表没给科目也认不出老师

        Assert.Empty(raised);
        Assert.Contains(h.Feed.Entries, e => e.Title.Contains("没有科目信息"));
    }

    [Fact]
    public void CoursewarePopup_SubjectMissingButTeacherMapped_UsesMappedSubject()
    {
        var (h, pipe, raised) = PopupHarness();
        var today = DateOnly.FromDateTime(DateTime.Now);
        h.Courseware.Register(Register("数学.pptx", "数学", "张老师", today));

        // 课表没给科目，但"张老师"在教师映射里是数学 → 按数学匹配
        pipe.OnClassStarted(today, null, "张老师");

        Assert.Equal([1], raised);
    }
}
