using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace SmartClassroom.Core.QQ;

/// <summary>
/// OneBot v11 客户端：正向 WebSocket 收事件 + HTTP 调动作。
/// SnowLuma / NapCat 通用。无第三方依赖，可在单测中用 fixture 回放。
/// </summary>
public sealed class OneBotClient : IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _wsUri;
    private ClientWebSocket? _ws;

    public OneBotClient(string httpBase, string wsUrl, string? accessToken = null, HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
        _http.BaseAddress ??= new Uri(httpBase.TrimEnd('/') + "/");
        if (accessToken is not null && _http.DefaultRequestHeaders.Authorization is null)
            _http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        _wsUri = new Uri(wsUrl);
    }

    /// <summary>连接正向 WS 并循环投递事件；cancel 后返回。断线抛异常，由上层重连。</summary>
    public async Task RunEventLoopAsync(
        Func<OneBotEvent, CancellationToken, Task> onEvent,
        CancellationToken cancel)
    {
        _ws = new ClientWebSocket();
        await _ws.ConnectAsync(_wsUri, cancel).ConfigureAwait(false);

        var buffer = new byte[64 * 1024];
        var sb = new StringBuilder();
        while (!cancel.IsCancellationRequested)
        {
            sb.Clear();
            WebSocketReceiveResult r;
            do
            {
                r = await _ws.ReceiveAsync(buffer, cancel).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close)
                    return;
                sb.Append(Encoding.UTF8.GetString(buffer, 0, r.Count));
            } while (!r.EndOfMessage);

            OneBotEvent? ev;
            try { ev = OneBotParser.Parse(sb.ToString()); }
            catch (JsonException) { continue; }
            if (ev is not null)
                await onEvent(ev, cancel).ConfigureAwait(false);
        }
    }

    /// <summary>通用动作调用：POST /{action}，retcode != 0 抛 OneBotException。</summary>
    public async Task<T?> InvokeAsync<T>(string action, object? args, CancellationToken cancel = default)
    {
        var json = JsonSerializer.Serialize(args ?? new { });
        using var res = await _http.PostAsync(action,
            new StringContent(json, Encoding.UTF8, "application/json"), cancel).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
        var envelope = JsonSerializer.Deserialize<OneBotResponse<T>>(body, OneBotJson.Options)
            ?? throw new OneBotException($"动作 {action} 返回空信封");
        if (!envelope.Ok)
            throw new OneBotException($"动作 {action} 失败 retcode={envelope.Retcode}: {envelope.Wording}{envelope.Message}");
        return envelope.Data;
    }

    public Task<FileUrlData?> GetGroupFileUrlAsync(long groupId, string fileId, long busid, CancellationToken cancel = default)
        => InvokeAsync<FileUrlData>("get_group_file_url", new { group_id = groupId, file_id = fileId, busid }, cancel);

    /// <summary>该账号所在的群列表（用于"选择要监听的群"，省得手打群号）。</summary>
    public Task<List<GroupInfoData>?> GetGroupListAsync(CancellationToken cancel = default)
        => InvokeAsync<List<GroupInfoData>>("get_group_list", null, cancel);

    /// <summary>当前注入实例登录的 QQ（用于"选择账号"：有人一台机器上登过好几个号）。</summary>
    public Task<LoginInfoData?> GetLoginInfoAsync(CancellationToken cancel = default)
        => InvokeAsync<LoginInfoData>("get_login_info", null, cancel);

    public Task<object?> SendGroupMessageAsync(long groupId, string text, CancellationToken cancel = default)
        => InvokeAsync<object>("send_group_msg", new { group_id = groupId, message = text }, cancel);

    public async ValueTask DisposeAsync()
    {
        if (_ws is not null)
        {
            try
            {
                if (_ws.State == WebSocketState.Open)
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
            catch { /* 关闭时忽略 */ }
            _ws.Dispose();
        }
        _http.Dispose();
    }
}

public sealed class OneBotException(string message) : Exception(message);
