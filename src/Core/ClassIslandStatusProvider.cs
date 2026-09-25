using ClassIsland.Shared.Enums;
using ClassIsland.Shared.IPC;
using ClassIsland.Shared.IPC.Abstractions.Services;
using dotnetCampus.Ipc.CompilerServices.GeneratedProxies;

namespace SmartClassroom.Core;

/// <summary>
/// 经官方 IPC 读取课表状态（ClassIsland.Shared.Ipc 2.1.0 net8.0 构建；
/// 与 2.1.x 主机同 dotnetCampus.Ipc alpha410 协议，联调时复核）。
/// 上课唯一口径：已加载课表 且 CurrentState == OnClass。
/// </summary>
public sealed class ClassIslandStatusProvider : IClassStatusProvider
{
    private readonly IpcClient _client = new();
    private IPublicLessonsService? _lessons;

    /// <summary>连接 ClassIsland。事件订阅（如需）必须在调用本方法前完成注册。</summary>
    public async Task ConnectAsync(CancellationToken cancel = default)
    {
        await _client.Connect().ConfigureAwait(false);
        _lessons = _client.Provider.CreateIpcProxy<IPublicLessonsService>(_client.PeerProxy!);
    }

    /// <summary>订阅上课/下课等事件（底层透出，调用方在 ConnectAsync 前注册）。</summary>
    public void AddNotifyHandler(string notifyId, Action handler)
        => _client.JsonIpcProvider.AddNotifyHandler(notifyId, handler);

    public Task<bool> IsInClassAsync(CancellationToken cancel = default)
    {
        if (_lessons is null)
            return Task.FromResult(false);
        try
        {
            return Task.FromResult(
                _lessons.IsClassPlanLoaded && _lessons.CurrentState == TimeState.OnClass);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }
}
