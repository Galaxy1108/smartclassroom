using SmartClassroom.Core;
using Xunit;

namespace SmartClassroom.Core.Tests;

public sealed class SummonGateTests
{
    [Theory]
    [InlineData("小明现在来一下", true)]
    [InlineData("立刻到办公室", true)]
    [InlineData("马上过来", true)]
    [InlineData("请立即来一趟", true)]
    [InlineData("小明来一下", false)]
    [InlineData("下课后来办公室", false)]
    public void IsUrgent_ClassifiesCorrectly(string text, bool expected)
    {
        Assert.Equal(expected, SummonGate.IsUrgent(text));
    }

    [Fact]
    public void DedupKey_MergesWithinTenMinutes()
    {
        var t0 = new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.FromHours(8));
        Assert.Equal(SummonGate.DedupKey("小明", t0), SummonGate.DedupKey("小明", t0.AddMinutes(9)));
        Assert.NotEqual(SummonGate.DedupKey("小明", t0), SummonGate.DedupKey("小明", t0.AddMinutes(11)));
        Assert.NotEqual(SummonGate.DedupKey("小明", t0), SummonGate.DedupKey("小红", t0));
    }
}

public sealed class TeacherMapTests
{
    private static TeacherMap Map() => new([
        new Teacher { Qq = 10001, Name = "张老师", Subject = "数学", Aliases = ["张数学"] },
    ]);

    [Fact]
    public void Resolve_ByQqNumber()
    {
        var s = Map().ToSender(10001, "随便什么名片", null);
        Assert.Equal("张老师", s.TeacherName);
        Assert.Equal("数学", s.Subject);
    }

    [Fact]
    public void Resolve_ByCardOrNickname()
    {
        Assert.Equal("张老师", Map().ToSender(99999, "张老师", null).TeacherName);
        Assert.Equal("张老师", Map().ToSender(99999, null, "张数学").TeacherName);
    }

    [Fact]
    public void Resolve_UnknownStaysUnknown()
    {
        var s = Map().ToSender(99999, "陌生人", null);
        Assert.Null(s.TeacherName);
        Assert.Null(s.Subject);
    }
}

/// <summary>
/// 老师名单热重载：设置页加完老师要**立刻**生效。
/// 以前运行时只在启动时读一次名单，用户加完还被当陌生人 ——
/// 现象是"我都加了怎么还被忽略"。
/// </summary>
public sealed class TeacherMapReloadTests
{
    [Fact]
    public void Reload_TakesEffectImmediately()
    {
        var map = new TeacherMap([new Teacher { Qq = 10001, Name = "张老师", Subject = "数学" }]);
        Assert.False(map.IsKnown(3667627856));

        map.Reload([
            new Teacher { Qq = 10001, Name = "张老师", Subject = "数学" },
            new Teacher { Qq = 3667627856, Name = "Strong猪", Subject = "道法" }
        ]);

        Assert.True(map.IsKnown(3667627856));
        Assert.Equal("Strong猪", map.ToSender(3667627856, null, null).TeacherName);
        Assert.Equal("道法", map.ToSender(3667627856, null, null).Subject);
    }

    [Fact]
    public void Reload_RemovesDeletedTeachers()
    {
        var map = new TeacherMap([new Teacher { Qq = 10001, Name = "张老师", Subject = "数学" }]);

        map.Reload([]);

        Assert.False(map.IsKnown(10001));
        Assert.Equal(0, map.Count);
    }
}
