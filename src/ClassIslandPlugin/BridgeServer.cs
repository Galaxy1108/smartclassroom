using System.Text.Json;
using ClassIsland.Core;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using SmartClassroom.ClassIslandPlugin.Providers;
using SmartClassroom.ClassIslandPlugin.Services;
using SmartClassroom.Contracts;

namespace SmartClassroom.ClassIslandPlugin;

/// <summary>
/// localhost 执行接口（Kestrel，仅绑 127.0.0.1:5199）：
/// GET /status ｜ POST /notify ｜ POST /exchange。
/// POST 需 Authorization: Bearer &lt;token&gt;，token 存于插件配置目录 bridge.token（首次启动自动生成）。
/// </summary>
public class BridgeServer(ExchangeService exchange) : IHostedService
{
    public const int Port = 5199;

    private WebApplication? _app;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // 等 ClassIsland 完全启动后再起服务（沿用官方模板的 AppStarted 模式）。
        AppBase.Current.AppStarted += (_, _) => StartServer();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_app is not null)
            await _app.StopAsync(cancellationToken);
    }

    private void StartServer()
    {
        var plugin = IAppHost.GetService<Plugin>();
        var token = LoadOrCreateToken(plugin.PluginConfigFolder);

        var builder = WebApplication.CreateBuilder();
        var app = builder.Build();
        app.Urls.Clear();
        app.Urls.Add($"http://127.0.0.1:{Port}");

        app.MapGet("/status", () =>
        {
            var lessons = IAppHost.GetService<ILessonsService>();
            return Results.Json(new PluginStatus
            {
                PluginVersion = "0.3.0",
                ClassPlanLoaded = lessons.IsClassPlanLoaded
            }, Json);
        });

        app.MapPost("/notify", async (HttpContext ctx) =>
        {
            if (!Authorized(ctx, token))
                return Results.Unauthorized();
            var req = await ctx.Request.ReadFromJsonAsync<NotifyRequest>(Json, ctx.RequestAborted);
            if (req is null)
                return Results.BadRequest("empty body");
            var provider = SmartClassroomProvider.Current;
            if (provider is null)
                return Results.Problem("provider not ready");
            var channel = req.Channel switch
            {
                NotifyChannels.Summon => SmartClassroomProvider.SummonChannelId,
                NotifyChannels.Exchange => SmartClassroomProvider.ExchangeChannelId,
                _ => SmartClassroomProvider.ManualChannelId
            };
            provider.Notify(channel, req.Title, req.Body);
            return Results.Ok();
        });

        app.MapPost("/exchange", async (HttpContext ctx) =>
        {
            if (!Authorized(ctx, token))
                return Results.Unauthorized();
            var req = await ctx.Request.ReadFromJsonAsync<ExchangeRequest>(Json, ctx.RequestAborted);
            if (req is null)
                return Results.BadRequest("empty body");
            var verdict = exchange.Handle(req);
            return Results.Json(verdict, Json);
        });

        _app = app;
        _ = app.RunAsync();
    }

    private static bool Authorized(HttpContext ctx, string token)
        => ctx.Request.Headers.Authorization.ToString() == $"Bearer {token}";

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
