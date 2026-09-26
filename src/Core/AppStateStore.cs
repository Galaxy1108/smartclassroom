using System.Text.Json;
using SmartClassroom.Contracts;

namespace SmartClassroom.Core;

/// <summary>需要跨重启保留的状态。</summary>
public sealed class PersistedState
{
    public List<HomeworkItem> Homework { get; set; } = [];
    public List<PendingItem> Pending { get; set; } = [];
    public List<ActivityEntry> Feed { get; set; } = [];
}

/// <summary>
/// 状态落盘（JSON，非 SQLite）。
/// 选择 JSON 的理由：数据量小（作业每天几十条、事件几百条）、零额外依赖、
/// 不引入 SQLite 的原生库（与"插件纯托管"的取舍一致）、出问题能直接打开看。
/// 写入用「临时文件 + 原子替换」，避免写一半断电留下坏文件。
/// </summary>
public static class AppStateStore
{
    private static readonly JsonSerializerOptions Json = ContractsJson.Options;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SmartClassroom", "state.json");

    public static PersistedState Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path))
                return new PersistedState();
            return JsonSerializer.Deserialize<PersistedState>(File.ReadAllText(path), Json)
                   ?? new PersistedState();
        }
        catch
        {
            // 坏文件不至于让应用起不来；保留坏文件供排查。
            TryBackup(path);
            return new PersistedState();
        }
    }

    public static void Save(PersistedState state, string? path = null)
    {
        path ??= DefaultPath;
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, Json));
        // 原子替换：先写 tmp，再覆盖目标。
        File.Move(tmp, path, overwrite: true);
    }

    private static void TryBackup(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Copy(path, path + ".bad", overwrite: true);
        }
        catch { /* 备份失败不影响启动 */ }
    }
}
