using System.Text;
using System.Text.Json;
using SmartClassroom.Contracts;

namespace SmartClassroom.Core;

/// <summary>App → 插件 localhost HTTP 客户端（Kestrel，127.0.0.1 + Bearer token）。</summary>
public sealed class PluginLink
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public PluginLink(string baseUrl, string token, HttpClient? http = null)
    {
        Token = token;
        _http = http ?? new HttpClient();
        _http.BaseAddress ??= new Uri(baseUrl.TrimEnd('/') + "/");
    }

    private string Token { get; }

    public async Task<bool> IsAliveAsync(CancellationToken cancel = default)
    {
        try
        {
            using var res = await _http.GetAsync("status", cancel).ConfigureAwait(false);
            return res.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task NotifyAsync(string channel, string title, string body, CancellationToken cancel = default)
    {
        var req = new NotifyRequest { Channel = channel, Title = title, Body = body };
        using var msg = new HttpRequestMessage(HttpMethod.Post, "notify")
        {
            Content = new StringContent(JsonSerializer.Serialize(req), Encoding.UTF8, "application/json")
        };
        msg.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token);
        using var res = await _http.SendAsync(msg, cancel).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
    }

    public async Task<ExchangeVerdict> ExchangeAsync(ExchangeRequest req, CancellationToken cancel = default)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Post, "exchange")
        {
            Content = new StringContent(JsonSerializer.Serialize(req), Encoding.UTF8, "application/json")
        };
        msg.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token);
        using var res = await _http.SendAsync(msg, cancel).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
        return JsonSerializer.Deserialize<ExchangeVerdict>(body, Json)
            ?? throw new InvalidOperationException("插件换课接口返回空");
    }
}
