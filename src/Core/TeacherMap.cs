using SmartClassroom.Contracts;

namespace SmartClassroom.Core;

/// <summary>
/// 教师映射：QQ 号 / 群名片 / 昵称 → 老师（姓名 + 科目）。
/// 三键任一命中即认定；未命中保持未知，由调用方决定保守策略。
/// </summary>
public sealed class TeacherMap
{
    private readonly Dictionary<long, Teacher> _byQq = new();
    private readonly Dictionary<string, Teacher> _byName = new(StringComparer.Ordinal);

    public TeacherMap(IEnumerable<Teacher> teachers)
    {
        foreach (var t in teachers)
        {
            _byQq[t.Qq] = t;
            foreach (var alias in t.Aliases)
                _byName[Normalize(alias)] = t;
            _byName[Normalize(t.Name)] = t;
        }
    }

    public Teacher? Resolve(long qq, string? card, string? nickname)
    {
        if (_byQq.TryGetValue(qq, out var t))
            return t;
        if (card is not null && _byName.TryGetValue(Normalize(card), out t))
            return t;
        if (nickname is not null && _byName.TryGetValue(Normalize(nickname), out t))
            return t;
        return null;
    }

    public SenderInfo ToSender(long qq, string? card, string? nickname)
    {
        var teacher = Resolve(qq, card, nickname);
        return new SenderInfo
        {
            UserId = qq,
            Card = card,
            Nickname = nickname,
            TeacherName = teacher?.Name,
            Subject = teacher?.Subject
        };
    }

    private static string Normalize(string s) => s.Trim();
}

/// <summary>教师条目（来自用户配置的映射表）。</summary>
public sealed record Teacher
{
    public required long Qq { get; init; }
    public required string Name { get; init; }
    public required string Subject { get; init; }

    /// <summary>曾用群名片 / 昵称别名。</summary>
    public List<string> Aliases { get; init; } = new();
}
