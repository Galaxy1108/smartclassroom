namespace SmartClassroom.Core;

/// <summary>
/// 功能开关。**默认全部关闭**：这些能力会读班级 QQ 的消息、自动落课、自动下载文件，
/// 属于有副作用的增强功能，必须由用户显式开启，避免装完就悄悄地动课表或落盘。
/// </summary>
public sealed record FeatureFlags
{
    /// <summary>召唤通知（"xxx 来一下"→ ClassIsland 通知，上课时排队）。</summary>
    public bool Summon { get; init; }

    /// <summary>作业自动录入（AI 整理老师作业并上墙）。</summary>
    public bool Homework { get; init; }

    /// <summary>换课自动处理（解析后交插件校验，合法则落课）。</summary>
    public bool Exchange { get; init; }

    /// <summary>群文件自动归档（按发送者分类下载）。</summary>
    public bool FileArchive { get; init; }

    /// <summary>上课时弹「您可能需要的课件」。</summary>
    public bool CoursewarePopup { get; init; }

    public static FeatureFlags AllDisabled { get; } = new();

    public bool AnyEnabled => Summon || Homework || Exchange || FileArchive || CoursewarePopup;

    /// <summary>给界面显示的一句话摘要。</summary>
    public string Describe()
    {
        var on = new List<string>();
        if (Summon) on.Add("召唤");
        if (Homework) on.Add("作业");
        if (Exchange) on.Add("换课");
        if (FileArchive) on.Add("文件归档");
        if (CoursewarePopup) on.Add("课件弹窗");
        return on.Count == 0 ? "全部关闭（不会对群消息做任何动作）" : "已开启：" + string.Join("、", on);
    }
}
