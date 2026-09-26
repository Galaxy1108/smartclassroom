using System.Text.Json;
using SmartClassroom.Core.AI;
using Xunit;

namespace SmartClassroom.Core.Tests;

/// <summary>
/// pi-ai 边车客户端测试。用纯 Node 写的假边车（无 npm 依赖）验证 stdio JSONL 协议；
/// 未安装 Node 时跳过进程级用例（该功能本身以 Node 为前提）。
/// </summary>
public sealed class PiAiSidecarTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sc-sidecar-" + Guid.NewGuid().ToString("N"));

    private const string StubScript = """
        import { createInterface } from 'node:readline';
        const rl = createInterface({ input: process.stdin });
        const reply = (id, data) =>
          process.stdout.write(JSON.stringify({ id, ok: true, data }) + '\n');
        rl.on('line', (line) => {
          if (!line.trim()) return;
          const req = JSON.parse(line);
          switch (req.cmd) {
            case 'ping':      reply(req.id, { pong: true, node: process.version }); break;
            case 'providers': reply(req.id, { providers: [
                                { id: 'openai', name: 'OpenAI' },
                                { id: 'deepseek', name: 'DeepSeek' }] }); break;
            case 'models':    reply(req.id, { models: [
                                { id: 'gpt-4o-mini', name: 'GPT-4o mini',
                                  contextWindow: 128000, vision: true, reasoning: false }] }); break;
            case 'complete':  reply(req.id, { text: `SYS=${req.system}|USER=${req.user}|BASE=${req.baseUrl ?? ''}` }); break;
            case 'boom':      process.stdout.write(JSON.stringify({ id: req.id, ok: false, error: '炸了' }) + '\n'); break;
            case 'junk':      process.stdout.write('not json at all\n');
                              reply(req.id, { pong: true }); break;
            case 'silent':    break; // 不回，用于超时用例
            default:          reply(req.id, {}); break;
          }
        });
        rl.on('close', () => process.exit(0));
        process.stderr.write('[stub] ready\n');
        """;

    private PiAiSidecarClient Client(int timeoutSeconds = 20, string? nodeOverride = null)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, NodeRuntime.SidecarScriptName), StubScript);
        return new PiAiSidecarClient(new SidecarOptions
        {
            SidecarDir = _dir,
            Provider = "deepseek",
            Model = "deepseek-flash",
            ApiKey = "sk-test",
            BaseUrl = "http://127.0.0.1:1/v1",
            TimeoutSeconds = timeoutSeconds,
            NodeExecutable = nodeOverride
        });
    }

    private static bool NodeUsable => NodeRuntime.IsUsable();

    [Fact]
    public void BuildRequest_KeepsValuesAndDropsNulls()
    {
        var line = PiAiSidecarClient.BuildRequest("7", "complete",
            new { provider = "openai", model = (string?)null, system = "s", user = "u" });
        using var doc = JsonDocument.Parse(line);
        Assert.Equal("7", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("complete", doc.RootElement.GetProperty("cmd").GetString());
        Assert.Equal("openai", doc.RootElement.GetProperty("provider").GetString());
        Assert.Equal("s", doc.RootElement.GetProperty("system").GetString());
        Assert.False(doc.RootElement.TryGetProperty("model", out _));
    }

    [Fact]
    public void BuildRequest_EscapesUnsafeText()
    {
        var line = PiAiSidecarClient.BuildRequest("1", "complete",
            new { system = "带\"引号\"和\n换行", user = "u" });
        using var doc = JsonDocument.Parse(line);
        Assert.Equal("带\"引号\"和\n换行", doc.RootElement.GetProperty("system").GetString());
    }

    [Fact]
    public async Task AskAsync_RoundTripsSystemAndUser()
    {
        if (!NodeUsable) return;
        await using var client = Client();
        var text = await client.AskAsync("你是助手", "你好");
        Assert.Equal("SYS=你是助手|USER=你好|BASE=http://127.0.0.1:1/v1", text);
    }

    [Fact]
    public async Task Catalog_ProvidersAndModels()
    {
        if (!NodeUsable) return;
        await using var client = Client();
        var providers = await client.ListProvidersAsync();
        Assert.Equal(2, providers.Length);
        Assert.Contains(providers, p => p.Id == "deepseek" && p.Name == "DeepSeek");

        var models = await client.ListModelsAsync("openai");
        var m = Assert.Single(models);
        Assert.Equal("gpt-4o-mini", m.Id);
        Assert.Equal(128000, m.ContextWindow);
        Assert.True(m.Vision);
        Assert.False(m.Reasoning);
    }

    [Fact]
    public async Task ErrorResponse_SurfacesAsAiException()
    {
        if (!NodeUsable) return;
        await using var client = Client();
        var ex = await Assert.ThrowsAsync<AiException>(() => client.SendAsync("boom", new { }));
        Assert.Contains("炸了", ex.Message);
    }

    [Fact]
    public async Task NonJsonNoiseOnStdout_IsIgnoredAndGoesToLog()
    {
        if (!NodeUsable) return;
        await using var client = Client();
        var logs = new List<string>();
        client.OnLog += logs.Add;
        var data = await client.SendAsync("junk", new { });
        Assert.True(data.TryGetProperty("pong", out _));
        Assert.Contains(logs, l => l.Contains("not json at all"));
    }

    [Fact]
    public async Task SilentCommand_TimesOutAsAiException()
    {
        if (!NodeUsable) return;
        await using var client = Client(timeoutSeconds: 1);
        var ex = await Assert.ThrowsAsync<AiException>(() => client.SendAsync("silent", new { }));
        Assert.Contains("超时", ex.Message);
    }

    [Fact]
    public async Task MultipleSequentialCalls_ReuseOneProcess()
    {
        if (!NodeUsable) return;
        await using var client = Client();
        Assert.True(await client.PingAsync());
        Assert.True(client.IsRunning);
        await client.SendAsync("ping", new { });
        Assert.True(client.IsRunning); // 第二次调用后仍是同一个常驻进程
    }

    [Fact]
    public async Task MissingScript_ThrowsAiException()
    {
        Directory.CreateDirectory(_dir); // 不放 sidecar.mjs
        await using var client = new PiAiSidecarClient(new SidecarOptions { SidecarDir = _dir });
        var ex = await Assert.ThrowsAsync<AiException>(async () => await client.SendAsync("ping", new { }));
        Assert.Contains("找不到边车脚本", ex.Message);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, true);
        }
        catch { /* 进程句柄未释放时忽略 */ }
    }
}
