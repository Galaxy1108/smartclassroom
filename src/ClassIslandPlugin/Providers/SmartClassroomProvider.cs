using ClassIsland.Core.Abstractions.Services.NotificationProviders;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Controls;
using ClassIsland.Core.Models.Notification;
using ClassIsland.Core.Models.Notification.Templates;

namespace SmartClassroom.ClassIslandPlugin.Providers;

/// <summary>
/// 智慧课堂桥接提醒提供方：三个渠道分别对应召唤通知、换课结果、手动操作请求。
/// 发送入口统一走 <see cref="Notify"/>，由本地桥接接口调用。
///
/// ⚠️ 内容必须用 ClassIsland 的**模板数据类**（TwoIconsMaskTemplateData / SimpleTextTemplateData）：
/// 直接塞字符串时它没有对应模板，渲染出来就是"没图标、文字偏移/尺寸不对"（实测踩到）。
/// 图标用 Fluent 字形（FluentIconSource），码位取自 Segoe/Fluent 图标表。
/// </summary>
[NotificationProviderInfo("7c9e6679-8f2e-4a3b-9c5d-1a2b3c4d5e6f", "智慧课堂桥接", "智慧课堂 App 的配套提醒通道")]
[NotificationChannelInfo("7c9e6679-8f2e-4a3b-9c5d-1a2b3c4d5e70", "召唤通知", SummonGlyph,
    "老师在 QQ 群召唤某人过去", null)]
[NotificationChannelInfo("7c9e6679-8f2e-4a3b-9c5d-1a2b3c4d5e71", "换课结果", ExchangeGlyph,
    "换课自动处理的结果回执", null)]
[NotificationChannelInfo("7c9e6679-8f2e-4a3b-9c5d-1a2b3c4d5e72", "手动请求", ManualGlyph,
    "需要用户在 ClassIsland 中手动操作的请求", null)]
public class SmartClassroomProvider : NotificationProviderBase
{
    public static SmartClassroomProvider? Current { get; private set; }

    public SmartClassroomProvider()
    {
        Current = this;
    }

    public static readonly Guid SummonChannelId = Guid.Parse("7c9e6679-8f2e-4a3b-9c5d-1a2b3c4d5e70");
    public static readonly Guid ExchangeChannelId = Guid.Parse("7c9e6679-8f2e-4a3b-9c5d-1a2b3c4d5e71");
    public static readonly Guid ManualChannelId = Guid.Parse("7c9e6679-8f2e-4a3b-9c5d-1a2b3c4d5e72");

    // 图标码位取自 FluentAvalonia 的 Symbol 枚举（ClassIsland 用的就是这套 Fluent System Icons 字体）。
    // 别照 Segoe MDL2 的码位猜：同一码位在这套字体里是别的图案（实测 E7E7 显示成笑脸）。
    private const string SummonGlyph = "\uF8009";    // AlertUrgent

    // ClassIsland 的模板资源键。**必须显式指定**：它不按数据类型自动选模板，
    // 不指定就会把数据对象 ToString() 出来（实测通知里显示成一长串类型名）。
    private const string MaskTemplateKey = "NotificationTwoIconsMaskTemplate";
    private const string OverlayTemplateKey = "NotificationSimpleTextOverlayTemplate";
    private const string ExchangeGlyph = "\uE117";   // Sync（调换）
    private const string ManualGlyph = "\uE171";     // Important

    /// <summary>按渠道发送一条提醒。mask 为遮罩大字，overlay 为正文，speech 为播报内容。</summary>
    public void Notify(Guid channelId, string mask, string? overlay = null, string? speech = null,
        TimeSpan? duration = null)
    {
        var d = duration ?? TimeSpan.FromSeconds(8);
        var request = new NotificationRequest
        {
            ChannelId = channelId,
            // 遮罩用 ClassIsland 的"两图标遮罩"模板数据：左图标 + 文字，尺寸与居中由模板负责
            MaskContent = new NotificationContent(new TwoIconsMaskTemplateData
            {
                Text = mask,
                LeftIconSource = new FluentIconSource(GlyphFor(channelId)),
                HasRightIcon = false
            })
            {
                ContentTemplateResourceKey = MaskTemplateKey,
                Duration = d,
                SpeechContent = speech ?? mask
            },
            // 正文用简单文本模板
            OverlayContent = overlay is null ? null : new NotificationContent(new SimpleTextTemplateData
            {
                Text = overlay
            })
            {
                ContentTemplateResourceKey = OverlayTemplateKey,
                Duration = d,
                SpeechContent = speech ?? overlay
            }
        };
        Channel(channelId).ShowNotification(request);
    }

    private static string GlyphFor(Guid channelId)
        => channelId == SummonChannelId ? SummonGlyph
           : channelId == ManualChannelId ? ManualGlyph
           : ExchangeGlyph;
}
