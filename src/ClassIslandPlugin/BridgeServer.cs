using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared;
using Microsoft.Extensions.Hosting;
using SmartClassroom.ClassIslandPlugin.Providers;
using SmartClassroom.ClassIslandPlugin.Services;
using SmartClassroom.Contracts;

namespace SmartClassroom.ClassIslandPlugin;

/// <summary>
/// localhost 执行接口（仅绑 127.0.0.1:5199）：
/// GET /status ｜ POST /notify ｜ POST /exchange。
/// POST 需 Authorization: Bearer &lt;token&gt;，token 存于插件配置目录 bridge.token（首次启动自动生成）。
///
/// 实现上**不用 Kestrel/ASP.NET**：ClassIsland 是普通 .NET 应用、不带 ASP.NET Core 运行时，
/// 插件里加载 Microsoft.AspNetCore 会 FileNotFoundException（实测踩到：桥接一直起不来，
/// 诊断日志里是 "Could not load file or assembly 'Microsoft.AspNetCore'"）。
/// 这里用 BCL 的 TcpListener 自己处理这点请求：零框架依赖、跨平台一致。
/// </summary>
public class BridgeServer(ExchangeService exchange) : IHostedService
{
    public const int Port = 5199;

    private static BridgeServer? _instance;

    private TcpListener? _listener;
    private string? _token;

    /// <summary>桥接 token（应用用「自动查找」读的就是它）。</summary>
    public string? Token => _token;

    private static readonly JsonSerializerOptions Json = ContractsJson.Options;

    /// <summary>已启动的实例。</summary>
    public static BridgeServer? Instance => _instance;

    /// <summary>
    /// 启动桥接服务（幂等、线程安全、异常只记诊断日志）。
    /// 调用时机必须在 ClassIsland 主机就绪之后：Initialize 期间解析服务会死锁，
    /// 而插件的 IHostedService 在 ClassIsland 2.1 上不会被启动。
    /// </summary>
    public static void StartOnce(string reason)
    {
        if (_instance is not null)
            return;
        try
        {
            Plugin.Diag($"StartOnce({reason}) 开始");
            var server = new BridgeServer(IAppHost.GetService<ExchangeService>());
            server.StartServer();
            _instance = server;
            Plugin.Diag($"桥接服务已启动，监听 127.0.0.1:{Port}");
        }
        catch (Exception ex)
        {
            Plugin.Diag("起桥接服务失败：" + ex);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // 有的 ClassIsland 版本会启动插件的 hosted service；不启动也没关系，Initialize 里有兜底。
        Plugin.Diag("BridgeServer.StartAsync 被调用");
        try { StartServer(); }
        catch (Exception ex) { Plugin.Diag("StartAsync 里起服务失败：" + ex); }
        _instance ??= this;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        try { _listener?.Stop(); } catch { /* 忽略 */ }
        _listener = null;
        return Task.CompletedTask;
    }

    private void StartServer()
    {
        if (_listener is not null)
            return;

        var plugin = IAppHost.GetService<Plugin>();
        _token = LoadOrCreateToken(plugin.PluginConfigFolder);
        Plugin.Diag($"token 已就绪（{_token.Length} 字符），准备监听 {Port}");

        var listener = new TcpListener(IPAddress.Loopback, Port);
        listener.Start();
        _listener = listener;
        _ = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (_listener is { } listener)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
            }
            catch
            {
                break;   // Stop() 之后 AcceptTcpClientAsync 会抛
            }
            _ = Task.Run(() => HandleClientAsync(client));
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                await using var stream = client.GetStream();
                await HandleRequestAsync(stream).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Plugin.Diag("处理请求出错：" + ex.Message);
            }
        }
    }

    private async Task HandleRequestAsync(NetworkStream stream)
    {
        var head = await ReadHeadAsync(stream).ConfigureAwait(false);
        if (head is null)
            return;

        var (method, path, headers, pending) = head.Value;
        var body = await ReadBodyAsync(stream, headers, pending).ConfigureAwait(false);

        if (method == "GET" && path == "/status")
        {
            var lessons = IAppHost.GetService<ILessonsService>();
            await WriteJsonAsync(stream, 200, new PluginStatus
            {
                PluginVersion = "0.35.9",
                ClassPlanLoaded = lessons.IsClassPlanLoaded
            }).ConfigureAwait(false);
            return;
        }

        if (method != "POST")
        {
            await WriteJsonAsync(stream, 404, new { error = "not found" }).ConfigureAwait(false);
            return;
        }

        if (!Authorized(headers))
        {
            await WriteJsonAsync(stream, 401, new { error = "unauthorized" }).ConfigureAwait(false);
            return;
        }

        if (path == "/notify")
        {
            var req = Deserialize<NotifyRequest>(body);
            if (req is null)
            {
                await WriteJsonAsync(stream, 400, new { error = "empty body" }).ConfigureAwait(false);
                return;
            }
            // 提供方是 ClassIsland 通过 DI 建的：插件自己的 hosted service 在 2.1 上不会被启动，
            // 所以 Current 可能还是 null —— 这里主动从 DI 取一次（构造时会设置 Current）。
            var provider = SmartClassroomProvider.Current
                           ?? TryResolveProvider();
            if (provider is null)
            {
                await WriteJsonAsync(stream, 503, new { error = "provider not ready" }).ConfigureAwait(false);
                return;
            }
            var channel = req.Channel switch
            {
                NotifyChannels.Summon => SmartClassroomProvider.SummonChannelId,
                NotifyChannels.Exchange => SmartClassroomProvider.ExchangeChannelId,
                _ => SmartClassroomProvider.ManualChannelId
            };
            // ⚠️ ClassIsland 的提醒 API 必须在 UI 线程上调用，否则抛 "Call from invalid thread"
            //（实测：HTTP 线程直接调用会得到空响应）。这里切回 UI 线程再发。
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                () => provider.Notify(channel, req.Title, req.Body));
            await WriteJsonAsync(stream, 200, new { ok = true }).ConfigureAwait(false);
            return;
        }

        if (path == "/exchange")
        {
            var req = Deserialize<ExchangeRequest>(body);
            if (req is null)
            {
                await WriteJsonAsync(stream, 400, new { error = "empty body" }).ConfigureAwait(false);
                return;
            }
            var verdict = exchange.Handle(req);
            await WriteJsonAsync(stream, 200, verdict).ConfigureAwait(false);
            return;
        }

        await WriteJsonAsync(stream, 404, new { error = "not found" }).ConfigureAwait(false);
    }

    /// <summary>从 DI 里取提醒提供方（取不到就返回 null，不要让请求炸掉）。</summary>
    private static SmartClassroomProvider? TryResolveProvider()
    {
        try { return IAppHost.GetService<SmartClassroomProvider>(); }
        catch (Exception ex)
        {
            Plugin.Diag("解析提醒提供方失败：" + ex.Message);
            return null;
        }
    }

    private bool Authorized(Dictionary<string, string> headers)
        => _token is not null
           && headers.TryGetValue("authorization", out var value)
           && value == $"Bearer {_token}";

    private static T? Deserialize<T>(byte[] body) where T : class
    {
        if (body.Length == 0)
            return null;
        try { return JsonSerializer.Deserialize<T>(body, Json); }
        catch { return null; }
    }

    // ---------- 极简 HTTP/1.1（够用就行：请求头 + Content-Length 正文，响应后关连接） ----------

    private static async Task<(string Method, string Path, Dictionary<string, string> Headers, byte[] Pending)?>
        ReadHeadAsync(NetworkStream stream)
    {
        var buffer = new byte[4096];
        var acc = new List<byte>(1024);
        var headEnd = -1;
        while (acc.Count < 64 * 1024)
        {
            var n = await stream.ReadAsync(buffer).ConfigureAwait(false);
            if (n <= 0)
                return null;
            for (var i = 0; i < n; i++)
                acc.Add(buffer[i]);
            headEnd = IndexOfHeadEnd(acc);
            if (headEnd >= 0)
                break;
        }
        if (headEnd < 0)
            return null;

        var text = Encoding.UTF8.GetString(acc.ToArray(), 0, headEnd);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
            return null;

        var parts = lines[0].Split(' ');
        if (parts.Length < 2)
            return null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var idx = line.IndexOf(':');
            if (idx > 0)
                headers[line[..idx].Trim()] = line[(idx + 1)..].Trim();
        }

        // 正文可能已经跟着头部一起读进来了
        var bodyStart = headEnd + 4;
        var pending = acc.Count > bodyStart ? acc.GetRange(bodyStart, acc.Count - bodyStart).ToArray() : [];
        return (parts[0].ToUpperInvariant(), parts[1], headers, pending);
    }

    private static async Task<byte[]> ReadBodyAsync(
        NetworkStream stream, Dictionary<string, string> headers, byte[] pending)
    {
        if (!headers.TryGetValue("content-length", out var raw)
            || !int.TryParse(raw, out var length) || length <= 0)
            return pending;

        var body = new byte[length];
        var copied = Math.Min(pending.Length, length);
        Array.Copy(pending, body, copied);
        var read = copied;
        while (read < length)
        {
            var n = await stream.ReadAsync(body.AsMemory(read, length - read)).ConfigureAwait(false);
            if (n <= 0)
                break;
            read += n;
        }
        return body;
    }

    private static int IndexOfHeadEnd(List<byte> data)
    {
        for (var i = 3; i < data.Count; i++)
        {
            if (data[i - 3] == '\r' && data[i - 2] == '\n' && data[i - 1] == '\r' && data[i] == '\n')
                return i - 3;
        }
        return -1;
    }

    private static async Task WriteJsonAsync(NetworkStream stream, int status, object payload)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {Reason(status)}\r\n"
            + "Content-Type: application/json; charset=utf-8\r\n"
            + $"Content-Length: {json.Length}\r\n"
            + "Connection: close\r\n\r\n");
        await stream.WriteAsync(head).ConfigureAwait(false);
        await stream.WriteAsync(json).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        400 => "Bad Request",
        401 => "Unauthorized",
        404 => "Not Found",
        503 => "Service Unavailable",
        _ => "OK"
    };

    internal static string LoadOrCreateToken(string configFolder)
    {
        Directory.CreateDirectory(configFolder);
        var path = Path.Combine(configFolder, "bridge.token");
        if (File.Exists(path))
            return File.ReadAllText(path).Trim();
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
        File.WriteAllText(path, token);
        return token;
    }
}
