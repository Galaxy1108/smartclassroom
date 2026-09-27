using System.Text;
using System.Text.Json;
using SmartClassroom.Contracts;

namespace SmartClassroom.Core;

/// <summary>App → 插件 localhost HTTP 客户端（Kestrel，127.0.0.1 + Bearer token）。</summary>
public sealed class PluginLink
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions Json = ContractsJson.Options;

    public PluginLink(string baseUrl, string token, HttpClient? http = null)
    {
        Token = token;
        _http = http ?? new HttpClient();
        _http.BaseAddress ??= new Uri(baseUrl.TrimEnd('/') + "/");
    }

    private string Token { get; }

    /// <summary>
    /// 取某天的课表（**临时层优先**：插件侧走 ClassIsland 的 GetClassPlanByDate，
    /// 它会先查按日期覆盖的临时层）。取不到返回 null，调用方降级。
    /// </summary>
    public async Task<ClassPlanDay?> GetClassPlanAsync(DateOnly date, CancellationToken cancel = default)
    {
        try
        {
            using var res = await _http.GetAsync($"classplan?date={date:yyyy-MM-dd}", cancel).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
                return null;
            var text = await res.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
            return System.Text.Json.JsonSerializer.Deserialize<ClassPlanDay>(text, Json);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把某天课表压成一行给 AI 看（"第1节语文、第2节数学…"）。</summary>
    public static string DescribeClassPlan(ClassPlanDay? day)
    {
        if (day is null || !day.HasPlan)
            return "（取不到课表）";
        var parts = day.Periods
            .Where(p => p.Subject.Length > 0)
            .Select(p => $"第{p.Index}节{p.Subject}");
        var text = string.Join("、", parts);
        return day.IsOverlay ? text + "（含临时调整）" : text;
    }

    public async Task<bool> IsAliveAsync(CancellationToken cancel = default)
    {
        try
        {
            using var res = await _http.GetAsync("status", cancel).ConfigureAwait(false);
            return res.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>读取插件状态（版本 / 课表是否加载）；插件未运行时返回 null。</summary>
    public async Task<PluginStatus?> StatusAsync(CancellationToken cancel = default)
    {
        try
        {
            using var res = await _http.GetAsync("status", cancel).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
                return null;
            var body = await res.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
            return JsonSerializer.Deserialize<PluginStatus>(body, Json);
        }
        catch { return null; }
    }

    /// <param name="wait">true = 等提醒显示完成再返回（排队通知逐条放）。</param>
    public async Task NotifyAsync(string channel, string title, string body,
        CancellationToken cancel = default, bool wait = false)
    {
        var req = new NotifyRequest { Channel = channel, Title = title, Body = body, Wait = wait };
        using var msg = new HttpRequestMessage(HttpMethod.Post, "notify")
        {
            Content = new StringContent(JsonSerializer.Serialize(req, Json), Encoding.UTF8, "application/json")
        };
        msg.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token);
        using var res = await _http.SendAsync(msg, cancel).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
    }

    public async Task<ExchangeVerdict> ExchangeAsync(ExchangeRequest req, CancellationToken cancel = default)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Post, "exchange")
        {
            Content = new StringContent(JsonSerializer.Serialize(req, Json), Encoding.UTF8, "application/json")
        };
        msg.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token);
        using var res = await _http.SendAsync(msg, cancel).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
        return JsonSerializer.Deserialize<ExchangeVerdict>(body, Json)
            ?? throw new InvalidOperationException("插件换课接口返回空");
    }
}
