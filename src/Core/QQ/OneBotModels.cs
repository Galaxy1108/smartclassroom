using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartClassroom.Core.QQ;

/// <summary>
/// OneBot v11 事件/动作的最小模型集（SnowLuma / NapCat 通用方言）。
/// 仅覆盖本应用需要的部分：群消息、群文件上传通知、通用动作信封。
/// </summary>
public static class OneBotJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };
}

/// <summary>OneBot 事件基座（post_type/message/notice/request/meta_event）。</summary>
public class OneBotEvent
{
    public string PostType { get; set; } = "";
    public string? MessageType { get; set; }
    public string? NoticeType { get; set; }
    public long Time { get; set; }
    public long SelfId { get; set; }
}

/// <summary>群消息事件（message.group）。message 可能是 string 或段数组，本类只保留解析后的纯文本。</summary>
public sealed class GroupMessageEvent : OneBotEvent
{
    public long GroupId { get; set; }
    public long UserId { get; set; }
    public long MessageId { get; set; }
    public string RawMessage { get; set; } = "";
    public string Text { get; set; } = "";
    public string? Card { get; set; }
    public string? Nickname { get; set; }
}

/// <summary>群文件上传通知（notice.group_upload）。</summary>
public sealed class GroupUploadEvent : OneBotEvent
{
    public long GroupId { get; set; }
    public long UserId { get; set; }
    public UploadedFile File { get; set; } = new();
}

public sealed class UploadedFile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public long Busid { get; set; }
}

/// <summary>动作调用通用信封。HTTP 200 且 retcode==0 才算成功。</summary>
/// <typeparam name="T">data 段类型。</typeparam>
public sealed class OneBotResponse<T>
{
    public string Status { get; set; } = "";
    public int Retcode { get; set; }
    public T? Data { get; set; }
    public string Message { get; set; } = "";
    public string Wording { get; set; } = "";

    public bool Ok => Status == "ok" && Retcode == 0;
}

public sealed class FileUrlData
{
    public string Url { get; set; } = "";
}

/// <summary>get_login_info 的结果：当前注入实例登录的 QQ。</summary>
public sealed class LoginInfoData
{
    public long UserId { get; set; }
    public string Nickname { get; set; } = "";
}
