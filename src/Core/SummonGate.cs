namespace ClassroomEnhancement.Core;

/// <summary>
/// 召唤消息的纯本地预检：紧急词判定 + 通知去重键。
/// AI 二分类是主路径；本类是 AI 超时/失败时的保守降级依据，也是单测的基本盘。
/// </summary>
public static class SummonGate
{
    /// <summary>出现任一词即视为"现在来一下"类紧急召唤。</summary>
    public static readonly string[] UrgentWords = ["现在", "立刻", "马上", "立即", "赶紧"];

    /// <summary>疑似召唤的触发词（宽松，用于从群消息流中捞候选）。</summary>
    public static readonly string[] SummonHints = ["来一下", "过来一下", "到", "去", "找", "叫"];

    public static bool IsUrgent(string text)
        => UrgentWords.Any(text.Contains);

    public static bool LooksLikeSummon(string text)
        => SummonHints.Any(text.Contains);

    /// <summary>去重键：同目标 10 分钟内合并为一条通知。</summary>
    public static string DedupKey(string target, DateTimeOffset now)
        => $"{target}@{now.ToUnixTimeSeconds() / 600}";
}
