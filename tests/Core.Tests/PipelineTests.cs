using System.Net;
using System.Text.Json;
using SmartClassroom.Contracts;
using SmartClassroom.Core.AI;
using SmartClassroom.Core.QQ;
using Xunit;

namespace SmartClassroom.Core.Tests;

public sealed class PipelineTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> fn) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken t)
            => Task.FromResult(fn(req));
    }

    private sealed class FakeStatus(bool inClass) : IClassStatusProvider
    {
        public Task<bool> IsInClassAsync(CancellationToken cancel = default) => Task.FromResult(inClass);
        public Task<CurrentLesson?> GetCurrentLessonAsync(CancellationToken cancel = default) => Task.FromResult<CurrentLesson?>(null);
    }

    private static string ChatReply(string content)
        => "{\"choices\":[{\"message\":{\"content\":" + JsonSerializer.Serialize(content) + "}}]}";

    private static HttpResponseMessage Json(object o, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(JsonSerializer.Serialize(o)) };

    private static GroupMessageEvent Msg(string text) => new()
    {
        GroupId = 1, UserId = 10001, MessageId = 7,
        RawMessage = text, Text = text, Card = "张老师", Nickname = "张数学"
    };

    private readonly List<(string Channel, string Title)> _sent = [];
    private readonly TeacherMap _teachers = new([new Teacher { Qq = 10001, Name = "张老师", Subject = "数学" }]);

    private PipelineService Build(
        string aiReply,
        bool inClass,
        string exchangeVerdict = """{"requestId":"r","legal":true,"message":"ok"}""",
        HttpStatusCode aiCode = HttpStatusCode.OK)
    {
        var gate = new ScheduleGate(new FakeStatus(inClass));
        var ai = new AiGateway(new AiOptions { BaseUrl = "http://x", Model = "m" },
            new HttpClient(new StubHandler(_ => new HttpResponseMessage(aiCode)
            { Content = new StringContent(ChatReply(aiReply)) })));
        var plugin = new PluginLink("http://p", "tok", new HttpClient(new StubHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/notify"))
            {
                var body = req.Content!.ReadAsStringAsync().Result;
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                string Prop(params string[] names)
                    => names.Select(n => root.TryGetProperty(n, out var v) ? v.GetString() : null)
                        .FirstOrDefault(v => v is not null) ?? "";
                _sent.Add((Prop("channel", "Channel"), Prop("title", "Title")));
                return Json(new { });
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(exchangeVerdict) };
        })));
        var oneBot = new OneBotClient("http://q", "ws://q", null, new HttpClient(new StubHandler(_ =>
            Json(new { status = "ok", retcode = 0, data = new { url = "http://x/f" } }))));
        var archiveRoot = Path.Combine(Path.GetTempPath(), "sc-pipe-" + Guid.NewGuid().ToString("N"));
        var archive = new FileArchive(new ArchiveOptions { Root = archiveRoot },
            new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([9]) })));
        return new PipelineService(_teachers, new AiAnalyzer(ai), gate, plugin, oneBot, archive,
            new CoursewareService(), new HomeworkStore(), new ActivityFeed(), new PendingStore(), TestFlags.AllOn);
    }

    // ================= 老师私聊 =================
    //
    // 老师也可能私聊发"来一下"或作业，所以私聊走同一套判定；
    // 但只认老师映射里的 QQ —— 不然谁私聊都触发。

    [Fact]
    public async Task PrivateMessage_FromMappedTeacher_IsHandled()
    {
        var p = Build("""{"is_summon":true,"target":"小明","urgent":true,"confidence":0.9}""", inClass: true);

        await p.OnPrivateMessageAsync(new PrivateMessageEvent
        {
            UserId = 10001, MessageId = 5, RawMessage = "小明现在来一下", Text = "小明现在来一下",
            Nickname = "张数学"
        });

        Assert.Single(_sent);
        Assert.Equal("summon", _sent[0].Channel);
    }

    [Fact]
    public async Task PrivateMessage_FromStranger_IsIgnored()
    {
        var p = Build("""{"is_summon":true,"target":"小明","urgent":true,"confidence":0.9}""", inClass: true);

        await p.OnPrivateMessageAsync(new PrivateMessageEvent
        {
            UserId = 99999, MessageId = 6, RawMessage = "小明现在来一下", Text = "小明现在来一下"
        });

        Assert.Empty(_sent);   // 陌生人私聊不处理
    }

    [Fact]
    public async Task SummonUrgent_InClass_SendsNow()
    {
        var p = Build("""{"is_summon":true,"target":"小明","urgent":true,"confidence":0.9}""", inClass: true);
        await p.OnGroupMessageAsync(Msg("小明现在来一下"));
        Assert.Single(_sent);
        Assert.Equal("summon", _sent[0].Channel);
    }

    [Fact]
    public async Task SummonNormal_InClass_QueuesThenFlushes()
    {
        var gate = new ScheduleGate(new FakeStatus(true));
        var p = Build("""{"is_summon":true,"target":"小明","urgent":false,"confidence":0.9}""", inClass: true);
        await p.OnGroupMessageAsync(Msg("小明来一下"));
        Assert.Empty(_sent);
        await p.OnClassEndedAsync();
        Assert.Single(_sent);
        Assert.Equal("summon", _sent[0].Channel);
    }

    [Fact]
    public async Task Summon_AiFails_FallsBack()
    {
        var p = Build("boom", inClass: true, aiCode: HttpStatusCode.InternalServerError);
        await p.OnGroupMessageAsync(Msg("小明来一下"));
        // 非urgent → 保守排队，不立即发送
        Assert.Empty(_sent);
        await p.OnClassEndedAsync();
        Assert.Single(_sent);
    }

    [Fact]
    public async Task Homework_GoesOnWall()
    {
        HomeworkStore? store = null;
        // 用可观察的 store 重建管线
        var gate = new ScheduleGate(new FakeStatus(false));
        var ai = new AiGateway(new AiOptions { BaseUrl = "http://x", Model = "m" },
            new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(ChatReply("""{"is_homework":true,"subject":"数学","date":"2026-09-25","items":["练习册P10"],"due":"","confidence":0.9}""")) })));
        store = new HomeworkStore();
        var plugin = new PluginLink("http://p", "t", new HttpClient(new StubHandler(_ => Json(new { }))));
        var oneBot = new OneBotClient("http://q", "ws://q");
        var pipe = new PipelineService(_teachers, new AiAnalyzer(ai), gate, plugin, oneBot,
            new FileArchive(new ArchiveOptions { Root = Path.GetTempPath() }),
            new CoursewareService(), store, new ActivityFeed(), new PendingStore(), TestFlags.AllOn);
        await pipe.OnGroupMessageAsync(Msg("今天数学作业：练习册P10"));
        Assert.Single(store.ForDate(new DateOnly(2026, 9, 25)));
    }

    [Fact]
    public async Task ExchangeIllegal_QueuesManual()
    {
        var gate = new ScheduleGate(new FakeStatus(true));
        var ai = new AiGateway(new AiOptions { BaseUrl = "http://x", Model = "m" },
            new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(ChatReply("""{"is_exchange":true,"kind":"Swap","from":{"date":"2026-09-25","period":1},"to":{"date":"2026-09-25","period":2},"new_subject":"","confidence":0.9}""")) })));
        var plugin = new PluginLink("http://p", "t", new HttpClient(new StubHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/notify"))
            {
                var body = req.Content!.ReadAsStringAsync().Result;
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                string Prop(params string[] names)
                    => names.Select(n => root.TryGetProperty(n, out var v) ? v.GetString() : null)
                        .FirstOrDefault(v => v is not null) ?? "";
                _sent.Add((Prop("channel", "Channel"), Prop("title", "Title")));
                return Json(new { });
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("""{"requestId":"r","legal":false,"message":"第2节不存在"}""") };
        })));
        var oneBot = new OneBotClient("http://q", "ws://q");
        var pipe = new PipelineService(_teachers, new AiAnalyzer(ai), gate, plugin, oneBot,
            new FileArchive(new ArchiveOptions { Root = Path.GetTempPath() }),
            new CoursewareService(), new HomeworkStore(), new ActivityFeed(), new PendingStore(), TestFlags.AllOn);
        await pipe.OnGroupMessageAsync(Msg("第一节和第二节换一下"));
        Assert.Equal(1, gate.PendingCount);
        await pipe.OnClassEndedAsync();
        Assert.Equal("manual", _sent[0].Channel);
    }

    [Fact]
    public async Task HomeworkFarOffDate_IsCoercedToToday_WithWarning()
    {
        // 实测事故：模型把 date 填成了它训练数据里的日期（2026-05-07），
        // 作业就会挂到那一天。日期不可信时必须按今天处理并在时间线里说明。
        var homework = new HomeworkStore();
        var feed = new ActivityFeed();
        var ai = new AiGateway(new AiOptions { BaseUrl = "http://x", Model = "m" },
            new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ChatReply(
                    """{"is_homework":true,"subject":"数学","date":"2026-05-07","items":["练习册P10"],"due":"","confidence":0.9}"""))
            })));
        var pipe = new PipelineService(_teachers, new AiAnalyzer(ai),
            new ScheduleGate(new FakeStatus(false)),
            new PluginLink("http://p", "t", new HttpClient(new StubHandler(_ => Json(new { })))),
            new OneBotClient("http://q", "ws://q", null, new HttpClient(new StubHandler(_ => Json(new { })))),
            new FileArchive(new ArchiveOptions { Root = Path.Combine(Path.GetTempPath(), "sc-pipe-" + Guid.NewGuid().ToString("N")) }),
            new CoursewareService(), homework, feed, new PendingStore(), TestFlags.AllOn);

        await pipe.OnGroupMessageAsync(Msg("今天数学作业：练习册P10"));

        var item = Assert.Single(homework.All);
        Assert.Equal(DateOnly.FromDateTime(DateTime.Now), item.Date);
        Assert.Contains(feed.Entries, e => e.Title.Contains("日期已纠偏"));
    }

    [Fact]
    public async Task Upload_TeacherFile_Archived()
    {
        var archiveRoot = Path.Combine(Path.GetTempPath(), "sc-pipe-up-" + Guid.NewGuid().ToString("N"));
        try
        {
            var gate = new ScheduleGate(new FakeStatus(false));
            var ai = new AiGateway(new AiOptions { BaseUrl = "http://x", Model = "m" },
                new HttpClient(new StubHandler(_ => Json(new { }))));
            var plugin = new PluginLink("http://p", "t", new HttpClient(new StubHandler(_ => Json(new { }))));
            var oneBot = new OneBotClient("http://q", "ws://q", null, new HttpClient(new StubHandler(_ =>
                Json(new { status = "ok", retcode = 0, data = new { url = "http://x/f" } }))));
            var courseware = new CoursewareService();
            var pipe = new PipelineService(_teachers, new AiAnalyzer(ai), gate, plugin, oneBot,
                new FileArchive(new ArchiveOptions { Root = archiveRoot },
                    new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([9]) }))),
                courseware, new HomeworkStore(), new ActivityFeed(), new PendingStore(), TestFlags.AllOn);
            await pipe.OnGroupUploadAsync(new GroupUploadEvent
            {
                GroupId = 1, UserId = 10001,
                File = new UploadedFile { Id = "f1", Name = "课件.pptx", Size = 10 }
            });
            var today = DateOnly.FromDateTime(DateTime.Now);
            var archived = Assert.Single(courseware.QueryDay(today));
            Assert.Equal("张老师", archived.Sender.TeacherName);
            Assert.Equal("数学", archived.Subject);   // 科目要进课件索引，上课弹窗靠它判"当科"
        }
        finally { if (Directory.Exists(archiveRoot)) Directory.Delete(archiveRoot, true); }
    }
}
