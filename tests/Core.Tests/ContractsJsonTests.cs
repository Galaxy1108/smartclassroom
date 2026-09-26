using System.Text.Json;
using SmartClassroom.Contracts;
using Xunit;

namespace SmartClassroom.Core.Tests;

/// <summary>跨进程契约的序列化约定：camelCase 属性名 + 字符串枚举，且能被自己读回。</summary>
public sealed class ContractsJsonTests
{
    private static ExchangeRequest Sample() => new()
    {
        RequestId = "r1",
        Kind = ExchangeKind.CrossDay,
        From = new ClassSlot { Date = new DateOnly(2026, 9, 25), PeriodIndex = 3, Subject = "数学" },
        To = new ClassSlot { Date = new DateOnly(2026, 9, 26), PeriodIndex = 5 },
        NewSubject = null,
        RawText = "明早第三节和今天第五节换",
        Sender = new SenderInfo { UserId = 10001, TeacherName = "张老师" },
        Source = new MessageRef { GroupId = 1, MessageId = 7 },
        Confidence = 0.9
    };

    [Fact]
    public void Properties_AreCamelCase()
    {
        var json = JsonSerializer.Serialize(Sample(), ContractsJson.Options);
        Assert.Contains("\"requestId\"", json);
        Assert.Contains("\"periodIndex\"", json);
        Assert.DoesNotContain("\"RequestId\"", json);
    }

    [Fact]
    public void Enums_AreStrings_NotNumbers()
    {
        var json = JsonSerializer.Serialize(Sample(), ContractsJson.Options);
        Assert.Contains("\"kind\":\"crossDay\"", json);
        // 明确的负例：数字枚举会在成员调整时静默改变含义
        Assert.DoesNotContain("\"kind\":2", json);
    }

    [Fact]
    public void RoundTrip_PreservesEverything()
    {
        var original = Sample();
        var json = JsonSerializer.Serialize(original, ContractsJson.Options);
        var back = JsonSerializer.Deserialize<ExchangeRequest>(json, ContractsJson.Options);
        Assert.Equal(original.Kind, back!.Kind);
        Assert.Equal(original.From.PeriodIndex, back.From.PeriodIndex);
        Assert.Equal(original.To!.PeriodIndex, back.To.PeriodIndex);
        Assert.Equal(original.Sender.TeacherName, back.Sender.TeacherName);
        Assert.Equal(original.Confidence, back.Confidence);
    }

    [Fact]
    public void LegacyPascalCaseAndNumericEnum_StillReadable()
    {
        // 旧版本写出的 PascalCase + 数字枚举载荷要能读（向前兼容）
        const string legacy = """
        {"RequestId":"r9","Kind":2,"From":{"Date":"2026-09-25","PeriodIndex":1},
         "RawText":"x","Sender":{"UserId":1},"Source":{"GroupId":1,"MessageId":1},"Confidence":0.5}
        """;
        var back = JsonSerializer.Deserialize<ExchangeRequest>(legacy, ContractsJson.Options);
        Assert.NotNull(back);
        Assert.Equal(ExchangeKind.CrossDay, back!.Kind);
        Assert.Equal(1, back.From.PeriodIndex);
    }

    [Fact]
    public void Nulls_AreOmitted()
    {
        var json = JsonSerializer.Serialize(Sample(), ContractsJson.Options);
        Assert.DoesNotContain("newSubject", json);
    }

    [Fact]
    public void PluginStatus_RoundTrips()
    {
        var json = JsonSerializer.Serialize(new PluginStatus { PluginVersion = "0.3.0", ClassPlanLoaded = true },
            ContractsJson.Options);
        Assert.Contains("\"pluginVersion\"", json);
        var back = JsonSerializer.Deserialize<PluginStatus>(json, ContractsJson.Options);
        Assert.Equal("0.3.0", back!.PluginVersion);
        Assert.True(back.ClassPlanLoaded);
    }
}
