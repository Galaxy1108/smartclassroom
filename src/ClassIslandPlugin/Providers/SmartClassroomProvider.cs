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

    // 图标码位从 ClassIsland 自带的字体文件里按**字形名**取：
    // 字体是 FluentSystemIcons-Resizable（嵌在 ClassIsland.Core.dll 里），
    // 字形名形如 ic_fluent_megaphone_20_regular。
    // 走过的弯路：照 Segoe MDL2 猜（E7E7 在这套字体里是笑脸）、
    // 照 FluentAvalonia 的 Symbol 枚举取（那是另一套字体，E171 显示成折线图）—— 都不对。
    private const string SummonGlyph = "\uEB70";    // ic_fluent_megaphone_20_regular（喊人）

    // ClassIsland 的模板资源键。**必须显式指定**：它不按数据类型自动选模板，
    // 不指定就会把数据对象 ToString() 出来（实测通知里显示成一长串类型名）。
    private const string MaskTemplateKey = "NotificationTwoIconsMaskTemplate";
    private const string OverlayTemplateKey = "NotificationSimpleTextOverlayTemplate";
    private const string ExchangeGlyph = "\uE15F";   // ic_fluent_arrow_swap_20_regular（对调）
    private const string ManualGlyph = "\uE9E4";     // ic_fluent_info_20_regular（提示）
    private const string BellGlyph = "\uE025";       // ic_fluent_alert_20_regular（Fluent 里 alert 就是铃铛）

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
                RightIconSource = new FluentIconSource(BellGlyph),
                HasRightIcon = true
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
