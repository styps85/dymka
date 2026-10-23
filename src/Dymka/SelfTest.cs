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
            w2.Close();
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
            _w.SetTabSearchTextForTest("wikipedia");
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
                w3.Close();
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
            Line("ИНФО уведомление: " + _w.ToastTextForTest);
            Check(_w.ToastTextForTest.Contains("Движок перезапущен"), "уведомление «Движок перезапущен, вкладки восстановлены»");
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

    public async Task RunAsync()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMinutes(8));
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
        _w.Close();
    }
}
