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


    const string TestExtensionKey = "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEA2sadAl7NNCgUS6J1CQf/mmni+aLShg87x051VgUYZONkefgr6SiApnzJREQzQsCtmmBEFUvOs5F9LLsAjL/UZAi7K/g6cn+xWMKdA3ebBPKdyCL/e+3Fw1RoBOxtlo6kaaLJ3N0XS8EIarHYdIXbZBBJgC+Vk324bt0/1HbGhhnqevlCaHU964IRaSALP9Nmm4J9MuwmS7oYOhH/hIBRL52nQHkCJ0M5QjZPASimYHmI73rJlRoQ6whXrT3B7c9FENxD9OcIaZkgrQm478Ffs69/GpS4xibxWF+wLZW4cqR8zdXE1UF7wVyKi/K3QMlLsIdXt4LIS+N1g3IZYJFe9QIDAQAB";
    const string TestExtensionId = "pkgnmdncaobbfaedogeekgjgakeoclfe";

    async Task TestExtensionsAsync(string testDir, BrowserTab probe, Microsoft.Web.WebView2.Core.CoreWebView2 core)
    {
        try
        {
            var ext = Path.Combine(_dir, "ext-nmtest");
            var host = Path.Combine(_dir, "nmhost");
            Directory.CreateDirectory(ext);
            Directory.CreateDirectory(host);
            File.WriteAllText(Path.Combine(ext, "manifest.json"),
                "{\"manifest_version\":3,\"name\":\"Dymka NM test\",\"version\":\"1.0\",\"key\":\"" + TestExtensionKey + "\"," +
                "\"permissions\":[\"nativeMessaging\"],\"background\":{\"service_worker\":\"bg.js\"}," +
                "\"content_scripts\":[{\"matches\":[\"https://trackers.dymka-test.org/*\"],\"js\":[\"cs.js\"],\"run_at\":\"document_idle\"}]}");
            File.WriteAllText(Path.Combine(ext, "bg.js"),
                "chrome.runtime.onMessage.addListener(function(m,s,send){" +
                "chrome.runtime.sendNativeMessage('com.dymka.nmtest',{ping:'dymka'},function(r){" +
                "var err=chrome.runtime.lastError?chrome.runtime.lastError.message:null;send({ok:!!r,r:r||null,err:err});});return true;});");
            File.WriteAllText(Path.Combine(ext, "cs.js"),
                "document.documentElement.setAttribute('data-ext','loaded');" +
                "chrome.runtime.sendMessage({go:1},function(r){document.documentElement.setAttribute('data-nm'," +
                "JSON.stringify(r||{none:chrome.runtime.lastError?chrome.runtime.lastError.message:'нет ответа'}));});");

            File.WriteAllText(Path.Combine(host, "host.ps1"),
                "$i=[Console]::OpenStandardInput();$o=[Console]::OpenStandardOutput();" +
                "$lb=New-Object byte[] 4;[void]$i.Read($lb,0,4);$len=[BitConverter]::ToInt32($lb,0);" +
                "$b=New-Object byte[] $len;$r=0;while($r -lt $len){$r+=$i.Read($b,$r,$len-$r)};" +
                "$m=[Text.Encoding]::UTF8.GetString($b);$resp=[Text.Encoding]::UTF8.GetBytes('{\"pong\":1,\"got\":'+$m+'}');" +
                "$o.Write([BitConverter]::GetBytes($resp.Length),0,4);$o.Write($resp,0,$resp.Length);$o.Flush()");
            File.WriteAllText(Path.Combine(host, "host.bat"),
                "@echo off\r\npowershell -NoProfile -ExecutionPolicy Bypass -File \"%~dp0host.ps1\"\r\n");
            var hostJson = Path.Combine(host, "com.dymka.nmtest.json");
            File.WriteAllText(hostJson,
                "{\"name\":\"com.dymka.nmtest\",\"description\":\"Dymka test\",\"path\":\"host.bat\",\"type\":\"stdio\"," +
                "\"allowed_origins\":[\"chrome-extension://" + TestExtensionId + "/\"]}");
            foreach (var root in new[] { @"Software\Microsoft\Edge", @"Software\Microsoft\Edge WebView", @"Software\Google\Chrome", @"Software\Chromium" })
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(root + @"\NativeMessagingHosts\com.dymka.nmtest");
                k.SetValue("", hostJson);
            }

            var added = await core.Profile.AddBrowserExtensionAsync(ext);
            Check(added.Id == TestExtensionId, $"расширение установлено в «Дымку» (id {added.Id})");

            File.WriteAllText(Path.Combine(testDir, "nm.html"), "<!doctype html><meta charset=\"utf-8\"><title>nm</title><h1>Проверка расширения</h1>");
            _w.Navigate(probe, "https://trackers.dymka-test.org/nm.html");
            await WaitFor(() => probe.Url.Contains("nm.html") && !probe.IsLoading, 20);
            string extState = await PollScript(core, "document.documentElement.getAttribute('data-ext')", v => v.Contains("loaded"), 12);
            Check(extState.Contains("loaded"), "расширение работает на странице (content script)");
            string nm = await PollScript(core, "document.documentElement.getAttribute('data-nm')", v => v != "null" && v.Length > 4, 20);
            Line("ИНФО связь расширения с программой на компьютере (native messaging): " + nm);
            Check(nm.Contains("pong"), "native messaging работает - плагины подписи смогут связаться со своей программой");
        }
        catch (Exception ex)
        {
            Line("ИСКЛЮЧЕНИЕ в проверке расширений: " + ex);
            _failed++;
        }
    }

    static async Task<string> PollScript(Microsoft.Web.WebView2.Core.CoreWebView2 core, string script, Func<string, bool> done, int seconds)
    {
        var until = DateTime.Now.AddSeconds(seconds);
        string v = "";
        while (DateTime.Now < until)
        {
            try { v = await core.ExecuteScriptAsync(script); if (done(v)) return v; } catch { }
            await Task.Delay(400);
        }
        return v;
    }

    async Task TestTabFeaturesAsync()
    {
        try
        {
            var a = _w.ActiveTab!;
            // меню адресной строки и QR-код
            _w.FocusAddress();
            _w.OpenAddressMenu();
            await Task.Delay(700);
            Capture(_w, "12-address-menu");
            _w.ShowQrForTest();
            await Task.Delay(500);
            Capture(_w, "12b-address-qr");
            _w.ClosePopups();

            // закрепление
            _w.SetPinned(a, true);
            Check(_w.Settings.Pinned.Count == 1, "вкладка закреплена и сохранена для следующего запуска");
            await Task.Delay(500);
            Capture(_w, "13-pinned");

            // раздельный просмотр
            var b = await _w.NewTabAsync("https://example.com/", activate: false);
            await WaitFor(() => b.View?.CoreWebView2 != null && !b.IsLoading && b.Url.Contains("example.com"), 30);
            _w.StartSplit(a, b);
            await Task.Delay(1500);
            Check(_w.IsSplit && a.View?.IsVisible == true && b.View?.IsVisible == true, "раздельный просмотр: обе страницы видны");
            Capture(_w, "14-split");
            _w.EndSplit();
            await Task.Delay(400);
            Check(!_w.IsSplit, "раздельный просмотр закрыт");

            // перенос в другой контейнер
            var url = b.Url;
            await _w.MoveToContainerAsync(b, "personal");
            var moved = _w.ActiveTab!;
            Check(moved.Container == "personal" && moved.Url == url, "вкладка перенесена в контейнер «Личное»");
            await Task.Delay(800);
            _w.SetPinned(a, false);

            // второе окно
            var w2 = _w.OpenWindow("https://example.com/", "work");
            Check(await WaitFor(() => w2.ActiveTab?.View?.CoreWebView2 != null && w2.ActiveTab.IsLoading == false, 30), "открылось второе окно");
            await Task.Delay(800);
            Capture(w2, "15-second-window");
            w2.Close();
            await Task.Delay(1500);
            Check(_w.IsVisible && Shared.Windows.Count == 1, "после закрытия второго окна первое продолжает работать");

            // перевод видео: встроенное расширение VOT
            _w.Settings.VideoTranslation = true;
            await _w.ResyncExtensionsAsync();
            var yt = await _w.NewTabAsync("https://www.youtube.com/watch?v=jNQXAC9IVRw", activate: true);
            await WaitFor(() => yt.Url.Contains("youtube.com") && !yt.IsLoading, 40);
            var ytCore = yt.View?.CoreWebView2;
            if (ytCore != null)
            {
                var exts = await ytCore.Profile.GetBrowserExtensionsAsync();
                Line("расширения профиля: " + string.Join("; ", exts.Select(x => $"{x.Name} ({(x.IsEnabled ? "вкл" : "выкл")})")));
                Check(exts.Any(x => x.Name.Contains("Voice Over Translation") && x.IsEnabled), "перевод видео: расширение установлено и включено");
                Check(exts.Any(x => x.Name.Contains("PDF")), "встроенный просмотр PDF движка на месте");
                string vot = await PollScript(ytCore, "!!document.querySelector('[class*=\"vot\"],[id*=\"vot\"]')", v => v == "true", 25);
                Line("ИНФО кнопка перевода на YouTube: " + (vot == "true" ? "есть" : "нет (на сервере сборки YouTube требует вход - видео не загружается)"));
            }
            await Task.Delay(1500);
            Capture(_w, "17-video-translation");
            _w.Settings.VideoTranslation = false;

            // окно расширений
            var ew = new ExtensionsWindow(_w.Settings, Path.Combine(_dir, "ext-copies")) { Owner = _w, Topmost = true };
            ew.Show();
            await Task.Delay(2500);
            Capture(ew, "16-extensions");
            ew.Close();
        }
        catch (Exception ex)
        {
            Line("ИСКЛЮЧЕНИЕ в проверке вкладок: " + ex);
            _failed++;
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

            // расширения: тестовое расширение + тестовая «программа на компьютере» (как у плагинов подписи)
            await TestExtensionsAsync(testDir, probe, startCore);

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

            await TestTabFeaturesAsync();

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
