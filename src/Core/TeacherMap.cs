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

    public TeacherMap(IEnumerable<Teacher> teachers) => Reload(teachers);

    /// <summary>
    /// 就地重载老师名单。**设置页改了名单要立刻生效** ——
    /// 运行时只在启动时读一次的话，用户加完老师还得重启，看到的现象就是
    /// "我都加了怎么还被当成陌生人"（实测踩到）。
    /// </summary>
    public void Reload(IEnumerable<Teacher> teachers)
    {
        _byQq.Clear();
        _byName.Clear();
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

    /// <summary>老师名单的一句话描述（喂给 AI 做"数学老师→某人"的匹配）。</summary>
    public string Describe(int limit = 12)
    {
        if (_byQq.Count == 0)
            return "（未配置）";
        var items = _byQq.Values
            .Take(limit)
            .Select(t => t.Subject.Length > 0 ? $"{t.Name}（{t.Subject}）" : t.Name);
        var text = string.Join("、", items);
        return _byQq.Count > limit ? text + $"…共 {_byQq.Count} 人" : text;
    }

    /// <summary>这个 QQ 是否在老师名单里（私聊只认名单里的人，陌生人一律忽略）。</summary>
    public bool IsKnown(long qq) => _byQq.ContainsKey(qq);

    /// <summary>名单人数（为 0 时不做陌生人过滤，避免用户没配名单就什么都不处理）。</summary>
    public int Count => _byQq.Count;

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
