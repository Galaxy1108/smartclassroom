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
        Assert.Null(OneBotParser.Parse("""{"post_type":"message","message_type":"private","user_id":1}"""));
    }

    [Fact]
    public void StripCq_RemovesCodes()
    {
        Assert.Equal("你好", OneBotParser.StripCq("[CQ:at,qq=1]你好[CQ:face,id=2]"));
    }
}
