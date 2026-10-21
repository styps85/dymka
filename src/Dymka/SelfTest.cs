using System.Text;
using System.Windows;

namespace Dymka;

/// <summary>Самопроверка для автосборки: запускает браузер, проходит основные сценарии,
/// сохраняет снимки окна и отчёт. Запуск: Dymka.exe --selftest папка --data папка.</summary>
sealed class SelfTest
{
    static string? _reportPath;
    static readonly StringBuilder Report = new();

    readonly MainWindow _w;
    readonly string _dir;
    int _failed;

    public SelfTest(MainWindow w, string dir)
    {
        _w = w;
        _dir = dir;
        Directory.CreateDirectory(dir);
        _reportPath = Path.Combine(dir, "selftest.txt");
    }

    static void Line(string s)
    {
        lock (Report) Report.AppendLine($"{DateTime.Now:HH:mm:ss} {s}");
        try { if (_reportPath != null) File.WriteAllText(_reportPath, Report.ToString()); } catch { }
    }

    public static void Fail(Exception ex)
    {
        Line("НЕОБРАБОТАННАЯ ОШИБКА: " + ex);
        Line("РЕЗУЛЬТАТ: ОШИБКА");
        Environment.Exit(2);
    }

    void Check(bool ok, string what)
    {
        Line((ok ? "OK      " : "ПРОВАЛ  ") + what);
        if (!ok) _failed++;
    }

    static async Task<bool> WaitFor(Func<bool> cond, int seconds)
    {
        var until = DateTime.Now.AddSeconds(seconds);
        while (DateTime.Now < until)
        {
            try { if (cond()) return true; } catch { }
            await Task.Delay(250);
        }
        return false;
    }

    void Capture(Window win, string name)
    {
        try
        {
            win.UpdateLayout();
            var bmp = Native.CaptureWindow(win);
            if (bmp == null) { Line("снимок не получен: " + name); return; }
            Native.SavePng(bmp, Path.Combine(_dir, name + ".png"));
            Line($"снимок {name}.png ({bmp.PixelWidth}x{bmp.PixelHeight})");
        }
        catch (Exception ex)
        {
            Line($"снимок {name}: {ex.Message}");
        }
    }

    public async Task RunAsync()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMinutes(4));
            Line("ПРЕВЫШЕНО ВРЕМЯ САМОПРОВЕРКИ");
            Line("РЕЗУЛЬТАТ: ОШИБКА");
            Environment.Exit(3);
        });

        try
        {
            Line("Дымка " + App.VersionText + ", самопроверка");
            _w.Topmost = true;
            _w.Activate();

            Check(await WaitFor(() => _w.ActiveTab?.View?.CoreWebView2 != null && _w.ActiveTab.IsLoading == false, 40),
                "движок WebView2 запущен, открыта стартовая страница");
            await Task.Delay(2500);
            Capture(_w, "01-top-start");

            var first = _w.ActiveTab!;
            _w.Navigate(first, "https://example.com/?utm_source=selftest&x=1");
            Check(await WaitFor(() => first.Url.StartsWith("https://example.com") && !first.IsLoading, 40),
                "переход на сайт: " + first.Url);
            Check(!first.Url.Contains("utm_source") && first.Url.Contains("x=1"), "метка utm_source удалена из адреса");

            var site = await _w.NewTabAsync("https://www.python.org/", activate: true);
            Check(await WaitFor(() => site.Url.Contains("python.org") && !site.IsLoading, 45), "вторая вкладка: " + site.Url);
            await Task.Delay(3500);
            Line($"python.org: заблокировано запросов трекеров {site.Blocked}");
            Capture(_w, "02-top-site");

            _w.OpenPrivacyPanel();
            await Task.Delay(900);
            Capture(_w, "03-privacy-panel");
            _w.ClosePopups();

            var third = await _w.NewTabAsync("https://www.wikipedia.org/", activate: false);
            Check(await WaitFor(() => third.View?.CoreWebView2 != null && !third.IsLoading, 40), "фоновая вкладка загружена");
            _w.SleepTab(third);
            Check(third.IsSleeping && third.View == null, "фоновая вкладка уснула");

            _w.SetLayout("side");
            await Task.Delay(1500);
            Capture(_w, "04-side-layout");

            _w.OpenMenuForTest();
            await Task.Delay(800);
            Capture(_w, "05-menu");
            _w.ClosePopups();

            _w.Activate(third);
            Check(await WaitFor(() => third.View?.CoreWebView2 != null && !third.IsLoading && !third.IsSleeping, 40),
                "спящая вкладка проснулась при открытии");
            await Task.Delay(1500);
            Capture(_w, "06-side-woken");

            var sw = _w.OpenSettingsForTest();
            sw.Topmost = true;
            await Task.Delay(1200);
            Capture(sw, "07-settings");
            sw.Close();

            _w.UpdateMemory();
            Line($"память браузера: {_w.MemoryBytes / (1024 * 1024)} МБ, вкладок {_w.Tabs.Count}");
            Check(_w.MemoryBytes > 0, "подсчёт памяти работает");

            _w.SetLayout("top");
            await Task.Delay(500);
        }
        catch (Exception ex)
        {
            Line("ИСКЛЮЧЕНИЕ: " + ex);
            _failed++;
        }

        Line(_failed == 0 ? "РЕЗУЛЬТАТ: OK" : $"РЕЗУЛЬТАТ: ОШИБКА (провалов: {_failed})");
        Environment.ExitCode = _failed == 0 ? 0 : 1;
        _w.Topmost = false;
        _w.Close();
    }
}
