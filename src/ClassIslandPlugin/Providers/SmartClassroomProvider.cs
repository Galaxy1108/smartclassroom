using ClassIsland.Core.Abstractions.Services.NotificationProviders;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Models.Notification;

namespace SmartClassroom.ClassIslandPlugin.Providers;

/// <summary>
/// 智慧课堂桥接提醒提供方：三个渠道分别对应召唤通知、换课结果、手动操作请求。
/// 发送入口统一走 <see cref="Notify"/>，由 Kestrel 本地接口层调用。
/// </summary>
[NotificationProviderInfo("7c9e6679-8f2e-4a3b-9c5d-1a2b3c4d5e6f", "智慧课堂桥接", "智慧课堂 App 的配套提醒通道")]
[NotificationChannelInfo("7c9e6679-8f2e-4a3b-9c5d-1a2b3c4d5e70", name: "召唤通知", description: "老师在 QQ 群召唤某人过去")]
[NotificationChannelInfo("7c9e6679-8f2e-4a3b-9c5d-1a2b3c4d5e71", name: "换课结果", description: "换课自动处理的结果回执")]
[NotificationChannelInfo("7c9e6679-8f2e-4a3b-9c5d-1a2b3c4d5e72", name: "手动请求", description: "需要用户在 ClassIsland 中手动操作的请求")]
public class SmartClassroomProvider : NotificationProviderBase
{
    public static readonly Guid SummonChannelId = Guid.Parse("7c9e6679-8f2e-4a3b-9c5d-1a2b3c4d5e70");
    public static readonly Guid ExchangeChannelId = Guid.Parse("7c9e6679-8f2e-4a3b-9c5d-1a2b3c4d5e71");
    public static readonly Guid ManualChannelId = Guid.Parse("7c9e6679-8f2e-4a3b-9c5d-1a2b3c4d5e72");

    /// <summary>按渠道发送一条提醒。mask 为遮罩大字，overlay 为正文，speech 为播报内容。</summary>
    public void Notify(Guid channelId, string mask, string? overlay = null, string? speech = null,
        TimeSpan? duration = null)
    {
        var d = duration ?? TimeSpan.FromSeconds(8);
        var request = new NotificationRequest
        {
            ChannelId = channelId,
            MaskContent = new NotificationContent(mask)
            {
                Duration = d,
                SpeechContent = speech ?? mask
            },
            OverlayContent = overlay is null ? null : new NotificationContent(overlay)
            {
                Duration = d,
                SpeechContent = speech ?? overlay
            }
        };
        Channel(channelId).ShowNotification(request);
    }
}
