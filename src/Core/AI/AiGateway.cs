using System.Text;
using System.Text.Json;

namespace SmartClassroom.Core.AI;

/// <summary>AI 网关配置（OpenAI-compatible：/chat/completions）。</summary>
public sealed record AiOptions
{
    public string BaseUrl { get; init; } = "";
    public string ApiKey { get; init; } = "";
    public string Model { get; init; } = "";
    public int TimeoutSeconds { get; init; } = 20;
}

/// <summary>
/// OpenAI-compatible HTTP 网关：只发 chat/completions，不绑定具体厂商。
/// 超时/失败抛 AiException，由上层按"保守降级"处理（召唤排队、作业/换课待确认）。
/// </summary>
public sealed class AiGateway(AiOptions options, HttpClient? http = null)
{
    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds) };

    public async Task<string> AskAsync(string system, string user, CancellationToken cancel = default)
    {
        var body = new
        {
            model = options.Model,
            temperature = 0.1,
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user }
            }
        };
        using var req = new HttpRequestMessage(HttpMethod.Post,
            options.BaseUrl.TrimEnd('/') + "/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        if (options.ApiKey.Length > 0)
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.ApiKey);

        HttpResponseMessage res;
        try { res = await _http.SendAsync(req, cancel).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TaskCanceledException or HttpRequestException)
        { throw new AiException("AI 请求失败（网络/超时）", ex); }

        var text = await res.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
            throw new AiException($"AI 返回 {(int)res.StatusCode}：{text[..Math.Min(text.Length, 300)]}");

        try
        {
            using var doc = JsonDocument.Parse(text);
            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content").GetString();
            return (content ?? "").Trim();
        }
        catch (Exception ex) { throw new AiException("AI 返回解析失败", ex); }
    }

    /// <summary>从模型输出中提取 JSON（容忍 markdown 代码围栏）。</summary>
    public static string ExtractJson(string output)
    {
        var t = output.Trim();
        if (t.StartsWith("```"))
        {
            var start = t.IndexOf('\n');
            var end = t.LastIndexOf("```");
            if (start >= 0 && end > start)
                return t.Substring(start + 1, end - start - 1).Trim();
        }
        var b = t.IndexOf('{');
        var e = t.LastIndexOf('}');
        return b >= 0 && e > b ? t.Substring(b, e - b + 1) : t;
    }
}

public sealed class AiException(string message, Exception? inner = null) : Exception(message, inner);
