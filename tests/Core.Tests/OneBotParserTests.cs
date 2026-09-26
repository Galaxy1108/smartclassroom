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
