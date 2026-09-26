using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartClassroom.Contracts;

/// <summary>
/// 契约序列化约定：写出用 camelCase，读入大小写不敏感（兼容旧文件/宽松对端）。
/// Events.cs 里声明的就是这个约定，此前只有注释没有落地，导致发出去的是 PascalCase。
/// </summary>
public static class ContractsJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        // 枚举按字符串传输：跨进程契约里用数字很脆（枚举成员一调整含义就漂移）。
        // allowIntegerValues 保留对旧数字载荷的兼容。
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: true) }
    };
}
