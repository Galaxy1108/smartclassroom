namespace SmartClassroom.Core.AI;

/// <summary>
/// AI 接入引擎。
/// HttpGateway：手写 OpenAI 兼容 HTTP 调用，零依赖，作为兜底与默认。
/// PiAiSidecar：Node 边车跑 pi-ai，拿到 41 个 provider 的模型目录与统一鉴权。
/// </summary>
public enum AiEngine
{
    HttpGateway,
    PiAiSidecar
}

public static class AiEngineParser
{
    public static AiEngine Parse(string? value) => value switch
    {
        "pi-ai" or "pi" or "sidecar" => AiEngine.PiAiSidecar,
        _ => AiEngine.HttpGateway
    };

    public static string ToStorage(this AiEngine engine)
        => engine == AiEngine.PiAiSidecar ? "pi-ai" : "http";
}
