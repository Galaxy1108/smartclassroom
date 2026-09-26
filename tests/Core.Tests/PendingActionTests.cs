using System.Net;
using System.Text.Json;
using SmartClassroom.Contracts;
using SmartClassroom.Core.AI;
using SmartClassroom.Core.QQ;
using Xunit;

namespace SmartClassroom.Core.Tests;

/// <summary>
/// 待确认事项的出口：AI 失败不丢消息，用户可重试 / 人工补录 / 忽略。
/// 这是事件页存在的意义所在，所以逐条锁住。
/// </summary>
public sealed class PendingActionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sc-pending-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }

    private sealed class Harness
    {
        public readonly List<(string Channel, string Title)> Sent = [];
        public readonly List<string> ExchangeBodies = [];
        public readonly HomeworkStore Homework = new();
        public readonly PendingStore Pending = new();
        public readonly ActivityFeed Feed = new();
        public readonly Queue<string> AiReplies = new();

        /// <summary>非 null 时 AI 一律返回 500（模拟失败）。</summary>
        public bool AiFails;

        public string ExchangeReply = """{"requestId":"r","legal":true,"message":"已对调"}""";

        public PipelineService Build(string archiveRoot)
        {
            var gate = new ScheduleGate(new AlwaysFree());
            var ai = new AiGateway(new AiOptions { BaseUrl = "http://ai", Model = "m" },
                new HttpClient(new StubHandler(_ =>
                {
                    if (AiFails)
                        return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") };
                    var content = AiReplies.Count > 0 ? AiReplies.Dequeue() : "{}";
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent(ChatReply(content)) };
                })));
            var plugin = new PluginLink("http://p", "t", new HttpClient(new StubHandler(req =>
            {
                var body = req.Content!.ReadAsStringAsync().Result;
                if (req.RequestUri!.AbsolutePath.EndsWith("/exchange"))
                {
                    ExchangeBodies.Add(body);
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ExchangeReply) };
                }
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                string Prop(params string[] names) => names
                    .Select(n => root.TryGetProperty(n, out var v) ? v.GetString() : null)
                    .FirstOrDefault(v => v is not null) ?? "";
                Sent.Add((Prop("channel", "Channel"), Prop("title", "Title")));
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            })));
            var oneBot = new OneBotClient("http://q", "ws://q");
            return new PipelineService(
                new TeacherMap([new Teacher { Qq = 10001, Name = "张老师", Subject = "数学" }]),
                new AiAnalyzer(ai), gate, plugin, oneBot,
                new FileArchive(new ArchiveOptions { Root = archiveRoot }),
                new CoursewareService(), Homework, Feed, Pending, TestFlags.AllOn);
        }
    }

    private sealed class AlwaysFree : IClassStatusProvider
    {
        public Task<bool> IsInClassAsync(CancellationToken cancel = default) => Task.FromResult(false);
        public Task<CurrentLesson?> GetCurrentLessonAsync(CancellationToken cancel = default)
            => Task.FromResult<CurrentLesson?>(null);
    }

    private static string ChatReply(string content)
        => "{\"choices\":[{\"message\":{\"content\":" + JsonSerializer.Serialize(content) + "}}]}";

    private static GroupMessageEvent Msg(string text) => new()
    {
        GroupId = 1, UserId = 10001, MessageId = 7,
        RawMessage = text, Text = text, Card = "张老师", Nickname = "张数学"
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> fn) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken t)
            => Task.FromResult(fn(req));
    }

    // ---------- AI 失败 → 进待确认 ----------

    [Fact]
    public async Task HomeworkAiFailure_CreatesPendingItemWithRawText()
    {
        var h = new Harness { AiFails = true };
        var pipe = h.Build(_dir);

        await pipe.OnGroupMessageAsync(Msg("今天数学作业：练习册P10"));

        var item = Assert.Single(h.Pending.All);
        Assert.Equal("homework", item.Kind);
        Assert.Equal("今天数学作业：练习册P10", item.RawText);   // 原文留住，这是关键
        Assert.Equal(10001, item.Sender.UserId);
        Assert.False(string.IsNullOrWhiteSpace(item.Reason));
    }

    [Fact]
    public async Task ExchangeAiFailure_CreatesPendingItem()
    {
        var h = new Harness { AiFails = true };
        var pipe = h.Build(_dir);

        await pipe.OnGroupMessageAsync(Msg("明天第三节和今天第五节换一下"));

        var item = Assert.Single(h.Pending.All);
        Assert.Equal("exchange", item.Kind);
    }

    [Fact]
    public async Task SummonAiFailure_KeepsPendingAndStillFallsBackToQueue()
    {
        var h = new Harness { AiFails = true };
        var pipe = h.Build(_dir);

        await pipe.OnGroupMessageAsync(Msg("小明来一下"));

        // 既留痕（可人工纠正），又不阻塞（保守降级仍排了队）
        Assert.Single(h.Pending.All);
        Assert.Contains(h.Feed.Entries, e => e.Kind == "summon");
    }

    // ---------- 出口 1：重试 ----------

    [Fact]
    public async Task RetryPending_AiRecovers_PendingClearedAndHomeworkAdded()
    {
        var h = new Harness { AiFails = true };
        var pipe = h.Build(_dir);
        await pipe.OnGroupMessageAsync(Msg("今天数学作业：练习册P10"));
        var id = Assert.Single(h.Pending.All).Id;

        // AI 恢复
        h.AiFails = false;
        h.AiReplies.Enqueue("""{"is_homework":true,"subject":"数学","date":"2026-09-25","items":["练习册P10"],"due":"","confidence":0.9}""");

        var message = await pipe.RetryPendingAsync(id);

        Assert.Contains("成功", message);
        Assert.Empty(h.Pending.All);
        Assert.Single(h.Homework.ForDate(new DateOnly(2026, 9, 25)));
    }

    [Fact]
    public async Task RetryPending_StillFailing_KeepsItemAndCountsRetry()
    {
        var h = new Harness { AiFails = true };
        var pipe = h.Build(_dir);
        await pipe.OnGroupMessageAsync(Msg("今天数学作业：练习册P10"));
        var id = Assert.Single(h.Pending.All).Id;

        var message = await pipe.RetryPendingAsync(id);

        Assert.Contains("仍未成功", message);
        var item = Assert.Single(h.Pending.All);
        Assert.Equal(1, item.RetryCount);
    }

    // ---------- 出口 2：人工补录 ----------

    [Fact]
    public async Task ResolveHomework_Manually_GoesOnWallAndClearsPending()
    {
        var h = new Harness { AiFails = true };
        var pipe = h.Build(_dir);
        await pipe.OnGroupMessageAsync(Msg("今天数学作业：练习册P10"));
        var id = Assert.Single(h.Pending.All).Id;

        var message = pipe.ResolveHomeworkAsync(id, "数学", ["练习册P10", "试卷一张"], new DateOnly(2026, 9, 25));

        Assert.Contains("上墙", message);
        Assert.Empty(h.Pending.All);
        var saved = Assert.Single(h.Homework.ForDate(new DateOnly(2026, 9, 25)));
        Assert.Equal(2, saved.Items.Count);
    }

    [Fact]
    public async Task ResolveHomework_RejectsEmptyInput()
    {
        var h = new Harness { AiFails = true };
        var pipe = h.Build(_dir);
        await pipe.OnGroupMessageAsync(Msg("今天数学作业：练习册P10"));
        var id = Assert.Single(h.Pending.All).Id;

        var message = pipe.ResolveHomeworkAsync(id, "  ", [], new DateOnly(2026, 9, 25));

        Assert.Contains("请至少", message);
        Assert.Single(h.Pending.All); // 输入非法时不消费掉这条
    }

    [Fact]
    public async Task ResolveSummon_Manually_TargetIsHonored()
    {
        var h = new Harness { AiFails = true };
        var pipe = h.Build(_dir);
        await pipe.OnGroupMessageAsync(Msg("小明来一下"));
        var id = Assert.Single(h.Pending.All).Id;

        var message = await pipe.ResolveSummonAsync(id, "小明", urgent: true);

        Assert.Contains("人工录入", message);
        Assert.Empty(h.Pending.All);
        Assert.Contains(h.Sent, s => s.Channel == NotifyChannels.Summon && s.Title.Contains("小明"));
    }

    [Fact]
    public async Task ResolveExchange_Manually_GoesThroughPluginValidation()
    {
        var h = new Harness { AiFails = true };
        var pipe = h.Build(_dir);
        await pipe.OnGroupMessageAsync(Msg("明天第三节和今天第五节换一下"));
        var id = Assert.Single(h.Pending.All).Id;

        var message = await pipe.ResolveExchangeAsync(id, ExchangeKind.Swap,
            new ClassSlot { Date = new DateOnly(2026, 9, 25), PeriodIndex = 1 },
            new ClassSlot { Date = new DateOnly(2026, 9, 25), PeriodIndex = 2 }, null);

        Assert.Equal("已对调", message);
        Assert.Empty(h.Pending.All);
        var body = Assert.Single(h.ExchangeBodies);
        // 契约约定 camelCase + 字符串枚举（数字枚举在跨进程契约里太脆）
        Assert.Contains("\"kind\":\"swap\"", body);
        Assert.Contains("10001", body); // 带上原发送者，便于插件记录
    }

    [Fact]
    public async Task ResolveExchange_IllegalVerdict_QueuesManualNotification()
    {
        var h = new Harness { AiFails = true };
        var pipe = h.Build(_dir);
        await pipe.OnGroupMessageAsync(Msg("明天第三节和今天第五节换一下"));
        var id = Assert.Single(h.Pending.All).Id;
        h.ExchangeReply = """{"requestId":"r","legal":false,"message":"第5节不存在"}""";

        await pipe.ResolveExchangeAsync(id, ExchangeKind.Swap,
            new ClassSlot { Date = new DateOnly(2026, 9, 25), PeriodIndex = 1 },
            new ClassSlot { Date = new DateOnly(2026, 9, 25), PeriodIndex = 9 }, null);

        Assert.Empty(h.Pending.All);
        Assert.True(pipe.Pending.Count == 0);
    }

    // ---------- 出口 3：忽略 ----------

    [Fact]
    public async Task IgnorePending_RemovesWithoutProcessing()
    {
        var h = new Harness { AiFails = true };
        var pipe = h.Build(_dir);
        await pipe.OnGroupMessageAsync(Msg("今天数学作业：练习册P10"));
        var id = Assert.Single(h.Pending.All).Id;

        Assert.True(pipe.IgnorePending(id));

        Assert.Empty(h.Pending.All);
        Assert.Empty(h.Homework.All);                       // 没有被错误地当成作业
        Assert.Contains(h.Feed.Entries, e => e.Title.StartsWith("已忽略"));
    }

    [Fact]
    public void IgnorePending_UnknownId_ReturnsFalse()
    {
        var h = new Harness();
        var pipe = h.Build(_dir);
        Assert.False(pipe.IgnorePending("不存在"));
    }

    // ---------- 正常路径不受影响 ----------

    [Fact]
    public async Task HappyPath_NoPendingCreated()
    {
        var h = new Harness();
        h.AiReplies.Enqueue("""{"is_homework":true,"subject":"数学","date":"2026-09-25","items":["练习册P10"],"due":"","confidence":0.9}""");
        var pipe = h.Build(_dir);

        await pipe.OnGroupMessageAsync(Msg("今天数学作业：练习册P10"));

        Assert.Empty(h.Pending.All);
        Assert.Single(h.Homework.All);
    }
}
