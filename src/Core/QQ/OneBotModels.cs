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

/// <summary>
/// 群消息与私聊消息的共同部分，让处理管线（召唤/作业/换课）两种来源共用一套逻辑。
/// 私聊时 <see cref="GroupId"/> 为 0。
/// </summary>
public interface IIncomingMessage
{
    long UserId { get; }
    long GroupId { get; }
    long MessageId { get; }
    string Text { get; }
}

/// <summary>群消息事件（message.group）。message 可能是 string 或段数组，本类只保留解析后的纯文本。</summary>
public sealed class GroupMessageEvent : OneBotEvent, IIncomingMessage
{
    public long GroupId { get; set; }
    public long UserId { get; set; }
    public long MessageId { get; set; }
    public string RawMessage { get; set; } = "";
    public string Text { get; set; } = "";
    public string? Card { get; set; }
    public string? Nickname { get; set; }
}

/// <summary>
/// 私聊消息事件（message.private）。
/// 老师也可能私聊发"来一下"或作业，所以单独建一个模型（GroupId 为 0）。
/// </summary>
public sealed class PrivateMessageEvent : OneBotEvent, IIncomingMessage
{
    /// <summary>私聊没有群号，固定 0（消息记录里用 0 表示私聊）。</summary>
    public long GroupId => 0;

    public long UserId { get; set; }
    public long MessageId { get; set; }
    public string RawMessage { get; set; } = "";
    public string Text { get; set; } = "";
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

/// <summary>get_group_list 的一项。</summary>
public sealed class GroupInfoData
{
    [System.Text.Json.Serialization.JsonPropertyName("group_id")]
    public long GroupId { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("group_name")]
    public string GroupName { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("member_count")]
    public int MemberCount { get; set; }
}

/// <summary>get_login_info 的结果：当前注入实例登录的 QQ。</summary>
public sealed class LoginInfoData
{
    /// <summary>
    /// 注意：OneBot 返回的是 snake_case 的 <c>user_id</c>，而动作调用走的是反序列化
    /// （事件那条路是 OneBotParser 手写读取，不受影响）。
    /// 少了这个映射就会永远读到 0 —— 表现就是"检测不到 QQ 账号"。
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("user_id")]
    public long UserId { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("nickname")]
    public string Nickname { get; set; } = "";
}
