using SmartClassroom.Core.AI;
using Xunit;

namespace SmartClassroom.Core.Tests;

public sealed class AiEngineTests
{
    [Theory]
    [InlineData("pi-ai", AiEngine.PiAiSidecar)]
    [InlineData("pi", AiEngine.PiAiSidecar)]
    [InlineData("sidecar", AiEngine.PiAiSidecar)]
    [InlineData("http", AiEngine.HttpGateway)]
    [InlineData("", AiEngine.HttpGateway)]
    [InlineData(null, AiEngine.HttpGateway)]
    [InlineData("随便什么", AiEngine.HttpGateway)]
    public void Parse_MapsToEngine(string? stored, AiEngine expected)
        => Assert.Equal(expected, AiEngineParser.Parse(stored));

    [Fact]
    public void ToStorage_RoundTrips()
    {
        foreach (var engine in new[] { AiEngine.HttpGateway, AiEngine.PiAiSidecar })
            Assert.Equal(engine, AiEngineParser.Parse(engine.ToStorage()));
    }

    [Fact]
    public void NodeRuntime_ReportsMissingScriptForEmptyDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sc-nodes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var text = NodeRuntime.DescribeReadiness(dir);
            Assert.Contains("缺少边车脚本", text);
            Assert.False(NodeRuntime.DependenciesInstalled(dir));
        }
        finally { Directory.Delete(dir, true); }
    }
}
