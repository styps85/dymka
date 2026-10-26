using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using Dymka.Core;

namespace Dymka;

/// <summary>Диагностика аварийных закрытий: отметка запуска с номером процесса и последней страницей,
/// а после аварии - записи журнала Windows о сбое или зависании Dymka.exe.</summary>
static class CrashDiagnostics
{
    static string _path = "";
    static RunFlag? _flag;
    static DateTime _lastWrite;

    public static void Start(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path))
            {
                var prev = RunFlag.Parse(File.ReadAllText(path));
                if (prev != null && prev.Pid > 0 && IsDymkaAlive(prev.Pid))
                    App.Log($"Предыдущее окно «Дымки» (процесс {prev.Pid}) ещё работает или закрывается - это не авария");
                else if (prev != null)
                {
                    App.Log("Предыдущий запуск завершился аварийно (браузер закрылся без выхода). Начат: " + prev.Start.ToString("yyyy-MM-dd HH:mm:ss") +
                            (prev.Url.Length > 0 ? $"; последняя открытая страница: {prev.Url} (в {prev.Updated:HH:mm:ss})" : ""));
                    var since = prev.Start;
                    _ = Task.Run(() => ReadWindowsLog(since));
                }
            }
            _flag = new RunFlag(DateTime.Now, Environment.ProcessId, "", DateTime.Now);
            File.WriteAllText(path, _flag.Format());
        }
        catch { }
    }

    /// <summary>Открыта страница (активная вкладка): запоминаем адрес без параметров - не чаще раза в 3 секунды.</summary>
    public static void NotePage(string? url)
    {
        if (_flag == null || _path.Length == 0) return;
        var safe = RunFlag.SafeUrl(url);
        if (safe == _flag.Url) return;
        _flag = _flag with { Url = safe, Updated = DateTime.Now };
        if (DateTime.Now - _lastWrite < TimeSpan.FromSeconds(3)) return;
        Flush();
    }

    public static void Flush()
    {
        if (_flag == null) return;
        try { File.WriteAllText(_path, _flag.Format()); _lastWrite = DateTime.Now; } catch { }
    }

    /// <summary>Нормальный выход: удаляем отметку, только если она наша (второй экземпляр мог записать свою).</summary>
    public static void Stop()
    {
        try
        {
            if (_path.Length == 0 || !File.Exists(_path)) return;
            var cur = RunFlag.Parse(File.ReadAllText(_path));
            if (cur == null || cur.Pid == 0 || cur.Pid == Environment.ProcessId) File.Delete(_path);
        }
        catch { }
    }

    static bool IsDymkaAlive(int pid)
    {
        if (pid == Environment.ProcessId) return false;
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited && p.ProcessName.Contains("Dymka", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Записи журнала «Приложение» Windows о сбое (Application Error, .NET Runtime, WER) или зависании Dymka.exe.</summary>
    static void ReadWindowsLog(DateTime since)
    {
        try
        {
            var from = since.ToUniversalTime().AddMinutes(-1).ToString("yyyy-MM-ddTHH:mm:ss.000Z");
            var xpath = "*[System[Provider[@Name='Application Error' or @Name='Application Hang' or @Name='.NET Runtime' or @Name='Windows Error Reporting']" +
                        $" and TimeCreated[@SystemTime>='{from}']]]";
            using var reader = new EventLogReader(new EventLogQuery("Application", PathType.LogName, xpath));
            int found = 0;
            for (EventRecord? r = reader.ReadEvent(); r != null && found < 4; r = reader.ReadEvent())
            {
                using (r)
                {
                    string text;
                    try { text = r.FormatDescription() ?? ""; } catch { text = ""; }
                    if (text.Length == 0) text = string.Join(" | ", r.Properties.Select(x => x.Value?.ToString() ?? ""));
                    if (!text.Contains("Dymka", StringComparison.OrdinalIgnoreCase)) continue;
                    found++;
                    text = text.Replace("\r", " ").Replace("\n", " ");
                    if (text.Length > 1500) text = text[..1500] + "…";
                    App.Log($"Журнал Windows, {r.ProviderName} ({r.Id}) {r.TimeCreated:yyyy-MM-dd HH:mm:ss}: {text}");
                }
            }
            if (found == 0) App.Log("Журнал Windows: записей о сбое Dymka.exe нет (процесс могли завершить извне, или Windows не записала сбой)");
        }
        catch (Exception ex) { App.Log("Журнал Windows недоступен: " + ex.Message); }
    }
}
