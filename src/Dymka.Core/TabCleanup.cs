namespace Dymka.Core;

/// <summary>Сведения о вкладке, нужные для очистки и поиска (без зависимости от WPF и движка).</summary>
public sealed record TabInfo(int Id, string Container, string Url, string Title, bool IsStart, bool IsPinned,
    bool IsActive, bool LoadFailed, DateTime LastActive);

public enum CleanupGroup { Duplicates, Empty, Errors, Old }

public sealed record CleanupItem(TabInfo Tab, CleanupGroup Group);

/// <summary>Что можно закрыть: дубликаты, пустые вкладки, страницы с ошибкой, давно не открывавшиеся.</summary>
public static class TabCleanup
{
    public const int DefaultOldDays = 7;

    public static string GroupTitle(CleanupGroup g) => g switch
    {
        CleanupGroup.Duplicates => "Дубликаты",
        CleanupGroup.Empty => "Пустые новые вкладки",
        CleanupGroup.Errors => "Страницы с ошибкой загрузки",
        _ => "Давно не открывались",
    };

    /// <summary>Отмечена ли группа по умолчанию («давно не открывались» - нет: это решает пользователь).</summary>
    public static bool CheckedByDefault(CleanupGroup g) => g != CleanupGroup.Old;

    /// <summary>Каждая вкладка попадает не больше чем в одну группу (в первую подходящую).
    /// Закреплённые и активная вкладка не закрываются никогда.</summary>
    public static List<CleanupItem> Find(IEnumerable<TabInfo> tabs, DateTime now, int oldDays = DefaultOldDays)
    {
        var list = tabs.ToList();
        var result = new List<CleanupItem>();
        var taken = new HashSet<int>();

        void Add(TabInfo t, CleanupGroup g)
        {
            if (t.IsPinned || t.IsActive || !taken.Add(t.Id)) return;
            result.Add(new CleanupItem(t, g));
        }

        // дубликаты: полностью одинаковый адрес в одном контейнере; остаётся закреплённая, активная или самая свежая
        foreach (var group in list.Where(t => !t.IsStart && t.Url.Length > 0)
                     .GroupBy(t => (t.Container, t.Url))
                     .Where(g => g.Count() > 1))
        {
            var keep = group.OrderByDescending(t => t.IsPinned).ThenByDescending(t => t.IsActive)
                .ThenByDescending(t => t.LastActive).First();
            foreach (var t in group.Where(t => t != keep).OrderBy(t => t.LastActive)) Add(t, CleanupGroup.Duplicates);
        }

        foreach (var t in list.Where(t => t.IsStart)) Add(t, CleanupGroup.Empty);
        foreach (var t in list.Where(t => t.LoadFailed && !t.IsStart)) Add(t, CleanupGroup.Errors);
        if (oldDays > 0)
            foreach (var t in list.Where(t => now - t.LastActive > TimeSpan.FromDays(oldDays)).OrderBy(t => t.LastActive))
                Add(t, CleanupGroup.Old);

        return result.OrderBy(r => r.Group).ToList();
    }

    /// <summary>Поиск вкладок по названию и адресу: все слова запроса должны встретиться (без учёта регистра).</summary>
    public static List<TabInfo> Search(IEnumerable<TabInfo> tabs, string? query)
    {
        var words = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var list = tabs.Where(t => !t.IsStart);
        if (words.Length == 0) return list.OrderByDescending(t => t.LastActive).ToList();
        return list
            .Select(t => (t, hay: (t.Title + " " + Unescape(t.Url)).ToLowerInvariant()))
            .Where(x => words.All(w => x.hay.Contains(w.ToLowerInvariant())))
            .OrderByDescending(x => x.t.Title.Contains(words[0], StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(x => x.t.LastActive)
            .Select(x => x.t)
            .ToList();
    }

    static string Unescape(string url)
    {
        try { return Uri.UnescapeDataString(url); } catch { return url; }
    }
}

/// <summary>Вкладка сохранённого сеанса.</summary>
public sealed class SessionTab
{
    public string Container { get; set; } = "work";
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTime LastActive { get; set; } = DateTime.Now;
}

/// <summary>Окно сохранённого сеанса: вкладки по порядку и номер активной.</summary>
public sealed class SessionWindow
{
    public List<SessionTab> Tabs { get; set; } = new();
    public int Active { get; set; } = -1;
    public string Container { get; set; } = "work";
}
