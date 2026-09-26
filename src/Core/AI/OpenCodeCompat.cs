namespace SmartClassroom.Core.AI;

/// <summary>
/// opencode.ai 端点（OpenCode Zen / Go）的兼容处理。
///
/// 它要求每个请求带 <c>x-opencode-session</c> 做路由，否则直接返回
/// 400 <c>MissingSessionID</c>。pi-ai 边车对内置 provider（opencode / opencode-go）
/// 会在给定 <c>sessionId</c> 时自动补这个头；而这里的内置直连要自己补。
/// </summary>
public static class OpenCodeCompat
{
    public const string SessionHeader = "x-opencode-session";

    /// <summary>是不是 opencode.ai 的端点（含子域）。</summary>
    public static bool IsOpenCodeEndpoint(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            return false;
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri))
            return false;
        var host = uri.Host;
        return host.Equals("opencode.ai", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".opencode.ai", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>会话 id：用于路由亲和（同一个进程稳定复用一个）。</summary>
    public static string NewSessionId() => "smartclassroom-" + Guid.NewGuid().ToString("N");
}
