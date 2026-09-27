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
public sealed class AiGateway(AiOptions options, HttpClient? http = null) : IAiClient
{
    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds) };

    /// <summary>opencode 端点要求的路由会话 id（每个实例稳定复用）。</summary>
    private readonly string _sessionId = OpenCodeCompat.NewSessionId();

    public async Task<string> AskAsync(string system, string user, CancellationToken cancel = default,
        Action<string>? onProgress = null, IReadOnlyList<AiImage>? images = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (attempt > 1)
                    onProgress?.Invoke($"正在重试（第 {attempt}/{AiRetry.MaxAttempts} 次）…");
                return await AskOnceAsync(system, user, cancel, images).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < AiRetry.MaxAttempts && !cancel.IsCancellationRequested
                                       && ex is not OperationCanceledException)
            {
                var delay = AiRetry.DelayFor(attempt);
                onProgress?.Invoke($"AI 请求失败（第 {attempt}/{AiRetry.MaxAttempts} 次）："
                                   + $"{AiRetry.Short(ex)} · {delay.TotalSeconds:0} 秒后重试");
                await Task.Delay(delay, cancel).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                onProgress?.Invoke($"AI 请求失败：{AiRetry.Short(ex)}");
                throw;
            }
        }
    }

    private async Task<string> AskOnceAsync(string system, string user, CancellationToken cancel,
        IReadOnlyList<AiImage>? images = null)
    {
        // 有图片时用 OpenAI 的内容数组（data URI），否则就是纯字符串 —— 纯文本端点收到数组可能报错
        object userContent = user;
        if (images is { Count: > 0 })
        {
            var parts = new List<object> { new { type = "text", text = user } };
            parts.AddRange(images.Select(i => (object)new
            {
                type = "image_url",
                image_url = new { url = $"data:{i.MimeType};base64,{i.Data}" }
            }));
            userContent = parts;
        }
        var body = new
        {
            model = options.Model,
            temperature = 0.1,
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = userContent }
            }
        };
        using var req = new HttpRequestMessage(HttpMethod.Post,
            options.BaseUrl.TrimEnd('/') + "/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        if (options.ApiKey.Length > 0)
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.ApiKey);
        // opencode.ai 不带这个头会直接 400 MissingSessionID（pi-ai 边车侧由 sidecar 补同样的头）。
        if (OpenCodeCompat.IsOpenCodeEndpoint(options.BaseUrl))
            req.Headers.TryAddWithoutValidation(OpenCodeCompat.SessionHeader, _sessionId);

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
            var choice = doc.RootElement.GetProperty("choices")[0];
            var message = choice.GetProperty("message");
            var content = message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() ?? ""
                : "";

            // 空内容不能当成"成功"——上层会把它当有效结果，表现为"连通但什么都得不到"。
            // 这里直接抛出带原因的异常，让调用方走保守降级，也让用户看到到底为什么空。
            if (content.Trim().Length == 0)
                throw new AiException(DescribeEmpty(choice, message));

            return content.Trim();
        }
        catch (AiException) { throw; }
        catch (Exception ex) { throw new AiException("AI 返回解析失败", ex); }
    }

    /// <summary>
    /// 解释"为什么没有内容"。常见原因：推理模型把输出都花在思考上（只有 reasoning_content）、
    /// 输出被长度上限截断、或模型给出了 refusal。
    /// </summary>
    internal static string DescribeEmpty(JsonElement choice, JsonElement message)
    {
        var parts = new List<string> { "AI 返回了空内容" };

        if (choice.TryGetProperty("finish_reason", out var f) && f.ValueKind == JsonValueKind.String)
            parts.Add($"finish_reason={f.GetString()}");

        var reasoningLen = 0;
        if (message.TryGetProperty("reasoning_content", out var r) && r.ValueKind == JsonValueKind.String)
            reasoningLen = r.GetString()?.Length ?? 0;
        if (reasoningLen > 0)
            parts.Add($"只有 reasoning_content（{reasoningLen} 字，推理模型把输出用在思考上）");

        if (message.TryGetProperty("refusal", out var rf) && rf.ValueKind == JsonValueKind.String
            && (rf.GetString()?.Length ?? 0) > 0)
            parts.Add("模型给出了 refusal");

        if (message.TryGetProperty("tool_calls", out var tc) && tc.ValueKind == JsonValueKind.Array
            && tc.GetArrayLength() > 0)
            parts.Add("只返回了 tool_calls");

        parts.Add("建议改用非推理模型，或放宽输出长度上限");
        return string.Join("；", parts);
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
