using SmartClassroom.Core;

namespace SmartClassroom.Core.Tests;

/// <summary>测试用的功能开关：既有用例都在验证"功能已开启"时的行为。</summary>
internal static class TestFlags
{
    public static readonly FeatureFlags AllOn = new()
    {
        Summon = true,
        Homework = true,
        Exchange = true,
        FileArchive = true,
        CoursewarePopup = true,
        Notice = true
    };
}
