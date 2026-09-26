namespace SmartClassroom.Core.AI;

/// <summary>
/// AI 调用抽象：同一套 prompt/解析上层，底层可换实现。
/// 目前两个实现：<see cref="AiGateway"/>（手写 HTTP，零依赖，兜底）与
/// <see cref="PiAiSidecarClient"/>（Node 边车跑 pi-ai，拿 provider/model 目录与多厂商）。
/// </summary>
public interface IAiClient
{
    Task<string> AskAsync(string system, string user, CancellationToken cancel = default);
}
