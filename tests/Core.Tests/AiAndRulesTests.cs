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
        var d = await new AiAnalyzer(ai).AnalyzeExchangeAsync("明早第三节数学和今天第五节换");
        Assert.Equal("CrossDay", d.Kind);
        Assert.Equal(3, d.From.Period);
        Assert.NotNull(d.To);
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
}
