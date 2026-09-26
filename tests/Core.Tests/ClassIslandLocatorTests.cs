using SmartClassroom.Core;
using Xunit;

namespace SmartClassroom.Core.Tests;

public sealed class ClassIslandLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sc-ci-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Fact]
    public void RelativeTokenPath_MatchesClassIslandLayout()
    {
        // ClassIsland: <root>/Config/Plugins/<plugin-id>/bridge.token
        Assert.Equal(Path.Combine("Config", "Plugins", "smartclassroom.bridge", "bridge.token"),
            ClassIslandLocator.RelativeTokenPath);
    }

    [Fact]
    public void FindTokenFile_FindsInCandidateRoot()
    {
        var dir = Path.Combine(_root, "Config", "Plugins", ClassIslandLocator.PluginId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ClassIslandLocator.TokenFileName), "abc123\n");

        var file = ClassIslandLocator.FindTokenFile([_root]);
        Assert.NotNull(file);
        Assert.Equal("abc123", ClassIslandLocator.TryReadToken([_root]));
    }

    [Fact]
    public void FindTokenFile_FirstMatchWins()
    {
        var empty = Path.Combine(_root, "empty");
        var good = Path.Combine(_root, "good");
        Directory.CreateDirectory(Path.Combine(good, "Config", "Plugins", ClassIslandLocator.PluginId));
        File.WriteAllText(
            Path.Combine(good, "Config", "Plugins", ClassIslandLocator.PluginId, ClassIslandLocator.TokenFileName),
            "tok");
        Directory.CreateDirectory(empty);

        Assert.Equal("tok", ClassIslandLocator.TryReadToken([empty, good]));
    }

    [Fact]
    public void TryReadToken_NullWhenNothingFound()
    {
        Directory.CreateDirectory(_root);
        Assert.Null(ClassIslandLocator.TryReadToken([_root]));
    }

    [Fact]
    public void ExplainSearch_ListsProbedPathsWhenMissing()
    {
        Directory.CreateDirectory(_root);
        var text = ClassIslandLocator.ExplainSearch([_root]);
        Assert.Contains("未找到", text);
        Assert.Contains(ClassIslandLocator.PluginId, text);
    }

    [Fact]
    public void CandidateRoots_NonEmptyAndUnique()
    {
        var roots = ClassIslandLocator.CandidateRoots();
        Assert.NotEmpty(roots);
        Assert.Equal(roots.Count, roots.Distinct().Count());
    }
}
