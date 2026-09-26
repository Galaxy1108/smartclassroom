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
    private readonly string? _token;
    private ClientWebSocket? _ws;

    /// <param name="wsToken">
    /// WS 专用 token。SnowLuma 的 HTTP 与 WS 是两个 token，只传 <paramref name="accessToken"/>
    /// 会让 WS 升级被拒（401）。留空则退回用 <paramref name="accessToken"/>。
    /// </param>
    public OneBotClient(string httpBase, string wsUrl, string? accessToken = null,
        HttpClient? http = null, string? wsToken = null)
    {
        _http = http ?? new HttpClient();
        _http.BaseAddress ??= new Uri(httpBase.TrimEnd('/') + "/");
        if (accessToken is not null && _http.DefaultRequestHeaders.Authorization is null)
            _http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        _token = string.IsNullOrWhiteSpace(wsToken) ? accessToken : wsToken;
        _wsUri = new Uri(wsUrl);
    }

    /// <summary>
    /// 带 token 的 WS 地址。**必须带**：SnowLuma 的 WS 服务同样要求鉴权，
    /// 不带 token 的连接会被直接拒绝（表现是 WebUI 里"ws-default 0 个客户端"，
    /// 应用侧则一直"QQ 未连接（重连中…）"，消息事件一条都收不到）。
    /// 有的实现只认 query 参数，所以 query 和 Authorization 头都带上。
    /// </summary>
    internal static Uri BuildWsUri(string wsUrl, string? token)
    {
        if (string.IsNullOrEmpty(token))
            return new Uri(wsUrl);
        var sep = wsUrl.Contains('?') ? '&' : '?';
        return new Uri(wsUrl + sep + "access_token=" + Uri.EscapeDataString(token));
    }

    /// <summary>连接正向 WS 并循环投递事件；cancel 后返回。断线抛异常，由上层重连。</summary>
    /// <param name="onEvent">事件处理。**它抛异常不会断连接**（见下），连接类错误才由上层重连。</param>
    /// <param name="onHandlerError">处理某条事件出错时的回调（记日志用），不传则忽略。</param>
    public async Task RunEventLoopAsync(
        Func<OneBotEvent, CancellationToken, Task> onEvent,
        CancellationToken cancel,
        Action<OneBotEvent, Exception>? onHandlerError = null,
        Action? onConnected = null)
    {
        _ws = new ClientWebSocket();
        if (_token is not null)
            _ws.Options.SetRequestHeader("Authorization", $"Bearer {_token}");
        await _ws.ConnectAsync(BuildWsUri(_wsUri.ToString(), _token), cancel).ConfigureAwait(false);
        // 连上就通知上层 —— 状态栏的"已连接"必须在这里更新，
        // 写在循环之后的话，只有断开时才会执行（实测一直显示"正在连接 QQ…"）。
        onConnected?.Invoke();

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
            if (ev is null)
                continue;
            try
            {
                await onEvent(ev, cancel).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // 一条消息处理失败（AI 解析、反序列化…）**不能**把整个事件循环掀翻：
                // 实测换课 JSON 反序列化异常会冒到这里，被当成"连接断开"，
                // 于是状态栏显示"QQ 未连接（重连中…）"，而消息其实一直在收。
                onHandlerError?.Invoke(ev, ex);
            }
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
