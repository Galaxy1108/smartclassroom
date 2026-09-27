using System.Net;
using System.Text;
using System.Text.Json;
using SmartClassroom.Core.AI;
using Xunit;

namespace SmartClassroom.Core.Tests;

/// <summary>
/// 图片要真的发给模型（deepseek-v4.1-flash 是多模态）。
/// 老师发的作业/通知截图很常见，读不懂图等于漏消息。
/// </summary>
public sealed class AiImageTests
{
    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken t)
        {
            Body = req.Content is null ? null : await req.Content.ReadAsStringAsync(t);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"choices":[{"message":{"content":"ok"}}]}""", Encoding.UTF8, "application/json")
            };
        }
    }

    [Fact]
    public async Task Gateway_WithImages_SendsDataUriParts()
    {
        var handler = new CaptureHandler();
        var gateway = new AiGateway(new AiOptions { BaseUrl = "http://x", Model = "m" },
            new HttpClient(handler));

        await gateway.AskAsync("sys", "看图", default, null,
            [new AiImage(Convert.ToBase64String([1, 2, 3]), "image/png")]);

        // 直接解析 JSON 断言结构（序列化会把中文转义成 \uXXXX，不能直接 Contains 中文）
        using var doc = JsonDocument.Parse(handler.Body!);
        var content = doc.RootElement.GetProperty("messages")[1].GetProperty("content");
        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        Assert.Equal("看图", content[0].GetProperty("text").GetString());
        Assert.Equal("image_url", content[1].GetProperty("type").GetString());
        Assert.StartsWith("data:image/png;base64,",
            content[1].GetProperty("image_url").GetProperty("url").GetString());
    }

    [Fact]
    public async Task Gateway_WithoutImages_StaysPlainString()
    {
        var handler = new CaptureHandler();
        var gateway = new AiGateway(new AiOptions { BaseUrl = "http://x", Model = "m" },
            new HttpClient(handler));

        await gateway.AskAsync("sys", "纯文本", default);

        using var doc = JsonDocument.Parse(handler.Body!);
        var content = doc.RootElement.GetProperty("messages")[1].GetProperty("content");
        // 纯文本端点收到内容数组可能报错，所以没图时必须还是字符串
        Assert.Equal(JsonValueKind.String, content.ValueKind);
        Assert.Equal("纯文本", content.GetString());
    }
}
