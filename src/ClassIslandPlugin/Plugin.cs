using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SmartClassroom.ClassIslandPlugin.Providers;

namespace SmartClassroom.ClassIslandPlugin;

[PluginEntrance]
public class Plugin : PluginBase
{
    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        // 提醒提供方同时是 IHostedService；基类构造中已向主机注册，沿用官方示例做法。
        services.AddHostedService<SmartClassroomProvider>();

        // Slice B：在此启动 Kestrel localhost 接口（/notify /exchange /status），
        // 并经 IProfileService 实现换课快照读取与临时层落课。
    }
}
