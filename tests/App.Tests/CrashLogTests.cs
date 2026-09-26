using Xunit;

namespace SmartClassroom.App.Tests;

/// <summary>
/// 崩溃日志的文本化。
/// 之前时间线里只显示 AggregateException 的外层消息——"有任务的异常没人观察"，
/// 完全看不出是什么炸了（真实案例：托盘 D-Bus 注册失败）。
/// </summary>
public sealed class CrashLogTests
{
    [Fact]
    public void Describe_UnwrapsAggregateException_ToTheRealCause()
    {
        var inner = new InvalidOperationException("org.freedesktop.DBus.Error.ServiceUnknown: The name is not activatable");
        var aggregate = new AggregateException("A Task's exception(s) were not observed either by Waiting on the Task or accessing its Exception property.",
            inner);

        var text = SmartClassroom.App.App.Describe(aggregate);

        Assert.Contains("InvalidOperationException", text);
        Assert.Contains("ServiceUnknown", text);
        Assert.DoesNotContain("were not observed", text);   // 外层那句噪音要去掉
    }

    [Fact]
    public void Describe_FlattensNestedAggregates()
    {
        var deep = new AggregateException(new AggregateException(new TimeoutException("超时了")));
        Assert.Contains("TimeoutException", SmartClassroom.App.App.Describe(deep));
        Assert.Contains("超时了", SmartClassroom.App.App.Describe(deep));
    }

    [Fact]
    public void Describe_PlainException_KeepsTypeAndMessage()
    {
        var text = SmartClassroom.App.App.Describe(new ArgumentNullException("path"));
        Assert.Contains("ArgumentNullException", text);
        Assert.Contains("path", text);
    }
}
