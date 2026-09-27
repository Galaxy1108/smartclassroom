namespace SmartClassroom.Core.AI;

/// <summary>
/// AI 调用抽象：同一套 prompt/解析上层，底层可换实现。
/// 目前两个实现：<see cref="AiGateway"/>（手写 HTTP，零依赖，兜底）与
/// <see cref="PiAiSidecarClient"/>（Node 边车跑 pi-ai，拿 provider/model 目录与多厂商）。
/// </summary>
public interface IAiClient
{
    /// <param name="onProgress">
    /// 进度回调：调用失败/正在重试这类**中间状态**都通过它上报，
    /// 让界面能把"AI 请求失败，正在重试（第 2 次）"实时写在任务进度下面
    /// （用户明确要求：失败也要实时可见，并且要重试）。
    /// </param>
    /// <param name="images">随消息一起发给模型的图片（视觉模型能读截图里的字）。</param>
    Task<string> AskAsync(string system, string user, CancellationToken cancel = default,
        Action<string>? onProgress = null, IReadOnlyList<AiImage>? images = null);
}

/// <summary>
/// AI 调用重试策略：opencode-go 之类的端点时不时抽风（实测一次请求挂了 76 秒），
/// 所以失败要重试几次，并且每次都把状态报给界面。
/// </summary>
public static class AiRetry
{
    public const int MaxAttempts = 3;

    /// <summary>重试等待的缩放系数。测试里置 0，避免失败用例白等好几秒。</summary>
    public static double DelayScale { get; set; } = 1.0;

    public static TimeSpan DelayFor(int attempt)
    {
        var seconds = attempt switch
        {
            1 => 1,
            2 => 3,
            _ => 5
        };
        return TimeSpan.FromSeconds(seconds * DelayScale);
    }

    /// <summary>把异常压成一行短说明（进度里显示用）。</summary>
    public static string Short(Exception ex)
    {
        var text = ex.Message.Replace('\n', ' ').Trim();
        return text.Length <= 80 ? text : text[..80] + "…";
    }
}

/// <summary>发给模型的图片（base64 + MIME）。老师发的作业/通知截图靠它读懂。</summary>
public sealed record AiImage(string Data, string MimeType)
{
    public const int MaxBytes = 4 * 1024 * 1024;   // 单张上限 4MB
    public const int MaxCount = 3;                 // 一条消息最多 3 张
}
