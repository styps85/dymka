namespace Dymka.Core;

/// <summary>Фоны новой вкладки: порядок, переключение стрелками, фон дня, времена года и праздники.
/// Тема фона берётся из имени файла: «зима», «winter», «новый год», «newyear», «8 марта», «march8» и т.п.;
/// фоны без темы показываются круглый год.</summary>
public static class Wallpapers
{
    public static readonly string[] Extensions = { ".jpg", ".jpeg", ".png", ".webp", ".bmp" };

    /// <summary>Темы: времена года и праздники с ключевыми словами в имени файла.</summary>
    static readonly (string tag, string[] words)[] Themes =
    {
        ("newyear", new[] { "newyear", "new-year", "new_year", "новый год", "новый-год", "новый_год", "новогод" }),
        ("march8", new[] { "march8", "march-8", "8march", "8-march", "8 марта", "8-марта", "8_марта" }),
        ("winter", new[] { "winter", "зима", "зимн" }),
        ("spring", new[] { "spring", "весна", "весен" }),
        ("summer", new[] { "summer", "лето", "летн" }),
        ("autumn", new[] { "autumn", "fall-", "осень", "осен" }),
    };

    public static bool IsHoliday(string tag) => tag is "newyear" or "march8";

    /// <summary>Тема фона по имени файла (пусто - круглый год).</summary>
    public static string ThemeOf(string fileName)
    {
        var n = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant().Replace('ё', 'е');
        foreach (var (tag, words) in Themes)
            if (words.Any(w => n.Contains(w))) return tag;
        return "";
    }

    public static string SeasonOf(DateTime d) => d.Month switch
    {
        12 or 1 or 2 => "winter",
        3 or 4 or 5 => "spring",
        6 or 7 or 8 => "summer",
        _ => "autumn",
    };

    /// <summary>Праздник, который сейчас «в воздухе» (пусто - никакого): Новый год 20.12-10.01, 8 Марта 1-9 марта.</summary>
    public static string HolidayOf(DateTime d)
    {
        if ((d.Month == 12 && d.Day >= 20) || (d.Month == 1 && d.Day <= 10)) return "newyear";
        if (d.Month == 3 && d.Day <= 9) return "march8";
        return "";
    }

    /// <summary>Порядок фонов: сначала стандартные, потом из галереи, потом свои; внутри - по имени.</summary>
    public static List<string> Order(IEnumerable<string> names) =>
        names.Where(n => Extensions.Contains(Path.GetExtension(n).ToLowerInvariant()))
             .OrderBy(n => Path.GetFileName(n).StartsWith("dymka-") ? 0 : Path.GetFileName(n).StartsWith("gallery-") ? 1 : 2)
             .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
             .ToList();

    /// <summary>Следующий (dir = 1) или предыдущий (dir = -1) фон по кругу.</summary>
    public static string Step(List<string> all, string current, int dir)
    {
        if (all.Count == 0) return "";
        int i = all.FindIndex(n => n.Equals(current, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return dir >= 0 ? all[0] : all[^1];
        return all[((i + dir) % all.Count + all.Count) % all.Count];
    }

    /// <summary>Из каких фонов выбирать фон дня. С временами года и праздниками: в праздник - праздничные (если есть),
    /// иначе фоны текущего времени года (если есть), иначе фоны без темы. Без них - все фоны, кроме праздничных.</summary>
    public static List<string> Pool(List<string> all, DateTime date, bool seasonal)
    {
        if (!seasonal)
        {
            var plain = all.Where(n => !IsHoliday(ThemeOf(n))).ToList();
            return plain.Count > 0 ? plain : all;
        }
        var holiday = HolidayOf(date);
        if (holiday.Length > 0)
        {
            var h = all.Where(n => ThemeOf(n) == holiday).ToList();
            if (h.Count > 0) return h;
        }
        var season = SeasonOf(date);
        var s = all.Where(n => ThemeOf(n) == season).ToList();
        if (s.Count > 0) return s;
        var untagged = all.Where(n => ThemeOf(n) == "").ToList();
        return untagged.Count > 0 ? untagged : all;
    }

    /// <summary>Фон дня: каждый день следующий из подходящих (одинаковый весь день).</summary>
    public static string OfTheDay(List<string> all, DateTime date, bool seasonal)
    {
        var pool = Pool(all, date, seasonal);
        if (pool.Count == 0) return "";
        int day = (int)(date.Date - new DateTime(2026, 1, 1)).TotalDays;
        return pool[((day % pool.Count) + pool.Count) % pool.Count];
    }

    /// <summary>Безопасное имя файла для фона из галереи или своего.</summary>
    public static string SafeName(string prefix, string name)
    {
        var baseName = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name).ToLowerInvariant();
        if (!Extensions.Contains(ext)) ext = ".jpg";
        var clean = new string(baseName.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or ' ' ? ch : '-').ToArray()).Trim();
        if (clean.Length == 0) clean = "fon";
        if (clean.Length > 60) clean = clean[..60];
        return prefix + clean.Replace(' ', '-') + ext;
    }
}
