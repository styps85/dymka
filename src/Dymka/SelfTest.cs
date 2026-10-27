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
        if (pw.SelectPrinterForTest("Microsoft Print to PDF"))
            Check(pw.PrintButtonTextForTest == "Сохранить", "для «Microsoft Print to PDF» кнопка называется «Сохранить»: " + pw.PrintButtonTextForTest);
        else
            Line("ИНФО принтера Microsoft Print to PDF нет в системе");
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
            Native.ClickCenter(_w.QrButtonForTest);           // настоящий щелчок мышью, как у пользователя
            Check(await WaitFor(() => _w.QrShownForTest, 4), "QR-код появляется по щелчку мышью, меню не закрывается");
            await Task.Delay(400);
            Capture(_w, "12b-address-qr");
            Native.ClickCenter(_w.CopyButtonForTest);
            await Task.Delay(600);
            string clip = "";
            try { clip = Clipboard.GetText(); } catch { }
            Check(clip == a.Url && !_w.AddressMenuOpen, "«Скопировать адрес» по щелчку мышью: адрес в буфере обмена");
            _w.ClosePopups();

            // закрепление
            _w.SetPinned(a, true);
            Check(_w.Settings.Pinned.Count == 1, "вкладка закреплена и сохранена для следующего запуска");
            Check(_w.PinnedExpanded, "при закреплении список закреплённых раскрылся");
            await Task.Delay(500);
            Capture(_w, "13-pinned");
            _w.SetLayout("top");
            await Task.Delay(700);
            Capture(_w, "13b-top-pin");
            _w.SetLayout("side");

            // раздельный просмотр
            var b = await _w.NewTabAsync("https://example.com/", activate: false);
            await WaitFor(() => b.Core != null && !b.IsLoading && b.Url.Contains("example.com"), 30);
            _w.StartSplit(a, b);
            await Task.Delay(1500);
            Check(_w.IsSplit && a.View?.IsVisible == true && b.View?.IsVisible == true, "раздельный просмотр: обе страницы видны");
            Capture(_w, "14-split");
            _w.EndSplit();
            await Task.Delay(400);
            Check(!_w.IsSplit, "раздельный просмотр закрыт");

            // ссылка «рядом» (как из меню правой кнопки)
            _w.EndSplit();
            _w.Activate(a);
            await _w.OpenLinkBesideForTest(a, "https://example.com/?beside=1");
            await Task.Delay(1500);
            Check(_w.IsSplit && _w.ActiveTab?.Url.Contains("example.com") == true, "ссылка открыта рядом в правой половине");
            _w.EndSplit();
            _w.Activate(b);

            // перенос в другой контейнер
            var url = b.Url;
            await _w.MoveToContainerAsync(b, "personal");
            var moved = _w.ActiveTab!;
            Check(moved.Container == "personal" && moved.Url == url, "вкладка перенесена в контейнер «Личное»");
            await Task.Delay(800);
            _w.SetPinned(a, false);

            // второе окно
            var w2 = _w.OpenWindow("https://example.com/", "work");
            Check(await WaitFor(() => w2.ActiveTab?.Core != null && w2.ActiveTab.IsLoading == false, 30), "открылось второе окно");
            _w.Topmost = false;
            w2.Topmost = true;
            w2.Activate();
            await Task.Delay(800);
            Capture(w2, "15-second-window");
            w2.Topmost = false;
            _w.Topmost = true;
            w2.CloseByAppForTest();
            await Task.Delay(1500);
            Check(_w.IsVisible && Shared.Windows.Count == 1, "после закрытия второго окна первое продолжает работать");

            // перевод видео: встроенное расширение VOT
            _w.Settings.VideoTranslation = true;
            await _w.ResyncExtensionsAsync();
            var yt = await _w.NewTabAsync("https://www.youtube.com/watch?v=jNQXAC9IVRw", activate: true);
            await WaitFor(() => yt.Url.Contains("youtube.com") && !yt.IsLoading, 40);
            var ytCore = yt.Core;
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

            // контейнеры на стартовой странице: у каждого свои плитки
            var st = await _w.NewTabAsync(null, activate: true, container: "work");
            await WaitFor(() => st.Core != null && !st.IsLoading, 30);
            await Task.Delay(1200);
            Capture(_w, "18-start-work");
            await st.Core!.ExecuteScriptAsync("document.querySelector('[data-container=\"personal\"]').click()");
            Check(await WaitFor(() => _w.ActiveTab?.Container == "personal" && _w.ActiveTab.IsStart && _w.ActiveTab.Core != null && !_w.ActiveTab.IsLoading, 20),
                "переключение контейнера на стартовой странице: вкладка перешла в «Личное»");
            var pcore = _w.ActiveTab!.Core!;
            var emptyShown = await PollScript(pcore, "!document.getElementById('empty').hidden", v => v == "true", 10);
            Check(emptyShown == "true", "у «Личного» свой (пока пустой) набор плиток");
            await Task.Delay(800);
            Capture(_w, "18b-start-personal");

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

    /// <summary>Ночная ошибка пользователя, очистка вкладок, поиск, сеанс и падение движка.</summary>
    async Task TestSessionAndRecoveryAsync()
    {
        try
        {
            // сценарий, после которого у пользователя упал движок: сайт с плитки -> «Перенести в» «Личное» ->
            // новая вкладка -> на стартовой странице переключить на «Работу» -> щёлкнуть по перенесённой вкладке
            var s1 = await _w.NewTabAsync(null, activate: true, container: "work");
            await WaitFor(() => s1.Core != null && !s1.IsLoading, 20);
            _w.Navigate(s1, "https://example.com/?tile=4pda");
            await WaitFor(() => s1.Url.Contains("tile=4pda") && !s1.IsLoading, 30);
            await _w.MoveToContainerAsync(s1, "personal");
            var movedTile = _w.ActiveTab!;
            Check(movedTile.Container == "personal" && !_w.Tabs.Contains(s1), "сценарий ночной ошибки: вкладка перенесена в «Личное»");
            var s2 = await _w.NewTabAsync(null, activate: true);
            await WaitFor(() => s2.Core != null && !s2.IsLoading, 20);
            await Task.Delay(800);
            await s2.Core!.ExecuteScriptAsync("document.querySelector('[data-container=\"work\"]').click()");
            Check(await WaitFor(() => _w.ActiveTab?.Container == "work" && _w.ActiveTab.IsStart && _w.ActiveTab.Core != null && !_w.ActiveTab.IsLoading, 20),
                "сценарий ночной ошибки: на стартовой странице выбрана «Работа»");
            Check(!_w.ListedTabs.Contains(movedTile), "в «Работе» вкладка из «Личного» в списке не показывается");
            await Task.Delay(1000);
            Capture(_w, "19-night-scenario");
            _w.Activate(movedTile);
            await WaitFor(() => movedTile.Core != null && !movedTile.IsLoading, 20);
            await Task.Delay(1500);
            Check(_w.EngineRestarts == 0 && movedTile.Core != null && _w.ActiveTab == movedTile,
                "сценарий ночной ошибки: вкладка открывается, движок работает");

            // очистка: дубликаты и пустые вкладки
            var d1 = await _w.NewTabAsync("https://example.com/?dup=1", activate: true, container: "work");
            await WaitFor(() => d1.Core != null && !d1.IsLoading && d1.Url.Contains("dup=1"), 30);
            var d2 = await _w.NewTabAsync("https://example.com/?dup=1", activate: false, container: "work");
            await WaitFor(() => d2.Core != null && !d2.IsLoading && d2.Url.Contains("dup=1"), 30);
            var e1 = await _w.NewTabAsync(null, activate: false, container: "work");
            var e2 = await _w.NewTabAsync(null, activate: false, container: "personal");
            _w.Activate(d1);
            await Task.Delay(500);
            var found = _w.FindCleanup();
            Line("ИНФО очистка нашла: " + string.Join("; ", found.Select(f => $"{f.Group}: {f.Tab.Title}")));
            Check(found.Count(f => f.Group == Dymka.Core.CleanupGroup.Duplicates && (f.Tab.Id == d1.Id || f.Tab.Id == d2.Id)) == 1,
                "очистка: из двух одинаковых вкладок к закрытию предложена одна");
            Check(found.Any(f => f.Tab.Id == e1.Id) && found.Any(f => f.Tab.Id == e2.Id), "очистка: пустые вкладки найдены (в обоих контейнерах)");
            Check(!found.Any(f => f.Tab.Id == d1.Id), "очистка: открытая сейчас вкладка не предлагается");
            _w.OpenCleanupForTest();
            var cw = _w.CleanupWindowForTest;
            Check(cw != null && cw.FoundCount == found.Count, "окно очистки открылось и показывает найденное");
            int countBefore = _w.Tabs.Count;
            var ids = cw?.SelectedIds ?? new List<int>();
            if (cw != null)
            {
                cw.Topmost = true;
                await Task.Delay(1200);
                Capture(cw, "20-cleanup");
                cw.Close();
            }
            int closed = _w.CloseTabsForCleanup(ids);
            await Task.Delay(400);
            Check(closed == ids.Count && closed > 0 && _w.Tabs.Count == countBefore - closed, $"очистка: закрыто вкладок {closed}");
            Check(_w.Tabs.Count(x => x.Url.Contains("dup=1")) == 1 && !_w.Tabs.Contains(e1) && !_w.Tabs.Contains(e2),
                "очистка: дубликат и пустые вкладки закрыты");
            Check(_w.ToastTextForTest.Contains("Закрыто вкладок"), "уведомление «Закрыто вкладок: N - Вернуть»");
            await Task.Delay(300);
            Capture(_w, "20b-cleanup-toast");
            _w.UndoCleanup();
            Check(_w.Tabs.Count == countBefore && _w.Tabs.Count(x => x.Url.Contains("dup=1")) == 2, "«Вернуть» вернуло закрытые вкладки");

            // поиск по вкладкам: Ctrl+Shift+A при фокусе в странице
            _w.ActiveTab!.View?.Focus();
            await Task.Delay(500);
            Native.PressCtrlShift(0x41); // Ctrl+Shift+A
            Check(await WaitFor(() => _w.TabSearchOpen, 5), "Ctrl+Shift+A открывает поиск по вкладкам");
            if (!_w.TabSearchOpen) _w.OpenTabSearch();
            await Task.Delay(600);
            Native.TypeLatin("wikip");   // настоящий набор с клавиатуры: текст должен попасть в строку поиска, а не на страницу
            Check(await WaitFor(() => _w.TabSearchTextForTest == "wikip", 4), "текст, набранный с клавиатуры, попадает в строку поиска по вкладкам: «" + _w.TabSearchTextForTest + "»");
            if (_w.TabSearchTextForTest != "wikip") _w.SetTabSearchTextForTest("wikipedia");
            await Task.Delay(500);
            var res = _w.TabSearchResults;
            Line("ИНФО поиск «wikipedia»: " + string.Join("; ", res.Select(r => r.Title + " [" + r.Place + "]")));
            Check(res.Count > 0 && res.All(r => (r.Title + r.Tab.Url).Contains("ikipedia", StringComparison.OrdinalIgnoreCase)), "поиск находит вкладку по названию и адресу");
            Capture(_w, "21-tab-search");
            if (res.Count > 0)
            {
                var target = res[0].Tab;
                _w.GoToSearchResult(res[0]);
                Check(_w.ActiveTab == target && !_w.TabSearchOpen, "выбор в поиске открывает найденную вкладку");
                await WaitFor(() => target.Core != null && !target.IsLoading, 30);
            }

            // сеанс: сохранение и восстановление (в отдельном окне, как при следующем запуске)
            _w.SaveSessionNow();
            var sess = _w.Settings.Session;
            int expected = _w.Tabs.Count(x => !x.IsPinned && !x.IsStart);
            Check(sess.Count == 1 && sess[0].Tabs.Count == expected, $"сеанс сохранён: вкладок {(sess.Count > 0 ? sess[0].Tabs.Count : 0)} из {expected}");
            if (sess.Count > 0)
            {
                var w3 = _w.OpenWindow(null, "work", sess[0]);
                bool restored = await WaitFor(() => w3.Tabs.Count == expected && w3.ActiveTab?.Core != null && !w3.ActiveTab.IsLoading, 40);
                Check(restored, $"сеанс восстановлен: вкладок {w3.Tabs.Count}");
                Check(w3.Tabs.Count(x => x.IsSleeping) == expected - 1, "восстановленные вкладки спят, загружена только активная");
                Check(w3.ActiveTab?.Url == sess[0].Tabs[Math.Max(0, sess[0].Active)].Url, "активна та же вкладка, что была");
                _w.Topmost = false;
                w3.Topmost = true;
                await Task.Delay(1000);
                Capture(w3, "22-session-restored");
                w3.Topmost = false;
                _w.Topmost = true;
                w3.CloseByAppForTest();
                await Task.Delay(1500);
                _w.SaveSessionNow();
                Check(_w.Settings.Session.Count == 1, "закрытое окно из сеанса убрано");
            }

            // падение движка: процесс завершается принудительно, браузер должен сам всё восстановить
            int restartsBefore = _w.EngineRestarts;
            var act = _w.ActiveTab!;
            Check(_w.KillEngineForTest(), "процесс движка завершён (имитация падения)");
            Check(await WaitFor(() => _w.EngineRestarts == restartsBefore + 1 && _w.ActiveTab?.Core != null && !_w.ActiveTab.IsLoading, 45),
                "после падения движок перезапущен, открытая вкладка загружена заново");
            Check(_w.ActiveTab == act && _w.Tabs.Where(x => x != act).All(x => x.View == null), "остальные вкладки сохранились и спят");
            Check(await WaitFor(() => _w.ToastTextForTest.Contains("Движок перезапущен"), 5), "уведомление «Движок перезапущен, вкладки восстановлены»");
            Capture(_w, "23-engine-restarted");
            var other = _w.Tabs.FirstOrDefault(x => x != act && !x.IsPinned && x.Container == act.Container && !x.IsStart);
            if (other != null)
            {
                _w.Activate(other);
                Check(await WaitFor(() => other.Core != null && !other.IsLoading, 40), "щелчок по вкладке после падения движка - без ошибки, вкладка открылась");
            }
            var fresh = await _w.NewTabAsync("https://example.com/?after=crash", activate: true);
            Check(await WaitFor(() => fresh.Core != null && !fresh.IsLoading && fresh.Url.Contains("after=crash"), 30), "новая вкладка после перезапуска движка работает");
        }
        catch (Exception ex)
        {
            Line("ИСКЛЮЧЕНИЕ в проверке сеанса и восстановления: " + ex);
            _failed++;
        }
    }

    string _testDir = "";

    static bool HasCyrillic(string s) => s.Any(ch => ch >= '\u0400' && ch <= '\u04FF');

    /// <summary>Перевод страниц через Яндекс Переводчик - настоящими запросами к сервису.</summary>
    async Task TestTranslationAsync()
    {
        try
        {
            File.WriteAllText(Path.Combine(_testDir, "ru.html"),
                "<!doctype html><html lang=\"ru\"><meta charset=\"utf-8\"><title>Русская страница</title><h1>Картотека арбитражных дел</h1>" +
                "<p>Судебное заседание назначено на понедельник. Просим представить документы по делу о банкротстве заблаговременно.</p></html>");
            string En(string title, string extra) =>
                "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>" + title + "</title>" +
                "<style>body{font:17px Segoe UI;max-width:760px;margin:40px auto;line-height:1.5}</style></head><body>" +
                "<h1>Good morning, dear friends</h1>" +
                "<p id=\"p1\">The court has scheduled a hearing for <a href=\"#\">next Monday</a> at ten o'clock in the morning.</p>" +
                "<p id=\"p2\">Please bring all the documents related to the bankruptcy case and a copy of your passport.</p>" +
                "<p><code id=\"code\">var total = 1;</code></p>" +
                "<p id=\"nt\" class=\"notranslate\">Do not translate this sentence.</p>" + extra + "</body></html>";
            File.WriteAllText(Path.Combine(_testDir, "en.html"), En("Translation test", ""));
            File.WriteAllText(Path.Combine(_testDir, "en2.html"), En("Second page", "<p id=\"p3\">The creditors meeting will be held online.</p>"));

            var tt = await _w.NewTabAsync(null, activate: true, container: "work");
            await WaitFor(() => tt.Core != null && !tt.IsLoading, 20);
            tt.Core!.SetVirtualHostNameToFolderMapping("trackers.dymka-test.org", _testDir,
                Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
            var core = tt.Core!;

            _w.Navigate(tt, "https://trackers.dymka-test.org/ru.html");
            await WaitFor(() => tt.Url.Contains("ru.html") && !tt.IsLoading, 20);
            await Task.Delay(2500);
            Check(!tt.IsForeign && !_w.TranslateButtonVisible, "русская страница: перевод не предлагается");

            _w.Navigate(tt, "https://trackers.dymka-test.org/en.html");
            await WaitFor(() => tt.Url.Contains("en.html") && !tt.IsLoading, 20);
            Check(await WaitFor(() => tt.IsForeign && _w.TranslateButtonVisible, 8), "английская страница распознана: в адресной строке кнопка «Перевести»");
            Capture(_w, "24-translate-offer");

            await _w.TranslatePageAsync(tt);
            await WaitFor(() => tt.TranslateState != TranslateState.Working, 40);
            var tr = MainWindow.TranslatorForTest;
            Line($"ИНФО перевод: состояние {tt.TranslateState}, язык {tt.SourceLang}, способ {tr.LastMethod}, запросов {tr.Requests}, ошибка «{tt.TranslateError}{(tt.TranslateError.Length == 0 ? tr.LastError : "")}»");
            Check(tt.TranslateState == TranslateState.Done && tt.SourceLang == "en", "страница переведена Яндекс Переводчиком (язык оригинала - английский)");
            await Task.Delay(800);
            string p1 = await core.ExecuteScriptAsync("document.getElementById('p1').innerText");
            Line("ИНФО перевод абзаца: " + p1);
            Check(HasCyrillic(p1), "текст абзаца теперь на русском");
            Check(await core.ExecuteScriptAsync("document.querySelector('#p1 a') !== null") == "true", "ссылка внутри абзаца на месте");
            Check((await core.ExecuteScriptAsync("document.getElementById('code').innerText")).Contains("var total"), "программный код не переводится");
            Check((await core.ExecuteScriptAsync("document.getElementById('nt').innerText")).Contains("Do not translate"), "помеченное «не переводить» не переводится");
            Capture(_w, "25-translated");

            await core.ExecuteScriptAsync("var d=document.createElement('p');d.id='dyn';d.textContent='The weather is very nice today.';document.body.appendChild(d);");
            string dyn = await PollScript(core, "document.getElementById('dyn').innerText", HasCyrillic, 15);
            Check(HasCyrillic(dyn), "абзац, появившийся после перевода, переведён сам: " + dyn);

            _w.Navigate(tt, "https://trackers.dymka-test.org/en2.html");
            await WaitFor(() => tt.Url.Contains("en2.html") && !tt.IsLoading, 20);
            string p3 = await PollScript(core, "document.getElementById('p3') ? document.getElementById('p3').innerText : ''", HasCyrillic, 25);
            Check(HasCyrillic(p3), "следующая страница того же сайта в этой вкладке переводится сама");

            _w.RestoreOriginal(tt);
            await Task.Delay(700);
            string orig = await core.ExecuteScriptAsync("document.getElementById('p1').innerText");
            Check(orig.Contains("court") && tt.TranslateState == TranslateState.None, "«Оригинал» возвращает текст страницы");

            await _w.ShowSelectionTranslationAsync("Good evening, the meeting of creditors will take place tomorrow.");
            Line("ИНФО перевод выделенного: " + _w.SelectionTranslationForTest);
            Check(HasCyrillic(_w.SelectionTranslationForTest), "перевод выделенного текста");
            await Task.Delay(400);
            Capture(_w, "26-selection-translation");
            _w.ClosePopups();
        }
        catch (Exception ex)
        {
            Line("ИСКЛЮЧЕНИЕ в проверке перевода: " + ex);
            _failed++;
        }
    }

    /// <summary>Пароли и автозаполнение: сохранение после входа, подсказка и подстановка, импорт CSV, карточки, главный пароль.</summary>
    async Task TestPasswordsAsync()
    {
        try
        {
            var v = Shared.Vault;
            File.WriteAllText(Path.Combine(_testDir, "login.html"),
                "<!doctype html><html lang=\"ru\"><meta charset=\"utf-8\"><title>Вход</title><style>body{font:16px Segoe UI;margin:40px}input{display:block;margin:6px 0 14px;padding:8px;width:280px}</style>" +
                "<h2>Вход в личный кабинет</h2><form id=\"f\" onsubmit=\"event.preventDefault();document.title='sent'\">" +
                "<label>Логин<input id=\"user\" name=\"login\" type=\"text\"></label><label>Пароль<input id=\"pw\" name=\"password\" type=\"password\"></label>" +
                "<button id=\"go\" type=\"submit\">Войти</button></form></html>");
            var lt = await _w.NewTabAsync(null, activate: true, container: "work");
            await WaitFor(() => lt.Core != null && !lt.IsLoading, 20);
            var core = lt.Core!;
            core.SetVirtualHostNameToFolderMapping("trackers.dymka-test.org", _testDir, Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
            _w.Navigate(lt, "https://trackers.dymka-test.org/login.html");
            await WaitFor(() => lt.Url.Contains("login.html") && !lt.IsLoading, 20);
            await Task.Delay(500);

            await core.ExecuteScriptAsync("document.getElementById('user').value='semenov';document.getElementById('pw').value='Секрет-123';document.getElementById('go').click();");
            Check(await WaitFor(() => _w.SavePromptOpen, 6), "после входа на сайт предлагается сохранить пароль");
            await Task.Delay(400);
            Capture(_w, "27-save-password");
            _w.ConfirmSavePassword();
            Check(v.Data?.Passwords.Any(x => x.Host == "trackers.dymka-test.org" && x.Username == "semenov" && x.Password == "Секрет-123") == true, "пароль сохранён");
            var vaultFile = File.ReadAllText(Path.Combine(_w.DataDir, "vault.dat"));
            Check(!vaultFile.Contains("Секрет") && !vaultFile.Contains("semenov"), "в файле хранилища пароль и логин зашифрованы");

            await core.ExecuteScriptAsync("document.getElementById('user').value='semenov';document.getElementById('pw').value='Секрет-123';document.getElementById('f').requestSubmit();");
            await Task.Delay(1200);
            Check(!_w.SavePromptOpen, "уже сохранённый пароль повторно не предлагается");

            _w.Navigate(lt, "https://trackers.dymka-test.org/login.html?again=1");
            await WaitFor(() => lt.Url.Contains("again=1") && !lt.IsLoading, 20);
            await Task.Delay(600);
            lt.View!.Focus();
            await Task.Delay(300);
            await core.ExecuteScriptAsync("document.getElementById('pw').focus()");
            Check(await WaitFor(() => _w.FillRowsForTest.Contains("semenov"), 6), "у поля пароля - подсказка с сохранённым логином");
            await Task.Delay(400);
            Capture(_w, "28-fill-password");
            if (_w.FillRowsForTest.Count > 0) _w.ClickFillRowForTest(0);
            string lp = await PollScript(core, "document.getElementById('user').value+'|'+document.getElementById('pw').value", s => s.Contains("Секрет"), 5);
            Check(lp == "\"semenov|Секрет-123\"", "логин и пароль подставлены по выбору в подсказке");

            // окно паролей: импорт CSV из Chrome и удаление файла
            _w.OpenVaultWindow("passwords");
            var vw = _w.VaultWindowForTest;
            Check(vw != null, "окно «Пароли и автозаполнение» открылось");
            if (vw != null)
            {
                vw.Topmost = true;
                var csv = Path.Combine(_dir, "chrome-passwords.csv");
                File.WriteAllText(csv, "name,url,username,password,note\nfedresurs.ru,https://fedresurs.ru/,ivanov,pass1,\nbank,https://online.bank.ru/,ivanov@mail.ru,\"p,2\",\n");
                var r = vw.ImportCsv(csv);
                Check(r.error == null && r.added == 2, $"импорт CSV: новых {r.added}");
                Check(VaultWindow.DeleteFileSecurely(csv) && !File.Exists(csv), "файл CSV с паролями удалён");
                await Task.Delay(800);
                Capture(vw, "29-vault-passwords");

                vw.ShowPageForTest("cards");
                vw.FillCardEditorForTest(new Dymka.Core.Card
                {
                    Title = "Я", LastName = "Иванов", FirstName = "Иван", MiddleName = "Петрович", BirthDate = "01.02.1980", Inn = "667100000000",
                    Snils = "123-456-789 00", PassportSeries = "6500", PassportNumber = "123456", PassportIssuedBy = "ГУ МВД России по Свердловской области",
                    PassportIssueDate = "05.06.2010", PassportCode = "660-001", Phone = "+7 900 000-00-00", Email = "ivanov@example.ru",
                    Addresses =
                    {
                        new Dymka.Core.CardAddress { Label = "Домашний", Postcode = "620001", Text = "г. Екатеринбург, ул. Ленина, д. 1, кв. 1" },
                        new Dymka.Core.CardAddress { Label = "Рабочий", Postcode = "620002", Text = "г. Екатеринбург, ул. Мира, д. 2, офис 3" },
                        new Dymka.Core.CardAddress { Label = "Почтовый", Postcode = "620000", Text = "г. Екатеринбург, Главпочтамт, а/я 24" },
                    },
                });
                vw.SaveCardFromEditor();
                await Task.Delay(800);
                Capture(vw, "30-vault-card");
                vw.ShowPageForTest("security");
                await Task.Delay(500);
                Capture(vw, "31-vault-security");
                vw.Close();
            }
            Check(v.Data?.Cards.Count == 1 && v.Data.Cards[0].Addresses.Count == 3, "карточка с тремя адресами сохранена");

            // анкета: подсказка у поля и заполнение карточкой
            string L(string id, string label, string type = "text") => $"<label for=\"{id}\">{label}</label><input id=\"{id}\" type=\"{type}\">";
            File.WriteAllText(Path.Combine(_testDir, "form.html"),
                "<!doctype html><html lang=\"ru\"><meta charset=\"utf-8\"><title>Анкета</title><style>body{font:15px Segoe UI;margin:30px;columns:2}label{display:block;margin-top:8px}input{width:300px;padding:5px}</style><form>" +
                L("last", "Фамилия") + L("first", "Имя") + L("middle", "Отчество") + L("bday", "Дата рождения", "date") + L("inn", "ИНН") + L("snils", "СНИЛС") +
                L("ser", "Серия паспорта") + L("num", "Номер паспорта") + L("issued", "Кем выдан") + L("idate", "Дата выдачи") + L("code", "Код подразделения") +
                L("reg", "Адрес регистрации") + L("zip", "Индекс") + L("postal", "Почтовый адрес") + L("addr", "Адрес") + L("phone", "Телефон", "tel") +
                L("mail", "Электронная почта") + L("comment", "Комментарий") + "</form></html>");
            _w.Navigate(lt, "https://trackers.dymka-test.org/form.html");
            await WaitFor(() => lt.Url.Contains("form.html") && !lt.IsLoading, 20);
            await Task.Delay(600);
            lt.View!.Focus();
            await Task.Delay(300);
            await core.ExecuteScriptAsync("document.getElementById('last').focus()");
            Check(await WaitFor(() => _w.FillRowsForTest.Contains("Я"), 6), "в поле «Фамилия» - подсказка с карточкой «Я»");
            await Task.Delay(400);
            Capture(_w, "32-fill-card");
            if (_w.FillRowsForTest.Count > 0) _w.ClickFillRowForTest(0);
            string Q(string id) => $"document.getElementById('{id}').value";
            string got = await PollScript(core, "[" + string.Join(",", new[] { "last", "first", "middle", "bday", "inn", "snils", "ser", "num", "issued", "idate", "code", "reg", "zip", "postal", "addr", "phone", "mail", "comment" }.Select(Q)) + "].join('|')",
                s => s.Contains("Иванов"), 5);
            Line("ИНФО анкета: " + got);
            Check(got == "\"Иванов|Иван|Петрович|1980-02-01|667100000000|123-456-789 00|6500|123456|ГУ МВД России по Свердловской области|05.06.2010|660-001|" +
                        "г. Екатеринбург, ул. Ленина, д. 1, кв. 1|620001|г. Екатеринбург, Главпочтамт, а/я 24|г. Екатеринбург, ул. Ленина, д. 1, кв. 1|+7 900 000-00-00|ivanov@example.ru|\"",
                "анкета заполнена: ФИО, дата, ИНН, СНИЛС, паспорт, адрес регистрации - домашний, почтовый - почтовый, комментарий не тронут");
            await Task.Delay(300);
            Capture(_w, "33-card-filled");
            await core.ExecuteScriptAsync("document.getElementById('addr').focus()");
            await WaitFor(() => _w.FillRowsForTest.Contains("Я"), 6);
            if (_w.FillRowsForTest.Count > 0) _w.ClickFillChipForTest(0, 1);
            string addr = await PollScript(core, Q("addr"), s => s.Contains("Мира"), 5);
            Check(addr.Contains("Мира"), "для поля «Адрес» адрес выбирается в подсказке (рабочий)");

            // главный пароль: хранилище закрывается, подсказка просит его ввести
            v.SetMasterPassword("Мастер-пароль-1");
            v.Lock();
            Check(!v.IsUnlocked, "хранилище с главным паролем закрыто");
            _w.Navigate(lt, "https://trackers.dymka-test.org/login.html?locked=1");
            await WaitFor(() => lt.Url.Contains("locked=1") && !lt.IsLoading, 20);
            await Task.Delay(600);
            lt.View!.Focus();
            await core.ExecuteScriptAsync("document.getElementById('pw').focus()");
            Check(await WaitFor(() => _w.FillRowsForTest.FirstOrDefault()?.Contains("главным паролем") == true, 6), "закрытое хранилище: подсказка просит главный пароль");
            MainWindow.UnlockForTest = "неверный";
            _w.ClickFillRowForTest(0);
            Check(!v.IsUnlocked, "неверный главный пароль не открывает хранилище");
            await core.ExecuteScriptAsync("document.getElementById('user').focus();document.getElementById('pw').focus()");
            await WaitFor(() => _w.FillRowsForTest.Count > 0, 6);
            MainWindow.UnlockForTest = "Мастер-пароль-1";
            _w.ClickFillRowForTest(0);
            Check(v.IsUnlocked && await WaitFor(() => _w.FillRowsForTest.Contains("semenov"), 6), "верный главный пароль открывает хранилище, подсказка с логином");
            MainWindow.UnlockForTest = null;
            v.SetMasterPassword(null);
            _w.ClosePopups();
        }
        catch (Exception ex)
        {
            Line("ИСКЛЮЧЕНИЕ в проверке паролей: " + ex);
            _failed++;
        }
    }

    /// <summary>Подсказки при вводе (адресная строка и стартовая страница) и отсутствие подсказки карточек в поле поиска.</summary>
    async Task TestSuggestionsAsync()
    {
        try
        {
            var st = await _w.NewTabAsync(null, activate: true, container: "work");
            await WaitFor(() => st.Core != null && !st.IsLoading, 20);
            await Task.Delay(800);
            _w.TypeInAddressForTest("картотека");
            Check(await WaitFor(() => _w.SuggestionsForTest.Any(s => s.Kind == "nav" && s.Url.Contains("kad.arbitr.ru")), 6),
                "адресная строка: «картотека» - предложение перейти на kad.arbitr.ru");
            await WaitFor(() => _w.SuggestionsForTest.Any(s => s.Kind == "search"), 4);
            Line("ИНФО подсказки: " + string.Join("; ", _w.SuggestionsForTest.Select(s => s.Kind + ": " + s.Text)));
            Check(_w.SuggestionsForTest.Any(s => s.Kind == "search"), "поисковые подсказки (" + _w.Settings.Engine.Name + ")");
            Capture(_w, "34-suggest-address");
            _w.ClosePopups();

            var core = st.Core!;
            await core.ExecuteScriptAsync("var q=document.getElementById('q');q.focus();q.value='картотека';q.dispatchEvent(new Event('input'));");
            string first = await PollScript(core, "(document.querySelector('#sugg div')||{}).innerText||''", v => v.Contains("Картотека"), 8);
            Check(first.Contains("Картотека"), "стартовая страница: подсказка «Перейти: Картотека…» - " + first);
            await Task.Delay(300);
            Capture(_w, "35-suggest-start");
            Check(_w.FillRowsForTest.Count == 0, "в поле поиска стартовой страницы подсказки карточек нет");
            await core.ExecuteScriptAsync("document.getElementById('q').value='';document.getElementById('q').blur();");
        }
        catch (Exception ex)
        {
            Line("ИСКЛЮЧЕНИЕ в проверке подсказок: " + ex);
            _failed++;
        }
    }

    /// <summary>Сертификаты Минцифры: тестовый «российский» сайт на этом компьютере с сертификатом от тестового корневого центра.</summary>
    async Task TestRussianCaAsync()
    {
        System.Net.Sockets.TcpListener? listener = null;
        var cts = new CancellationTokenSource();
        try
        {
            using var rootKey = System.Security.Cryptography.RSA.Create(2048);
            using var leafKey = System.Security.Cryptography.RSA.Create(2048);
            var rrq = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=Dymka Test Russian Root", rootKey,
                System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
            rrq.CertificateExtensions.Add(new System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension(true, false, 0, true));
            rrq.CertificateExtensions.Add(new System.Security.Cryptography.X509Certificates.X509KeyUsageExtension(
                System.Security.Cryptography.X509Certificates.X509KeyUsageFlags.KeyCertSign | System.Security.Cryptography.X509Certificates.X509KeyUsageFlags.CrlSign, true));
            using var root = rrq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2));
            var lrq = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=rutest.dymka-test.ru", leafKey,
                System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
            var san = new System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder();
            san.AddDnsName("rutest.dymka-test.ru");
            san.AddDnsName("rutest.dymka-test.com");
            lrq.CertificateExtensions.Add(san.Build());
            lrq.CertificateExtensions.Add(new System.Security.Cryptography.X509Certificates.X509KeyUsageExtension(
                System.Security.Cryptography.X509Certificates.X509KeyUsageFlags.DigitalSignature | System.Security.Cryptography.X509Certificates.X509KeyUsageFlags.KeyEncipherment, true));
            lrq.CertificateExtensions.Add(new System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension(
                new System.Security.Cryptography.OidCollection { new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1") }, false));
            using var leafPub = lrq.Create(root, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1), new byte[] { 7, 7, 7, 7 });
            using var leafWithKey = System.Security.Cryptography.X509Certificates.RSACertificateExtensions.CopyWithPrivateKey(leafPub, leafKey);
            var serverCert = new System.Security.Cryptography.X509Certificates.X509Certificate2(
                leafWithKey.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx), (string?)null,
                System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.Exportable);
            RussianRoots.ExtraRootsForTest.Add(new System.Security.Cryptography.X509Certificates.X509Certificate2(root.RawData));
            Check(RussianRoots.Roots.Count >= 2 && RussianRoots.Intermediates.Count == 2 &&
                  RussianRoots.Intermediates.All(c => c.Issuer.Contains("Russian Trusted Root CA")),
                "встроены корневой и оба выпускающих сертификата Минцифры (Sub CA и Sub CA 2024)");

            listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            var l = listener;
            _ = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    System.Net.Sockets.TcpClient c;
                    try { c = await l.AcceptTcpClientAsync(cts.Token); } catch { break; }
                    _ = Task.Run(() => ServeTls(c, serverCert));
                }
            });

            var tab = await _w.NewTabAsync(null, activate: true, container: "bank");
            await WaitFor(() => tab.Core != null && !tab.IsLoading, 20);
            var core = tab.Core!;
            _w.Navigate(tab, $"https://rutest.dymka-test.ru:{port}/");
            bool ok = await WaitFor(() => core.DocumentTitle == "Российский сертификат", 20);
            Check(ok, "сайт .ru с сертификатом «Минцифры» (тестовый центр) открывается");
            Check(MainWindow.UsesRussianCa("rutest.dymka-test.ru"), "браузер отметил сайт как защищённый сертификатом Минцифры");
            await Task.Delay(500);
            _w.OpenPrivacyPanel();
            await Task.Delay(700);
            Capture(_w, "36-russian-ca");
            _w.ClosePopups();

            _w.Navigate(tab, $"https://rutest.dymka-test.com:{port}/");
            await Task.Delay(6000);
            Line("ИНФО сайт .com с тем же сертификатом: заголовок «" + core.DocumentTitle + "»");
            Check(core.DocumentTitle != "Российский сертификат" && !MainWindow.UsesRussianCa("rutest.dymka-test.com"),
                "сайт не в российской зоне с таким сертификатом не открывается (сертификаты Минцифры действуют только для .ru, .рф, .su, .рус)");
            Capture(_w, "37-russian-ca-com-blocked");
        }
        catch (Exception ex)
        {
            Line("ИСКЛЮЧЕНИЕ в проверке сертификатов: " + ex);
            _failed++;
        }
        finally
        {
            cts.Cancel();
            try { listener?.Stop(); } catch { }
            RussianRoots.ExtraRootsForTest.Clear();
        }
    }

    /// <summary>Скачивание файлов сертификатов (.cer/.crt): обычной ссылкой, ссылкой в новое окно, через скрытый фрейм
    /// и с заголовком «вложение». Браузер не должен закрываться.</summary>
    async Task TestDownloadsAsync()
    {
        System.Net.Sockets.TcpListener? listener = null;
        var cts = new CancellationTokenSource();
        try
        {
            byte[] cert;
            using (var rs = typeof(App).Assembly.GetManifestResourceStream("Dymka.ru-root.crt")!)
            using (var ms = new MemoryStream()) { rs.CopyTo(ms); cert = ms.ToArray(); }
            listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            var l = listener;
            _ = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    System.Net.Sockets.TcpClient c;
                    try { c = await l.AcceptTcpClientAsync(cts.Token); } catch { break; }
                    _ = Task.Run(() => ServeDownload(c, cert));
                }
            });

            // нажатия из скрипта - не нажатия человека: разрешаем тестовой странице несколько скачиваний подряд без вопроса
            _w.Settings.SitePermissions[Dymka.Core.AppSettings.PermissionKey("127.0.0.1", "downloads")] = true;
            var tab = await _w.NewTabAsync(null, activate: true, container: "work");
            await WaitFor(() => tab.Core != null && !tab.IsLoading, 20);
            var core = tab.Core!;
            _w.Navigate(tab, $"http://127.0.0.1:{port}/");
            Check(await WaitFor(() => core.DocumentTitle == "Скачать сертификаты", 20), "страница со ссылками на сертификаты открылась");
            var cases = new (string id, string what)[]
            {
                ("direct", "обычной ссылкой"), ("blank", "ссылкой в новом окне"), ("frame", "через скрытый фрейм"), ("attach", "как вложение"),
            };
            var dlDir = Path.Combine(_dir, "downloads");
            Directory.CreateDirectory(dlDir);
            _w.Settings.DownloadFolder = dlDir;   // папка из настроек (окно «Сохранить как» в самопроверке не открывается)
            foreach (var (id, what) in cases)
            {
                int before = _w.Downloads, tabs = _w.Tabs.Count;
                if (_w.ActiveTab != tab) _w.ActivateForTest(tab);
                await core.ExecuteScriptAsync($"document.getElementById('{id}').click()");
                bool done = await WaitFor(() => _w.Downloads > before, 20);
                await Task.Delay(1500);
                Check(done, $"файл сертификата скачался {what}");
                Check(Shared.Windows.Contains(_w) && _w.IsLoaded, $"браузер не закрылся после скачивания {what}");
                if (id == "blank")
                {
                    Check(await WaitFor(() => _w.Tabs.Count == tabs, 10) && _w.ActiveTab == tab,
                        "вкладка, открытая ради скачивания, закрылась после него, вернулась исходная");
                }
                Line($"ИНФО вкладок до {tabs}, после {_w.Tabs.Count}");
            }
            var saved = Directory.GetFiles(dlDir);
            Check(saved.Length >= cases.Length, $"файлы сохранены в папку из настроек: {saved.Length} шт. ({string.Join(", ", saved.Select(Path.GetFileName))})");
            _w.Settings.DownloadFolder = "";
            await Task.Delay(500);
            Capture(_w, "39-downloads");
            _w.CloseTab(tab, remember: false);
        }
        catch (Exception ex)
        {
            Line("ИСКЛЮЧЕНИЕ в проверке скачивания: " + ex);
            _failed++;
        }
        finally
        {
            cts.Cancel();
            try { listener?.Stop(); } catch { }
        }
    }

    /// <summary>Звук на вкладке: значок и выключение звука; мини-окно с видео при переходе на другую вкладку.</summary>
    async Task TestAudioAndPipAsync()
    {
        try
        {
            var tab = await _w.NewTabAsync(null, activate: true, container: "work");
            await WaitFor(() => tab.Core != null && !tab.IsLoading, 20);
            var core = tab.Core!;
            core.NavigateToString("<!doctype html><meta charset=\"utf-8\"><title>Видео со звуком</title><body style=\"margin:20px;font:15px Segoe UI\">" +
                "<canvas id=\"c\" width=\"320\" height=\"180\" style=\"display:none\"></canvas><video id=\"v\" width=\"480\" height=\"270\" playsinline></video>" +
                "<script>var c=document.getElementById('c'),x=c.getContext('2d'),n=0;setInterval(function(){n++;x.fillStyle='hsl('+(n*7%360)+',70%,50%)';" +
                "x.fillRect(0,0,320,180);x.fillStyle='#fff';x.font='40px Segoe UI';x.fillText('Дымка '+n,40,100);},40);" +
                "document.getElementById('v').srcObject=c.captureStream(25);</script></body>");
            Check(await WaitFor(() => core.DocumentTitle == "Видео со звуком", 15), "страница с видео открылась");
            // запуск видео и звука «нажатием пользователя»
            await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", System.Text.Json.JsonSerializer.Serialize(new
            {
                expression = "(async()=>{var a=new AudioContext();var o=a.createOscillator();var g=a.createGain();g.gain.value=0.05;o.connect(g);g.connect(a.destination);o.start();" +
                             "await a.resume();var v=document.getElementById('v');v.muted=false;await v.play();return 'ok';})()",
                userGesture = true, awaitPromise = true, returnByValue = true,
            }));
            Check(await WaitFor(() => tab.IsPlayingAudio && tab.ShowAudio, 15), "на вкладке со звуком появился значок динамика");
            _w.ToggleMute(tab);
            Check(tab.IsMuted && core.IsMuted && tab.ShowAudio, "щелчок по значку выключает звук вкладки");
            await Task.Delay(800);
            Capture(_w, "45-tab-muted");
            _w.ToggleMute(tab);
            Check(!tab.IsMuted && !core.IsMuted, "повторный щелчок включает звук");

            var other = _w.Tabs.First(t => t != tab && !t.IsSleeping);
            _w.Activate(other);
            await WaitFor(() => tab.InPip || _w.LastPipResult.Length > 0, 15);
            Line("ИНФО мини-окно с видео: ответ страницы «" + _w.LastPipResult + "»");
            Check(tab.InPip, "при переходе на другую вкладку видео со звуком ушло в мини-окно");
            await Task.Delay(1500);
            try
            {
                var shot = Native.CaptureScreen();
                if (shot != null) { Native.SavePng(shot, Path.Combine(_dir, "46-pip-screen.png")); Line("снимок 46-pip-screen.png"); }
            }
            catch (Exception ex) { Line("снимок экрана: " + ex.Message); }
            // служебная страница Edge (так открывает настройки шестерёнка мини-окна) - вместо неё настройки «Дымки»
            int tabsBefore = _w.Tabs.Count;
            await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", System.Text.Json.JsonSerializer.Serialize(new
            {
                expression = "window.open('edge://settings/appearance/browserBehavior','_blank');1", userGesture = true, returnByValue = true,
            }));
            bool settingsOpened = await WaitFor(() => Application.Current.Windows.OfType<SettingsWindow>().Any(), 5);
            Line($"ИНФО ссылка на настройки Edge: окно настроек «Дымки» {(settingsOpened ? "открылось" : "не открылось")}, вкладок {tabsBefore} -> {_w.Tabs.Count}");
            foreach (var sw2 in Application.Current.Windows.OfType<SettingsWindow>().ToList()) sw2.Close();
            Check(_w.Tabs.Count == tabsBefore, "служебная страница Edge не открывается отдельной вкладкой или окном");

            // «Вернуться на вкладку» в мини-окне: окно закрывается, а видео продолжает играть
            await core.ExecuteScriptAsync("document.exitPictureInPicture()");
            Check(await WaitFor(() => _w.ActiveTab == tab, 8), "кнопка «Вернуться на вкладку» в мини-окне открывает вкладку с видео");
            await Task.Delay(1500);
            var inPip = await core.ExecuteScriptAsync("!!document.pictureInPictureElement");
            Check(!tab.InPip && inPip == "false", "при возвращении на вкладку видео вернулось на страницу");
            _w.CloseTab(tab, remember: false);
        }
        catch (Exception ex)
        {
            Line("ИСКЛЮЧЕНИЕ в проверке звука и мини-окна: " + ex);
            _failed++;
        }
    }

    /// <summary>Меню вкладки: закрыть вкладки справа (ниже), слева (выше), другие; перенести в новое окно.</summary>
    async Task TestTabMenuAsync()
    {
        MainWindow? w2 = null;
        try
        {
            w2 = _w.OpenWindow(null, "personal");
            await WaitFor(() => w2.IsLoaded && w2.Tabs.Count >= 1 && w2.Tabs[0].Core != null, 30);
            for (int i = 0; i < 4; i++) await w2.NewTabAsync(null, activate: false, container: "personal");
            await Task.Delay(800);
            var order = w2.VisibleTabOrderForTest;
            Check(order.Count == 5, "в новом окне 5 вкладок: " + order.Count);
            var mid = order[2];
            w2.ShowTabMenu(mid);
            await Task.Delay(600);
            Check(w2.TabMenuOpenForTest, "правый щелчок по вкладке открывает её меню");
            Capture(w2, "47-tab-menu");
            w2.ClosePopups();

            w2.CloseTabsGroup(w2.VisibleTabOrderForTest.Skip(3).ToList(), mid);
            await Task.Delay(500);
            order = w2.VisibleTabOrderForTest;
            Check(order.Count == 3 && order[^1] == mid && w2.ActiveTab == mid, "«Закрыть вкладки справа/ниже» закрыла две вкладки после выбранной");
            w2.CloseTabsGroup(order.Take(2).ToList(), mid);
            await Task.Delay(500);
            order = w2.VisibleTabOrderForTest;
            Check(order.Count == 1 && order[0] == mid, "«Закрыть вкладки слева/выше» закрыла вкладки до выбранной");
            for (int i = 0; i < 2; i++) await w2.NewTabAsync(null, activate: false, container: "personal");
            await Task.Delay(400);
            w2.CloseTabsGroup(w2.VisibleTabOrderForTest.Where(t => t != mid).ToList(), mid);
            await Task.Delay(500);
            Check(w2.VisibleTabOrderForTest.Count == 1, "«Закрыть другие вкладки» оставила одну вкладку");

            var extra = await w2.NewTabAsync(null, activate: false, container: "personal");
            int windows = Shared.Windows.Count;
            w2.MoveTabToNewWindow(extra);
            Check(await WaitFor(() => Shared.Windows.Count == windows + 1, 15) && !w2.Tabs.Contains(extra),
                "«Перенести в новое окно» открыла вкладку в отдельном окне");
            var w3 = Shared.Windows.LastOrDefault(x => x != _w && x != w2);
            await Task.Delay(800);
            w3?.CloseByAppForTest();
        }
        catch (Exception ex)
        {
            Line("ИСКЛЮЧЕНИЕ в проверке меню вкладки: " + ex);
            _failed++;
        }
        finally
        {
            try { w2?.CloseByAppForTest(); } catch { }
            await Task.Delay(800);
        }
    }

    /// <summary>Обои новой вкладки и перетаскивание вкладок.</summary>
    async Task TestWallpaperAndDragAsync()
    {
        try
        {
            // картинка-градиент для обоев
            var file = Path.Combine(_dir, "test-wallpaper.png");
            var wb = new System.Windows.Media.Imaging.WriteableBitmap(640, 360, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
            var px = new byte[640 * 360 * 4];
            for (int y = 0; y < 360; y++)
                for (int x = 0; x < 640; x++)
                {
                    int i = (y * 640 + x) * 4;
                    px[i] = (byte)(230 + x * 25 / 640); px[i + 1] = (byte)(205 + y * 40 / 360); px[i + 2] = (byte)(245 - x * 20 / 640); px[i + 3] = 255;
                }
            wb.WritePixels(new Int32Rect(0, 0, 640, 360), px, 640 * 4, 0);
            Native.SavePng(wb, file);
            var wallDir = Path.Combine(_w.DataDir, "ui", "wallpapers");
            Check(Directory.Exists(wallDir) && Directory.GetFiles(wallDir, "dymka-*.jpg").Length == 6, "стандартные фоны распакованы (6 шт.)");
            var tab = await _w.NewTabAsync(null, activate: true);
            await WaitFor(() => tab.Core != null && !tab.IsLoading, 20);
            var core = tab.Core!;
            async Task<string> Js(string js) => await core.ExecuteScriptAsync(js);
            async Task<bool> JsWait(string js, int seconds = 8)
            {
                for (int i = 0; i < seconds * 4; i++) { if (await Js(js) == "true") return true; await Task.Delay(250); }
                return false;
            }
            Check(await JsWait("getComputedStyle(document.body).backgroundImage.indexOf('wallpapers/dymka-0-haze')>=0"), "по умолчанию на новой вкладке голубая дымка");
            Check(await JsWait("document.body.classList.contains('clock-on')&&document.getElementById('time').textContent.length>=4"), "на новой вкладке часы и дата");
            await Js("document.getElementById('ntpBtn').click()");
            Check(await JsWait("!document.getElementById('ntp').hidden"), "кнопка внизу справа открывает меню новой вкладки");
            await Task.Delay(500);
            Capture(_w, "48-ntp-menu");
            await Js("document.getElementById('nextBg').click()");
            Check(await JsWait("getComputedStyle(document.body).backgroundImage.indexOf('wallpapers/dymka-1')>=0&&!document.getElementById('ntp').hidden"),
                "стрелка меняет фон на вкладке и в миниатюре, меню не закрывается");
            Check(_w.Settings.Wallpaper.StartsWith("wallpapers/dymka-1"), "выбранный фон запомнен: " + _w.Settings.Wallpaper);
            await Js("document.getElementById('prevBg').click()");
            Check(await JsWait("getComputedStyle(document.body).backgroundImage.indexOf('wallpapers/dymka-0-haze')>=0"), "стрелка назад возвращает предыдущий фон");
            await Js("document.getElementById('optClock').click()");
            Check(await JsWait("!document.body.classList.contains('clock-on')") && !_w.Settings.ShowClock, "часы выключаются переключателем");
            await Js("document.getElementById('optClock').click()");
            // сведения о защите скрываются кнопкой внизу слева
            await Js("document.getElementById('ntpBtn').click()");
            await Js("document.getElementById('statsBtn').click()");
            Check(await JsWait("document.body.classList.contains('stats-off')&&getComputedStyle(document.querySelector('.stats')).display==='none'") && !_w.Settings.ShowStartStats,
                "кнопка внизу слева скрывает сведения о трекерах, спящих вкладках и cookie");
            await Task.Delay(400);
            Capture(_w, "48b-stats-hidden");
            await Js("document.getElementById('statsBtn').click()");
            Check(await JsWait("!document.body.classList.contains('stats-off')") && _w.Settings.ShowStartStats, "и показывает их снова");
            // плитки: неполный ряд по центру; перетаскивание меняет порядок
            Check(await JsWait("(()=>{var g=document.getElementById('grid'),t=[...g.children];if(!t.length)return false;" +
                "var gr=g.getBoundingClientRect(),a=t[0].getBoundingClientRect(),b=t[t.length-1].getBoundingClientRect();" +
                "return getComputedStyle(g).justifyContent==='center'&&Math.abs((a.left-gr.left)-(gr.right-b.right))<4;})()"), "плитки сайтов стоят по центру");
            var before = string.Join(",", _w.Settings.ShortcutsFor(tab.Container).Select(x => x.Url));
            if (_w.Settings.ShortcutsFor(tab.Container).Count >= 3)
            {
                await Js("(()=>{var t=[...document.querySelectorAll('.tile[data-url]')],dt=new DataTransfer(),r=t[2].getBoundingClientRect();" +
                    "t[0].dispatchEvent(new DragEvent('dragstart',{bubbles:true,dataTransfer:dt}));" +
                    "t[2].dispatchEvent(new DragEvent('dragover',{bubbles:true,cancelable:true,dataTransfer:dt,clientX:r.right-2,clientY:r.top+10}));" +
                    "t[2].dispatchEvent(new DragEvent('drop',{bubbles:true,cancelable:true,dataTransfer:dt,clientX:r.right-2,clientY:r.top+10}));" +
                    "t[0].dispatchEvent(new DragEvent('dragend',{bubbles:true,dataTransfer:dt}));})()");
                var expected = before.Split(',');
                var want = string.Join(",", new[] { expected[1], expected[2], expected[0] }.Concat(expected.Skip(3)));
                bool moved = await WaitFor(() => string.Join(",", _w.Settings.ShortcutsFor(tab.Container).Select(x => x.Url)) == want, 5);
                Check(moved, "перетаскивание плитки меняет порядок сайтов и сохраняет его");
                Dymka.Core.AppSettings.ReorderShortcuts(_w.Settings.ShortcutsFor(tab.Container), expected);
                _w.Settings.Save();
            }
            await Js("document.getElementById('optBg').click()");
            Check(await JsWait("!document.body.classList.contains('wp')") && !_w.Settings.WallpaperOn, "фон выключается переключателем");
            await Js("document.getElementById('optBg').click()");
            Check(await JsWait("document.body.classList.contains('wp')") && _w.Settings.WallpaperOn, "и включается обратно");
            await Js("document.getElementById('optDaily').click()");
            Check(await JsWait("document.getElementById('optDaily').checked&&!document.getElementById('optSeasonal').disabled") && _w.Settings.WallpaperDaily,
                "«Новый фон каждый день» включается, становится доступен переключатель времён года");
            await Js("document.getElementById('optDaily').click()");

            _w.AddCustomWallpaper(file);
            Check(_w.Settings.Wallpaper.StartsWith("wallpapers/custom-") && File.Exists(Path.Combine(_w.DataDir, "ui", _w.Settings.Wallpaper.Replace('/', '\\'))),
                "свой фон скопирован в папку фонов: " + _w.Settings.Wallpaper);
            Check(await JsWait("getComputedStyle(document.body).backgroundImage.indexOf('custom-')>=0"), "свой фон сразу показан на новой вкладке");
            // тёмное оформление: окно и новая вкладка перекрашиваются сразу
            ThemeManager.Apply("dark");
            Check(await JsWait("matchMedia('(prefers-color-scheme: dark)').matches"), "в тёмном оформлении страницы получают тёмную схему");
            var surf = ((System.Windows.Media.SolidColorBrush)System.Windows.Application.Current.Resources["Surface"]).Color;
            Check(ThemeManager.IsDark && surf.R < 0x40, $"в тёмном оформлении окно тёмное ({surf})");
            await Task.Delay(800);
            Capture(_w, "50-dark-start");
            var sw = _w.OpenSettingsForTest();
            await Task.Delay(800);
            Capture(sw, "51-dark-settings");
            sw.Close();
            ThemeManager.Apply("light");
            Check(await JsWait("!matchMedia('(prefers-color-scheme: dark)').matches") && !ThemeManager.IsDark, "светлое оформление возвращается");
            await Js("document.getElementById('ntpBtn').click()");
            _w.CloseTab(tab, remember: false);

            // галерея фонов (каталог в репозитории «Дымки»)
            _w.OpenGallery();
            var g = _w.Tabs.FirstOrDefault(t => Dymka.Core.UrlTools.IsGalleryPage(t.Url));
            Check(g != null, "галерея фонов открылась во вкладке");
            if (g != null)
            {
                await WaitFor(() => g.Core != null && !g.IsLoading, 20);
                var gc = g.Core!;
                bool loaded = false;
                for (int i = 0; i < 60 && !loaded; i++) { await Task.Delay(500); loaded = await gc.ExecuteScriptAsync("document.querySelectorAll('#catalog .card').length>5") == "true"; }
                var msg = await gc.ExecuteScriptAsync("document.getElementById('msg').hidden?'':document.getElementById('msg').textContent");
                Check(loaded, "в галерее показан каталог фонов " + msg);
                if (loaded)
                {
                    await Task.Delay(1500);
                    Capture(_w, "49-gallery");
                    await gc.ExecuteScriptAsync("[...document.querySelectorAll('#chips button')].find(b=>b.textContent==='Зима').click()");
                    await Task.Delay(400);
                    var winter = await gc.ExecuteScriptAsync("(()=>{var c=[...document.querySelectorAll('#catalog .card')];return c.length>0&&c.every(x=>x.querySelector('.tag')&&x.querySelector('.tag').textContent==='Зима');})()");
                    Check(winter == "true", "фильтр «Зима» в галерее оставляет только зимние фоны");
                    foreach (var (tag, label) in new[] { ("asia", "Азия"), ("landscape", "Пейзажи"), ("cities", "Города"), ("abstract", "Абстракции"), ("flowers", "Цветы") })
                    {
                        var r = await gc.ExecuteScriptAsync("(()=>{var b=document.querySelector('#chips button[data-filter=\"tag:" + tag + "\"]');if(!b)return 'нет кнопки';b.click();" +
                            "var c=[...document.querySelectorAll('#catalog .card')];return c.length>0&&c.every(x=>x.dataset.tags.split(' ').indexOf('" + tag + "')>=0)?'ok '+c.length:'ошибка '+c.length;})()");
                        var rr = r.Trim('"');
                        Check(rr.StartsWith("ok"), $"подборка «{label}» в галерее: {rr}");
                        if (tag == "asia") { await Task.Delay(800); Capture(_w, "49b-gallery-asia"); }
                    }
                    await gc.ExecuteScriptAsync("document.querySelector('#chips button').click()");
                    await Task.Delay(300);
                    // предпросмотр: щелчок по картинке, стрелка вправо, закрытие по Esc
                    await gc.ExecuteScriptAsync("document.querySelectorAll('#catalog .card .img')[1].click()");
                    await Task.Delay(300);
                    var pv1 = await gc.ExecuteScriptAsync("document.getElementById('pv').hidden?'':document.querySelector('#pv .bar b').textContent");
                    await gc.ExecuteScriptAsync("document.querySelector('#pv .nav.next').click()");
                    await Task.Delay(1500);
                    var pv2 = await gc.ExecuteScriptAsync("document.querySelector('#pv .bar b').textContent");
                    Check(pv1.Length > 2 && pv2.Length > 2 && pv1 != pv2, $"предпросмотр фона открывается и листается стрелкой: {pv1} → {pv2}");
                    Capture(_w, "49c-gallery-preview");
                    await gc.ExecuteScriptAsync("document.dispatchEvent(new KeyboardEvent('keydown',{key:'Escape'}))");
                    await Task.Delay(200);
                    Check(await gc.ExecuteScriptAsync("document.getElementById('pv').hidden") == "true", "предпросмотр закрывается по Esc");
                    // в просмотре: «Скачать» кладёт фон в папку, не ставя его, затем «Удалить» убирает
                    var lname = (await gc.ExecuteScriptAsync("(()=>{var c=[...document.querySelectorAll('#catalog .card')].find(x=>x.dataset.local.indexOf('gallery-')===0&&!x.querySelector('.del'));if(!c)return '';c.querySelector('.img').click();return c.dataset.local;})()")).Trim('"');
                    if (lname.Length > 0)
                    {
                        var lpath = Path.Combine(_w.DataDir, "ui", "wallpapers", lname);
                        var wpBefore = _w.Settings.Wallpaper;
                        await Task.Delay(300);
                        Check(await gc.ExecuteScriptAsync("document.getElementById('pvFile').textContent") == "\"Скачать\"", "в просмотре нескачанного фона - кнопка «Скачать»");
                        await gc.ExecuteScriptAsync("document.getElementById('pvFile').click()");
                        Check(await WaitFor(() => File.Exists(lpath), 20) && _w.Settings.Wallpaper == wpBefore, "«Скачать» сохраняет фон в папку и не меняет текущий фон");
                        bool del = false;
                        for (int i = 0; i < 20 && !del; i++) { await Task.Delay(300); del = await gc.ExecuteScriptAsync("document.getElementById('pvFile').textContent") == "\"Удалить\""; }
                        Check(del, "после скачивания кнопка становится «Удалить»");
                        await Task.Delay(300);
                        Capture(_w, "49d-gallery-preview-delete");
                        await gc.ExecuteScriptAsync("document.getElementById('pvFile').click()");
                        Check(await WaitFor(() => !File.Exists(lpath), 10), "«Удалить» убирает скачанный фон с компьютера");
                        await gc.ExecuteScriptAsync("document.dispatchEvent(new KeyboardEvent('keydown',{key:'Escape'}))");
                    }
                    await gc.ExecuteScriptAsync("var c=[...document.querySelectorAll('#catalog .card')].find(x=>x.dataset.local.indexOf('gallery-')===0);c.querySelector('.btn.primary').click()");
                    Check(await WaitFor(() => _w.Settings.Wallpaper.StartsWith("wallpapers/gallery-") &&
                                              File.Exists(Path.Combine(_w.DataDir, "ui", _w.Settings.Wallpaper.Replace('/', '\\'))), 30),
                        "фон из галереи скачан в папку фонов и поставлен: " + _w.Settings.Wallpaper);
                }
                _w.CloseTab(g, remember: false);
            }

            // перетаскивание: первая вкладка встаёт на место третьей
            var order = _w.VisibleTabOrderForTest;
            if (order.Count >= 3)
            {
                var a = order[0]; var c = order[2];
                _w.MoveTabTo(a, c);
                var after = _w.VisibleTabOrderForTest;
                Check(after.IndexOf(a) == 2 && after.IndexOf(c) == 1, "перетаскивание вкладки меняет порядок вкладок");
                _w.MoveTabTo(a, after[0]);
                Check(_w.VisibleTabOrderForTest[0] == a, "вкладку можно вернуть на прежнее место");
            }
            else Line("ИНФО для проверки перетаскивания мало вкладок: " + order.Count);
        }
        catch (Exception ex)
        {
            Line("ИСКЛЮЧЕНИЕ в проверке обоев и перетаскивания: " + ex);
            _failed++;
        }
    }

    static async Task ServeDownload(System.Net.Sockets.TcpClient client, byte[] cert)
    {
        using (client)
        {
            try
            {
                var st = client.GetStream();
                var buf = new byte[8192];
                int n = await st.ReadAsync(buf);
                var req = Encoding.ASCII.GetString(buf, 0, n);
                var path = req.Split(' ').Length > 1 ? req.Split(' ')[1] : "/";
                byte[] body;
                string head;
                if (path == "/" || path.StartsWith("/?"))
                {
                    body = Encoding.UTF8.GetBytes("<!doctype html><meta charset=\"utf-8\"><title>Скачать сертификаты</title><body style=\"font:16px Segoe UI;margin:40px\">" +
                        "<p><a id=\"direct\" href=\"/russian_trusted_root_ca.cer\">Корневой</a></p>" +
                        "<p><a id=\"blank\" target=\"_blank\" href=\"/russian_trusted_sub_ca.cer\">Выпускающий (в новом окне)</a></p>" +
                        "<p><button id=\"frame\" onclick=\"var f=document.createElement('iframe');f.style.display='none';f.src='/sub_2024.crt';document.body.appendChild(f)\">Через фрейм</button></p>" +
                        "<p><a id=\"attach\" href=\"/attach.crt\">Вложение</a></p></body>");
                    head = $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
                }
                else
                {
                    body = cert;
                    string disp = path.StartsWith("/attach") ? "Content-Disposition: attachment; filename=\"russian_trusted_sub_ca_2024.crt\"\r\n" : "";
                    head = $"HTTP/1.1 200 OK\r\nContent-Type: application/x-x509-ca-cert\r\n{disp}Content-Length: {body.Length}\r\nConnection: close\r\n\r\n";
                }
                await st.WriteAsync(Encoding.ASCII.GetBytes(head));
                await st.WriteAsync(body);
                await st.FlushAsync();
            }
            catch { }
        }
    }

    static async Task ServeTls(System.Net.Sockets.TcpClient client, System.Security.Cryptography.X509Certificates.X509Certificate2 cert)
    {
        using (client)
        using (var ssl = new System.Net.Security.SslStream(client.GetStream(), false))
        {
            try
            {
                await ssl.AuthenticateAsServerAsync(new System.Net.Security.SslServerAuthenticationOptions
                {
                    ServerCertificate = cert,
                    ApplicationProtocols = new List<System.Net.Security.SslApplicationProtocol> { System.Net.Security.SslApplicationProtocol.Http11 },
                });
                var buf = new byte[8192];
                await ssl.ReadAsync(buf);
                var body = Encoding.UTF8.GetBytes("<!doctype html><meta charset=\"utf-8\"><title>Российский сертификат</title><body style=\"font:16px Segoe UI;margin:40px\"><h1>Тестовый сайт с сертификатом «Минцифры»</h1></body>");
                var head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await ssl.WriteAsync(head);
                await ssl.WriteAsync(body);
                await ssl.FlushAsync();
            }
            catch { }
        }
    }

    /// <summary>Банковские карты: вкладка в окне, подстановка на странице оплаты и в форме оплаты во фрейме; CVC не подставляется.</summary>
    async Task TestBankCardsAsync()
    {
        try
        {
            _w.OpenVaultWindow("bankcards");
            var vw = _w.VaultWindowForTest;
            if (vw != null)
            {
                vw.Topmost = true;
                vw.FillBankEditorForTest("Сбер зарплатная", "2202200012345672", "ivan ivanov", "0528", "СберБанк");
                Check(vw.BankNumberTextForTest == "2202 2000 1234 5672", "номер карты при вводе сразу разбивается по 4 цифры: " + vw.BankNumberTextForTest);
                Check(vw.BankExpTextForTest == "05/28", "в сроке косая черта ставится сама: " + vw.BankExpTextForTest);
                Check(vw.SaveBankFromEditor(askOnTypo: false), "банковская карта сохранена");
                await Task.Delay(700);
                Capture(vw, "38-vault-bank");
                vw.Close();
            }
            var bc = Shared.Vault.Data?.BankCards.FirstOrDefault();
            Check(bc != null && bc.Number == "2202200012345672" && bc.ExpYear == 2028 && bc.Holder == "IVAN IVANOV" && bc.Bank == "СберБанк", "данные карты: номер, срок, владелец, банк (CVC не хранится)");
            var vaultFile = File.ReadAllText(Path.Combine(_w.DataDir, "vault.dat"));
            Check(!vaultFile.Contains("2202200012345672") && !vaultFile.Contains("5672"), "номер карты в файле зашифрован");

            string Field(string id, string label, string type = "text", string extra = "") => $"<label for=\"{id}\">{label}</label><input id=\"{id}\" type=\"{type}\" {extra}>";
            var payFields = Field("num", "Номер карты", "tel") + Field("exp", "Срок действия", "text", "placeholder=\"ММ/ГГ\"") +
                            Field("holder", "Имя владельца карты") + Field("cvc", "CVC", "password", "maxlength=\"3\"");
            File.WriteAllText(Path.Combine(_testDir, "pay.html"),
                "<!doctype html><html lang=\"ru\"><meta charset=\"utf-8\"><title>Оплата</title><style>body{font:15px Segoe UI;margin:30px}label{display:block;margin-top:10px}input{width:300px;padding:6px}</style>" +
                "<h2>Оплата заказа</h2><form>" + payFields + "</form></html>");
            File.WriteAllText(Path.Combine(_testDir, "payframe.html"),
                "<!doctype html><html lang=\"ru\"><meta charset=\"utf-8\"><style>body{font:14px Segoe UI;margin:0;padding:12px 20px}label{display:block;margin-top:6px}input{width:300px;padding:6px;height:22px}</style><form>" + payFields +
                "</form><script>document.addEventListener('input',function(){parent.postMessage('pay:'+document.getElementById('num').value+'|'+document.getElementById('exp').value+'|'+document.getElementById('holder').value+'|'+document.getElementById('cvc').value,'*');},true);</script></html>");
            File.WriteAllText(Path.Combine(_testDir, "payhost.html"),
                "<!doctype html><html lang=\"ru\"><meta charset=\"utf-8\"><title>Магазин</title><style>body{margin:0;font:15px Segoe UI}iframe{position:absolute;left:40px;top:60px;width:440px;height:330px;border:1px solid #ccc}</style>" +
                "<h3 style=\"margin:20px 40px\">Магазин: форма оплаты банка во фрейме</h3><iframe src=\"https://other.dymka-test.net/payframe.html\"></iframe>" +
                "<script>window.addEventListener('message',function(e){if(typeof e.data==='string'&&e.data.indexOf('pay:')===0)document.title=e.data;});</script></html>");

            var pt = await _w.NewTabAsync(null, activate: true, container: "personal");
            await WaitFor(() => pt.Core != null && !pt.IsLoading, 20);
            var core = pt.Core!;
            core.SetVirtualHostNameToFolderMapping("trackers.dymka-test.org", _testDir, Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
            core.SetVirtualHostNameToFolderMapping("other.dymka-test.net", _testDir, Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
            _w.Navigate(pt, "https://trackers.dymka-test.org/pay.html");
            await WaitFor(() => pt.Url.Contains("pay.html") && !pt.IsLoading, 20);
            await Task.Delay(600);
            pt.View!.Focus();
            await Task.Delay(300);
            await core.ExecuteScriptAsync("document.getElementById('num').focus()");
            Check(await WaitFor(() => _w.FillRowsForTest.Contains("Сбер зарплатная"), 6), "в поле «Номер карты» - подсказка с банковской картой");
            await Task.Delay(400);
            Capture(_w, "39-fill-bankcard");
            if (_w.FillRowsForTest.Count > 0) _w.ClickFillRowForTest(0);
            string got = await PollScript(core, "['num','exp','holder','cvc'].map(function(i){return document.getElementById(i).value}).join('|')+'|'+document.activeElement.id", s => s.Contains("2202"), 5);
            Check(got == "\"2202200012345672|05/28|IVAN IVANOV||cvc\"", "оплата: номер, срок и владелец подставлены, CVC пустой и курсор в нём: " + got);
            await Task.Delay(300);
            Capture(_w, "40-bankcard-filled");

            // форма оплаты банка во встроенном окне (фрейме) другого сайта
            _w.Navigate(pt, "https://trackers.dymka-test.org/payhost.html");
            await WaitFor(() => pt.Url.Contains("payhost.html") && !pt.IsLoading, 20);
            await Task.Delay(1500);
            double zoom = 1;
            try { zoom = pt.View.ZoomFactor; } catch { }
            // поле номера во фрейме: фрейм с (41,61), поле - примерно (20+150, 12+20+17) внутри
            var screenPt = pt.View.PointToScreen(new Point((41 + 170) * zoom, (61 + 52) * zoom));
            Native.ClickAt(screenPt);
            bool frameHint = await WaitFor(() => _w.FillRowsForTest.Contains("Сбер зарплатная"), 6);
            Check(frameHint, "форма оплаты во фрейме: подсказка с картой");
            await Task.Delay(300);
            Capture(_w, "41-fill-bankcard-frame");
            if (frameHint)
            {
                _w.ClickFillRowForTest(0);
                await WaitFor(() => core.DocumentTitle.StartsWith("pay:2202"), 6);
                await Task.Delay(500);
                Line("ИНФО форма во фрейме: " + core.DocumentTitle);
                Check(core.DocumentTitle.StartsWith("pay:2202200012345672|05/28|IVAN IVANOV|"), "форма оплаты во фрейме заполнена (CVC - нет)");
                Check(core.DocumentTitle.EndsWith("|"), "CVC во фрейме не подставлен");
            }
            _w.ClosePopups();

            // окно в окне (как у Яндекс Пэй): магазин -> фрейм платёжного сервиса -> фрейм банка с полями
            File.WriteAllText(Path.Combine(_testDir, "payouter.html"),
                "<!doctype html><meta charset=\"utf-8\"><style>body{margin:0}iframe{position:absolute;left:10px;top:10px;width:420px;height:300px;border:0}</style>" +
                "<iframe src=\"https://third.dymka-test.org/payframe.html\"></iframe><script>window.addEventListener('message',function(e){parent.postMessage(e.data,'*');});</script>");
            File.WriteAllText(Path.Combine(_testDir, "payhost2.html"),
                "<!doctype html><html lang=\"ru\"><meta charset=\"utf-8\"><title>Магазин 2</title><style>body{margin:0;font:15px Segoe UI}iframe{position:absolute;left:40px;top:60px;width:460px;height:340px;border:1px solid #ccc}</style>" +
                "<h3 style=\"margin:20px 40px\">Окно в окне: платёжный сервис и банк</h3><iframe src=\"https://other.dymka-test.net/payouter.html\"></iframe>" +
                "<script>window.addEventListener('message',function(e){if(typeof e.data==='string'&&e.data.indexOf('pay:')===0)document.title=e.data;});</script></html>");
            core.SetVirtualHostNameToFolderMapping("third.dymka-test.org", _testDir, Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
            _w.Navigate(pt, "https://trackers.dymka-test.org/payhost2.html");
            await WaitFor(() => pt.Url.Contains("payhost2.html") && !pt.IsLoading, 20);
            await Task.Delay(2000);
            Native.ClickAt(pt.View!.PointToScreen(new Point((51 + 170) * zoom, (71 + 52) * zoom)));
            bool nested = await WaitFor(() => _w.FillRowsForTest.Contains("Сбер зарплатная"), 6);
            Check(nested, "форма оплаты во вложенном фрейме (окно в окне): подсказка с картой");
            await Task.Delay(300);
            Capture(_w, "42-fill-bankcard-nested");
            if (nested)
            {
                _w.ClickFillRowForTest(0);
                await WaitFor(() => core.DocumentTitle.StartsWith("pay:2202"), 6);
                Check(core.DocumentTitle.StartsWith("pay:2202200012345672|05/28|IVAN IVANOV|"), "вложенная форма оплаты заполнена: " + core.DocumentTitle);
            }
            _w.ClosePopups();

            // каждое поле - в своём встроенном окне (так делают многие банки)
            string One(string id, string label, string type, string extra = "") =>
                "<!doctype html><meta charset=\"utf-8\"><style>body{margin:0;font:14px Segoe UI;padding:4px}input{width:260px;padding:6px}</style>" +
                $"<label for=\"{id}\">{label}</label><br><input id=\"{id}\" type=\"{type}\" {extra}>" +
                $"<script>document.addEventListener('input',function(){{parent.postMessage('f:{id}='+document.getElementById('{id}').value,'*');}},true);</script>";
            File.WriteAllText(Path.Combine(_testDir, "f-num.html"), One("num", "Номер карты", "tel"));
            File.WriteAllText(Path.Combine(_testDir, "f-exp.html"), One("exp", "Действует до", "text", "placeholder=\"ММ/ГГ\""));
            File.WriteAllText(Path.Combine(_testDir, "f-cvc.html"), One("cvc", "Код", "password", "maxlength=\"3\""));
            File.WriteAllText(Path.Combine(_testDir, "payhosted.html"),
                "<!doctype html><html lang=\"ru\"><meta charset=\"utf-8\"><title>-</title><style>body{margin:0;font:15px Segoe UI}iframe{position:absolute;left:40px;width:300px;height:64px;border:0}</style>" +
                "<h3 style=\"margin:16px 40px\">Поля карты в отдельных окнах</h3>" +
                "<iframe style=\"top:60px\" src=\"https://other.dymka-test.net/f-num.html\"></iframe>" +
                "<iframe style=\"top:130px\" src=\"https://other.dymka-test.net/f-exp.html\"></iframe>" +
                "<iframe style=\"top:200px\" src=\"https://other.dymka-test.net/f-cvc.html\"></iframe>" +
                "<script>var v={num:'',exp:'',cvc:''};window.addEventListener('message',function(e){if(typeof e.data==='string'&&e.data.indexOf('f:')===0){var p=e.data.slice(2).split('=');v[p[0]]=p[1];document.title='h:'+v.num+'|'+v.exp+'|'+v.cvc;}});</script></html>");
            _w.Navigate(pt, "https://trackers.dymka-test.org/payhosted.html");
            await WaitFor(() => pt.Url.Contains("payhosted.html") && !pt.IsLoading, 20);
            await Task.Delay(2000);
            Native.ClickAt(pt.View!.PointToScreen(new Point((40 + 4 + 130) * zoom, (60 + 4 + 20 + 14) * zoom)));
            bool hosted = await WaitFor(() => _w.FillRowsForTest.Contains("Сбер зарплатная"), 6);
            Check(hosted, "поле номера в отдельном окне: подсказка с картой");
            if (hosted)
            {
                _w.ClickFillRowForTest(0);
                await WaitFor(() => core.DocumentTitle.Contains("05/28"), 6);
                Line("ИНФО поля в отдельных окнах: " + core.DocumentTitle);
                Check(core.DocumentTitle == "h:2202200012345672|05/28|", "срок заполнен и в соседнем окне, CVC пустой");
            }
            await Task.Delay(300);
            Capture(_w, "43-bankcard-hosted");
            _w.ClosePopups();

            // новая карта, введённая на сайте: «Сохранить карту?»
            File.WriteAllText(Path.Combine(_testDir, "paynew.html"),
                "<!doctype html><html lang=\"ru\"><meta charset=\"utf-8\"><title>Новая карта</title><style>body{font:15px Segoe UI;margin:30px}label{display:block;margin-top:10px}input{width:300px;padding:6px}</style>" +
                "<form onsubmit=\"event.preventDefault();document.title='paid'\">" + payFields + "<button id=\"go\">Оплатить</button></form></html>");
            _w.Navigate(pt, "https://trackers.dymka-test.org/paynew.html");
            await WaitFor(() => pt.Url.Contains("paynew.html") && !pt.IsLoading, 20);
            await Task.Delay(500);
            await core.ExecuteScriptAsync("document.getElementById('num').value='4111 1111 1111 1111';document.getElementById('exp').value='12/29';document.getElementById('holder').value='PETR PETROV';document.getElementById('cvc').value='123';document.getElementById('go').click();");
            Check(await WaitFor(() => _w.SaveCardPromptOpen, 6), "после оплаты новой картой - предложение сохранить карту");
            await Task.Delay(400);
            Capture(_w, "44-save-card");
            _w.ConfirmSaveCard();
            var nc = Shared.Vault.Data?.BankCards.FirstOrDefault(c => c.Number == "4111111111111111");
            Check(nc != null && nc.ExpMonth == 12 && nc.ExpYear == 2029 && nc.Holder == "PETR PETROV", "новая карта сохранена: номер, срок, владелец");
            Check(!File.ReadAllText(Path.Combine(_w.DataDir, "vault.dat")).Contains("4111"), "сохранённая карта зашифрована");
            Check(!System.Text.Json.JsonSerializer.Serialize(Shared.Vault.Data!.BankCards).Contains("\"123\""), "код CVC не сохранён");
            _w.ClosePopups();
        }
        catch (Exception ex)
        {
            Line("ИСКЛЮЧЕНИЕ в проверке банковских карт: " + ex);
            _failed++;
        }
    }

    /// <summary>Проверка обновлений: оба способа (API GitHub и страница выпусков) должны отвечать.
    /// Эта сборка новее опубликованной, поэтому «новых версий нет» - правильный ответ.</summary>
    async Task TestUpdateCheckAsync()
    {
        try
        {
            var api = await UpdateChecker.CheckApiAsync();
            Line("проверка обновлений через API GitHub: " + (api.Error ?? (api.Update != null ? "есть версия " + api.Update.Version : "новых версий нет")) +
                 " (отказ API из-за лимита на общем адресе сборочного сервера - не ошибка, для этого и есть запасной способ)");
            var page = await UpdateChecker.CheckPageAsync();
            Check(page.Error == null, "проверка обновлений по странице выпусков работает" + (page.Error != null ? ": " + page.Error : ""));
            var all = await UpdateChecker.CheckAsync();
            Check(all.Error == null && all.Update == null, $"проверка обновлений: у сборки {App.VersionText} новых версий нет" +
                  (all.Error != null ? " - ошибка: " + all.Error : all.Update != null ? " - неожиданно найдена " + all.Update.Version : ""));
        }
        catch (Exception ex) { Check(false, "проверка обновлений: " + ex.Message); }
    }

    /// <summary>Кнопка «Закрыть» на сайте: окно, открытое страницей, закрывает само себя (window.close()).
    /// Должна закрыться только эта вкладка, а не браузер.</summary>
    async Task TestPageCloseAsync(BrowserTab probe)
    {
        try
        {
            _w.Activate(probe);
            await Task.Delay(300);
            int tabs = _w.Tabs.Count;
            var res = await _w.EvalWithGestureForTest(probe.Core!,
                "(function(){var w=window.open('','_blank');if(!w)return 'заблокировано';w.document.write('<title>Подпись</title><button>Закрыть</button><script>setTimeout(function(){window.close()},1500)<\\/script>');w.document.close();return 'ok';})()");
            Line("окно, открытое страницей: " + res);
            bool opened = await WaitFor(() => _w.Tabs.Count > tabs, 8);
            Check(opened, "страница открыла своё окно (во вкладке)");
            await Task.Delay(400);
            Capture(_w, "52-page-popup");
            bool closed = await WaitFor(() => _w.Tabs.Count == tabs, 10);
            await Task.Delay(1500);
            Check(closed && Shared.Windows.Contains(_w) && _w.IsLoaded && _w.IsVisible,
                "кнопка «Закрыть» на сайте закрыла только вкладку страницы, браузер остался открыт");
        }
        catch (Exception ex) { Check(false, "закрытие окна страницей: " + ex.Message); }
    }

    /// <summary>Мини-плеер: страница с Media Session в фоновой вкладке, команды «следующий» и «пауза».</summary>
    async Task TestMediaBarAsync(string testDir, BrowserTab probe)
    {
        BrowserTab? other = null;
        try
        {
            File.WriteAllText(Path.Combine(testDir, "music.html"),
                "<!doctype html><meta charset=\"utf-8\"><title>Музыка</title><audio id=a></audio><script>" +
                "navigator.mediaSession.metadata=new MediaMetadata({title:'Тестовый трек',artist:'Дымка'});" +
                "navigator.mediaSession.playbackState='playing';" +
                "['play','pause','nexttrack','previoustrack'].forEach(function(a){navigator.mediaSession.setActionHandler(a,function(){" +
                "document.title='media:'+a;navigator.mediaSession.playbackState=a==='pause'?'paused':'playing';});});</script>");
            _w.Navigate(probe, "https://trackers.dymka-test.org/music.html");
            await WaitFor(() => probe.Url.Contains("music.html") && !probe.IsLoading, 20);
            await Task.Delay(500);
            var core = probe.Core!;
            Check(await core.ExecuteScriptAsync("typeof window.__dymkaMedia") == "\"function\"", "на странице есть управление мини-плеером");
            _w.MediaTestTab = probe;
            other = await _w.NewTabAsync(null, activate: true);
            bool shown = await WaitFor(() => _w.MediaBarShownForTest && _w.MediaBarTitleForTest == "Тестовый трек", 8);
            Check(shown, "мини-плеер появился, когда музыкальная вкладка ушла в фон: " + _w.MediaBarTitleForTest);
            await Task.Delay(600);
            Capture(_w, "47-media-bar");
            _w.MediaCommandForTest("next");
            Check(await WaitFor(() => core.DocumentTitle == "media:nexttrack", 6), "кнопка «следующий трек» переключает трек на сайте");
            _w.MediaCommandForTest("toggle");
            Check(await WaitFor(() => core.DocumentTitle == "media:pause", 6), "кнопка «пауза» ставит музыку на паузу");
            await Task.Delay(1500);
            Check(_w.MediaBarShownForTest, "на паузе мини-плеер остаётся (можно продолжить)");
            // светлый вид
            _w.MediaStyleForTest("light");
            await Task.Delay(500);
            Check(_w.MediaLight, "мини-плеер переключается на светлый вид");
            Capture(_w, "47b-media-light");
            _w.MediaStyleForTest("dark");
            // прилипание к левому краю: плеер вертикальный, только кнопки
            _w.MediaDropForTest(10, 120);
            await Task.Delay(600);
            var (card, page) = _w.MediaRectsForTest();
            Check(_w.MediaVerticalForTest && _w.Settings.MiniPlayerEdge == "left" && card.Left >= page.Left - 1 && card.Left - page.Left < 16 && card.Height > card.Width,
                $"у левого края мини-плеер прилипает и становится вертикальным (карточка {card.Left - page.Left:0} от края, {card.Width:0}×{card.Height:0})");
            Capture(_w, "47c-media-left");
            // смена вида вкладок: плеер остаётся у левого края области страницы и не заходит на боковой список вкладок
            var layout = _w.Settings.Layout;
            _w.Settings.Layout = layout == "side" ? "top" : "side";
            _w.ApplyLayoutForTest();
            await Task.Delay(1200);
            (card, page) = _w.MediaRectsForTest();
            Check(_w.MediaVerticalForTest && card.Left >= page.Left - 1 && card.Left - page.Left < 16 && card.Bottom <= page.Bottom + 1,
                $"после смены вида вкладок ({_w.Settings.Layout}) мини-плеер у края страницы, не на списке вкладок");
            Capture(_w, "47d-media-left-" + _w.Settings.Layout);
            _w.Settings.Layout = layout;
            _w.ApplyLayoutForTest();
            await Task.Delay(800);
            // к правому краю - тоже вертикальный; к нижнему - снова горизонтальный
            _w.MediaDropForTest(100000, 200);
            await Task.Delay(400);
            (card, page) = _w.MediaRectsForTest();
            Check(_w.MediaVerticalForTest && _w.Settings.MiniPlayerEdge == "right" && page.Right - card.Right < 40 && card.Right <= page.Right + 1,
                $"у правого края мини-плеер тоже вертикальный (до края {page.Right - card.Right:0}, {card.Width:0}×{card.Height:0})");
            _w.MediaDropForTest(300, 100000);
            await Task.Delay(400);
            (card, page) = _w.MediaRectsForTest();
            Check(!_w.MediaVerticalForTest && _w.Settings.MiniPlayerEdge == "bottom" && page.Bottom - card.Bottom < 40 && card.Bottom <= page.Bottom + 1,
                $"у нижнего края мини-плеер горизонтальный (до края {page.Bottom - card.Bottom:0}, {card.Width:0}×{card.Height:0})");
            Capture(_w, "47e-media-bottom");
            // перетаскивание в нижний левый угол с дрожанием мыши: вид не должен прыгать туда-обратно
            (card, page) = _w.MediaRectsForTest();
            double ph = page.Height, pw = page.Width;
            var path = new List<Point>();
            for (int i = 0; i <= 40; i++) path.Add(new Point(pw / 2 - i * (pw / 2 - 30) / 40, ph - 30 + (i % 2 == 0 ? 4 : -4)));
            for (int i = 0; i < 30; i++) path.Add(new Point(20 + (i % 3) * 12, ph - 20 - (i % 4) * 8));        // дрожание в самом углу
            for (int i = 0; i < 30; i++) path.Add(new Point(50 + (i % 2) * 14, ph / 2 + (i % 3) * 6));         // у границы бокового края
            int flips = _w.MediaDragPathForTest(path);
            Check(flips <= 1, $"перетаскивание к углу: вид мини-плеера не «моргает» (сменился {flips} раз)");
            _w.MediaResetForTest();
            _w.Activate(probe);
            Check(await WaitFor(() => !_w.MediaBarShownForTest, 4), "на самой музыкальной вкладке мини-плеер скрыт");
        }
        catch (Exception ex) { Check(false, "мини-плеер: " + ex.Message); }
        finally
        {
            _w.MediaTestTab = null;
            if (other != null) _w.CloseTab(other, remember: false);
            _w.Activate(probe);
        }
    }

    public async Task RunAsync()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMinutes(10));
            Line("ПРЕВЫШЕНО ВРЕМЯ САМОПРОВЕРКИ");
            Line("РЕЗУЛЬТАТ: ОШИБКА");
            Environment.Exit(3);
        });

        try
        {
            Line("Дымка " + App.VersionText + ", самопроверка");
            _w.Topmost = true;
            _w.Activate();

            Check(await WaitFor(() => _w.ActiveTab?.Core != null && _w.ActiveTab.IsLoading == false, 40),
                "движок WebView2 запущен, открыта стартовая страница");
            await Task.Delay(2500);
            Capture(_w, "01-top-start");

            var startCore = _w.ActiveTab!.Core!;
            var tiles = await startCore.ExecuteScriptAsync("document.querySelectorAll('.tile:not(.add)').length");
            Check(tiles == _w.Settings.ShortcutsFor(_w.ActiveTab!.Container).Count.ToString(), $"стартовая страница получила плитки сайтов ({tiles})");

            await TestUpdateCheckAsync();

            // страница с трекерами: счётчики Google, Яндекса, Mail.ru и пиксель Facebook должны быть заблокированы
            var testDir = Path.Combine(_dir, "web");
            _testDir = testDir;
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

            await TestMediaBarAsync(testDir, probe);
            await TestPageCloseAsync(probe);

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
            Check(await WaitFor(() => third.Core != null && third.Title.Contains("Wikipedia") && !third.IsLoading, 40),
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
            Check(await WaitFor(() => third.Core != null && !third.IsLoading && !third.IsSleeping, 40),
                "спящая вкладка проснулась при открытии");
            await Task.Delay(1500);
            Capture(_w, "06-side-woken");

            await TestTabFeaturesAsync();
            await TestTranslationAsync();
            await TestPasswordsAsync();
            await TestBankCardsAsync();
            await TestSuggestionsAsync();
            await TestRussianCaAsync();
            await TestDownloadsAsync();
            await TestAudioAndPipAsync();
            await TestTabMenuAsync();
            await TestWallpaperAndDragAsync();
            await TestSessionAndRecoveryAsync();

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
        _w.CloseByAppForTest();
    }
}
