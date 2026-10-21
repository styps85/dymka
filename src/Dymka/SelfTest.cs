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

    /// <summary>Ctrl+P с фокусом внутри страницы и window.print() должны открывать окно печати «Дымки».</summary>
    async Task TestPrintTriggers(string shot, BrowserTab tab, Microsoft.Web.WebView2.Core.CoreWebView2 core, bool scripted)
    {
        tab.View!.Focus();
        await Task.Delay(600);
        Native.PressCtrl(0x50); // Ctrl+P
        bool opened = await WaitFor(() => _w.PrintWindowForTest != null, 6);
        Check(opened, $"Ctrl+P ({shot}) открывает окно печати «Дымки»");
        await Task.Delay(1500);
        Capture(_w, shot);   // на снимке не должно быть стандартного окна печати движка
        _w.PrintWindowForTest?.Close();
        await Task.Delay(1300);
        Capture(_w, shot + "-after");   // после закрытия окна «Дымки» стандартного окна движка быть не должно
        if (!scripted) return;

        await core.ExecuteScriptAsync("setTimeout(function(){window.print();},0)");
        Check(await WaitFor(() => _w.PrintWindowForTest != null, 6), "кнопка «Печать» на сайте (window.print) открывает окно «Дымки»");
        _w.PrintWindowForTest?.Close();
        await Task.Delay(1300);

        await core.ExecuteScriptAsync(
            "var f=document.createElement('iframe');f.srcdoc='<button>p</button>';document.body.appendChild(f);" +
            "f.onload=function(){f.contentWindow.print();};");
        Check(await WaitFor(() => _w.PrintWindowForTest != null, 6), "печать из фрейма на странице открывает окно «Дымки»");
        _w.PrintWindowForTest?.Close();
        await Task.Delay(1300);
    }

    async Task TestPrintDialog(string shot, int expectedPages)
    {
        _w.OpenPrintDialog();
        var pw = _w.PrintWindowForTest;
        Check(pw != null, "окно печати открылось");
        if (pw == null) return;
        pw.Topmost = true;
        await WaitFor(() => pw.PreviewReady, 30);
        Check(pw.TotalPages == expectedPages, $"предпросмотр печати: страниц {pw.TotalPages} (ожидалось {expectedPages})");
        pw.SelectAllButLastForTest();
        var sel = pw.SelectedPages();
        Check(sel != null && Dymka.Core.PageRanges.ToRangeString(sel) == "1-" + (expectedPages - 1),
            "«все, кроме последней»: " + (sel == null ? "ошибка" : Dymka.Core.PageRanges.ToRangeString(sel)));
        pw.SetPerSheetForTest(2);
        await Task.Delay(1200);
        Capture(pw, shot);
        pw.Close();
        await Task.Delay(300);
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

            var startCore = _w.ActiveTab!.View!.CoreWebView2;
            var tiles = await startCore.ExecuteScriptAsync("document.querySelectorAll('.tile:not(.add)').length");
            Check(tiles == _w.Settings.Shortcuts.Count.ToString(), $"стартовая страница получила плитки сайтов ({tiles})");

            // страница с трекерами: счётчики Google, Яндекса, Mail.ru и пиксель Facebook должны быть заблокированы
            var testDir = Path.Combine(_dir, "web");
            Directory.CreateDirectory(testDir);
            File.WriteAllText(Path.Combine(testDir, "trackers.html"),
                "<!doctype html><html><head><meta charset=\"utf-8\"><title>Проверка трекеров</title>" +
                "<script src=\"https://www.google-analytics.com/analytics.js\"></script>" +
                "<script src=\"https://www.googletagmanager.com/gtag/js?id=G-TEST\"></script>" +
                "<script src=\"https://connect.facebook.net/en_US/fbevents.js\"></script></head><body>" +
                "<h1>Проверка трекеров</h1>" +
                "<img src=\"https://mc.yandex.ru/watch/12345\" alt=\"\">" +
                "<img src=\"https://top-fwz1.mail.ru/counter?id=1\" alt=\"\">" +
                "<img id=\"ok\" src=\"https://www.python.org/static/favicon.ico\" alt=\"\">" +
                "</body></html>");
            startCore.SetVirtualHostNameToFolderMapping("trackers.dymka-test.org", testDir,
                Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
            var probe = _w.ActiveTab!;
            probe.SeenHosts = new List<string>();
            _w.Navigate(probe, "https://trackers.dymka-test.org/trackers.html");
            await WaitFor(() => probe.Url.Contains("trackers.html") && !probe.IsLoading, 30);
            await Task.Delay(2500);
            Line("запросы страницы, которые видел браузер: " + string.Join(", ", probe.SeenHosts));
            probe.SeenHosts = null;
            Check(probe.Blocked >= 5, $"трекеры на тестовой странице заблокированы: {probe.Blocked} из 5");
            var okImg = await startCore.ExecuteScriptAsync("document.getElementById('ok').naturalWidth > 0");
            Check(okImg == "true", "обычная картинка с другого сайта загрузилась (блокировка не лишняя)");
            Capture(_w, "01b-trackers");

            // сторонние cookie: страница на одном сайте, во фрейме - другой сайт, который пытается записать cookie
            File.WriteAllText(Path.Combine(testDir, "frame.html"),
                "<!doctype html><meta charset=\"utf-8\"><script>document.cookie='t3=1; SameSite=None; Secure; Max-Age=600';" +
                "parent.postMessage('frame-cookie:' + (document.cookie.indexOf('t3=1') >= 0 ? 'записан' : 'заблокирован'), '*');</script>");
            File.WriteAllText(Path.Combine(testDir, "cookies.html"),
                "<!doctype html><meta charset=\"utf-8\"><title>cookie</title><script>" +
                "document.cookie='t1=1; SameSite=Lax; Secure; Max-Age=600';" +
                "window.addEventListener('message', function(e){ document.title = 'первая сторона: ' + (document.cookie.indexOf('t1=1') >= 0 ? 'записан' : 'нет') + '; ' + e.data; });" +
                "</script><iframe src=\"https://other.dymka-test.net/frame.html\"></iframe>");
            startCore.SetVirtualHostNameToFolderMapping("other.dymka-test.net", testDir,
                Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
            _w.Navigate(probe, "https://trackers.dymka-test.org/cookies.html");
            await WaitFor(() => startCore.DocumentTitle.Contains("frame-cookie"), 15);
            var ct = startCore.DocumentTitle;
            Check(ct.Contains("первая сторона: записан"), "cookie самого сайта сохраняются: " + ct);
            Check(ct.Contains("frame-cookie:заблокирован"), "сторонние cookie (во фрейме другого сайта) заблокированы");

            // окно печати: документ на 3 страницы, «все, кроме последней», 2 страницы на листе
            File.WriteAllText(Path.Combine(testDir, "long.html"),
                "<!doctype html><meta charset=\"utf-8\"><title>Три страницы</title><style>body{font:28px Segoe UI}" +
                ".p{break-after:page;height:400px;border:4px solid #3D63DD;padding:30px}</style>" +
                "<div class=p>Страница 1</div><div class=p>Страница 2</div><div style=\"height:400px;border:4px solid #C2552E;padding:30px\">Страница 3</div>");
            _w.Navigate(probe, "https://trackers.dymka-test.org/long.html");
            await WaitFor(() => probe.Url.Contains("long.html") && !probe.IsLoading, 20);
            await Task.Delay(800);
            await TestPrintTriggers("10-ctrlp-page", probe, startCore, scripted: true);
            await TestPrintDialog("08-print", 3);

            // тот же документ в виде PDF (встроенный просмотрщик PDF)
            var pdfPath = Path.Combine(testDir, "long.pdf");
            await startCore.PrintToPdfAsync(pdfPath, null);
            _w.Navigate(probe, "https://trackers.dymka-test.org/long.pdf");
            await WaitFor(() => probe.Url.Contains("long.pdf") && !probe.IsLoading, 20);
            await Task.Delay(2500);
            await TestPrintTriggers("11-ctrlp-pdf", probe, startCore, scripted: false);
            await TestPrintDialog("09-print-pdf", 3);

            // горячие клавиши, когда фокус внутри страницы
            int tabsBefore = _w.Tabs.Count;
            probe.View!.Focus();
            await Task.Delay(500);
            Native.PressCtrl(0x54); // Ctrl+T
            bool keyOk = await WaitFor(() => _w.Tabs.Count == tabsBefore + 1, 5);
            Check(keyOk, "Ctrl+T при фокусе в странице открывает вкладку");
            if (keyOk)
            {
                Native.PressCtrl(0x57); // Ctrl+W
                await WaitFor(() => _w.Tabs.Count == tabsBefore, 5);
                Line("ИНФО Ctrl+W: вкладок " + _w.Tabs.Count);
            }
            if (_w.ActiveTab != probe) _w.Activate(probe);

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
            Check(await WaitFor(() => third.View?.CoreWebView2 != null && third.Title.Contains("Wikipedia") && !third.IsLoading, 40),
                "фоновая вкладка загружена: " + third.Title);
            _w.UpdateMemory();
            long before = _w.MemoryBytes;
            _w.SleepTab(third);
            Check(third.IsSleeping && third.View == null, "фоновая вкладка уснула");
            await Task.Delay(2500);
            _w.UpdateMemory();
            Line($"память до усыпления {before / 1048576} МБ, после {_w.MemoryBytes / 1048576} МБ");

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
            Line("по процессам: " + _w.MemoryDetails);
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
