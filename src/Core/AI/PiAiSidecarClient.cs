using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace SmartClassroom.Core.AI;

/// <summary>pi-ai 边车配置。</summary>
public sealed record SidecarOptions
{
    /// <summary>含 sidecar.mjs 与 node_modules 的目录。</summary>
    public required string SidecarDir { get; init; }

    /// <summary>node 可执行文件；null 走 <see cref="NodeRuntime.FindNode"/>。</summary>
    public string? NodeExecutable { get; init; }

    public string? Provider { get; init; }
    public string? Model { get; init; }
    public string? ApiKey { get; init; }

    /// <summary>非空则走"自定义 OpenAI 兼容端点"，忽略 Provider。</summary>
    public string? BaseUrl { get; init; }

    public int TimeoutSeconds { get; init; } = 40;
}

/// <summary>边车返回的模型条目。</summary>
public sealed record SidecarModel(string Id, string Name, long ContextWindow, bool Vision, bool Reasoning);

/// <summary>边车返回的 provider 条目。</summary>
public sealed record SidecarProvider(string Id, string Name);

/// <summary>
/// pi-ai 边车客户端：spawn Node 子进程，按行 JSON 收发。
/// 进程常驻复用；崩溃后下次调用自动重启；所有失败都抛 <see cref="AiException"/>，由上层保守降级。
/// 已用 tools/mock-openai.py 对真实 pi-ai 做过端到端验证（自定义 baseUrl 路径）。
/// </summary>
public sealed class PiAiSidecarClient : IAiClient, IAsyncDisposable
{
    private readonly SidecarOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private Process? _process;
    private int _seq;
    private volatile bool _disposed;

    /// <summary>stderr 日志（pi-ai 的内部日志走这里，不污染协议）。</summary>
    public event Action<string>? OnLog;

    public PiAiSidecarClient(SidecarOptions options) => _options = options;

    public bool IsRunning => _process is { HasExited: false };

    // ---- 高层 API ----

    public async Task<string> AskAsync(string system, string user, CancellationToken cancel = default)
    {
        var data = await SendAsync("complete", new
        {
            provider = _options.Provider,
            model = _options.Model,
            apiKey = _options.ApiKey,
            baseUrl = _options.BaseUrl,
            system,
            user
        }, cancel).ConfigureAwait(false);
        return data.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
    }

    public async Task<SidecarProvider[]> ListProvidersAsync(CancellationToken cancel = default)
    {
        var data = await SendAsync("providers", new { }, cancel).ConfigureAwait(false);
        if (!data.TryGetProperty("providers", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];
        return arr.EnumerateArray()
            .Select(p => new SidecarProvider(Str(p, "id"), Str(p, "name")))
            .Where(p => p.Id.Length > 0)
            .ToArray();
    }

    public async Task<SidecarModel[]> ListModelsAsync(string provider, CancellationToken cancel = default)
    {
        var data = await SendAsync("models", new { provider }, cancel).ConfigureAwait(false);
        if (!data.TryGetProperty("models", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];
        return arr.EnumerateArray().Select(m => new SidecarModel(
            Str(m, "id"), Str(m, "name"),
            m.TryGetProperty("contextWindow", out var cw) && cw.TryGetInt64(out var n) ? n : 0,
            m.TryGetProperty("vision", out var v) && v.ValueKind == JsonValueKind.True,
            m.TryGetProperty("reasoning", out var r) && r.ValueKind == JsonValueKind.True)).ToArray();
    }

    public async Task<bool> PingAsync(CancellationToken cancel = default)
    {
        try
        {
            var data = await SendAsync("ping", new { }, cancel).ConfigureAwait(false);
            return data.TryGetProperty("pong", out var p) && p.ValueKind == JsonValueKind.True;
        }
        catch (AiException)
        {
            return false;
        }
    }

    private static string Str(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    // ---- 协议层 ----

    /// <summary>发送一条命令并等待同 id 的应答。</summary>
    public async Task<JsonElement> SendAsync(string cmd, object payload, CancellationToken cancel = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var id = Interlocked.Increment(ref _seq).ToString();

        await EnsureStartedAsync(cancel).ConfigureAwait(false);

        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var line = BuildRequest(id, cmd, payload);
        try
        {
            var stdin = _process?.StandardInput;
            if (stdin is null)
                throw new AiException("AI 边车未启动");
            await _gate.WaitAsync(cancel).ConfigureAwait(false);
            try
            {
                await stdin.WriteLineAsync(line).ConfigureAwait(false);
                await stdin.FlushAsync(cancel).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
            var done = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, timeout.Token)).ConfigureAwait(false);
            if (done != tcs.Task)
            {
                _pending.TryRemove(id, out _);
                throw new AiException($"AI 边车超时（{_options.TimeoutSeconds}s，命令 {cmd}）");
            }
            return await tcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            _pending.TryRemove(id, out _);
            throw new AiException($"AI 边车超时（{_options.TimeoutSeconds}s，命令 {cmd}）");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            _pending.TryRemove(id, out _);
            throw new AiException($"AI 边车写入失败：{ex.Message}", ex);
        }
    }

    public static string BuildRequest(string id, string cmd, object payload)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var sb = new StringBuilder();
        sb.Append("{\"id\":").Append(JsonSerializer.Serialize(id))
          .Append(",\"cmd\":").Append(JsonSerializer.Serialize(cmd));
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Null)
                continue;
            sb.Append(',').Append(JsonSerializer.Serialize(prop.Name)).Append(':').Append(prop.Value.GetRawText());
        }
        return sb.Append('}').ToString();
    }

    private async Task EnsureStartedAsync(CancellationToken cancel)
    {
        if (_process is { HasExited: false })
            return;
        await _gate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            if (_process is { HasExited: false })
                return;
            StartProcess();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void StartProcess()
    {
        var script = Path.Combine(_options.SidecarDir, NodeRuntime.SidecarScriptName);
        if (!File.Exists(script))
            throw new AiException($"找不到边车脚本：{script}");

        var node = _options.NodeExecutable
                   ?? NodeRuntime.FindNode()
                   ?? NodeRuntime.NodeExeName;   // 双保险：FindNode 理论上总会给个名字
        var psi = new ProcessStartInfo(node, $"\"{script}\"")
        {
            WorkingDirectory = _options.SidecarDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => OnStdoutLine(e.Data);
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) OnLog?.Invoke(e.Data); };
        p.Exited += (_, _) => FailAllPending("AI 边车进程已退出");
        try
        {
            p.Start();
        }
        catch (Exception ex)
        {
            throw new AiException($"启动 AI 边车失败（{node}）：{ex.Message}", ex);
        }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        _process = p;
    }

    internal void OnStdoutLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;
        // 只认协议行；非 JSON 行进日志，避免 pi-ai 偶发输出打断协议。
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            OnLog?.Invoke(line);
            return;
        }
        using (doc)
        {
            var root = doc.RootElement;
            var id = root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString() : null;
            if (id is null || !_pending.TryRemove(id, out var tcs))
                return;
            if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
            {
                tcs.TrySetResult(root.TryGetProperty("data", out var data) ? data.Clone() : default);
            }
            else
            {
                var err = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String
                    ? e.GetString() : "未知错误";
                tcs.TrySetException(new AiException($"AI 边车返回错误：{err}"));
            }
        }
    }

    private void FailAllPending(string reason)
    {
        foreach (var key in _pending.Keys.ToArray())
        {
            if (_pending.TryRemove(key, out var tcs))
                tcs.TrySetException(new AiException(reason));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        var p = _process;
        _process = null;
        if (p is not null)
        {
            try
            {
                if (!p.HasExited)
                {
                    p.StandardInput.Close(); // 边车收到 EOF 会在写完在途请求后自行退出
                    if (!p.WaitForExit(3000))
                        p.Kill(entireProcessTree: true);
                }
            }
            catch { /* 退出路径不抛 */ }
            p.Dispose();
        }
        FailAllPending("AI 边车已释放");
        _gate.Dispose();
        await Task.CompletedTask;
    }
}
