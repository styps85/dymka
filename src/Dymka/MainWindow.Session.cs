using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Dymka.Core;
using Microsoft.Web.WebView2.Core;

namespace Dymka;

/// <summary>Восстановление после падения движка, сеанс (вкладки между запусками), очистка вкладок, поиск по вкладкам.</summary>
public partial class MainWindow
{
    // ===================================================================== движок и его падение

    CoreWebView2Environment? _subscribedEnv;

    /// <summary>Общий для всех окон движок; если он упал или закрылся, создаётся заново.</summary>
    async Task<CoreWebView2Environment> AcquireEnvAsync()
    {
        Shared.EnvTask ??= CreateEnvironmentAsync();
        CoreWebView2Environment env;
        try
        {
            env = await Shared.EnvTask;
        }
        catch
        {
            Shared.EnvTask = null;   // следующая попытка начнётся заново, а не с той же ошибки
            throw;
        }
        _env = env;
        if (_subscribedEnv != env)
        {
            _subscribedEnv = env;
            env.BrowserProcessExited += (_, e) =>
            {
                App.Log($"Процесс движка завершился: {e.BrowserProcessExitKind} (pid {e.BrowserProcessId})");
                lock (Shared.ExitedEnvs) Shared.ExitedEnvs.Add(env);
                if (e.BrowserProcessExitKind == CoreWebView2BrowserProcessExitKind.Failed) OnEngineLost(env);
                else ForgetEnv(env);   // обычное завершение (страниц не осталось): следующая страница запустит движок заново
            };
        }
        return env;
    }

    static void ForgetEnv(CoreWebView2Environment env)
    {
        if (Shared.EnvTask is { IsCompletedSuccessfully: true } t && t.Result == env) Shared.EnvTask = null;
    }

    bool _recovering;

    /// <summary>Сколько раз в этом окне движок перезапускался после падения (для самопроверки).</summary>
    public int EngineRestarts { get; private set; }

    void OnEngineLost(CoreWebView2Environment dead)
    {
        if (_recovering || _closingForGood || _cleanupDone) return;
        // событие приходит от каждой страницы окна - восстанавливаемся один раз
        if (_env != dead && _env != null) return;
        _recovering = true;
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            try { await RecoverEngineAsync(dead); }
            catch (Exception ex) { App.Log("Восстановление движка: " + ex); }
            finally { _recovering = false; }
        }));
    }

    async Task RecoverEngineAsync(CoreWebView2Environment dead)
    {
        App.Log("Движок упал - перезапускаю, вкладок в окне: " + _tabs.Count);
        ClosePopups();
        _printWindow?.Close();
        if (_splitLeft != null) EndSplit();

        // «мёртвые» страницы убираем, адреса и названия остаются - вкладки становятся спящими
        foreach (var t in _tabs.ToList())
        {
            t.IsLoading = false;
            if (t.View == null) continue;
            DisposeView(t);
            if (t != _active) t.IsSleeping = true;
        }
        _creating.Clear();
        // ждём, пока остатки упавшего движка освободят папку профиля (обычно доли секунды)
        var until = DateTime.Now.AddSeconds(4);
        while (DateTime.Now < until)
        {
            lock (Shared.ExitedEnvs) if (Shared.ExitedEnvs.Contains(dead)) break;
            await Task.Delay(100);
        }
        ForgetEnv(dead);
        Shared.ExtensionsSynced.Clear();

        Exception? last = null;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                await AcquireEnvAsync();
                last = null;
                break;
            }
            catch (Exception ex)
            {
                last = ex;
                App.Log($"Перезапуск движка, попытка {attempt + 1}: {ex.Message}");
                await Task.Delay(1000 * (attempt + 1));
            }
        }
        if (last != null)
        {
            RefreshViews();
            Dialogs.Show(this, "Движок браузера остановился",
                "Не удалось перезапустить движок WebView2. Закройте «Дымку» и откройте её снова - вкладки восстановятся.",
                new[] { "Закрыть" });
            return;
        }

        ShowToast("Движок перезапущен, вкладки восстановлены", seconds: 5);
        var a = _active;
        if (a != null)
        {
            a.IsSleeping = false;
            await EnsureViewAsync(a, string.IsNullOrEmpty(a.Url) ? UrlTools.StartUrl : a.Url, navigate: true);
        }
        RefreshViews();
        UpdateToolbar();
        EngineRestarts++;
    }

    /// <summary>Для самопроверки: завершить процесс движка, как при его падении.</summary>
    public bool KillEngineForTest()
    {
        var core = _active?.Core;
        if (core == null) return false;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById((int)core.BrowserProcessId);
            p.Kill();
            return true;
        }
        catch (Exception ex)
        {
            App.Log("Самопроверка: не удалось завершить движок: " + ex.Message);
            return false;
        }
    }

    // ===================================================================== сеанс

    bool _sessionReady;
    DispatcherTimer? _sessionTimer;

    /// <summary>Окно, открытое при восстановлении сеанса: какие вкладки в нём восстановить.</summary>
    public SessionWindow? RestoreFrom { get; init; }

    void ScheduleSessionSave()
    {
        if (!_sessionReady || Shared.SessionFrozen) return;
        if (_sessionTimer == null)
        {
            _sessionTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(2) };
            _sessionTimer.Tick += (_, _) =>
            {
                _sessionTimer!.Stop();
                SaveSessionNow();
            };
        }
        _sessionTimer.Stop();
        _sessionTimer.Start();
    }

    /// <summary>Записывает открытые вкладки всех окон (закреплённые хранятся отдельно, пустые не сохраняются).</summary>
    public void SaveSessionNow()
    {
        if (Shared.SessionFrozen) return;
        if (!_settings.RestoreSession)
        {
            if (_settings.Session.Count > 0)
            {
                _settings.Session.Clear();
                _settings.Save();
            }
            return;
        }
        _settings.Session = Shared.Windows
            .Where(w => w._sessionReady && !w._closingForGood)
            .Select(w => w.CaptureSession())
            .Where(s => s.Tabs.Count > 0)
            .ToList();
        _settings.Save();
    }

    SessionWindow CaptureSession()
    {
        var tabs = _tabs.Where(t => !t.IsPinned && !t.IsStart && !string.IsNullOrEmpty(t.Url)).ToList();
        return new SessionWindow
        {
            Container = _container,
            Active = _active == null ? -1 : tabs.IndexOf(_active),
            Tabs = tabs.Select(t => new SessionTab
            {
                Container = t.Container,
                Url = t.Url,
                Title = t.Title,
                LastActive = t.LastActive,
            }).ToList(),
        };
    }

    /// <summary>Первое окно при запуске: вкладки прошлого сеанса, остальные окна открываются отдельно.</summary>
    bool RestoreSessionAtStartup()
    {
        if (!_settings.RestoreSession || _settings.Session.Count == 0) return false;
        var saved = _settings.Session.ToList();
        bool ok = RestoreWindow(saved[0]);
        foreach (var sw in saved.Skip(1).Where(s => s.Tabs.Count > 0))
            OpenWindow(null, sw.Container, sw);
        return ok;
    }

    /// <summary>Вкладки появляются спящими (память не тратится), загружается только активная.</summary>
    bool RestoreWindow(SessionWindow sw)
    {
        BrowserTab? active = null;
        var added = new List<BrowserTab>();
        for (int i = 0; i < sw.Tabs.Count; i++)
        {
            var st = sw.Tabs[i];
            if (string.IsNullOrEmpty(st.Url) || UrlTools.IsStartPage(st.Url)) continue;
            var tab = new BrowserTab(AppSettings.ContainerById(st.Container).Id)
            {
                Url = st.Url,
                Title = string.IsNullOrWhiteSpace(st.Title) ? UrlTools.HostOf(st.Url) : st.Title,
                IsSleeping = true,
                LastActive = st.LastActive > DateTime.Now ? DateTime.Now : st.LastActive,
            };
            tab.PropertyChanged += OnTabPropertyChanged;
            _tabs.Add(tab);
            added.Add(tab);
            if (i == sw.Active) active = tab;
        }
        if (added.Count == 0) return false;
        active ??= added.LastOrDefault(t => t.Container == sw.Container) ?? added[^1];
        Activate(active);
        App.Log($"Восстановлено вкладок: {added.Count}");
        return true;
    }

    // ===================================================================== очистка вкладок

    TabInfo InfoOf(BrowserTab t) =>
        new(t.Id, t.Container, t.Url, t.Title, t.IsStart, t.IsPinned, t == _active || InSplit(t), t.LoadFailed, t.LastActive);

    public List<CleanupItem> FindCleanup(int? oldDays = null) =>
        TabCleanup.Find(_tabs.Select(InfoOf), DateTime.Now, oldDays ?? _settings.CleanupOldDays);

    void Cleanup_Click(object sender, RoutedEventArgs e)
    {
        ClosePopups();
        OpenCleanup();
    }

    public CleanupWindow? CleanupWindowForTest { get; private set; }

    void OpenCleanup()
    {
        var w = new CleanupWindow(_settings, days => FindCleanup(days)) { Owner = this };
        CleanupWindowForTest = w;
        w.Closed += (_, _) => CleanupWindowForTest = null;
        if (_selfTestDir != null)
        {
            w.Show();
            return;
        }
        if (w.ShowDialog() == true)
            CloseTabsForCleanup(w.SelectedIds);
    }

    public void OpenCleanupForTest() => OpenCleanup();

    sealed record ClosedTab(string Container, string Url, string Title, DateTime LastActive, int Index);
    List<ClosedTab> _lastCleanup = new();

    public int CloseTabsForCleanup(IEnumerable<int> ids)
    {
        var set = ids.ToHashSet();
        var list = _tabs.Where(t => set.Contains(t.Id) && t != _active && !InSplit(t) && !t.IsPinned).ToList();
        if (list.Count == 0) return 0;
        _lastCleanup = list.Select(t => new ClosedTab(t.Container, t.Url, t.Title, t.LastActive, _tabs.IndexOf(t))).ToList();
        foreach (var t in list) CloseTab(t, remember: false);
        App.Log("Очистка вкладок: закрыто " + list.Count);
        ShowToast($"Закрыто вкладок: {list.Count}", "Вернуть", UndoCleanup);
        return list.Count;
    }

    /// <summary>«Вернуть»: закрытые очисткой вкладки возвращаются на свои места (спящими).</summary>
    public void UndoCleanup()
    {
        var items = _lastCleanup;
        _lastCleanup = new();
        foreach (var c in items.OrderBy(c => c.Index))
        {
            var tab = new BrowserTab(c.Container)
            {
                Url = c.Url,
                Title = c.Title,
                LastActive = c.LastActive,
                IsSleeping = !UrlTools.IsStartPage(c.Url) && c.Url.Length > 0,
            };
            if (!tab.IsSleeping) tab.Url = UrlTools.StartUrl;
            tab.PropertyChanged += OnTabPropertyChanged;
            _tabs.Insert(Math.Min(Math.Max(c.Index, _tabs.Count(t => t.IsPinned)), _tabs.Count), tab);
            if (!tab.IsSleeping) _ = EnsureViewAsync(tab, UrlTools.StartUrl, navigate: true);
        }
        RefreshViews();
        if (items.Count > 0) ShowToast("Вкладки возвращены");
    }

    // ===================================================================== поиск по вкладкам

    public sealed record TabSearchItem(BrowserTab Tab, MainWindow Window, string Title, string Host, string Place, Brush ContainerBrush);

    void TabSearch_Click(object sender, RoutedEventArgs e)
    {
        ClosePopups();
        OpenTabSearch();
    }

    public void OpenTabSearch()
    {
        ClosePopups();
        TabSearchBox.Text = "";
        FillTabSearch();
        TabSearchPopup.IsOpen = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            TabSearchBox.Focus();
            Keyboard.Focus(TabSearchBox);
        }), DispatcherPriority.Input);
    }

    public bool TabSearchOpen => TabSearchPopup.IsOpen;

    /// <summary>Вкладки, видимые сейчас в списке (текущий контейнер), - для самопроверки.</summary>
    public IEnumerable<BrowserTab> ListedTabs => _topView.Cast<BrowserTab>().Concat(_pinnedView.Cast<BrowserTab>());

    void FillTabSearch()
    {
        var windows = Shared.Windows.Where(w => !w._closingForGood).ToList();
        var all = windows.SelectMany(w => w._tabs.Select(t => (w, t))).ToList();
        var byId = all.ToDictionary(x => x.t.Id);
        var found = TabCleanup.Search(all.Select(x => x.w.InfoOf(x.t)), TabSearchBox.Text);
        var items = new List<TabSearchItem>();
        foreach (var info in found.Take(60))
        {
            var (w, t) = byId[info.Id];
            var c = AppSettings.ContainerById(t.Container);
            string place = c.Name + (windows.Count > 1 ? $" · окно {windows.IndexOf(w) + 1}" : "") + (t.IsSleeping ? " · спит" : "");
            string host = t.Url.StartsWith("http") ? Uri.UnescapeDataString(t.Url) : t.Url;
            items.Add(new TabSearchItem(t, w, t.Title, host, place, BrushCache.Get(c.Color)));
        }
        TabSearchList.ItemsSource = items;
        if (items.Count > 0) TabSearchList.SelectedIndex = 0;
        TabSearchEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TabSearchHint.Visibility = TabSearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public IReadOnlyList<TabSearchItem> TabSearchResults => (TabSearchList.ItemsSource as List<TabSearchItem>) ?? new List<TabSearchItem>();

    public void SetTabSearchTextForTest(string text) => TabSearchBox.Text = text;

    void TabSearchBox_TextChanged(object sender, TextChangedEventArgs e) => FillTabSearch();

    void TabSearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        int n = TabSearchList.Items.Count;
        switch (e.Key)
        {
            case Key.Down:
                if (n > 0) TabSearchList.SelectedIndex = (TabSearchList.SelectedIndex + 1) % n;
                TabSearchList.ScrollIntoView(TabSearchList.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up:
                if (n > 0) TabSearchList.SelectedIndex = (TabSearchList.SelectedIndex - 1 + n) % n;
                TabSearchList.ScrollIntoView(TabSearchList.SelectedItem);
                e.Handled = true;
                break;
            case Key.Enter:
                GoToSearchResult(TabSearchList.SelectedItem as TabSearchItem);
                e.Handled = true;
                break;
            case Key.Escape:
                TabSearchPopup.IsOpen = false;
                _active?.View?.Focus();
                e.Handled = true;
                break;
        }
    }

    void TabSearchList_Click(object sender, MouseButtonEventArgs e)
    {
        var d = e.OriginalSource as DependencyObject;
        while (d != null && d is not ListBoxItem) d = VisualTreeHelper.GetParent(d);
        if (d is ListBoxItem { DataContext: TabSearchItem item }) GoToSearchResult(item);
    }

    /// <summary>Переход к найденной вкладке (в том числе в другом окне и другом контейнере).</summary>
    public void GoToSearchResult(TabSearchItem? item)
    {
        TabSearchPopup.IsOpen = false;
        if (item == null || !item.Window._tabs.Contains(item.Tab)) return;
        var w = item.Window;
        if (w != this)
        {
            if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
            ((Window)w).Activate();
        }
        w.Activate(item.Tab);
    }
}
