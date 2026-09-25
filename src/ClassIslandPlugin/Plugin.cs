using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SmartClassroom.ClassIslandPlugin.Providers;
using SmartClassroom.ClassIslandPlugin.Services;

namespace SmartClassroom.ClassIslandPlugin;

[PluginEntrance]
public class Plugin : PluginBase
{
    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        // 提醒提供方同时是 IHostedService；基类构造中已向主机注册，沿用官方示例做法。
        services.AddHostedService<SmartClassroomProvider>();
        services.AddSingleton<ExchangeService>();
        services.AddHostedService<BridgeServer>();
    }
}
