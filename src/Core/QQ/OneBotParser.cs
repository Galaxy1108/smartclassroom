using System.Text.Json;

namespace SmartClassroom.Core.QQ;

/// <summary>OneBot 事件解析：只关心群消息与群文件上传，其余返回 null。</summary>
public static class OneBotParser
{
    public static OneBotEvent? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("post_type", out var pt))
            return null;

        return pt.GetString() switch
        {
            "message" => ParseMessage(root),
            "notice" => ParseNotice(root),
            _ => null
        };
    }

    private static OneBotEvent? ParseMessage(JsonElement root)
    {
        var messageType = root.GetPropertyOrNull("message_type");
        if (messageType == "private")
            return ParsePrivateMessage(root);
        if (messageType != "group")
            return null;
        var ev = new GroupMessageEvent
        {
            PostType = "message",
            MessageType = "group",
            Time = root.GetInt64OrZero("time"),
            SelfId = root.GetInt64OrZero("self_id"),
            GroupId = root.GetInt64OrZero("group_id"),
            UserId = root.GetInt64OrZero("user_id"),
            MessageId = root.GetInt64OrZero("message_id"),
        };
        if (root.TryGetProperty("sender", out var sender))
        {
            ev.Card = sender.GetStringOrNull("card");
            ev.Nickname = sender.GetStringOrNull("nickname");
        }
        // 群里直接发文件时也是一个 file 段 —— 当作上传事件处理（否则会变成一条空文本消息）
        if (ExtractFile(root) is { } groupFile)
            return ToFileEvent(root, groupFile, ev.GroupId, ev.UserId);
        // 图片：挂到事件上交给 AI（视觉模型能读截图里的字），同时给文本一个占位符，
        // 别让事件页显示一片空白。
        var (imagePlaceholder, images) = ExtractImages(root, ev.MessageId);
        ev.Images = images;
        (ev.RawMessage, ev.Text) = ExtractText(root);
        if (imagePlaceholder.Length > 0)
            ev.Text = ev.Text.Length > 0 ? $"{ev.Text} {imagePlaceholder}" : imagePlaceholder;
        return ev;
    }

    /// <summary>
    /// 消息里的 file 段（SnowLuma 对私聊/群文件都会给：
    /// data = { file, file_id, name, size, url, file_hash }）。
    /// </summary>
    /// <summary>
    /// 消息里的图片段。**不能只认文本段**：老师发的作业/通知截图就是图片，
    /// 以前整条被当成"没有内容的文本"丢掉（用户："图片是被忽略掉"）。
    /// 返回 (占位文本, 可归档的图片文件)。
    /// </summary>
    private static (string Placeholder, List<UploadedImage> Images) ExtractImages(JsonElement root, long messageId)
    {
        var images = new List<UploadedImage>();
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Array)
            return ("", images);
        var count = 0;
        foreach (var seg in message.EnumerateArray())
        {
            if (seg.GetStringOrNull("type") != "image" || !seg.TryGetProperty("data", out var data))
                continue;
            count++;
            var url = data.GetStringOrNull("url") ?? data.GetStringOrNull("file") ?? "";
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                continue;   // 内联图片拿不到直链，只能留占位符
            // 扩展名优先看段里的 file 字段（"abc.jpg"），再看 URL 路径
            //（SnowLuma 的图片直链常常没有扩展名，例如 /get_image?x=1）。
            var raw = data.GetStringOrNull("file") ?? "";
            var ext = Path.GetExtension(raw);
            if (ext.Length is 0 or > 5)
                ext = Path.GetExtension(new Uri(url).AbsolutePath);
            if (ext.Length is 0 or > 5) ext = ".png";
            images.Add(new UploadedImage { Url = url, Name = $"图片_{messageId}_{count}{ext}" });
        }
        return (count == 0 ? "" : count == 1 ? "[图片]" : $"[{count} 张图片]", images);
    }

    private static UploadedFile? ExtractFile(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var seg in message.EnumerateArray())
        {
            if (seg.GetStringOrNull("type") != "file" || !seg.TryGetProperty("data", out var data))
                continue;
            var name = data.GetStringOrNull("name") ?? data.GetStringOrNull("file") ?? "";
            var id = data.GetStringOrNull("id") ?? data.GetStringOrNull("file_id") ?? "";
            var url = data.GetStringOrNull("url") ?? "";
            long size = 0;
            if (data.TryGetProperty("size", out var s) && s.TryGetInt64(out var sv)) size = sv;
            else if (data.TryGetProperty("file_size", out var fs) && fs.TryGetInt64(out var fsv)) size = fsv;
            return new UploadedFile { Id = id, Name = name, Size = size, Url = url };
        }
        return null;
    }

    private static GroupUploadEvent ToFileEvent(JsonElement root, UploadedFile file, long groupId, long userId)
        => new()
        {
            PostType = "message",
            NoticeType = "group_upload",
            Time = root.GetInt64OrZero("time"),
            SelfId = root.GetInt64OrZero("self_id"),
            GroupId = groupId,          // 私聊为 0
            UserId = userId,
            MessageId = root.GetInt64OrZero("message_id"),
            File = file
        };

    private static OneBotEvent? ParsePrivateMessage(JsonElement root)
    {
        var ev = new PrivateMessageEvent
        {
            PostType = "message",
            MessageType = "private",
            Time = root.GetInt64OrZero("time"),
            SelfId = root.GetInt64OrZero("self_id"),
            UserId = root.GetInt64OrZero("user_id"),
            MessageId = root.GetInt64OrZero("message_id"),
        };
        if (root.TryGetProperty("sender", out var sender))
            ev.Nickname = sender.GetStringOrNull("nickname");
        // 私聊文件：SnowLuma 发的是带 file 段的消息（不是 notice），必须在这里认出来，
        // 否则整条消息会被当成"没有内容的文本"丢掉 —— 用户看到的就是"文件没保存"。
        if (ExtractFile(root) is { } privateFile)
            return ToFileEvent(root, privateFile, 0, ev.UserId);
        var (imagePlaceholder, images) = ExtractImages(root, ev.MessageId);
        ev.Images = images;
        (ev.RawMessage, ev.Text) = ExtractText(root);
        if (imagePlaceholder.Length > 0)
            ev.Text = ev.Text.Length > 0 ? $"{ev.Text} {imagePlaceholder}" : imagePlaceholder;
        return ev;
    }

    private static OneBotEvent? ParseNotice(JsonElement root)
    {
        if (root.GetPropertyOrNull("notice_type") != "group_upload")
            return null;
        var ev = new GroupUploadEvent
        {
            PostType = "notice",
            NoticeType = "group_upload",
            Time = root.GetInt64OrZero("time"),
            SelfId = root.GetInt64OrZero("self_id"),
            GroupId = root.GetInt64OrZero("group_id"),
            UserId = root.GetInt64OrZero("user_id"),
        };
        if (root.TryGetProperty("file", out var file))
        {
            ev.File = new UploadedFile
            {
                Id = file.GetStringOrEmpty("id"),
                Name = file.GetStringOrEmpty("name"),
                Size = file.GetInt64OrZero("size"),
                Busid = file.GetInt64OrZero("busid"),
            };
        }
        return ev;
    }

    /// <summary>
    /// message 可能是 string（含 CQ 码）或段数组。
    /// 返回 (raw, 纯文本)：text 段拼接；image 等非文本段丢弃（v0.2 不做 OCR，图片作业由人工确认）。
    /// </summary>
    internal static (string Raw, string Text) ExtractText(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var msg))
            return ("", "");
        if (msg.ValueKind == JsonValueKind.String)
        {
            var raw = msg.GetString() ?? "";
            return (raw, StripCq(raw));
        }
        if (msg.ValueKind == JsonValueKind.Array)
        {
            var raws = new List<string>();
            var texts = new List<string>();
            foreach (var seg in msg.EnumerateArray())
            {
                if (!seg.TryGetProperty("type", out var segType) || segType.GetString() != "text")
                    continue;
                var t = seg.TryGetProperty("data", out var data)
                    && data.TryGetProperty("text", out var text)
                    ? text.GetString() ?? "" : "";
                raws.Add(t);
                texts.Add(t);
            }
            return (string.Concat(raws), string.Concat(texts));
        }
        return ("", "");
    }

    /// <summary>去掉 [CQ:at,qq=..]、[CQ:image,..] 等 CQ 码，保留可读文本。</summary>
    public static string StripCq(string raw)
    {
        var sb = new System.Text.StringBuilder(raw.Length);
        int i = 0;
        while (i < raw.Length)
        {
            if (raw[i] == '[' && raw.AsSpan(i).StartsWith("[CQ:"))
            {
                int end = raw.IndexOf(']', i);
                if (end < 0) break;
                i = end + 1;
            }
            else sb.Append(raw[i++]);
        }
        return sb.ToString().Trim();
    }

    private static string? GetPropertyOrNull(this JsonElement el, string name)
        => el.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() : null : null;

    private static string? GetStringOrNull(this JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long GetInt64OrZero(this JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt64(out var n) => n,
            JsonValueKind.String when long.TryParse(v.GetString(), out var n) => n,
            _ => 0
        };
    }

    private static string GetStringOrEmpty(this JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
