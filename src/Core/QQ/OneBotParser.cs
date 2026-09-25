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
        if (root.GetPropertyOrNull("message_type") != "group")
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
        (ev.RawMessage, ev.Text) = ExtractText(root);
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
