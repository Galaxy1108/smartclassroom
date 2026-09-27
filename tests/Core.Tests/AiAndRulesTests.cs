using SmartClassroom.Contracts;
using SmartClassroom.Core.AI;
using Xunit;

namespace SmartClassroom.Core.Tests;

public sealed class RuleEngineTests
{
    [Theory]
    [InlineData("明天下午第三节和今天第五节换一下", RuleEngine.Kind.Exchange)]
    [InlineData("今天数学作业：练习册第10页", RuleEngine.Kind.Homework)]
    [InlineData("小明来一下", RuleEngine.Kind.Summon)]
    [InlineData("小明现在来一下", RuleEngine.Kind.Summon)]
    [InlineData("大家注意明天穿校服", RuleEngine.Kind.None)]
    public void ClassifyLocal_RoutesCorrectly(string text, RuleEngine.Kind expected)
    {
        Assert.Equal(expected, RuleEngine.ClassifyLocal(text));
    }
}

public sealed class AiGatewayTests
{
    private sealed class StubHandler(string payload, System.Net.HttpStatusCode code = System.Net.HttpStatusCode.OK)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken t)
            => Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(payload) });
    }

    private static string ChatReply(string content)
        => "{\"choices\":[{\"message\":{\"content\":" + System.Text.Json.JsonSerializer.Serialize(content) + "}}]}";

    [Fact]
    public async Task AskAsync_ReturnsContent()
    {
        var ai = new AiGateway(new AiOptions { BaseUrl = "http://x", Model = "m" },
            new HttpClient(new StubHandler(ChatReply("hello"))));
        Assert.Equal("hello", await ai.AskAsync("s", "u"));
    }

    [Fact]
    public async Task AskAsync_ThrowsOnFailure()
    {
        var ai = new AiGateway(new AiOptions { BaseUrl = "http://x", Model = "m" },
            new HttpClient(new StubHandler("boom", System.Net.HttpStatusCode.InternalServerError)));
        await Assert.ThrowsAsync<AiException>(() => ai.AskAsync("s", "u"));
    }

    [Fact]
    public void ExtractJson_StripsCodeFence()
    {
        Assert.Equal("""{"a":1}""", AiGateway.ExtractJson("```json\n{\"a\":1}\n```"));
        Assert.Equal("""{"a":1}""", AiGateway.ExtractJson("""前面废话 {"a":1} 后面废话"""));
    }

    [Fact]
    public async Task AnalyzeSummon_ParsesDraft()
    {
        var reply = """{"is_summon":true,"target":"小明","urgent":true,"confidence":0.9}""";
        var ai = new AiGateway(new AiOptions { BaseUrl = "http://x", Model = "m" },
            new HttpClient(new StubHandler(ChatReply(reply))));
        var d = await new AiAnalyzer(ai).AnalyzeSummonAsync("小明现在来一下");
        Assert.True(d.IsSummon);
        Assert.Equal("小明", d.Target);
        Assert.True(d.Urgent);
    }

    [Fact]
    public async Task AnalyzeExchange_ParsesCrossDay()
    {
        var reply = """{"is_exchange":true,"kind":"CrossDay","from":{"date":"2026-09-26","period":3,"subject":"数学"},"to":{"date":"2026-09-25","period":5},"new_subject":"","confidence":0.85}""";
        var ai = new AiGateway(new AiOptions { BaseUrl = "http://x", Model = "m" },
            new HttpClient(new StubHandler(ChatReply(reply))));
        var list = await new AiAnalyzer(ai).AnalyzeExchangeAsync("明早第三节数学和今天第五节换");
        var d = Assert.Single(list);            // 只改一节时模型返回单个对象
        Assert.Equal("CrossDay", d.Kind);
        Assert.Equal(3, d.From.Period);
        Assert.NotNull(d.To);
    }

    /// <summary>
    /// 一次改多节课（"明天的自习课全部改成语文"）：模型返回**数组**，每个元素一节课。
    /// 这样插件与校验器完全不用改，各走一次单节次流程，而且都落在同一个临时层里。
    /// </summary>
    [Fact]
    public async Task AnalyzeExchange_ParsesArrayOfPeriods()
    {
        var reply = """
            [{"is_exchange":true,"kind":"Replace","from":{"date":"2026-09-28","period":2,"subject":"自习"},"new_subject":"语文","confidence":0.9},
             {"is_exchange":true,"kind":"Replace","from":{"date":"2026-09-28","period":5,"subject":"自习"},"new_subject":"语文","confidence":0.9},
             {"is_exchange":true,"kind":"Replace","from":{"date":"2026-09-28","period":7,"subject":"自习"},"new_subject":"语文","confidence":0.9}]
            """;
        var ai = new AiGateway(new AiOptions { BaseUrl = "http://x", Model = "m" },
            new HttpClient(new StubHandler(ChatReply(reply))));

        var list = await new AiAnalyzer(ai).AnalyzeExchangeAsync("明天的自习课全部改为语文");

        Assert.Equal(3, list.Count);
        Assert.All(list, d => Assert.Equal("Replace", d.Kind));
        Assert.All(list, d => Assert.Equal("语文", d.NewSubject));
        Assert.Equal([2, 5, 7], list.Select(d => d.From!.Period).ToArray());
    }

    [Fact]
    public async Task AskAsync_EmptyContent_ThrowsWithDiagnosis_NotEmptyString()
    {
        var reply = """{"choices":[{"finish_reason":"length","message":{"content":""}}]}""";
        var ai = new AiGateway(new AiOptions { BaseUrl = "http://x", Model = "m" },
            new HttpClient(new StubHandler(reply)));

        // 关键：空内容必须报错，而不是当成成功的空字符串——
        // 否则界面上只会显示"可用，但返回为空"，完全查不出原因。
        var ex = await Assert.ThrowsAsync<AiException>(() => ai.AskAsync("s", "u"));
        Assert.Contains("空内容", ex.Message);
        Assert.Contains("finish_reason=length", ex.Message);
    }

    [Fact]
    public async Task AskAsync_ReasoningOnlyContent_ExplainsReasoningModel()
    {
        var reply = """{"choices":[{"finish_reason":"stop","message":{"content":"","reasoning_content":"想了很久但没输出"}}]}""";
        var ai = new AiGateway(new AiOptions { BaseUrl = "http://x", Model = "m" },
            new HttpClient(new StubHandler(reply)));

        var ex = await Assert.ThrowsAsync<AiException>(() => ai.AskAsync("s", "u"));
        Assert.Contains("reasoning_content", ex.Message);
        Assert.Contains("推理模型", ex.Message);
    }

    [Fact]
    public async Task AskAsync_Refusal_IsReported()
    {
        var reply = """{"choices":[{"finish_reason":"stop","message":{"content":"","refusal":"我不能这么做"}}]}""";
        var ai = new AiGateway(new AiOptions { BaseUrl = "http://x", Model = "m" },
            new HttpClient(new StubHandler(reply)));

        var ex = await Assert.ThrowsAsync<AiException>(() => ai.AskAsync("s", "u"));
        Assert.Contains("refusal", ex.Message);
    }

    [Fact]
    public async Task AskAsync_WhitespaceContent_AlsoCountsAsEmpty()
    {
        var reply = """{"choices":[{"message":{"content":"   \n  "}}]}""";
        var ai = new AiGateway(new AiOptions { BaseUrl = "http://x", Model = "m" },
            new HttpClient(new StubHandler(reply)));
        await Assert.ThrowsAsync<AiException>(() => ai.AskAsync("s", "u"));
    }

    [Fact]
    public async Task AskAsync_NormalContent_StillWorks()
    {
        var ai = new AiGateway(new AiOptions { BaseUrl = "http://x", Model = "m" },
            new HttpClient(new StubHandler(ChatReply("  可用  "))));
        Assert.Equal("可用", await ai.AskAsync("s", "u"));   // 顺带裁剪空白
    }

    // ================= opencode.ai 的路由头 =================
    //
    // 真实事故：把服务地址填成 https://opencode.ai/zen/go/v1 后，端点返回
    // 400 {"type":"MissingSessionID"}，而 pi-ai 只留下一句"结束原因: error"。
    // 两边都必须带 x-opencode-session。

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Last;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken t)
        {
            Last = req;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new StringContent(ChatReply("ok")) });
        }
    }

    [Theory]
    [InlineData("https://opencode.ai/zen/go/v1", true)]
    [InlineData("https://opencode.ai/zen/v1", true)]
    [InlineData("https://api.opencode.ai/v1", true)]
    [InlineData("https://api.deepseek.com/v1", false)]
    [InlineData("http://127.0.0.1:18899/v1", false)]
    [InlineData("", false)]
    [InlineData("opencode.ai", false)]          // 不是绝对 URL，不做猜测
    public void IsOpenCodeEndpoint_DetectsHost(string url, bool expected)
        => Assert.Equal(expected, OpenCodeCompat.IsOpenCodeEndpoint(url));

    [Fact]
    public async Task AskAsync_OpenCodeEndpoint_SendsSessionHeader()
    {
        var handler = new CapturingHandler();
        var ai = new AiGateway(new AiOptions
        {
            BaseUrl = "https://opencode.ai/zen/go/v1",
            ApiKey = "k",
            Model = "deepseek-v4.1-flash"
        }, new HttpClient(handler));

        await ai.AskAsync("s", "u");

        Assert.True(handler.Last!.Headers.TryGetValues(OpenCodeCompat.SessionHeader, out var values));
        Assert.StartsWith("smartclassroom-", Assert.Single(values!));
    }

    [Fact]
    public async Task AskAsync_OtherEndpoint_DoesNotSendSessionHeader()
    {
        var handler = new CapturingHandler();
        var ai = new AiGateway(new AiOptions { BaseUrl = "https://api.deepseek.com/v1", ApiKey = "k", Model = "m" },
            new HttpClient(handler));

        await ai.AskAsync("s", "u");

        Assert.False(handler.Last!.Headers.Contains(OpenCodeCompat.SessionHeader));
    }
}

public sealed class HomeworkDateCoercionTests
{
    private static readonly DateOnly Today = new(2026, 9, 26);

    [Theory]
    [InlineData("2026-09-26", "2026-09-26")]   // 今天
    [InlineData("2026-09-27", "2026-09-27")]   // 明天
    [InlineData("2026-10-05", "2026-10-05")]   // 一周多以后，仍算有效
    [InlineData("2026-09-25", "2026-09-25")]   // 昨天（老师补发）
    [InlineData("2026-05-07", "2026-09-26")]   // 实测遇到过的"训练数据日期" → 按今天
    [InlineData("2027-03-01", "2026-09-26")]   // 太远 → 按今天
    [InlineData("下周", "2026-09-26")]          // 解析不出来 → 按今天
    [InlineData("", "2026-09-26")]
    [InlineData(null, "2026-09-26")]
    public void CoerceHomeworkDate(string? raw, string expected)
        => Assert.Equal(DateOnly.Parse(expected), RuleEngine.CoerceHomeworkDate(raw, Today));
}
