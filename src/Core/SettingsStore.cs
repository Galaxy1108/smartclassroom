using System.Text.Json;

namespace SmartClassroom.Core;

/// <summary>应用设置（AI / OneBot / 归档 / 教师映射），JSON 落盘。
/// 默认路径 LocalApplicationData/SmartClassroom/settings.json；密钥明文存放（单机教室机，文档注明）。</summary>
public sealed class AppSettings
{
    public string AiBaseUrl { get; set; } = "";
    public string AiApiKey { get; set; } = "";
    public string AiModel { get; set; } = "";
    public string OneBotHttp { get; set; } = "http://127.0.0.1:3000";
    public string OneBotWs { get; set; } = "ws://127.0.0.1:3001";
    public string OneBotToken { get; set; } = "";
    public List<long> GroupIds { get; set; } = [];
    public string ArchiveRoot { get; set; } = "";
    public string PluginToken { get; set; } = "";
    public List<Teacher> Teachers { get; set; } = [];
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SmartClassroom", "settings.json");

    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings();
        }
        catch { /* 损坏则回默认 */ }
        return new AppSettings();
    }

    public static void Save(AppSettings settings, string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(settings, Json));
    }
}
