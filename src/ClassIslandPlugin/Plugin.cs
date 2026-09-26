using ClassIsland.Core;
using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Extensions.Registry;
using ClassIsland.Core.Abstractions.Services.NotificationProviders;
using ClassIsland.Core.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SmartClassroom.ClassIslandPlugin.Providers;
using SmartClassroom.ClassIslandPlugin.Services;

namespace SmartClassroom.ClassIslandPlugin;

[PluginEntrance]
public class Plugin : PluginBase
{
    /// <summary>
    /// 诊断日志：写在自己目录下的 bridge-diag.log。
    /// 排查"插件加载了但桥接服务没起来"这类问题时，ClassIsland 的日志里看不到插件内部情况。
    /// </summary>
    internal static void Diag(string message)
    {
        try
        {
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "bridge-diag.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch { /* 写不进去就算了 */ }
    }

    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        Diag("Initialize 被调用");
        // ⚠️ AddHostedService<T>() 只注册成 IHostedService，**不注册 T 本身** ——
        // 而 ClassIsland 2.1 又不启动插件的 hosted service，于是提供方永远不会被构造，
        // 桥接发通知时拿到的是 null（诊断日志：Service SmartClassroomProvider is null!）。
        // 所以显式注册它自己，并额外注册成 NotificationProviderBase，
        // 让 ClassIsland 的提醒宿主枚举时能拿到它。
        // ClassIsland 的提醒提供方必须用它自己的注册 API（AddNotificationProvider），
        // 光 AddSingleton/AddHostedService 是不够的 —— 那样 ClassIsland 的提醒宿主找不到它，
        // 桥接发通知时就会报"没有找到与 ... 对应的提醒提供方"（实测踩到）。
        services.AddNotificationProvider<SmartClassroomProvider>();
        services.AddSingleton<ExchangeService>();
        // 有的 ClassIsland 版本会启动插件的 hosted service，保留这条（不启动也没关系）
        services.AddHostedService<BridgeServer>();

        // ⚠️ 实测 ClassIsland 2.1 上**插件的 IHostedService 根本不会被启动**
        //（诊断日志里只有 "Initialize 被调用"，BridgeServer.StartAsync 从未被调用），
        // 所以桥接服务不能只靠 hosted service 起。这里两条路都挂上（幂等）：
        //   1) AppStarted 事件（官方推荐时机）
        //   2) 延时兜底 —— 本插件可能加载得比 AppStarted 还晚，那时事件已经触发过
        AppBase.Current.AppStarted += (_, _) => BridgeServer.StartOnce("AppStarted");
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            BridgeServer.StartOnce("延时兜底");
        });
    }
}
