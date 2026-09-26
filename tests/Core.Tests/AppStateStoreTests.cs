using SmartClassroom.Contracts;
using SmartClassroom.Core;
using Xunit;

namespace SmartClassroom.Core.Tests;

/// <summary>状态落盘：作业 / 待处理 / 事件跨重启保留。</summary>
public sealed class AppStateStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "sc-state-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        foreach (var p in new[] { _path, _path + ".tmp", _path + ".bad" })
            if (File.Exists(p)) File.Delete(p);
    }

    private static HomeworkItem Hw() => new()
    {
        HomeworkId = "h1", Subject = "数学", Date = new DateOnly(2026, 9, 25),
        Items = ["练习册P10", "试卷一张"], Due = "明天",
        Sender = new SenderInfo { UserId = 10001, TeacherName = "张老师" },
        Source = new MessageRef { GroupId = 1, MessageId = 7 }
    };

    private static PendingItem Pending() => new()
    {
        Id = "p1", Kind = "exchange", Title = "换课解析失败",
        RawText = "明天第三节和今天第五节换", Reason = "AI 超时",
        Sender = new SenderInfo { UserId = 10001, TeacherName = "张老师" },
        Source = new MessageRef { GroupId = 1, MessageId = 8 },
        CreatedAt = DateTimeOffset.Parse("2026-09-25T10:00:00+08:00"),
        RetryCount = 2
    };

    [Fact]
    public void RoundTrip_PreservesEverything()
    {
        AppStateStore.Save(new PersistedState
        {
            Homework = [Hw()],
            Pending = [Pending()],
            Feed = [new ActivityEntry(DateTimeOffset.Parse("2026-09-25T10:00:00+08:00"), "summon", "标题", "细节")]
        }, _path);

        var back = AppStateStore.Load(_path);

        var hw = Assert.Single(back.Homework);
        Assert.Equal("数学", hw.Subject);
        Assert.Equal(new DateOnly(2026, 9, 25), hw.Date);
        Assert.Equal(2, hw.Items.Count);
        Assert.Equal("明天", hw.Due);
        Assert.Equal("张老师", hw.Sender.TeacherName);

        var p = Assert.Single(back.Pending);
        Assert.Equal("exchange", p.Kind);
        Assert.Equal(2, p.RetryCount);          // 重试计数也要留住，否则会把已经失败两次的当新问题
        Assert.Equal(8, p.Source.MessageId);   // Pending 用的是 MessageId=8

        var f = Assert.Single(back.Feed);
        Assert.Equal("summon", f.Kind);
    }

    [Fact]
    public void MissingFile_ReturnsEmpty()
    {
        var s = AppStateStore.Load(Path.Combine(Path.GetTempPath(), "sc-nope-" + Guid.NewGuid().ToString("N") + ".json"));
        Assert.Empty(s.Homework);
        Assert.Empty(s.Pending);
        Assert.Empty(s.Feed);
    }

    [Fact]
    public void CorruptFile_DoesNotThrow_AndKeepsBackup()
    {
        File.WriteAllText(_path, "{ this is not json");
        var s = AppStateStore.Load(_path);
        Assert.Empty(s.Homework);
        Assert.True(File.Exists(_path + ".bad"), "坏文件应留一份 .bad 供排查");
    }

    [Fact]
    public void Save_IsAtomic_NoTempLeftBehind()
    {
        AppStateStore.Save(new PersistedState { Homework = [Hw()] }, _path);
        Assert.True(File.Exists(_path));
        Assert.False(File.Exists(_path + ".tmp"), "临时文件应已被替换掉");
    }

    [Fact]
    public void Stores_RestoreFromState()
    {
        var homework = new HomeworkStore();
        var pending = new PendingStore();
        var feed = new ActivityFeed();

        homework.ReplaceAll([Hw()]);
        pending.ReplaceAll([Pending()]);
        feed.ReplaceAll([new ActivityEntry(DateTimeOffset.Now.AddMinutes(-1), "a", "旧", ""),
                         new ActivityEntry(DateTimeOffset.Now, "b", "新", "")]);

        Assert.Single(homework.All);
        Assert.Single(pending.All);
        Assert.Equal(2, feed.Entries.Count);
        Assert.Equal("新", feed.Entries[0].Title);   // 仍按时间倒序
    }

    [Fact]
    public void FeedReplaceAll_RespectsCapacity()
    {
        var feed = new ActivityFeed(capacity: 2);
        feed.ReplaceAll(Enumerable.Range(0, 5)
            .Select(i => new ActivityEntry(DateTimeOffset.Now.AddSeconds(i), "k", $"t{i}", "")));
        Assert.Equal(2, feed.Entries.Count);
        Assert.Equal("t4", feed.Entries[0].Title);
    }
}
