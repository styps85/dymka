using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using Dymka.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Dymka;

/// <summary>Контейнер (рабочее пространство) для боковой панели и меню.</summary>
public sealed class ContainerVM : INotifyPropertyChanged
{
    public string Id { get; }
    public string Name { get; }
    public Brush Brush { get; }

    public ContainerVM(ContainerInfo c)
    {
        Id = c.Id;
        Name = c.Name;
        Brush = BrushCache.Get(c.Color);
    }

    bool _isCurrent;
    public bool IsCurrent { get => _isCurrent; set { if (_isCurrent != value) { _isCurrent = value; Raise(); } } }

    int _count;
    public int Count
    {
        get => _count;
        set { if (_count != value) { _count = value; Raise(); Raise(nameof(CountText)); Raise(nameof(Tip)); } }
    }

    public string CountText => _count == 0 ? "" : _count.ToString();
    public string Tip => $"{Name}: вкладок {_count}";

    public event PropertyChangedEventHandler? PropertyChanged;
    void Raise([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public partial class MainWindow : Window
{
    readonly AppSettings _settings;
    readonly string _dataDir;
    readonly string _uiDir;
    readonly string _listsDir;
    readonly string? _selfTestDir;
    readonly string? _startupUrl;

    CoreWebView2Environment? _env;
    volatile Blocklist _blocklist = new();

    readonly ObservableCollection<BrowserTab> _tabs = new();
    readonly ListCollectionView _topView, _awakeView, _sleepView;
    readonly List<ContainerVM> _containers;
    readonly Stack<(string container, string url)> _closed = new();
    readonly HashSet<string> _usedContainers = new();

    BrowserTab? _active;
    string _container;
    bool _cleanupDone;
    bool _fullscreen;
    Rect _restoreBounds;
    WindowState _restoreState;
    int _unsavedBlocked;

    readonly DispatcherTimer _sleepTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    readonly DispatcherTimer _memTimer = new() { Interval = TimeSpan.FromSeconds(4) };

    /// <summary>Кнопки «Печать» на сайтах (window.print) открывают окно печати «Дымки».</summary>
    const string PrintScript =
        "(function(){try{var w=window;if(w.top!==w)return;var o=w.print;w.print=function(){try{w.chrome.webview.postMessage({__dymka:'print'});}catch(e){o.call(w);}};}catch(e){}})();";

    const string GpcScript =
        "try{Object.defineProperty(Navigator.prototype,'globalPrivacyControl',{get:function(){return true},configurable:true});}catch(e){}";

    public MainWindow(AppSettings settings, string dataDir, string? selfTestDir, string? startupUrl)
    {
        _settings = settings;
        _dataDir = dataDir;
        _uiDir = Path.Combine(dataDir, "ui");
        _listsDir = Path.Combine(dataDir, "lists");
        _selfTestDir = selfTestDir;
        _startupUrl = startupUrl;
        _container = AppSettings.ContainerById(settings.LastContainer).Id;

        InitializeComponent();

        _topView = new ListCollectionView(_tabs) { Filter = o => ((BrowserTab)o).Container == _container };
        _awakeView = new ListCollectionView(_tabs) { Filter = o => o is BrowserTab t && t.Container == _container && !t.IsSleeping };
        _sleepView = new ListCollectionView(_tabs) { Filter = o => o is BrowserTab t && t.Container == _container && t.IsSleeping };
        TopTabs.ItemsSource = _topView;
        SideAwake.ItemsSource = _awakeView;
        SideSleeping.ItemsSource = _sleepView;

        _containers = AppSettings.Containers.Select(c => new ContainerVM(c)).ToList();
        ContainerSeg.ItemsSource = _containers;
        ContainerList.ItemsSource = _containers;

        Address.TextChanged += (_, _) => UpdatePlaceholder();

        RestoreWindowBounds();
        ApplyLayout();
        UpdateContainerUi();

        SourceInitialized += (_, _) => Native.RoundCorners(this);
        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += (_, _) => OnStateChanged();
        Deactivated += (_, _) => ClosePopups();
        PreviewKeyDown += OnPreviewKeyDown;
        _sleepTimer.Tick += (_, _) => SleepIdleTabs();
        _memTimer.Tick += (_, _) => UpdateMemory();
    }

    // ===================================================================== запуск

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_uiDir);
            WriteStartPage();
            _env = await CreateEnvironmentAsync();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            Dialogs.Show(this, "Не найден движок WebView2",
                "Для работы «Дымки» нужен компонент Microsoft Edge WebView2 Runtime. В Windows 10 и 11 он обычно уже установлен. " +
                "Скачать его можно на сайте Microsoft: developer.microsoft.com/microsoft-edge/webview2.",
                new[] { "Закрыть" });
            _cleanupDone = true;
            Close();
            return;
        }
        catch (Exception ex)
        {
            App.Log("Ошибка запуска движка: " + ex);
            Dialogs.Show(this, "Не удалось запустить браузер", ex.Message, new[] { "Закрыть" });
            _cleanupDone = true;
            Close();
            return;
        }

        _ = Task.Run(() => Blocklist.Build(_listsDir)).ContinueWith(t =>
        {
            if (t.Status == TaskStatus.RanToCompletion) _blocklist = t.Result;
        }, TaskScheduler.Default);

        if (_settings.PendingCleanup.Count > 0)
        {
            try { await CleanupContainersAsync(_settings.PendingCleanup.ToList()); }
            catch (Exception ex) { App.Log("Очистка после прошлого запуска: " + ex.Message); }
        }

        await NewTabAsync(_startupUrl, activate: true);

        _sleepTimer.Start();
        _memTimer.Start();
        UpdateMemory();

        if (_settings.AutoUpdateLists && _selfTestDir == null && DateTime.Now - _settings.LastListUpdate > TimeSpan.FromDays(7))
            _ = UpdateListsAsync();

        if (_settings.CheckUpdates && _selfTestDir == null && MemTestFile == null)
            _ = CheckUpdatesAsync(silent: true);

        if (_selfTestDir != null)
            _ = new SelfTest(this, _selfTestDir).RunAsync();
        else if (MemTestFile != null)
            _ = RunMemTestAsync(MemTestFile);
    }

    /// <summary>Замер памяти для автосборки: стартовая страница, пауза, запись результата, выход.</summary>
    public string? MemTestFile { get; init; }

    async Task RunMemTestAsync(string file)
    {
        var until = DateTime.Now.AddSeconds(40);
        while (DateTime.Now < until && (_active?.View?.CoreWebView2 == null || _active.IsLoading)) await Task.Delay(250);
        await Task.Delay(6000);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        await Task.Delay(1500);
        UpdateMemory();
        File.WriteAllText(file, $"{MemoryBytes / 1048576} МБ ({MemoryDetails})");
        _cleanupDone = true;
        Close();
    }

    void WriteStartPage()
    {
        using var s = typeof(MainWindow).Assembly.GetManifestResourceStream("Dymka.Web.start.html");
        if (s == null) return;
        using var f = File.Create(Path.Combine(_uiDir, "start.html"));
        s.CopyTo(f);
    }

    async Task<CoreWebView2Environment> CreateEnvironmentAsync()
    {
        var udf = Path.Combine(_dataDir, "Profile");
        Directory.CreateDirectory(udf);
        PatchProfilePreferences(udf);

        var disabled = string.Join(",",
            "BrowsingTopics", "Fledge", "InterestGroupStorage", "PrivacySandboxAdsAPIs", "AttributionReportingCrossAppWeb",
            "msEdgeShoppingAssistant", "msEdgeCollections", "OptimizationHints",
            "MediaRouter", "DialMediaRouteProvider");
        var args = string.Join(" ",
            "--disable-features=" + disabled,
            "--force-webrtc-ip-handling-policy=default_public_interface_only",
            "--no-pings",
            "--disable-domain-reliability",
            "--disable-breakpad",
            _settings.BlockThirdPartyCookies ? "--test-third-party-cookie-phaseout" : "");

        var options = new CoreWebView2EnvironmentOptions(args.Trim(), "ru");
        return await CoreWebView2Environment.CreateAsync(null, udf, options);
    }

    /// <summary>Включает «Блокировать сторонние cookie» в настройках профилей движка (до его запуска).</summary>
    void PatchProfilePreferences(string udf)
    {
        var root = Path.Combine(udf, "EBWebView");
        if (!Directory.Exists(root)) return;
        foreach (var c in AppSettings.Containers)
        {
            foreach (var name in new[] { c.Id, "Profile " + c.Id })
            {
                var prefs = Path.Combine(root, name, "Preferences");
                if (!File.Exists(prefs)) continue;
                try
                {
                    var node = JsonNode.Parse(File.ReadAllText(prefs)) as JsonObject ?? new JsonObject();
                    if (node["profile"] is not JsonObject profile)
                    {
                        profile = new JsonObject();
                        node["profile"] = profile;
                    }
                    profile["cookie_controls_mode"] = _settings.BlockThirdPartyCookies ? 1 : 0;
                    profile["block_third_party_cookies"] = _settings.BlockThirdPartyCookies;
                    File.WriteAllText(prefs, node.ToJsonString());
                }
                catch (Exception ex)
                {
                    App.Log("Настройки профиля " + name + ": " + ex.Message);
                }
            }
        }
    }

    // ===================================================================== вкладки

    public BrowserTab? ActiveTab => _active;
    public IReadOnlyList<BrowserTab> Tabs => _tabs;
    public AppSettings Settings => _settings;

    public async Task<BrowserTab> NewTabAsync(string? url, bool activate, string? container = null)
    {
        var tab = new BrowserTab(container ?? _container);
        tab.PropertyChanged += OnTabPropertyChanged;
        if (!string.IsNullOrEmpty(url)) tab.Url = url;

        int idx = _active != null ? _tabs.IndexOf(_active) : -1;
        if (idx >= 0 && idx < _tabs.Count - 1) _tabs.Insert(idx + 1, tab); else _tabs.Add(tab);
        MarkContainerUsed(tab.Container);

        if (activate) Activate(tab);
        else RefreshViews();
        await EnsureViewAsync(tab, string.IsNullOrEmpty(url) ? UrlTools.StartUrl : url, navigate: true);
        if (activate && tab.IsStart && tab == _active)
        {
            Address.Focus();
        }
        return tab;
    }

    readonly Dictionary<BrowserTab, Task> _creating = new();

    Task EnsureViewAsync(BrowserTab tab, string? url, bool navigate)
    {
        if (tab.View != null) return Task.CompletedTask;
        if (_creating.TryGetValue(tab, out var running)) return running;
        var t = CreateViewAsync(tab, url, navigate);
        _creating[tab] = t;
        return t;
    }

    async Task CreateViewAsync(BrowserTab tab, string? url, bool navigate)
    {
        try
        {
            var view = new WebView2
            {
                Visibility = tab == _active ? Visibility.Visible : Visibility.Hidden,
                DefaultBackgroundColor = System.Drawing.Color.White,
            };
            tab.View = view;
            ContentHost.Children.Add(view);
            var opts = _env!.CreateCoreWebView2ControllerOptions();
            opts.ProfileName = tab.Container;
            await view.EnsureCoreWebView2Async(_env, opts);
            if (!_tabs.Contains(tab) || tab.View != view)
            {
                ContentHost.Children.Remove(view);
                view.Dispose();
                return;
            }
            ConfigureCore(tab, view.CoreWebView2);
            if (navigate && !string.IsNullOrEmpty(url))
                NavigateCore(view.CoreWebView2, url);
            if (tab == _active)
            {
                view.Visibility = Visibility.Visible;
                if (!tab.IsStart) view.Focus();
            }
            else
            {
                TrySetMemoryLevel(view.CoreWebView2, low: true);
            }
        }
        catch (Exception ex)
        {
            App.Log("Создание вкладки: " + ex);
        }
        finally
        {
            _creating.Remove(tab);
        }
    }

    void ConfigureCore(BrowserTab tab, CoreWebView2 core)
    {
        var s = core.Settings;
        s.IsStatusBarEnabled = true;
        s.AreDevToolsEnabled = true;
        s.IsZoomControlEnabled = true;
        s.IsBuiltInErrorPageEnabled = true;
        s.AreBrowserAcceleratorKeysEnabled = true;
        try { s.IsReputationCheckingRequired = _settings.UseSmartScreen(tab.Container); } catch { }
        try
        {
            // выглядеть как обычный Chrome - самый распространённый браузер, без лишней приметы «Edg/…»
            var ua = s.UserAgent;
            int edg = ua.IndexOf(" Edg/", StringComparison.Ordinal);
            if (edg > 0)
            {
                int end = ua.IndexOf(' ', edg + 5);
                s.UserAgent = end > 0 ? ua.Remove(edg, end - edg) : ua[..edg];
            }
        }
        catch { }

        try
        {
            // трекеры блокирует сам браузер по своим спискам: так счётчик на щите честный, а выключение блокировки на сайте
            // действительно выключает её (встроенная «строгая» защита движка срабатывала бы до нас и мимо исключений)
            core.Profile.PreferredTrackingPreventionLevel = CoreWebView2TrackingPreventionLevel.Basic;
            s.IsPasswordAutosaveEnabled = false;
            s.IsGeneralAutofillEnabled = false;
        }
        catch (Exception ex) { App.Log("Профиль: " + ex.Message); }

        core.SetVirtualHostNameToFolderMapping(UrlTools.StartHost, _uiDir, CoreWebView2HostResourceAccessKind.DenyCors);
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        _ = core.AddScriptToExecuteOnDocumentCreatedAsync(GpcScript);
        _ = core.AddScriptToExecuteOnDocumentCreatedAsync(PrintScript);

        core.WebResourceRequested += (_, e) => OnWebResourceRequested(tab, e);
        core.NavigationStarting += (_, e) => OnNavigationStarting(tab, core, e);
        core.NavigationCompleted += (_, e) =>
        {
            tab.IsLoading = false;
            tab.CanGoBack = core.CanGoBack;
            tab.CanGoForward = core.CanGoForward;
        };
        core.SourceChanged += (_, _) => tab.Url = core.Source;
        core.HistoryChanged += (_, _) =>
        {
            tab.CanGoBack = core.CanGoBack;
            tab.CanGoForward = core.CanGoForward;
        };
        core.DocumentTitleChanged += (_, _) =>
            tab.Title = UrlTools.IsStartPage(core.Source) ? "Новая вкладка" : core.DocumentTitle;
        core.FaviconChanged += async (_, _) => await LoadFaviconAsync(tab, core);
        core.NewWindowRequested += (_, e) => OnNewWindowRequested(tab, e);
        core.PermissionRequested += (_, e) => OnPermissionRequested(e);
        core.WebMessageReceived += (_, e) => OnWebMessage(tab, core, e);
        core.IsDocumentPlayingAudioChanged += (_, _) => tab.IsPlayingAudio = core.IsDocumentPlayingAudio;
        core.ContainsFullScreenElementChanged += (_, _) =>
        {
            if (tab == _active) SetFullscreen(core.ContainsFullScreenElement);
        };
        core.ProcessFailed += (_, e) =>
        {
            App.Log($"Сбой процесса движка: {e.ProcessFailedKind} ({tab.Url})");
            if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited ||
                e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
            {
                try { core.Reload(); } catch { }
            }
        };
    }

    void NavigateCore(CoreWebView2 core, string url)
    {
        try { core.Navigate(url); }
        catch (ArgumentException)
        {
            core.Navigate(string.Format(_settings.Engine.Template, Uri.EscapeDataString(url)));
        }
    }

    public void Navigate(BrowserTab? tab, string url)
    {
        if (tab == null) return;
        var core = tab.View?.CoreWebView2;
        if (core != null) NavigateCore(core, url);
        else
        {
            tab.Url = url;
            tab.IsSleeping = false;
            _ = EnsureViewAsync(tab, url, navigate: true);
        }
    }

    public void Activate(BrowserTab tab)
    {
        if (_active == tab && tab.View != null) return;
        var old = _active;
        if (old != null && old != tab)
        {
            old.IsActive = false;
            old.LastActive = DateTime.Now;
            if (old.View != null)
            {
                old.View.Visibility = Visibility.Hidden;
                TrySetMemoryLevel(old.View.CoreWebView2, low: true);
            }
        }

        _active = tab;
        tab.IsActive = true;
        tab.LastActive = DateTime.Now;

        if (tab.Container != _container)
        {
            _container = tab.Container;
            _settings.LastContainer = _container;
            UpdateContainerUi();
        }

        if (tab.View == null)
        {
            bool wasSleeping = tab.IsSleeping;
            tab.IsSleeping = false;
            if (wasSleeping)
                _ = EnsureViewAsync(tab, string.IsNullOrEmpty(tab.Url) ? UrlTools.StartUrl : tab.Url, navigate: true);
        }
        else
        {
            tab.View.Visibility = Visibility.Visible;
            TrySetMemoryLevel(tab.View.CoreWebView2, low: false);
            if (!tab.IsStart && !Address.IsKeyboardFocused) tab.View.Focus();
        }

        RefreshViews();
        UpdateToolbar();
    }

    static void TrySetMemoryLevel(CoreWebView2? core, bool low)
    {
        if (core == null) return;
        try
        {
            core.MemoryUsageTargetLevel = low ? CoreWebView2MemoryUsageTargetLevel.Low : CoreWebView2MemoryUsageTargetLevel.Normal;
        }
        catch { }
    }

    public void CloseTab(BrowserTab tab)
    {
        if (!_tabs.Contains(tab)) return;
        if (!tab.IsStart && !string.IsNullOrEmpty(tab.Url)) _closed.Push((tab.Container, tab.Url));

        var sameContainer = _tabs.Where(t => t.Container == tab.Container).ToList();
        int pos = sameContainer.IndexOf(tab);
        _tabs.Remove(tab);
        tab.PropertyChanged -= OnTabPropertyChanged;
        DisposeView(tab);

        if (tab == _active)
        {
            _active = null;
            sameContainer.Remove(tab);
            if (sameContainer.Count > 0)
                Activate(sameContainer[Math.Min(pos, sameContainer.Count - 1)]);
            else
                _ = NewTabAsync(null, activate: true, container: tab.Container);
        }
        RefreshViews();
    }

    void DisposeView(BrowserTab tab)
    {
        var v = tab.View;
        tab.View = null;
        if (v == null) return;
        ContentHost.Children.Remove(v);
        try { v.Dispose(); } catch { }
    }

    public void SleepTab(BrowserTab tab)
    {
        if (tab == _active || tab.View == null) return;
        DisposeView(tab);
        tab.IsSleeping = true;
        tab.IsLoading = false;
        RefreshViews();
    }

    void SleepIdleTabs()
    {
        if (_settings.SleepMinutes <= 0) return;
        var limit = TimeSpan.FromMinutes(_settings.SleepMinutes);
        foreach (var t in _tabs.ToList())
            if (t != _active && t.View != null && !t.IsPlayingAudio && DateTime.Now - t.LastActive > limit)
                SleepTab(t);

        if (_unsavedBlocked > 0)
        {
            _unsavedBlocked = 0;
            _settings.Save();
        }
    }

    void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender == _active) UpdateToolbar();
    }

    void RefreshViews()
    {
        _topView.Refresh();
        _awakeView.Refresh();
        _sleepView.Refresh();
        SleepHeader.Visibility = _sleepView.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var c in _containers)
            c.Count = _tabs.Count(t => t.Container == c.Id);
    }

    void MarkContainerUsed(string id)
    {
        if (!_usedContainers.Add(id)) return;
        if (_settings.ClearCookiesOnExit && !_settings.PendingCleanup.Contains(id))
        {
            _settings.PendingCleanup.Add(id);
            _settings.Save();
        }
    }

    // ===================================================================== контейнеры

    void UpdateContainerUi()
    {
        var info = AppSettings.ContainerById(_container);
        foreach (var c in _containers) c.IsCurrent = c.Id == _container;
        ContainerDot.Fill = BrushCache.Get(info.Color);
        ContainerName.Text = info.Name;
    }

    void SwitchContainer(string id)
    {
        ClosePopups();
        if (id == _container) return;
        _container = id;
        _settings.LastContainer = id;
        UpdateContainerUi();
        var target = _tabs.Where(t => t.Container == id).OrderByDescending(t => t.LastActive).FirstOrDefault();
        if (target != null) Activate(target);
        else _ = NewTabAsync(null, activate: true, container: id);
        RefreshViews();
    }

    void Container_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string id) SwitchContainer(id);
    }

    void ContainerChip_Click(object sender, RoutedEventArgs e)
    {
        RefreshViews();
        ContainerMenu.IsOpen = true;
    }

    // ===================================================================== события движка

    void OnNavigationStarting(BrowserTab tab, CoreWebView2 core, CoreWebView2NavigationStartingEventArgs e)
    {
        var uri = e.Uri ?? "";
        if (!UrlTools.IsStartPage(uri) && !e.IsRedirected)
        {
            var cleaned = UrlTools.CleanUrl(uri);
            if (cleaned != null && cleaned != uri)
            {
                e.Cancel = true;
                core.Navigate(cleaned);
                return;
            }
        }
        tab.PendingUrl = uri;
        tab.PageHost = UrlTools.HostOf(uri);
        tab.Blocked = 0;
        tab.IsLoading = true;
        if (tab.Favicon != null && UrlTools.RegistrableDomain(UrlTools.HostOf(tab.Url)) != UrlTools.RegistrableDomain(tab.PageHost))
            tab.Favicon = null;
    }

    void OnWebResourceRequested(BrowserTab tab, CoreWebView2WebResourceRequestedEventArgs e)
    {
        string url = e.Request.Uri;
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return;
        var host = UrlTools.HostOf(url);
        var page = tab.PageHost;
        if (host.Length == 0 || page.Length == 0 || page == UrlTools.StartHost) return;

        // сам переход на страницу не блокируем никогда
        if (e.ResourceContext == CoreWebView2WebResourceContext.Document && url == tab.PendingUrl) return;

        bool thirdParty = UrlTools.IsThirdParty(host, page);
        tab.SeenHosts?.Add(host + " (" + e.ResourceContext + ")");
        if (thirdParty && !_settings.IsTrackerException(page) && _blocklist.IsBlocked(host))
        {
            e.Response = _env!.CreateWebResourceResponse(new MemoryStream(), 403, "Blocked", "Content-Type: text/plain");
            tab.Blocked++;
            _settings.CountBlocked();
            _unsavedBlocked++;
            return;
        }

        try
        {
            e.Request.Headers.SetHeader("Sec-GPC", "1");
            if (thirdParty && _settings.BlockThirdPartyCookies && !_settings.IsTrackerException(page))
                e.Request.Headers.RemoveHeader("Cookie");
        }
        catch { }
    }

    async Task LoadFaviconAsync(BrowserTab tab, CoreWebView2 core)
    {
        try
        {
            if (string.IsNullOrEmpty(core.FaviconUri) || UrlTools.IsStartPage(core.Source))
            {
                tab.Favicon = null;
                return;
            }
            using var stream = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
            if (stream == null) { tab.Favicon = null; return; }
            var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            if (ms.Length == 0) { tab.Favicon = null; return; }
            ms.Position = 0;
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.DecodePixelWidth = 32;
            img.StreamSource = ms;
            img.EndInit();
            img.Freeze();
            tab.Favicon = img;
        }
        catch
        {
            tab.Favicon = null;
        }
    }

    async void OnNewWindowRequested(BrowserTab source, CoreWebView2NewWindowRequestedEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            bool background = Native.IsCtrlDown() && !(e.WindowFeatures?.HasSize ?? false);
            var tab = new BrowserTab(source.Container) { Url = e.Uri ?? "" };
            tab.PropertyChanged += OnTabPropertyChanged;
            int idx = _tabs.IndexOf(source);
            if (idx >= 0 && idx < _tabs.Count - 1) _tabs.Insert(idx + 1, tab); else _tabs.Add(tab);
            if (!background) Activate(tab); else RefreshViews();
            await EnsureViewAsync(tab, null, navigate: false);
            if (tab.View?.CoreWebView2 != null)
            {
                e.NewWindow = tab.View.CoreWebView2;
                e.Handled = true;
            }
        }
        catch (Exception ex)
        {
            App.Log("Новое окно: " + ex.Message);
        }
        finally
        {
            deferral.Complete();
        }
    }

    static readonly Dictionary<CoreWebView2PermissionKind, (string key, string what)> PermissionNames = new()
    {
        [CoreWebView2PermissionKind.Camera] = ("camera", "камере"),
        [CoreWebView2PermissionKind.Microphone] = ("microphone", "микрофону"),
        [CoreWebView2PermissionKind.Geolocation] = ("geolocation", "вашему местоположению"),
        [CoreWebView2PermissionKind.Notifications] = ("notifications", "показу уведомлений"),
        [CoreWebView2PermissionKind.ClipboardRead] = ("clipboard", "чтению буфера обмена"),
        [CoreWebView2PermissionKind.MultipleAutomaticDownloads] = ("downloads", "скачиванию нескольких файлов подряд"),
    };

    void OnPermissionRequested(CoreWebView2PermissionRequestedEventArgs e)
    {
        try { e.SavesInProfile = false; } catch { }
        var host = UrlTools.HostOf(e.Uri);
        if (!PermissionNames.TryGetValue(e.PermissionKind, out var p) || host.Length == 0)
        {
            e.State = CoreWebView2PermissionState.Deny;
            return;
        }
        if (_settings.SitePermissions.TryGetValue(AppSettings.PermissionKey(host, p.key), out bool allowed))
        {
            e.State = allowed ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
            return;
        }
        if (p.key == "notifications")
        {
            e.State = CoreWebView2PermissionState.Deny;     // уведомления по умолчанию запрещены, разрешить можно в панели сайта
            return;
        }

        var deferral = e.GetDeferral();
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                var (index, remember) = Dialogs.Show(this, "Запрос доступа",
                    $"Сайт {host} просит доступ к {p.what}.",
                    new[] { "Разрешить", "Запретить" }, defaultIndex: 1,
                    checkbox: "Запомнить для этого сайта", checkboxValue: true);
                bool allow = index == 0;
                e.State = allow ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
                if (remember)
                {
                    _settings.SitePermissions[AppSettings.PermissionKey(host, p.key)] = allow;
                    _settings.Save();
                }
            }
            catch
            {
                e.State = CoreWebView2PermissionState.Deny;
            }
            finally
            {
                deferral.Complete();
            }
        }));
    }

    void OnWebMessage(BrowserTab tab, CoreWebView2 core, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("__dymka", out var dm) && dm.GetString() == "print")
            {
                if (tab == _active) Dispatcher.BeginInvoke(new Action(OpenPrintDialog));
                return;
            }
            if (!UrlTools.IsStartPage(e.Source)) return;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            switch (type)
            {
                case "init":
                    SendStartData(core);
                    break;
                case "go":
                    var target = UrlTools.ToNavigable(root.GetProperty("q").GetString(), _settings.Engine.Template);
                    if (target != null) NavigateCore(core, target);
                    break;
                case "add":
                    var url = UrlTools.ToNavigable(root.GetProperty("url").GetString(), _settings.Engine.Template);
                    var title = root.TryGetProperty("title", out var ti) ? ti.GetString() : null;
                    if (url != null && url.StartsWith("http"))
                    {
                        if (string.IsNullOrWhiteSpace(title))
                        {
                            title = UrlTools.HostOf(url);
                            if (title.StartsWith("www.")) title = title[4..];
                        }
                        _settings.Shortcuts.Add(new Shortcut { Title = title!.Trim(), Url = url });
                        _settings.Save();
                    }
                    SendStartData(core);
                    break;
                case "remove":
                    var ru = root.GetProperty("url").GetString();
                    _settings.Shortcuts.RemoveAll(s => s.Url == ru);
                    _settings.Save();
                    SendStartData(core);
                    break;
            }
        }
        catch (Exception ex)
        {
            App.Log("Стартовая страница: " + ex.Message);
        }
    }

    void SendStartData(CoreWebView2 core)
    {
        var data = new
        {
            type = "data",
            shortcuts = _settings.Shortcuts.Select(s =>
            {
                var tab = new BrowserTab("x") { Url = s.Url, Title = s.Title };
                return new
                {
                    title = s.Title,
                    url = s.Url,
                    letter = UrlTools.InitialFor(s.Title, s.Url),
                    fg = ((SolidColorBrush)tab.InitialFg).Color.ToString().Remove(1, 2),
                };
            }),
            blocked = _settings.BlockedToday,
            sleeping = _tabs.Count(t => t.IsSleeping),
            clearOnExit = _settings.ClearCookiesOnExit,
            engine = _settings.Engine.Name,
        };
        core.PostWebMessageAsJson(JsonSerializer.Serialize(data));
    }

    // ===================================================================== панель адреса

    void UpdateToolbar()
    {
        var t = _active;
        BackBtn.IsEnabled = t?.CanGoBack == true;
        ForwardBtn.IsEnabled = t?.CanGoForward == true;
        ReloadGlyph.Data = (Geometry)FindResource(t?.IsLoading == true ? "IcStop" : "IcReload");
        ReloadBtn.ToolTip = t?.IsLoading == true ? "Остановить загрузку (Esc)" : "Обновить (F5)";

        bool web = t != null && !t.IsStart && (t.Url.StartsWith("http://") || t.Url.StartsWith("https://"));
        ShieldBtn.IsEnabled = web;
        ShieldBtn.Tag = web ? t!.Blocked.ToString() : "0";
        bool exception = web && _settings.IsTrackerException(t!.PageHost);
        ShieldBtn.ToolTip = !web ? "Защита работает на всех сайтах"
            : exception ? "На этом сайте блокировка выключена"
            : $"Заблокировано на этой странице: {t!.Blocked}";
        UpdateAddress();
    }

    void UpdateAddress()
    {
        if (Address.IsKeyboardFocused) return;
        var t = _active;
        var url = t?.Url ?? "";
        if (t == null || t.IsStart || url == "about:blank")
        {
            Address.Text = "";
            AddressDisplay.Visibility = Visibility.Collapsed;
            AddrIcon.Data = (Geometry)FindResource("IcSearch");
            AddrIcon.Foreground = (Brush)FindResource("Muted");
        }
        else
        {
            Address.Text = url;
            var (host, rest) = UrlTools.SplitForDisplay(url);
            AddrHost.Text = host;
            AddrRest.Text = rest;
            AddressDisplay.Visibility = Visibility.Visible;
            if (url.StartsWith("https://"))
            {
                AddrIcon.Data = (Geometry)FindResource("IcLock");
                AddrIcon.Foreground = (Brush)FindResource("Green");
            }
            else if (url.StartsWith("http://"))
            {
                AddrIcon.Data = (Geometry)FindResource("IcWarn");
                AddrIcon.Foreground = BrushCache.Get("#B26A00");
            }
            else
            {
                AddrIcon.Data = (Geometry)FindResource("IcInfo");
                AddrIcon.Foreground = (Brush)FindResource("Muted");
            }
        }
        UpdatePlaceholder();
    }

    void UpdatePlaceholder()
    {
        AddressPlaceholder.Visibility = Address.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void Address_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        AddressDisplay.Visibility = Visibility.Collapsed;
        AddressBorder.BorderBrush = (Brush)FindResource("Accent");
        AddressBorder.Background = Brushes.White;
        Dispatcher.BeginInvoke(new Action(Address.SelectAll), DispatcherPriority.Input);
    }

    void Address_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        AddressBorder.BorderBrush = Brushes.Transparent;
        AddressBorder.Background = (Brush)FindResource("Field");
        UpdateAddress();
    }

    void Address_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!Address.IsKeyboardFocusWithin)
        {
            Address.Focus();
            e.Handled = true;
        }
    }

    void Address_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var target = UrlTools.ToNavigable(Address.Text, _settings.Engine.Template);
            if (target == null) return;
            e.Handled = true;
            if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0)
            {
                _ = NewTabAsync(target, activate: true);
                return;
            }
            var tab = _active;
            if (tab == null) return;
            tab.View?.Focus();
            Keyboard.ClearFocus();
            Navigate(tab, target);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Keyboard.ClearFocus();
            _active?.View?.Focus();
            UpdateAddress();
        }
    }

    public void FocusAddress()
    {
        Address.Focus();
        Address.SelectAll();
    }

    void Back_Click(object sender, RoutedEventArgs e) { try { _active?.View?.CoreWebView2?.GoBack(); } catch { } }
    void Forward_Click(object sender, RoutedEventArgs e) { try { _active?.View?.CoreWebView2?.GoForward(); } catch { } }

    void Reload_Click(object sender, RoutedEventArgs e)
    {
        var core = _active?.View?.CoreWebView2;
        if (core == null) return;
        try
        {
            if (_active!.IsLoading) core.Stop(); else core.Reload();
        }
        catch { }
    }

    void NewTab_Click(object sender, RoutedEventArgs e)
    {
        ClosePopups();
        _ = NewTabAsync(null, activate: true);
    }

    static bool IsInsideButton(object? source)
    {
        var d = source as DependencyObject;
        while (d != null)
        {
            if (d is Button) return true;
            if (d is Border b && b.Tag is BrowserTab) return false;
            d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    void Tab_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInsideButton(e.OriginalSource)) return;
        if ((sender as FrameworkElement)?.Tag is BrowserTab tab)
        {
            Activate(tab);
            e.Handled = true;
        }
    }

    void Tab_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && (sender as FrameworkElement)?.Tag is BrowserTab tab)
        {
            CloseTab(tab);
            e.Handled = true;
        }
    }

    void TabClose_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is BrowserTab tab) CloseTab(tab);
        e.Handled = true;
    }

    // ===================================================================== панель защиты

    void Shield_Click(object sender, RoutedEventArgs e) => OpenPrivacyPanel();

    public void OpenPrivacyPanel()
    {
        var t = _active;
        if (t == null || t.IsStart) return;
        var host = t.PageHost.Length > 0 ? t.PageHost : UrlTools.HostOf(t.Url);
        PpHost.Text = host;
        bool secure = t.Url.StartsWith("https://");
        PpSecure.Text = secure ? "Соединение защищено" : "Соединение не защищено";
        PpLockIcon.Data = (Geometry)FindResource(secure ? "IcLock" : "IcWarn");
        PpLockIcon.Foreground = secure ? (Brush)FindResource("Green") : BrushCache.Get("#B26A00");
        PpLockBg.Background = secure ? (Brush)FindResource("GreenSoft") : BrushCache.Get("#FFF2DC");

        PpCount.Text = t.Blocked.ToString();
        PpTrackers.Text = t.Blocked.ToString();
        PpCookies.Text = _settings.BlockThirdPartyCookies ? "блокируются" : "разрешены";

        PpBlockToggle.IsChecked = !_settings.IsTrackerException(host);
        PpCookieToggle.IsChecked = _settings.KeepsCookies(host);
        UpdateCookieHint(host);
        BuildPermissionRows(host);
        PrivacyPopup.IsOpen = true;
    }

    void UpdateCookieHint(string host)
    {
        PpCookieHint.Text = _settings.KeepsCookies(host) ? "Сайт в белом списке: вход сохранится"
            : _settings.ClearCookiesOnExit ? "Удалятся при закрытии браузера"
            : "Удаление при закрытии выключено";
    }

    static readonly (string key, string name)[] PanelPermissions =
    {
        ("camera", "Камера"), ("microphone", "Микрофон"), ("geolocation", "Местоположение"), ("notifications", "Уведомления"),
    };

    void BuildPermissionRows(string host)
    {
        PpPermissions.Children.Clear();
        foreach (var (key, name) in PanelPermissions)
        {
            var pk = AppSettings.PermissionKey(host, key);
            string state = _settings.SitePermissions.TryGetValue(pk, out bool a)
                ? (a ? "Разрешено" : "Запрещено")
                : key == "notifications" ? "Запрещено" : "Спрашивать";

            var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
            var btn = new Button
            {
                Style = (Style)FindResource("PlainButton"),
                Cursor = Cursors.Hand,
                ToolTip = "Нажмите, чтобы изменить",
                Content = new TextBlock
                {
                    Text = state,
                    FontSize = 12.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = state == "Разрешено" ? (Brush)FindResource("Green") : (Brush)FindResource("Muted"),
                },
            };
            btn.Click += (_, _) =>
            {
                // Спрашивать -> Разрешено -> Запрещено -> Спрашивать
                if (!_settings.SitePermissions.TryGetValue(pk, out bool cur)) _settings.SitePermissions[pk] = true;
                else if (cur) _settings.SitePermissions[pk] = false;
                else _settings.SitePermissions.Remove(pk);
                _settings.Save();
                BuildPermissionRows(host);
            };
            DockPanel.SetDock(btn, Dock.Right);
            row.Children.Add(btn);
            row.Children.Add(new TextBlock { Text = name, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center });
            PpPermissions.Children.Add(row);
        }
    }

    void PpBlockToggle_Click(object sender, RoutedEventArgs e)
    {
        var t = _active;
        if (t == null) return;
        _settings.SetListMembership(_settings.TrackerExceptions, t.PageHost, PpBlockToggle.IsChecked != true);
        _settings.Save();
        try { t.View?.CoreWebView2?.Reload(); } catch { }
        UpdateToolbar();
    }

    void PpCookieToggle_Click(object sender, RoutedEventArgs e)
    {
        var t = _active;
        if (t == null) return;
        _settings.SetListMembership(_settings.CookieWhitelist, t.PageHost, PpCookieToggle.IsChecked == true);
        _settings.Save();
        UpdateCookieHint(t.PageHost);
    }

    async void ClearSite_Click(object sender, RoutedEventArgs e)
    {
        var t = _active;
        var core = t?.View?.CoreWebView2;
        PrivacyPopup.IsOpen = false;
        if (t == null || core == null) return;
        try
        {
            var reg = UrlTools.RegistrableDomain(t.PageHost);
            var cm = core.CookieManager;
            var all = await cm.GetCookiesAsync(null);
            foreach (var c in all)
            {
                var d = c.Domain.TrimStart('.');
                if (UrlTools.RegistrableDomain(d) == reg) cm.DeleteCookie(c);
            }
            await core.ExecuteScriptAsync("try{localStorage.clear();sessionStorage.clear();}catch(e){}");
            core.Reload();
        }
        catch (Exception ex)
        {
            App.Log("Очистка сайта: " + ex.Message);
        }
    }

    // ===================================================================== меню

    void Menu_Click(object sender, RoutedEventArgs e)
    {
        UpdateMemory();
        bool side = _settings.Layout == "side";
        LayoutItemText.Text = side ? "Вкладки сверху" : "Вкладки сбоку";
        LayoutGlyph.Data = (Geometry)FindResource(side ? "IcLayoutTop" : "IcLayoutSide");
        ReopenItem.IsEnabled = _closed.Count > 0;
        MainMenu.IsOpen = true;
    }

    public void OpenMenuForTest() => Menu_Click(this, new RoutedEventArgs());

    internal void ClosePopups()
    {
        MainMenu.IsOpen = false;
        PrivacyPopup.IsOpen = false;
        ContainerMenu.IsOpen = false;
    }

    void ReopenClosed_Click(object sender, RoutedEventArgs e)
    {
        ClosePopups();
        ReopenClosed();
    }

    void ReopenClosed()
    {
        if (_closed.Count == 0) return;
        var (container, url) = _closed.Pop();
        _ = NewTabAsync(url, activate: true, container: container);
    }

    void SleepOthers_Click(object sender, RoutedEventArgs e)
    {
        ClosePopups();
        foreach (var t in _tabs.ToList())
            if (t != _active) SleepTab(t);
        UpdateMemory();
    }

    void ToggleLayout_Click(object sender, RoutedEventArgs e)
    {
        ClosePopups();
        SetLayout(_settings.Layout == "side" ? "top" : "side");
    }

    public void SetLayout(string layout)
    {
        _settings.Layout = layout;
        _settings.Save();
        ApplyLayout();
    }

    void Print_Click(object sender, RoutedEventArgs e)
    {
        ClosePopups();
        OpenPrintDialog();
    }

    PrintWindow? _printWindow;

    public void OpenPrintDialog()
    {
        var tab = _active;
        var core = tab?.View?.CoreWebView2;
        if (tab == null || core == null || tab.IsStart || _printWindow != null) return;
        _printWindow = new PrintWindow(core, _env!, _settings, tab.Title) { Owner = this };
        _printWindow.Closed += (_, _) => _printWindow = null;
        if (_selfTestDir != null) _printWindow.Show(); else _printWindow.ShowDialog();
    }

    public PrintWindow? PrintWindowForTest => _printWindow;

    void Downloads_Click(object sender, RoutedEventArgs e)
    {
        ClosePopups();
        try { _active?.View?.CoreWebView2?.OpenDefaultDownloadDialog(); } catch { }
    }

    void DevTools_Click(object sender, RoutedEventArgs e)
    {
        ClosePopups();
        try { _active?.View?.CoreWebView2?.OpenDevToolsWindow(); } catch { }
    }

    async void ClearAllNow_Click(object sender, RoutedEventArgs e)
    {
        ClosePopups();
        var (i, _) = Dialogs.Show(this, "Удалить данные сайтов?",
            "Будут удалены cookie, кэш и сохранённые данные всех сайтов во всех контейнерах. " +
            "Cookie сайтов из белого списка останутся, поэтому вход на них сохранится.",
            new[] { "Удалить", "Отмена" }, defaultIndex: 1);
        if (i != 0) return;
        await CleanupContainersAsync(AppSettings.Containers.Select(c => c.Id).ToList());
        foreach (var t in _tabs.Where(t => t.View != null))
            try { t.View!.CoreWebView2?.Reload(); } catch { }
    }

    void Settings_Click(object sender, RoutedEventArgs e)
    {
        ClosePopups();
        var w = new SettingsWindow(_settings, _blocklist.Count) { Owner = this };
        if (w.ShowDialog() == true)
        {
            _settings.Save();
            ApplyLayout();
            UpdateToolbar();
            foreach (var t in _tabs)
                try { if (t.View?.CoreWebView2 != null) t.View.CoreWebView2.Settings.IsReputationCheckingRequired = _settings.UseSmartScreen(t.Container); } catch { }
            if (w.UpdateListsRequested) _ = UpdateListsAsync();
        }
    }

    public SettingsWindow OpenSettingsForTest()
    {
        var w = new SettingsWindow(_settings, _blocklist.Count) { Owner = this };
        w.Show();
        return w;
    }

    void About_Click(object sender, RoutedEventArgs e)
    {
        ClosePopups();
        string engine = "";
        try { engine = _env?.BrowserVersionString ?? ""; } catch { }
        Dialogs.Show(this, "Дымка " + App.VersionText,
            "Лёгкий конфиденциальный браузер.\n\n" +
            $"Движок: Microsoft Edge WebView2 {engine}.\n" +
            $"В списке блокировки доменов: {_blocklist.Count:N0}.\n" +
            $"Заблокировано сегодня: {_settings.BlockedToday:N0}.\n\n" +
            "Данные браузера хранятся в папке:\n" + _dataDir,
            new[] { "Закрыть" });
    }

    // ===================================================================== обновления

    UpdateInfo? _update;
    bool _updating;

    async Task CheckUpdatesAsync(bool silent)
    {
        try
        {
            if (silent) await Task.Delay(TimeSpan.FromSeconds(8));
            _update = await UpdateChecker.CheckAsync();
        }
        catch (Exception ex)
        {
            App.Log("Проверка обновлений: " + ex.Message);
            _update = null;
        }
        UpdateDot.Visibility = _update != null ? Visibility.Visible : Visibility.Collapsed;
        UpdateItemText.Text = _update != null ? $"Обновить до версии {_update.Version}" : "Проверить обновления";
        if (!silent && _update == null)
            Dialogs.Show(this, "Обновлений нет", $"У вас последняя версия «Дымки» ({App.VersionText}).", new[] { "Хорошо" });
        else if (!silent && _update != null)
            await InstallUpdateAsync();
    }

    async void UpdateItem_Click(object sender, RoutedEventArgs e)
    {
        ClosePopups();
        if (_updating) return;
        if (_update == null) await CheckUpdatesAsync(silent: false);
        else await InstallUpdateAsync();
    }

    async Task InstallUpdateAsync()
    {
        var u = _update;
        if (u == null || _updating) return;
        var (i, _) = Dialogs.Show(this, $"Доступна версия {u.Version}",
            $"Сейчас установлена версия {App.VersionText}. Скачать обновление? Это займёт около минуты, " +
            "потом браузер перезапустится. Открытые вкладки закроются.",
            new[] { "Обновить", "Позже" });
        if (i != 0) return;
        _updating = true;
        UpdateItemText.Text = "Скачиваю обновление…";
        bool ready = false;
        try { ready = await UpdateChecker.PrepareAsync(u); }
        catch (Exception ex) { App.Log("Обновление: " + ex.Message); }
        _updating = false;
        if (ready)
        {
            Close();
            return;
        }
        UpdateItemText.Text = $"Обновить до версии {u.Version}";
        Dialogs.Show(this, "Не удалось обновить автоматически",
            "Откроется страница выпуска: скачайте новую версию и замените ею старый файл (или папку).",
            new[] { "Открыть" });
        _ = NewTabAsync(u.PageUrl, activate: true);
    }

    // ===================================================================== память

    public long MemoryBytes { get; private set; }
    public string MemoryDetails { get; private set; } = "";

    static long MemoryOf(int pid)
    {
        long v = Native.PrivateWorkingSet(pid);
        if (v > 0) return v;
        try { using var p = Process.GetProcessById(pid); return p.WorkingSet64; } catch { return 0; }
    }

    internal void UpdateMemory()
    {
        if (_env == null) return;
        long total = 0;
        var parts = new List<string>();
        try
        {
            long own = MemoryOf(Environment.ProcessId);
            total += own;
            parts.Add($"окно {own / 1048576} МБ");
            foreach (var info in _env.GetProcessInfos())
            {
                long m = MemoryOf(info.ProcessId);
                if (m <= 0) continue;
                total += m;
                parts.Add($"{info.Kind} {m / 1048576} МБ");
            }
        }
        catch { return; }
        MemoryDetails = string.Join(", ", parts);

        MemoryBytes = total;
        string text = total >= 1024L * 1024 * 1024
            ? $"{total / (1024.0 * 1024 * 1024):0.0} ГБ"
            : $"{total / (1024 * 1024)} МБ";
        SideMemText.Text = text;
        MenuMemText.Text = text;
        if (SideMemBar.Parent is FrameworkElement parent && parent.ActualWidth > 0)
            SideMemBar.Width = parent.ActualWidth * Math.Min(1.0, total / (2048.0 * 1024 * 1024));
    }

    // ===================================================================== списки блокировки

    async Task UpdateListsAsync()
    {
        try
        {
            int n = await BlocklistUpdater.UpdateAsync(_listsDir);
            if (n > 0)
            {
                _settings.LastListUpdate = DateTime.Now;
                _settings.Save();
                var fresh = await Task.Run(() => Blocklist.Build(_listsDir));
                _blocklist = fresh;
            }
        }
        catch (Exception ex)
        {
            App.Log("Обновление списков: " + ex.Message);
        }
    }

    // ===================================================================== очистка данных

    async Task CleanupContainersAsync(IList<string> ids)
    {
        if (_env == null) return;
        foreach (var id in ids.Distinct().ToList())
        {
            WebView2? temp = null;
            try
            {
                var core = _tabs.FirstOrDefault(t => t.Container == id && t.View?.CoreWebView2 != null)?.View?.CoreWebView2;
                if (core == null)
                {
                    temp = new WebView2 { Visibility = Visibility.Hidden, Width = 1, Height = 1 };
                    ContentHost.Children.Add(temp);
                    var opts = _env.CreateCoreWebView2ControllerOptions();
                    opts.ProfileName = id;
                    await temp.EnsureCoreWebView2Async(_env, opts);
                    core = temp.CoreWebView2;
                }

                var cm = core.CookieManager;
                var cookies = await cm.GetCookiesAsync(null);
                foreach (var c in cookies)
                    if (!_settings.KeepsCookies(c.Domain.TrimStart('.')))
                        cm.DeleteCookie(c);

                await core.Profile.ClearBrowsingDataAsync(
                    CoreWebView2BrowsingDataKinds.DiskCache |
                    CoreWebView2BrowsingDataKinds.CacheStorage |
                    CoreWebView2BrowsingDataKinds.IndexedDb |
                    CoreWebView2BrowsingDataKinds.LocalStorage |
                    CoreWebView2BrowsingDataKinds.WebSql |
                    CoreWebView2BrowsingDataKinds.FileSystems |
                    CoreWebView2BrowsingDataKinds.ServiceWorkers |
                    CoreWebView2BrowsingDataKinds.BrowsingHistory |
                    CoreWebView2BrowsingDataKinds.DownloadHistory |
                    CoreWebView2BrowsingDataKinds.GeneralAutofill |
                    CoreWebView2BrowsingDataKinds.PasswordAutosave);
                _settings.PendingCleanup.Remove(id);
            }
            catch (Exception ex)
            {
                App.Log($"Очистка контейнера {id}: {ex.Message}");
            }
            finally
            {
                if (temp != null)
                {
                    ContentHost.Children.Remove(temp);
                    try { temp.Dispose(); } catch { }
                }
            }
        }
        _settings.Save();
    }

    async void OnClosing(object? sender, CancelEventArgs e)
    {
        SaveWindowBounds();
        if (_cleanupDone || !_settings.ClearCookiesOnExit || _usedContainers.Count == 0 || _env == null)
        {
            _settings.Save();
            return;
        }
        e.Cancel = true;
        _cleanupDone = true;
        Hide();
        try
        {
            var work = CleanupContainersAsync(_usedContainers.ToList());
            await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(10)));
        }
        catch { }
        _settings.Save();
        await Dispatcher.BeginInvoke(new Action(Close));
    }

    // ===================================================================== вид окна

    void ApplyLayout()
    {
        if (_fullscreen) return;
        bool side = _settings.Layout == "side";
        Sidebar.Visibility = side ? Visibility.Visible : Visibility.Collapsed;
        SideCol.Width = new GridLength(side ? 260 : 0);
        TopStrip.Visibility = side ? Visibility.Collapsed : Visibility.Visible;
        TopRow.Height = new GridLength(side ? 0 : 46);
        Grid.SetRow(WindowButtons, side ? 1 : 0);
        ToolbarDock.Margin = side ? new Thickness(12, 0, 148, 0) : new Thickness(12, 0, 10, 0);
        WindowChrome.GetWindowChrome(this).CaptionHeight = side ? 56 : 46;
        RefreshViews();
    }

    void OnStateChanged()
    {
        bool max = WindowState == WindowState.Maximized;
        RootBorder.Margin = max && !_fullscreen ? Native.MaximizedOverhang(this) : new Thickness(0);
        MaxGlyph.Data = (Geometry)FindResource(max ? "IcRestore" : "IcMax");
    }

    void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void SetFullscreen(bool on)
    {
        if (on == _fullscreen) return;
        if (on)
        {
            _restoreState = WindowState;
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
            _restoreBounds = new Rect(Left, Top, Width, Height);
            _fullscreen = true;
            TopRow.Height = new GridLength(0);
            ToolRow.Height = new GridLength(0);
            SideCol.Width = new GridLength(0);
            Sidebar.Visibility = Visibility.Collapsed;
            TopStrip.Visibility = Visibility.Collapsed;
            Toolbar.Visibility = Visibility.Collapsed;
            WindowButtons.Visibility = Visibility.Collapsed;
            RootBorder.Margin = new Thickness(0);
            WindowChrome.GetWindowChrome(this).CaptionHeight = 0;
            var b = Native.MonitorBounds(this);
            Left = b.Left; Top = b.Top; Width = b.Width; Height = b.Height;
            Topmost = true;
        }
        else
        {
            _fullscreen = false;
            Topmost = false;
            ToolRow.Height = new GridLength(56);
            Toolbar.Visibility = Visibility.Visible;
            WindowButtons.Visibility = Visibility.Visible;
            Left = _restoreBounds.Left; Top = _restoreBounds.Top;
            Width = _restoreBounds.Width; Height = _restoreBounds.Height;
            ApplyLayout();
            WindowState = _restoreState;
            OnStateChanged();
        }
    }

    void RestoreWindowBounds()
    {
        if (_selfTestDir != null)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = 0; Top = 0;
            Width = Math.Min(1280, SystemParameters.WorkArea.Width);
            Height = Math.Min(800, SystemParameters.WorkArea.Height);
            return;
        }
        var s = _settings;
        if (s.WindowWidth >= MinWidth && s.WindowHeight >= MinHeight)
        {
            Width = Math.Min(s.WindowWidth, SystemParameters.VirtualScreenWidth);
            Height = Math.Min(s.WindowHeight, SystemParameters.VirtualScreenHeight);
        }
        if (!double.IsNaN(s.WindowLeft) && !double.IsNaN(s.WindowTop))
        {
            var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (vs.Contains(new Point(s.WindowLeft + 40, s.WindowTop + 20)))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = s.WindowLeft;
                Top = s.WindowTop;
            }
        }
        if (s.WindowMaximized) WindowState = WindowState.Maximized;
    }

    void SaveWindowBounds()
    {
        if (_selfTestDir != null || _fullscreen) return;
        var r = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (r.Width > 0 && r.Height > 0)
        {
            _settings.WindowLeft = r.Left;
            _settings.WindowTop = r.Top;
            _settings.WindowWidth = r.Width;
            _settings.WindowHeight = r.Height;
        }
        _settings.WindowMaximized = WindowState == WindowState.Maximized;
    }

    // ===================================================================== клавиатура

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mod = Keyboard.Modifiers;
        bool ctrl = (mod & ModifierKeys.Control) != 0;
        bool shift = (mod & ModifierKeys.Shift) != 0;
        bool alt = (mod & ModifierKeys.Alt) != 0;
        bool handled = true;

        if (ctrl && !shift && key == Key.T) _ = NewTabAsync(null, activate: true);
        else if (ctrl && shift && key == Key.T) ReopenClosed();
        else if (ctrl && (key == Key.W || key == Key.F4)) { if (_active != null) CloseTab(_active); }
        else if ((ctrl && key == Key.L) || (alt && key == Key.D) || key == Key.F6) FocusAddress();
        else if (ctrl && (key == Key.Tab || key == Key.PageDown || key == Key.PageUp)) CycleTab(shift || key == Key.PageUp ? -1 : 1);
        else if (ctrl && key >= Key.D1 && key <= Key.D9) SelectTabByNumber(key - Key.D1 + 1);
        else if (key == Key.F5 || (ctrl && key == Key.R)) Reload_Click(this, new RoutedEventArgs());
        else if (alt && key == Key.Left) Back_Click(this, new RoutedEventArgs());
        else if (alt && key == Key.Right) Forward_Click(this, new RoutedEventArgs());
        else if (ctrl && key == Key.J) Downloads_Click(this, new RoutedEventArgs());
        else if (ctrl && !shift && key == Key.P) OpenPrintDialog();
        else if (key == Key.Escape && _active?.IsLoading == true && !Address.IsKeyboardFocused)
        {
            try { _active.View?.CoreWebView2?.Stop(); } catch { }
        }
        else handled = false;

        if (handled) e.Handled = true;
    }

    void CycleTab(int dir)
    {
        var list = _tabs.Where(t => t.Container == _container).ToList();
        if (list.Count < 2 || _active == null) return;
        int i = list.IndexOf(_active);
        Activate(list[(i + dir + list.Count) % list.Count]);
    }

    void SelectTabByNumber(int n)
    {
        var list = _tabs.Where(t => t.Container == _container).ToList();
        if (list.Count == 0) return;
        Activate(n == 9 || n > list.Count ? list[^1] : list[n - 1]);
    }
}
