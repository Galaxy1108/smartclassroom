using SmartClassroom.Core.QQ;
using Xunit;

namespace SmartClassroom.Core.Tests;

/// <summary>OneBot 解析器：用录制的真实事件 JSON 回放验证。</summary>
public sealed class OneBotParserTests
{
    [Fact]
    public void Parse_GroupMessage_StripsCqAndKeepsSender()
    {
        // 说明：测试运行时把 fixtures 复制到输出目录（见 csproj）。
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "group_message_cq.json"));
        var ev = Assert.IsType<GroupMessageEvent>(OneBotParser.Parse(json));
        Assert.Equal(100200300L, ev.GroupId);
        Assert.Equal(10001L, ev.UserId);
        Assert.Equal("张老师", ev.Card);
        Assert.Equal("张数学", ev.Nickname);
        Assert.DoesNotContain("[CQ:", ev.Text);
        Assert.Contains("小明现在来一下", ev.Text);
    }

    [Fact]
    public void Parse_GroupUpload_ExtractsFile()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "group_upload.json"));
        var ev = Assert.IsType<GroupUploadEvent>(OneBotParser.Parse(json));
        Assert.Equal(100200300L, ev.GroupId);
        Assert.Equal(10002L, ev.UserId);
        Assert.Equal("第三章课件.pptx", ev.File.Name);
        Assert.Equal("/abc-def-123", ev.File.Id);
        Assert.Equal(102L, ev.File.Busid);
    }

    [Fact]
    public void Parse_IrrelevantEvents_ReturnsNull()
    {
        Assert.Null(OneBotParser.Parse("""{"post_type":"meta_event","meta_event_type":"heartbeat"}"""));
        Assert.Null(OneBotParser.Parse("""{"post_type":"message","message_type":"discuss","user_id":1}"""));
    }

    [Fact]
    public void Parse_PrivateMessage_ReturnsPrivateEvent()
    {
        // 老师私聊也可能发"来一下"或作业，所以要认出来（GroupId 固定 0）
        var json = """
            {"post_type":"message","message_type":"private","sub_type":"friend","time":1,"self_id":2,
             "user_id":10001,"message_id":99,"raw_message":"张老师 来一下","message":"张老师 来一下",
             "sender":{"nickname":"张三"}}
            """;

        var ev = Assert.IsType<PrivateMessageEvent>(OneBotParser.Parse(json));

        Assert.Equal(10001L, ev.UserId);
        Assert.Equal(99L, ev.MessageId);
        Assert.Equal(0L, ev.GroupId);            // 私聊没有群号
        Assert.Equal("张老师 来一下", ev.Text);
        Assert.Equal("张三", ev.Nickname);
    }

    [Fact]
    public void StripCq_RemovesCodes()
    {
        Assert.Equal("你好", OneBotParser.StripCq("[CQ:at,qq=1]你好[CQ:face,id=2]"));
    }
}

/// <summary>
/// 动作调用走的是反序列化（事件那条路是 OneBotParser 手写读 snake_case），
/// 所以动作模型里的 snake_case 字段必须有 JsonPropertyName 映射。
/// 踩过的坑：LoginInfoData.UserId 少了映射 → 永远读到 0 → "检测不到 QQ 账号"。
/// </summary>
public sealed class OneBotActionModelTests
{
    [Fact]
    public void LoginInfo_MapsSnakeCaseUserId()
    {
        // SnowLuma 的真实返回
        var json = """{"status":"ok","retcode":0,"data":{"user_id":100000002,"nickname":"测试昵称B"}}""";

        var envelope = System.Text.Json.JsonSerializer.Deserialize<OneBotResponse<LoginInfoData>>(
            json, OneBotJson.Options);

        Assert.NotNull(envelope);
        Assert.True(envelope!.Ok);
        Assert.Equal(100000002, envelope.Data!.UserId);
        Assert.Equal("测试昵称B", envelope.Data.Nickname);
    }

    [Fact]
    public void GroupList_MapsSnakeCaseFields()
    {
        // get_group_list 的真实形状
        var json = """
            {"status":"ok","retcode":0,"data":[
              {"group_id":1077826412,"group_name":"✨AstrBot 4群✨","member_count":321},
              {"group_id":987654321,"group_name":"高二(3)班","member_count":52}]}
            """;

        var envelope = System.Text.Json.JsonSerializer.Deserialize<OneBotResponse<List<GroupInfoData>>>(
            json, OneBotJson.Options);

        Assert.Equal(2, envelope!.Data!.Count);
        Assert.Equal(1077826412, envelope.Data[0].GroupId);
        Assert.Equal("✨AstrBot 4群✨", envelope.Data[0].GroupName);
        Assert.Equal(52, envelope.Data[1].MemberCount);
    }

    [Fact]
    public void FileUrl_MapsUrl()
    {
        var json = """{"status":"ok","retcode":0,"data":{"url":"http://x/f"}}""";
        var envelope = System.Text.Json.JsonSerializer.Deserialize<OneBotResponse<FileUrlData>>(
            json, OneBotJson.Options);
        Assert.Equal("http://x/f", envelope!.Data!.Url);
    }
}

/// <summary>
/// WS 连接必须带 token：SnowLuma 的 WS 服务同样要求鉴权，不带会被直接拒绝
/// （实测表现：WebUI 里"ws-default 0 个客户端"，应用一直"QQ 未连接（重连中…）"，
/// 消息事件一条都收不到）。
/// </summary>
public sealed class OneBotWsUriTests
{
    [Fact]
    public void BuildWsUri_AppendsAccessToken()
    {
        var uri = OneBotClient.BuildWsUri("ws://127.0.0.1:3011", "abc-123");

        Assert.Equal("ws://127.0.0.1:3011/?access_token=abc-123", uri.ToString());
    }

    [Fact]
    public void BuildWsUri_KeepsExistingQuery()
    {
        var uri = OneBotClient.BuildWsUri("ws://127.0.0.1:3011/ws?x=1", "tok");

        Assert.Contains("x=1", uri.ToString());
        Assert.Contains("access_token=tok", uri.ToString());
    }

    [Fact]
    public void BuildWsUri_NoToken_LeavesUrlAlone()
    {
        var uri = OneBotClient.BuildWsUri("ws://127.0.0.1:3011", null);
        Assert.DoesNotContain("access_token", uri.ToString());
        Assert.Equal("127.0.0.1", uri.Host);
        Assert.Equal(3011, uri.Port);
    }

    [Fact]
    public void BuildWsUri_EscapesToken()
        => Assert.Contains("access_token=a%2Bb", OneBotClient.BuildWsUri("ws://x/", "a+b").ToString());
}

/// <summary>
/// 私聊/群里的文件段。实测踩到：SnowLuma 把私聊文件转成**带 file 段的消息**（不是 notice），
/// 而解析器只认文本段 → 整条消息被当成空文本丢掉，用户看到的就是"文件没保存"。
/// </summary>
public sealed class OneBotFileSegmentTests
{
    [Fact]
    public void Parse_PrivateFileSegment_ReturnsFileEvent()
    {
        // SnowLuma 的转换结果：data = { file, file_id, name, size, url, file_hash }
        var json = """
            {"post_type":"message","message_type":"private","time":1,"self_id":2,"user_id":10001,
             "message_id":9,
             "message":[{"type":"file","data":{"file":"cherenkov_animation.html","file_id":"abc",
                        "name":"cherenkov_animation.html","size":7414272,
                        "url":"http://127.0.0.1:3000/get_file?x=1","file_hash":"h"}}],
             "sender":{"nickname":"张老师"}}
            """;

        var ev = Assert.IsType<GroupUploadEvent>(OneBotParser.Parse(json));

        Assert.Equal(0L, ev.GroupId);                       // 私聊：GroupId 为 0
        Assert.Equal(10001L, ev.UserId);
        Assert.Equal("cherenkov_animation.html", ev.File.Name);
        Assert.Equal(7414272, ev.File.Size);
        Assert.True(ev.File.HasUrl);                        // 私聊文件靠这个直链下载
    }

    [Fact]
    public void Parse_GroupFileSegment_ReturnsFileEvent()
    {
        var json = """
            {"post_type":"message","message_type":"group","time":1,"self_id":2,"group_id":100200300,
             "user_id":10001,"message_id":7,
             "message":[{"type":"file","data":{"name":"第三章课件.pptx","size":1024,"file_id":"f1"}}],
             "sender":{"card":"张老师"}}
            """;

        var ev = Assert.IsType<GroupUploadEvent>(OneBotParser.Parse(json));

        Assert.Equal(100200300L, ev.GroupId);
        Assert.Equal("第三章课件.pptx", ev.File.Name);
        Assert.False(ev.File.HasUrl);                       // 群文件没带 url → 走 get_group_file_url
    }

    [Fact]
    public void Parse_TextMessage_StillWorks()
    {
        var json = """
            {"post_type":"message","message_type":"private","time":1,"self_id":2,"user_id":10001,
             "message_id":9,"message":[{"type":"text","data":{"text":"小明来一下"}}],
             "sender":{"nickname":"张老师"}}
            """;

        var ev = Assert.IsType<PrivateMessageEvent>(OneBotParser.Parse(json));
        Assert.Equal("小明来一下", ev.Text);
    }
}

/// <summary>
/// 图片消息不能整条丢掉（用户："图片是被忽略掉，可能认为是富文本消息"）。
/// 老师发的作业/通知截图很常见：能拿到直链就当文件归档，同时给文本一个占位符。
/// </summary>
public sealed class OneBotImageSegmentTests
{
    [Fact]
    public void Image_WithUrl_IsAttachedToMessage()
    {
        var json = """
            {"post_type":"message","message_type":"group","time":1,"self_id":2,"group_id":100200300,
             "user_id":10001,"message_id":42,
             "message":[{"type":"image","data":{"file":"abc.jpg","url":"http://127.0.0.1:3000/get_image?x=1"}}],
             "sender":{"card":"张老师"}}
            """;

        // 图片挂在消息上（交给视觉模型读），不再是"文件事件"
        var ev = Assert.IsType<GroupMessageEvent>(OneBotParser.Parse(json));

        var img = Assert.Single(ev.Images);
        Assert.Equal("http://127.0.0.1:3000/get_image?x=1", img.Url);
        Assert.EndsWith(".jpg", img.Name);
        Assert.Equal("[图片]", ev.Text);
    }

    [Fact]
    public void Image_WithoutUrl_LeavesPlaceholderText()
    {
        var json = """
            {"post_type":"message","message_type":"group","time":1,"self_id":2,"group_id":100200300,
             "user_id":10001,"message_id":42,
             "message":[{"type":"text","data":{"text":"看这个"}},
                        {"type":"image","data":{"file":"abc.jpg"}}],
             "sender":{"card":"张老师"}}
            """;

        var ev = Assert.IsType<GroupMessageEvent>(OneBotParser.Parse(json));

        Assert.Equal("看这个 [图片]", ev.Text);   // 不再是空文本
    }

    [Fact]
    public void MultipleImages_GetCountedPlaceholder()
    {
        var json = """
            {"post_type":"message","message_type":"private","time":1,"self_id":2,"user_id":10001,
             "message_id":42,
             "message":[{"type":"image","data":{"file":"a.jpg"}},{"type":"image","data":{"file":"b.jpg"}}],
             "sender":{"nickname":"张老师"}}
            """;

        var ev = Assert.IsType<PrivateMessageEvent>(OneBotParser.Parse(json));
        Assert.Equal("[2 张图片]", ev.Text);
    }
}

/// <summary>
/// 自己发出去的消息/文件不能被当成"收到的"。
/// 实测反馈："为什么会把我给对方发过去的文件识别为课件" ——
/// SnowLuma 对自发消息用 post_type:"message_sent" 标记，我们以前照单全收。
/// </summary>
public sealed class SelfSentMessageTests
{
    [Fact]
    public void SelfSentPrivateFile_IsIgnored()
    {
        var json = """
            {"post_type":"message_sent","message_type":"private","time":1,"self_id":3768914943,
             "user_id":10001,"message_id":7,
             "message":[{"type":"file","data":{"name":"屏幕录像.mp4","file_id":"v1","size":420000}}],
             "sender":{"nickname":"张老师"}}
            """;

        Assert.Null(OneBotParser.Parse(json));
    }

    [Fact]
    public void SelfSentGroupMessage_IsIgnored()
    {
        var json = """
            {"post_type":"message","message_type":"group","time":1,"self_id":3768914943,
             "group_id":100200300,"user_id":3768914943,"message_id":8,
             "message":[{"type":"text","data":{"text":"我发的"}}],"sender":{}}
            """;

        Assert.Null(OneBotParser.Parse(json));
    }

    [Fact]
    public void IncomingMessage_StillParsed()
    {
        var json = """
            {"post_type":"message","message_type":"private","time":1,"self_id":3768914943,
             "user_id":10001,"message_id":9,
             "message":[{"type":"text","data":{"text":"作业"}}],"sender":{"nickname":"张老师"}}
            """;

        Assert.NotNull(OneBotParser.Parse(json));
    }
}
