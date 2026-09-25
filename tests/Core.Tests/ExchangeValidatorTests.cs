using SmartClassroom.Contracts;
using Xunit;

namespace SmartClassroom.Core.Tests;

public sealed class ExchangeValidatorTests
{
    private static readonly DateOnly Today = new(2026, 9, 25);

    private static Dictionary<DateOnly, ClassDaySnapshot> Days() => new()
    {
        [Today] = new ClassDaySnapshot
        {
            Date = Today,
            Periods = [new PeriodSlot { Index = 1, Subject = "语文" }, new PeriodSlot { Index = 2, Subject = "数学" }]
        },
        [Today.AddDays(1)] = new ClassDaySnapshot
        {
            Date = Today.AddDays(1),
            Periods = [new PeriodSlot { Index = 1, Subject = "英语" }, new PeriodSlot { Index = 2, Subject = "物理" }]
        }
    };

    private static ExchangeRequest Req(ExchangeKind kind, DateOnly from, int fp, DateOnly? to = null, int tp = 0, string? ns = null, double conf = 0.9)
        => new()
        {
            RequestId = "r1", Kind = kind,
            From = new ClassSlot { Date = from, PeriodIndex = fp },
            To = to is null ? null : new ClassSlot { Date = to.Value, PeriodIndex = tp },
            NewSubject = ns, RawText = "test",
            Sender = new SenderInfo { UserId = 1 },
            Source = new MessageRef { GroupId = 1, MessageId = 1 },
            Confidence = conf
        };

    [Fact]
    public void Swap_Legal()
    {
        var v = ExchangeValidator.Validate(Req(ExchangeKind.Swap, Today, 1, Today, 2), Days(), Today);
        Assert.True(v.Legal);
        Assert.Contains("对调", v.Message);
    }

    [Fact]
    public void Swap_MissingTargetPeriod_Illegal()
    {
        var v = ExchangeValidator.Validate(Req(ExchangeKind.Swap, Today, 1, Today, 9), Days(), Today);
        Assert.False(v.Legal);
        Assert.Contains("第9节不存在", v.Message);
    }

    [Fact]
    public void Swap_TaughtPeriod_Illegal()
    {
        var v = ExchangeValidator.Validate(Req(ExchangeKind.Swap, Today, 1, Today, 2), Days(), Today,
            isPeriodFuture: _ => false);
        Assert.False(v.Legal);
        Assert.Contains("已上过", v.Message);
    }

    [Fact]
    public void Replace_Legal()
    {
        var v = ExchangeValidator.Validate(Req(ExchangeKind.Replace, Today, 2, ns: "化学"), Days(), Today);
        Assert.True(v.Legal);
        Assert.Contains("化学", v.Message);
    }

    [Fact]
    public void CrossDay_BothDaysKnown_Legal()
    {
        var v = ExchangeValidator.Validate(Req(ExchangeKind.CrossDay, Today.AddDays(1), 1, Today, 2), Days(), Today);
        Assert.True(v.Legal);
        Assert.Contains("临时层", v.Message);
    }

    [Fact]
    public void CrossDay_UnknownDay_Illegal()
    {
        var v = ExchangeValidator.Validate(Req(ExchangeKind.CrossDay, Today.AddDays(5), 1, Today, 2), Days(), Today);
        Assert.False(v.Legal);
    }

    [Fact]
    public void PastDate_Illegal()
    {
        var v = ExchangeValidator.Validate(Req(ExchangeKind.Replace, Today.AddDays(-1), 1, ns: "化学"), Days(), Today);
        Assert.False(v.Legal);
        Assert.Contains("已过去", v.Message);
    }

    [Fact]
    public void LowConfidence_Illegal()
    {
        var v = ExchangeValidator.Validate(Req(ExchangeKind.Swap, Today, 1, Today, 2, conf: 0.2), Days(), Today);
        Assert.False(v.Legal);
        Assert.Contains("置信度", v.Message);
    }
}
