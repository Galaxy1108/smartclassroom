using System.Runtime.CompilerServices;
using SmartClassroom.Core.AI;

namespace SmartClassroom.Core.Tests;

/// <summary>
/// 测试期的全局设置：AI 重试等待置 0。
/// 否则每个"AI 失败"用例都要白等 1s+3s（实测把整个 Core 套件从 2 秒拖到 92 秒）。
/// </summary>
public static class TestSetup
{
    [ModuleInitializer]
    public static void Initialize() => AiRetry.DelayScale = 0;
}
