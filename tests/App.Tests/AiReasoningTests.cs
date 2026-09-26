using Avalonia.Headless.XUnit;
using SmartClassroom.App.ViewModels;
using SmartClassroom.Core;
using SmartClassroom.Core.AI;
using Xunit;

namespace SmartClassroom.App.Tests;

/// <summary>
/// 推理强度设置。
/// 起因：界面显示「可用，但返回为空」—— 实际是推理模型把输出全用在思考上，
/// 最终没有正文。现在默认 minimal 并在设置页可调。
/// </summary>
public sealed class AiReasoningTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "sc-reason-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [AvaloniaFact]
    public void DefaultsToMinimal()
    {
        var vm = new SettingsViewModel(_path);
        Assert.Equal("minimal", vm.AiReasoning);
        Assert.Equal(["minimal", "low", "medium", "high"], vm.ReasoningLevels);
    }

    [AvaloniaFact]
    public void PersistsAcrossReload()
    {
        var vm = new SettingsViewModel(_path) { AiReasoning = "low" };
        Assert.Equal("low", new SettingsViewModel(_path).AiReasoning);
    }

    [Fact]
    public void SidecarOptions_DefaultReasoningIsMinimal()
        => Assert.Equal("minimal", new SidecarOptions { SidecarDir = "/tmp" }.Reasoning);

    [Fact]
    public void AskRequest_IncludesReasoningAndMaxTokens()
    {
        // 请求体要真的把推理强度带上，否则设置形同虚设
        var line = PiAiSidecarClient.BuildRequest("1", "complete", new
        {
            provider = "deepseek",
            model = "deepseek-flash",
            reasoning = "minimal",
            maxTokens = 4096,
            system = "s",
            user = "u"
        });
        Assert.Contains("\"reasoning\":\"minimal\"", line);
        Assert.Contains("\"maxTokens\":4096", line);
    }

    [Fact]
    public void AskRequest_OmitsNullMaxTokens()
    {
        var line = PiAiSidecarClient.BuildRequest("1", "complete", new
        {
            reasoning = "minimal",
            maxTokens = (int?)null
        });
        Assert.DoesNotContain("maxTokens", line);
        Assert.Contains("reasoning", line);
    }
}
