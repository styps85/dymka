using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Dymka.Core;
using Microsoft.Web.WebView2.Core;
using QRCoder;

namespace Dymka;

/// <summary>Закреплённые вкладки, раздельный просмотр, несколько окон, меню адресной строки, расширения.</summary>
public partial class MainWindow
{
    // ===================================================================== раздельный просмотр

    BrowserTab? _splitLeft, _splitRight;

    bool InSplit(BrowserTab? t) => t != null && _splitLeft != null && (t == _splitLeft || t == _splitRight);

    bool IsShown(BrowserTab t) => t == _active || InSplit(t);

    public bool IsSplit => _splitLeft != null;

    public void StartSplit(BrowserTab left, BrowserTab right)
    {
        if (left == right) return;
        if (_splitLeft != null) EndSplit();
        _splitLeft = left;
        _splitRight = right;
        LeftCol.Width = new GridLength(1, GridUnitType.Star);
        SplitCol.Width = new GridLength(6);
        RightCol.Width = new GridLength(1, GridUnitType.Star);
        RightCol.MinWidth = 200;
        MarkRow.Height = new GridLength(3);
        Splitter.Visibility = Visibility.Visible;

        foreach (var (t, col) in new[] { (left, 0), (right, 2) })
        {
            if (t.View != null)
            {
                Grid.SetColumn(t.View, col);
                t.View.Visibility = Visibility.Visible;
                TrySetMemoryLevel(t.View.CoreWebView2, low: false);
            }
            else
            {
                t.IsSleeping = false;
                _ = EnsureViewAsync(t, string.IsNullOrEmpty(t.Url) ? UrlTools.StartUrl : t.Url, navigate: true);
            }
        }
        Activate(right);
        UpdateSplitMarks();
    }

    public void EndSplit()
    {
        var l = _splitLeft;
        var r = _splitRight;
        _splitLeft = _splitRight = null;
        LeftCol.Width = new GridLength(1, GridUnitType.Star);
        SplitCol.Width = new GridLength(0);
        RightCol.Width = new GridLength(0);
        RightCol.MinWidth = 0;
        MarkRow.Height = new GridLength(0);
        Splitter.Visibility = Visibility.Collapsed;
        foreach (var t in new[] { l, r })
        {
            if (t?.View == null) continue;
            Grid.SetColumn(t.View, 0);
            if (t != _active)
            {
                t.View.Visibility = Visibility.Hidden;
                TrySetMemoryLevel(t.View.CoreWebView2, low: true);
            }
        }
        UpdateSplitMarks();
    }

    void UpdateSplitMarks()
    {
        LeftMark.Visibility = _splitLeft != null && _active == _splitLeft ? Visibility.Visible : Visibility.Hidden;
        RightMark.Visibility = _splitRight != null && _active == _splitRight ? Visibility.Visible : Visibility.Hidden;
    }

    // ===================================================================== закреплённые вкладки

    public void SetPinned(BrowserTab tab, bool pin)
    {
        if (tab.IsPinned == pin) return;
        int from = _tabs.IndexOf(tab);
        if (from < 0) return;
        tab.IsPinned = pin;
        int pinnedOthers = _tabs.Count(t => t.IsPinned && t != tab);
        int to = Math.Min(pinnedOthers, _tabs.Count - 1);
        if (from != to) _tabs.Move(from, to);
        SavePinned();
        RefreshViews();
    }

    void SavePinned()
    {
        _settings.Pinned = Shared.Windows
            .SelectMany(w => w._tabs)
            .Where(t => t.IsPinned && !t.IsStart && !string.IsNullOrEmpty(t.Url))
            .Select(t => new PinnedSite { Container = t.Container, Url = t.Url, Title = t.Title })
            .ToList();
        _settings.Save();
    }

    /// <summary>При запуске закреплённые вкладки появляются спящими и загружаются по щелчку.</summary>
    void RestorePinned()
    {
        int i = 0;
        foreach (var p in _settings.Pinned.ToList())
        {
            if (string.IsNullOrEmpty(p.Url)) continue;
            var tab = new BrowserTab(AppSettings.ContainerById(p.Container).Id)
            {
                Url = p.Url,
                Title = string.IsNullOrWhiteSpace(p.Title) ? UrlTools.HostOf(p.Url) : p.Title,
                IsPinned = true,
                IsSleeping = true,
            };
            tab.PropertyChanged += OnTabPropertyChanged;
            _tabs.Insert(i++, tab);
        }
        RefreshViews();
    }

    // ===================================================================== окна и контейнеры

    public MainWindow OpenWindow(string? url, string? container)
    {
        var w = new MainWindow(_settings, _dataDir, null, url) { InitialContainer = container };
        var b = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.WindowState = WindowState.Normal;
        var area = SystemParameters.WorkArea;
        w.Width = Math.Min(b.Width, area.Width);
        w.Height = Math.Min(b.Height, area.Height);
        w.Left = Math.Max(area.Left, Math.Min(b.Left + 36, area.Right - w.Width));
        w.Top = Math.Max(area.Top, Math.Min(b.Top + 36, area.Bottom - w.Height));
        w.Show();
        return w;
    }

    void NewWindow_Click(object sender, RoutedEventArgs e)
    {
        ClosePopups();
        OpenWindow(null, _container);
    }

    public async Task MoveToContainerAsync(BrowserTab tab, string container)
    {
        if (tab.Container == container) return;
        var nt = new BrowserTab(container) { Url = tab.Url, Title = tab.Title, IsPinned = tab.IsPinned };
        nt.PropertyChanged += OnTabPropertyChanged;
        int idx = _tabs.IndexOf(tab);
        _tabs.Insert(idx + 1, nt);
        Activate(nt);
        CloseTab(tab, remember: false);
        await EnsureViewAsync(nt, string.IsNullOrEmpty(nt.Url) ? UrlTools.StartUrl : nt.Url, navigate: true);
        if (nt.IsPinned) SavePinned();
        ShowToast("Вкладка перенесена в «" + AppSettings.ContainerById(container).Name + "»");
    }

    // ===================================================================== уведомление

    DispatcherTimer? _toastTimer;

    public void ShowToast(string text)
    {
        ToastText.Text = text;
        Toast.IsOpen = false;
        Toast.HorizontalOffset = 0;
        Toast.IsOpen = true;
        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.8) };
        _toastTimer.Tick += (_, _) => { Toast.IsOpen = false; _toastTimer?.Stop(); };
        _toastTimer.Start();
    }

    // ===================================================================== меню адресной строки

    string _addressAtMenuOpen = "";

    bool ActiveIsWebPage =>
        _active != null && !_active.IsStart && (_active.Url.StartsWith("http://") || _active.Url.StartsWith("https://"));

    public void OpenAddressMenu()
    {
        var t = _active;
        if (t == null || !ActiveIsWebPage) return;

        AmTitle.Text = t.Title;
        AmHost.Text = Uri.UnescapeDataString(t.Url);
        AmPinText.Text = t.IsPinned ? "Открепить вкладку" : "Закрепить вкладку";
        bool shortcut = _settings.IsShortcut(t.Url);
        AmShortcutText.Text = shortcut ? "Уже на стартовой странице" : "Добавить на стартовую страницу";
        AmShortcutBtn.IsEnabled = !shortcut;
        AmQrBox.Visibility = Visibility.Collapsed;

        AmContainers.Children.Clear();
        foreach (var c in AppSettings.Containers.Where(c => c.Id != t.Container))
        {
            var id = c.Id;
            var b = MakeChip(c.Name, BrushCache.Get(c.Color));
            b.Click += async (_, _) => { CloseAddressMenu(); await MoveToContainerAsync(t, id); };
            AmContainers.Children.Add(b);
        }

        bool split = InSplit(t);
        AmSplitEndBtn.Visibility = split ? Visibility.Visible : Visibility.Collapsed;
        AmSplitPanel.Visibility = split ? Visibility.Collapsed : Visibility.Visible;
        AmSplitChoices.Children.Clear();
        if (!split)
        {
            var nb = MakeChip("Новая вкладка", null);
            nb.Click += async (_, _) =>
            {
                CloseAddressMenu();
                var left = _active!;
                var right = await NewTabAsync(null, activate: false, container: left.Container);
                StartSplit(left, right);
                Address.Focus();
            };
            AmSplitChoices.Children.Add(nb);
            foreach (var other in _tabs.Where(x => x != t && x.Container == t.Container && !x.IsStart)
                         .OrderByDescending(x => x.LastActive).Take(4))
            {
                var o = other;
                var title = o.Title.Length > 24 ? o.Title[..23] + "…" : o.Title;
                var ob = MakeChip(title, null);
                ob.ToolTip = o.Url;
                ob.Click += (_, _) => { CloseAddressMenu(); StartSplit(t, o); };
                AmSplitChoices.Children.Add(ob);
            }
        }

        _addressAtMenuOpen = Address.Text;
        AddressMenu.IsOpen = true;
    }

    Button MakeChip(string text, Brush? dot)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        if (dot != null)
            panel.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 8, Height = 8, Fill = dot, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
            });
        panel.Children.Add(new TextBlock { Text = text, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center });
        return new Button
        {
            Content = panel,
            Style = (Style)FindResource("SecondaryButton"),
            Height = 30,
            Padding = new Thickness(11, 0, 11, 0),
            FontWeight = FontWeights.Normal,
            Margin = new Thickness(0, 2, 6, 2),
            Focusable = false,
        };
    }

    void CloseAddressMenu()
    {
        AddressMenu.IsOpen = false;
        Keyboard.ClearFocus();
        _active?.View?.Focus();
    }

    static bool TrySetClipboard(string text)
    {
        for (int i = 0; i < 5; i++)
        {
            try { Clipboard.SetText(text); return true; }
            catch { Thread.Sleep(60); }
        }
        return false;
    }

    void AmCopy_Click(object sender, RoutedEventArgs e)
    {
        var t = _active;
        CloseAddressMenu();
        if (t == null) return;
        ShowToast(TrySetClipboard(t.Url) ? "Адрес скопирован" : "Не удалось скопировать - буфер обмена занят");
    }

    void AmCopyTitle_Click(object sender, RoutedEventArgs e)
    {
        var t = _active;
        CloseAddressMenu();
        if (t == null) return;
        ShowToast(TrySetClipboard($"{t.Title} - {t.Url}") ? "Название и адрес скопированы" : "Не удалось скопировать - буфер обмена занят");
    }

    void AmQr_Click(object sender, RoutedEventArgs e)
    {
        var t = _active;
        if (t == null) return;
        if (AmQrBox.Visibility == Visibility.Visible)
        {
            AmQrBox.Visibility = Visibility.Collapsed;
            return;
        }
        try
        {
            using var gen = new QRCodeGenerator();
            using var data = gen.CreateQrCode(t.Url, QRCodeGenerator.ECCLevel.M);
            var png = new PngByteQRCode(data).GetGraphic(8);
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = new MemoryStream(png);
            img.EndInit();
            img.Freeze();
            AmQrImage.Source = img;
            AmQrBox.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            App.Log("QR-код: " + ex.Message);
        }
    }

    public void ShowQrForTest() => AmQr_Click(this, new RoutedEventArgs());

    void AmPin_Click(object sender, RoutedEventArgs e)
    {
        var t = _active;
        CloseAddressMenu();
        if (t == null) return;
        SetPinned(t, !t.IsPinned);
        ShowToast(t.IsPinned ? "Вкладка закреплена: она откроется и при следующем запуске" : "Вкладка откреплена");
    }

    void AmShortcut_Click(object sender, RoutedEventArgs e)
    {
        var t = _active;
        CloseAddressMenu();
        if (t == null) return;
        var title = string.IsNullOrWhiteSpace(t.Title) ? UrlTools.NormalizeDomain(t.Url) : t.Title.Trim();
        if (title.Length > 28) title = title[..27] + "…";
        _settings.Shortcuts.Add(new Shortcut { Title = title, Url = t.Url });
        _settings.Save();
        ShowToast("Сайт добавлен на стартовую страницу");
    }

    void AmNewWindow_Click(object sender, RoutedEventArgs e)
    {
        var t = _active;
        CloseAddressMenu();
        if (t == null) return;
        OpenWindow(t.Url, t.Container);
        CloseTab(t, remember: false);
    }

    void AmSplitEnd_Click(object sender, RoutedEventArgs e)
    {
        CloseAddressMenu();
        EndSplit();
    }

    // ===================================================================== расширения

    string ExtensionsDir => Path.Combine(_dataDir, "extensions");

    /// <summary>Сверяет расширения профиля (контейнера) с настройками: ставит новые, включает и выключает, удаляет лишние.</summary>
    async Task EnsureExtensionsAsync(string container, CoreWebView2 core)
    {
        if (!Shared.ExtensionsSynced.Add(container)) return;
        IReadOnlyList<CoreWebView2BrowserExtension> installed;
        try
        {
            installed = await core.Profile.GetBrowserExtensionsAsync();
        }
        catch (Exception ex)
        {
            App.Log($"Расширения [{container}]: список не получен: {ex.GetType().Name} {ex.Message}");
            return;
        }
        var wanted = _settings.Extensions;
        foreach (var ext in installed)
        {
            try
            {
                var entry = wanted.FirstOrDefault(w => w.Id == ext.Id || w.Name == ext.Name);
                if (entry == null)
                {
                    // удаляем только то, что ставила «Дымка»; встроенные компоненты движка (просмотр PDF и др.) не трогаем
                    if (!_settings.ManagedExtensionIds.Contains(ext.Id) || IsVot(ext)) continue;
                    await ext.RemoveAsync();
                    _settings.ManagedExtensionIds.Remove(ext.Id);
                    _settings.Save();
                }
                else if (ext.IsEnabled != entry.Enabled)
                {
                    await ext.EnableAsync(entry.Enabled);
                }
            }
            catch (Exception ex)
            {
                App.Log($"Расширение «{ext.Name}» [{container}]: {ex.Message}");
            }
        }
        try { await EnsureVotAsync(core.Profile, installed); }
        catch (Exception ex) { App.Log($"Перевод видео [{container}]: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}"); }
        foreach (var entry in wanted)
        {
            if (installed.Any(x => x.Id == entry.Id || x.Name == entry.Name)) continue;
            var folder = Path.Combine(ExtensionsDir, entry.Id);
            if (!File.Exists(Path.Combine(folder, "manifest.json"))) continue;
            try
            {
                var added = await core.Profile.AddBrowserExtensionAsync(folder);
                if (!_settings.ManagedExtensionIds.Contains(added.Id)) { _settings.ManagedExtensionIds.Add(added.Id); _settings.Save(); }
                if (!entry.Enabled) await added.EnableAsync(false);
            }
            catch (Exception ex)
            {
                App.Log($"Расширение {entry.Name} [{container}]: 0x{ex.HResult:X8} {ex.Message}");
            }
        }
    }

    static bool IsVot(CoreWebView2BrowserExtension e) => e.Name.Contains("Voice Over Translation", StringComparison.OrdinalIgnoreCase);

    /// <summary>Встроенный перевод видео: распаковывает расширение VOT и ставит его в профиль (или убирает, если выключено).</summary>
    async Task EnsureVotAsync(CoreWebView2Profile profile, IReadOnlyList<CoreWebView2BrowserExtension> installed)
    {
        var vot = installed.FirstOrDefault(IsVot);
        if (!_settings.VideoTranslation)
        {
            if (vot != null) await vot.RemoveAsync();
            return;
        }
        var folder = ExtractVot();
        if (folder == null) return;
        if (vot != null && _settings.BuiltinVotFolder == folder)
        {
            if (!vot.IsEnabled) await vot.EnableAsync(true);
            return;
        }
        if (vot != null) await vot.RemoveAsync();
        await profile.AddBrowserExtensionAsync(folder);
        _settings.BuiltinVotFolder = folder;
        _settings.Save();
    }

    string? ExtractVot()
    {
        try
        {
            using var s = typeof(MainWindow).Assembly.GetManifestResourceStream("Dymka.vot.zip");
            if (s == null) return null;
            var ms = new MemoryStream();
            s.CopyTo(ms);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(ms.ToArray()))[..10].ToLowerInvariant();
            var folder = Path.Combine(_dataDir, "builtin", "vot-" + hash);
            if (!File.Exists(Path.Combine(folder, "manifest.json")))
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
                ms.Position = 0;
                using var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Read);
                zip.ExtractToDirectory(folder);
            }
            return folder;
        }
        catch (Exception ex)
        {
            App.Log("Перевод видео: " + ex.Message);
            return null;
        }
    }

    public async Task ResyncExtensionsAsync()
    {
        Shared.ExtensionsSynced.Clear();
        foreach (var w in Shared.Windows)
            foreach (var group in w._tabs.Where(t => t.View?.CoreWebView2 != null).GroupBy(t => t.Container))
                await w.EnsureExtensionsAsync(group.Key, group.First().View!.CoreWebView2);
    }

    async void Extensions_Click(object sender, RoutedEventArgs e)
    {
        ClosePopups();
        var w = new ExtensionsWindow(_settings, ExtensionsDir) { Owner = this };
        w.ShowDialog();
        if (!w.Changed) return;
        _settings.Save();
        await ResyncExtensionsAsync();
        ShowToast("Расширения обновлены. Перезагрузите открытые страницы (F5)");
    }
}
