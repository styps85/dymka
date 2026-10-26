namespace Dymka.Core;

/// <summary>Отметка «браузер запущен» (running.flag): когда начат, номер процесса, последняя открытая страница.
/// Если при следующем запуске отметка осталась, а процесса уже нет - прошлый запуск завершился аварийно.</summary>
public sealed record RunFlag(DateTime Start, int Pid, string Url, DateTime Updated)
{
    public string Format() =>
        $"start={Start:yyyy-MM-dd HH:mm:ss}\npid={Pid}\nurl={Url}\nupdated={Updated:yyyy-MM-dd HH:mm:ss}\n";

    /// <summary>Разбор отметки. Старый вид (только дата запуска) тоже понимается.</summary>
    public static RunFlag? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (!text.Contains('=') && DateTime.TryParse(text, out var old)) return new RunFlag(old, 0, "", old);
        DateTime start = default, upd = default;
        int pid = 0;
        string url = "";
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var k = line[..eq];
            var v = line[(eq + 1)..];
            if (k == "start") DateTime.TryParse(v, out start);
            else if (k == "pid") int.TryParse(v, out pid);
            else if (k == "url") url = v;
            else if (k == "updated") DateTime.TryParse(v, out upd);
        }
        if (start == default) return null;
        return new RunFlag(start, pid, url, upd == default ? start : upd);
    }

    /// <summary>Адрес для журнала: без параметров после «?», чтобы туда не попадали поисковые запросы и токены.</summary>
    public static string SafeUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return "";
        int q = url.IndexOfAny(new[] { '?', '#' });
        var s = q >= 0 ? url[..q] : url;
        return s.Length > 300 ? s[..300] : s;
    }
}
