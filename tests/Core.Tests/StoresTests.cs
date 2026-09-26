using SmartClassroom.Contracts;
using Xunit;

namespace SmartClassroom.Core.Tests;

public sealed class StoresTests
{
    private static HomeworkItem Hw(string subject, DateOnly date, params string[] items) => new()
    {
        HomeworkId = Guid.NewGuid().ToString(),
        Subject = subject, Date = date, Items = items.ToList(),
        Sender = new SenderInfo { UserId = 1 },
        Source = new MessageRef { GroupId = 1, MessageId = 1 }
    };

    [Fact]
    public void HomeworkStore_MergesSameSubjectDay()
    {
        var store = new HomeworkStore();
        var d = new DateOnly(2026, 9, 25);
        store.AddOrMerge(Hw("数学", d, "练习册P10"));
        var merged = store.AddOrMerge(Hw("数学", d, "练习册P10", "试卷一张"));
        Assert.Single(store.All);
        Assert.Equal(2, merged.Items.Count);
    }

    [Fact]
    public void HomeworkStore_KeepsDifferentDaysSeparate()
    {
        var store = new HomeworkStore();
        store.AddOrMerge(Hw("数学", new DateOnly(2026, 9, 25), "a"));
        store.AddOrMerge(Hw("数学", new DateOnly(2026, 9, 26), "b"));
        Assert.Equal(2, store.All.Count);
        Assert.Single(store.ForDate(new DateOnly(2026, 9, 25)));
    }

    [Fact]
    public void ActivityFeed_CapsAndOrders()
    {
        var feed = new ActivityFeed(3);
        feed.Append("summon", "t1", "d1");
        feed.Append("homework", "t2", "d2");
        feed.Append("exchange", "t3", "d3");
        feed.Append("file", "t4", "d4");
        Assert.Equal(3, feed.Entries.Count);
        Assert.Equal("t4", feed.Entries[0].Title);
    }

    [Fact]
    public void ActivityFeed_KeepsSeverity_AndCanBeCleared()
    {
        var feed = new ActivityFeed();
        feed.Append("crash", "后台任务异常", "boom", ActivitySeverity.Error);
        feed.Append("file", "已归档：a.pptx", "", ActivitySeverity.Success);
        feed.Append("summon", "普通记录", "x");                        // 默认 Info

        // 时间线是"新的在前"
        Assert.Equal(ActivitySeverity.Info, feed.Entries[0].Severity);
        Assert.Equal(ActivitySeverity.Success, feed.Entries[1].Severity);
        Assert.Equal(ActivitySeverity.Error, feed.Entries[2].Severity);

        feed.Clear();
        Assert.Empty(feed.Entries);
    }

    [Fact]
    public void ActivityFeed_Clear_ThenAppend_StartsFresh()
    {
        var feed = new ActivityFeed(3);
        feed.Append("a", "t1", "d1");
        feed.Clear();
        feed.Append("b", "t2", "d2");
        Assert.Equal("t2", Assert.Single(feed.Entries).Title);
    }

    [Fact]
    public void SettingsStore_Roundtrips()
    {
        var path = Path.Combine(Path.GetTempPath(), "sc-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var s = new AppSettings { AiModel = "m", GroupIds = [123], Teachers = [new Teacher { Qq = 1, Name = "张", Subject = "数" }] };
            SettingsStore.Save(s, path);
            var back = SettingsStore.Load(path);
            Assert.Equal("m", back.AiModel);
            Assert.Equal([123], back.GroupIds);
            Assert.Single(back.Teachers);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SettingsStore_MissingFile_ReturnsDefaults()
    {
        var s = SettingsStore.Load("/nonexistent/sc-settings.json");
        Assert.Equal("http://127.0.0.1:3000", s.OneBotHttp);
    }
}
