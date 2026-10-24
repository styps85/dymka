using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Dymka.Core;
using Microsoft.Web.WebView2.Core;

namespace Dymka;

/// <summary>Встроенные сертификаты Минцифры.</summary>
static class RussianRoots
{
    static List<X509Certificate2>? _roots, _subs;
    public static readonly List<X509Certificate2> ExtraRootsForTest = new();

    static X509Certificate2? Load(string name)
    {
        try
        {
            using var s = typeof(RussianRoots).Assembly.GetManifestResourceStream(name);
            if (s == null) return null;
            using var r = new StreamReader(s);
            return X509Certificate2.CreateFromPem(r.ReadToEnd());
        }
        catch (Exception ex)
        {
            App.Log("Сертификат Минцифры не загружен: " + ex.Message);
            return null;
        }
    }

    public static List<X509Certificate2> Roots
    {
        get
        {
            _roots ??= new[] { Load("Dymka.ru-root.crt") }.Where(c => c != null).Cast<X509Certificate2>().ToList();
            return _roots.Concat(ExtraRootsForTest).ToList();
        }
    }

    public static List<X509Certificate2> Intermediates =>
        _subs ??= new[] { Load("Dymka.ru-sub.crt"), Load("Dymka.ru-sub-2024.crt") }.Where(c => c != null).Cast<X509Certificate2>().ToList();
}

/// <summary>Российские сертификаты и подсказки при вводе в адресную строку.</summary>
public partial class MainWindow
{
    // ===================================================================== сертификаты Минцифры

    void OnServerCertificateError(BrowserTab tab, CoreWebView2ServerCertificateErrorDetectedEventArgs e)
    {
        try
        {
            var host = UrlTools.HostOf(e.RequestUri);
            if (!_settings.TrustRussianCa || host.Length == 0) return;
            if (!RussianCa.IsAllowedHost(host, _settings.RussianCaSites))
            {
                App.Log($"Сертификат {host}: не доверенный ({e.ErrorStatus}), сайт не в российской зоне");
                return;
            }
            var cert = e.ServerCertificate;
            var chain = cert.PemEncodedIssuerCertificateChain?.ToList() ?? new List<string>();
            if (RussianCa.Verify(cert.ToPemEncoding(), chain, host, RussianRoots.Roots, RussianRoots.Intermediates, out var why))
            {
                e.Action = CoreWebView2ServerCertificateErrorAction.AlwaysAllow;
                lock (Shared.RussianCaHosts) Shared.RussianCaHosts.Add(host.ToLowerInvariant());
                App.Log($"Сертификат {host}: принят по сертификату Минцифры");
            }
            else App.Log($"Сертификат {host}: не принят ({e.ErrorStatus}; {why})");
        }
        catch (Exception ex)
        {
            App.Log("Проверка сертификата: " + ex.Message);
        }
    }

    public static bool UsesRussianCa(string host)
    {
        lock (Shared.RussianCaHosts) return Shared.RussianCaHosts.Contains(host.ToLowerInvariant());
    }

    // ===================================================================== подсказки при вводе

    static readonly HttpClient SuggestHttp = new() { Timeout = TimeSpan.FromSeconds(3) };
    CancellationTokenSource? _suggestCts;
    readonly DispatcherTimer _suggestTimer = new() { Interval = TimeSpan.FromMilliseconds(140) };
    bool _suggestHooked;
    List<Suggestion> _suggestions = new();
    int _suggestIndex = -1;
    string _suggestFor = "";

    /// <summary>Свои сайты для подсказок: плитки всех контейнеров, открытые вкладки, сайты с сохранёнными паролями.</summary>
    IEnumerable<(string title, string url)> UserSites()
    {
        foreach (var list in _settings.ShortcutsByContainer.Values)
            foreach (var s in list) yield return (s.Title, s.Url);
        foreach (var w in Shared.Windows)
            foreach (var t in w._tabs.Where(t => !t.IsStart && t.Url.StartsWith("http")))
                yield return (t.Title, t.Url);
        var v = Shared.Vault;
        if (v != null && v.Exists && (v.IsUnlocked || !v.HasMasterPassword) && v.Data is { } d)
            foreach (var p in d.Passwords) yield return (UrlTools.NormalizeDomain(p.Host), p.Url.Length > 0 ? p.Url : "https://" + p.Host);
    }

    /// <summary>Подсказки для ввода: свои и встроенные сайты сразу, поисковые - по сети (если включено).</summary>
    public async Task<List<Suggestion>> GetSuggestionsAsync(string input, CancellationToken ct)
    {
        input = input.Trim();
        if (input.Length < 2 || input.Contains("://")) return new();
        var nav = Suggest.Local(input, UserSites());
        var remote = new List<string>();
        if (_settings.SearchSuggestions)
        {
            try
            {
                var json = await SuggestHttp.GetStringAsync(Suggest.RemoteUrl(_settings.SearchEngineId, input), ct);
                remote = Suggest.ParseRemote(json);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException) { }
        }
        return Suggest.Merge(input, nav, remote, _settings.Engine.Name);
    }

    void ScheduleSuggestions()
    {
        if (!_suggestHooked)
        {
            _suggestHooked = true;
            _suggestTimer.Tick += async (_, _) =>
            {
                _suggestTimer.Stop();
                await UpdateSuggestionsAsync();
            };
        }
        _suggestTimer.Stop();
        _suggestTimer.Start();
    }

    async Task UpdateSuggestionsAsync()
    {
        var text = Address.Text;
        if (!Address.IsKeyboardFocused || text.Trim().Length < 2 || AddressMenu.IsOpen)
        {
            SuggestPopup.IsOpen = false;
            return;
        }
        _suggestCts?.Cancel();
        var cts = _suggestCts = new CancellationTokenSource();
        List<Suggestion> items;
        try { items = await GetSuggestionsAsync(text, cts.Token); }
        catch { return; }
        if (cts.IsCancellationRequested || Address.Text != text || !Address.IsKeyboardFocused) return;
        _suggestions = items;
        _suggestIndex = -1;
        _suggestFor = text;
        RenderSuggestions();
    }

    void RenderSuggestions()
    {
        if (_suggestions.Count == 0) { SuggestPopup.IsOpen = false; return; }
        var panel = new StackPanel();
        for (int i = 0; i < _suggestions.Count; i++)
        {
            var s = _suggestions[i];
            int idx = i;
            var row = new DockPanel();
            var icon = new Glyph
            {
                Data = (Geometry)FindResource(s.Kind == "nav" ? "IcGlobe" : "IcSearch"), Size = 16,
                Foreground = (Brush)FindResource(s.Kind == "nav" ? "Accent" : "Muted"), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            var hint = new TextBlock
            {
                Text = s.Kind == "nav" ? s.Hint : "",
                FontSize = 12.5, Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            DockPanel.SetDock(hint, Dock.Right);
            row.Children.Add(hint);
            row.Children.Add(new TextBlock
            {
                Text = s.Kind == "nav" ? "Перейти: " + s.Text : s.Text, FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = s.Kind == "nav" ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center,
            });
            var btn = new Button { Style = (Style)FindResource("MenuRow"), Content = row, Focusable = false, Tag = idx };
            if (i == _suggestIndex) btn.Background = (Brush)FindResource("AccentSoft");
            btn.Click += (_, _) => AcceptSuggestion(idx);
            panel.Children.Add(btn);
        }
        SuggestPanel.Child = panel;
        SuggestPopup.Width = Math.Max(360, AddressBorder.ActualWidth);
        SuggestPopup.IsOpen = true;
    }

    void MoveSuggestion(int dir)
    {
        if (!SuggestPopup.IsOpen || _suggestions.Count == 0) return;
        _suggestIndex = (_suggestIndex + dir + _suggestions.Count + 1) % (_suggestions.Count + 1);
        if (_suggestIndex == _suggestions.Count) _suggestIndex = -1;
        RenderSuggestions();
    }

    void AcceptSuggestion(int idx)
    {
        if (idx < 0 || idx >= _suggestions.Count) return;
        var s = _suggestions[idx];
        SuggestPopup.IsOpen = false;
        var target = s.Kind == "nav" ? s.Url : string.Format(_settings.Engine.Template, Uri.EscapeDataString(s.Text));
        var tab = _active;
        if (tab == null) return;
        tab.View?.Focus();
        Keyboard.ClearFocus();
        Navigate(tab, target);
    }

    public void TypeInAddressForTest(string s)
    {
        FocusAddress();
        Address.Text = s;
        Address.CaretIndex = s.Length;
    }

    public IReadOnlyList<Suggestion> SuggestionsForTest => SuggestPopup.IsOpen ? _suggestions : new List<Suggestion>();
    public void AcceptSuggestionForTest(int i) => AcceptSuggestion(i);

    void Address_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!SuggestPopup.IsOpen) return;
        switch (e.Key)
        {
            case Key.Down: MoveSuggestion(1); e.Handled = true; break;
            case Key.Up: MoveSuggestion(-1); e.Handled = true; break;
            case Key.Enter when _suggestIndex >= 0: AcceptSuggestion(_suggestIndex); e.Handled = true; break;
            case Key.Escape: SuggestPopup.IsOpen = false; e.Handled = true; break;
        }
    }

    /// <summary>Подсказки для поля поиска стартовой страницы.</summary>
    async Task SendStartSuggestionsAsync(CoreWebView2 core, string q)
    {
        _suggestCts?.Cancel();
        var cts = _suggestCts = new CancellationTokenSource();
        List<Suggestion> items;
        try { items = await GetSuggestionsAsync(q, cts.Token); }
        catch { return; }
        if (cts.IsCancellationRequested) return;
        try
        {
            core.PostWebMessageAsJson(JsonSerializer.Serialize(new
            {
                type = "suggest", q,
                items = items.Select(s => new { kind = s.Kind, text = s.Text, url = s.Url, hint = s.Hint }),
            }));
        }
        catch { }
    }
}
