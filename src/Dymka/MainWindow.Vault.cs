using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Dymka.Core;
using Microsoft.Web.WebView2.Core;

namespace Dymka;

/// <summary>Пароли и автозаполнение: подсказка у поля, подстановка по вашему выбору, сохранение пароля после входа.</summary>
public partial class MainWindow
{
    static Vault TheVault => Shared.Vault;
    public string DataDir => _dataDir;

    static string? _formsScript;
    static string FormsScript
    {
        get
        {
            if (_formsScript != null) return _formsScript;
            using var s = typeof(MainWindow).Assembly.GetManifestResourceStream("Dymka.Web.forms.js");
            using var r = new StreamReader(s!);
            return _formsScript = r.ReadToEnd();
        }
    }

    /// <summary>Открыть хранилище, если оно закрыто главным паролем. false - вы отказались.</summary>
    public bool EnsureVaultOpen()
    {
        var v = TheVault;
        if (!v.HasMasterPassword || v.IsUnlocked) return true;
        if (UnlockForTest != null)
        {
            try { v.Unlock(UnlockForTest); return true; } catch { return false; }
        }
        var pw = Dialogs.AskPassword(this, "Главный пароль",
            "Пароли и карточки закрыты главным паролем. Введите его, чтобы открыть.", confirm: false, validate: p =>
            {
                try { v.Unlock(p); return null; }
                catch (WrongPasswordException) { return "Неверный главный пароль"; }
                catch (Exception ex) { return "Не удалось открыть хранилище: " + ex.Message; }
            });
        return pw != null && v.IsUnlocked;
    }

    /// <summary>Для самопроверки: главный пароль вместо диалога.</summary>
    public static string? UnlockForTest { get; set; }

    /// <summary>Автоблокировка: хранилище с главным паролем закрывается после простоя.</summary>
    void CheckVaultLock()
    {
        var v = TheVault;
        if (v == null || !v.IsUnlocked || !v.HasMasterPassword || _settings.VaultLockMinutes <= 0) return;
        if (DateTime.Now - v.LastUsed > TimeSpan.FromMinutes(_settings.VaultLockMinutes))
        {
            v.Lock();
            FillPopup.IsOpen = false;
            App.Log("Хранилище паролей закрыто после простоя");
        }
    }

    // ===================================================================== подсказка у поля

    readonly DispatcherTimer _fillBlurTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    bool _fillBlurHooked;
    List<FieldInfo>? _fillFields;

    sealed record FillRow(string Title, string Sub, Action Act, List<(string label, Action act)>? Chips = null, bool Muted = false);

    readonly List<FillRow> _fillRows = new();

    void OnFormMessage(BrowserTab tab, CoreWebView2 core, string kind, JsonElement root)
    {
        if (tab != _active) return;
        switch (kind)
        {
            case "pw-focus":
                ShowPasswordHints(tab, core, root.GetProperty("rect").Clone());
                break;
            case "card-focus":
                var fields = root.GetProperty("fields").EnumerateArray().Select(f => new FieldInfo(
                    f.GetProperty("i").GetInt32(), Str(f, "type"), Str(f, "name"), Str(f, "id"), Str(f, "ac"), Str(f, "label"), Str(f, "ph"))).ToList();
                ShowCardHints(tab, core, root.GetProperty("rect").Clone(), root.GetProperty("focus").GetInt32(), fields);
                break;
            case "form-blur":
                if (!_fillBlurHooked)
                {
                    _fillBlurHooked = true;
                    _fillBlurTimer.Tick += (_, _) =>
                    {
                        _fillBlurTimer.Stop();
                        if (FillPopup.Child is UIElement c && c.IsMouseOver) return;
                        FillPopup.IsOpen = false;
                    };
                }
                _fillBlurTimer.Stop();
                _fillBlurTimer.Start();
                break;
            case "pw-submit":
                OnPasswordSubmitted(tab, core, Str(root, "user"), Str(root, "pass"));
                break;
        }
    }

    static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    void ShowPasswordHints(BrowserTab tab, CoreWebView2 core, JsonElement rect)
    {
        var v = TheVault;
        if (!v.Exists) return;
        var host = UrlTools.HostOf(core.Source);
        if (host.Length == 0) return;
        _fillRows.Clear();
        if (v.HasMasterPassword && !v.IsUnlocked)
        {
            _fillRows.Add(new FillRow("Пароли закрыты главным паролем", "Нажмите, чтобы открыть", () =>
            {
                FillPopup.IsOpen = false;
                if (EnsureVaultOpen()) ShowPasswordHints(tab, core, rect);
            }));
        }
        else
        {
            var entries = Vault.ForHost(v.Data!.Passwords, host);
            if (entries.Count == 0) return;
            foreach (var e in entries.Take(8))
            {
                var entry = e;
                _fillRows.Add(new FillRow(entry.Username.Length > 0 ? entry.Username : "(без логина)",
                    "••••••••" + (entry.Host != host ? "  ·  " + entry.Host : ""), () => FillLogin(tab, core, entry)));
            }
        }
        _fillRows.Add(new FillRow("Управление паролями…", "", () => OpenVaultWindow("passwords"), Muted: true));
        OpenFillPopup(tab, rect);
    }

    void ShowCardHints(BrowserTab tab, CoreWebView2 core, JsonElement rect, int focus, List<FieldInfo> fields, bool forced = false)
    {
        var v = TheVault;
        if ((!_settings.OfferAutofill && !forced) || !v.Exists) return;
        var kinds = FormFill.ClassifyAll(fields);
        if (!forced && (focus < 0 || focus >= kinds.Length || kinds[focus] == FieldKind.None)) { FillPopup.IsOpen = false; return; }
        if (forced && !kinds.Any(k => k != FieldKind.None))
        {
            ShowToast("В этой форме не нашлось знакомых полей (ФИО, ИНН, паспорт, адрес…)", seconds: 4);
            return;
        }
        _fillFields = fields;
        _fillRows.Clear();
        if (v.HasMasterPassword && !v.IsUnlocked)
        {
            _fillRows.Add(new FillRow("Карточки закрыты главным паролем", "Нажмите, чтобы открыть", () =>
            {
                FillPopup.IsOpen = false;
                if (EnsureVaultOpen()) ShowCardHints(tab, core, rect, focus, fields, forced);
            }));
        }
        else
        {
            var cards = v.Data!.Cards;
            if (cards.Count == 0)
            {
                if (forced) ShowToast("Карточек пока нет: добавьте их в «⋯» - «Пароли и автозаполнение»", seconds: 4);
                return;
            }
            bool hasAddress = kinds.Any(k => k is FieldKind.Address or FieldKind.Postcode);
            foreach (var c in cards)
            {
                var card = c;
                var sub = string.Join(" · ", new[] { card.ShortName, card.Inn.Length > 0 ? "ИНН " + card.Inn : "" }.Where(s => s.Length > 0));
                List<(string, Action)>? chips = null;
                if (hasAddress && card.Addresses.Count > 1)
                {
                    chips = new();
                    for (int i = 0; i < card.Addresses.Count; i++)
                    {
                        int idx = i;
                        chips.Add((card.Addresses[i].Label, () => FillCard(tab, core, card, idx)));
                    }
                }
                _fillRows.Add(new FillRow(card.Title, sub, () => FillCard(tab, core, card, null), chips));
            }
        }
        _fillRows.Add(new FillRow("Управление карточками…", "", () => OpenVaultWindow("cards"), Muted: true));
        OpenFillPopup(tab, rect);
    }

    void OpenFillPopup(BrowserTab tab, JsonElement rect)
    {
        _fillBlurTimer.Stop();
        var panel = new StackPanel();
        foreach (var row in _fillRows)
        {
            var r = row;
            var content = new StackPanel();
            content.Children.Add(new TextBlock
            {
                Text = r.Title, FontWeight = r.Muted ? FontWeights.Normal : FontWeights.SemiBold, FontSize = 13.5,
                Foreground = (Brush)FindResource(r.Muted ? "Accent" : "Ink"), TextTrimming = TextTrimming.CharacterEllipsis,
            });
            if (r.Sub.Length > 0)
                content.Children.Add(new TextBlock { Text = r.Sub, FontSize = 12, Foreground = (Brush)FindResource("Muted"), TextTrimming = TextTrimming.CharacterEllipsis });
            if (panel.Children.Count > 0 && !r.Muted)
                panel.Children.Add(new Border { Height = 1, Background = (Brush)FindResource("Line"), Margin = new Thickness(10, 4, 10, 4) });
            else if (r.Muted)
                panel.Children.Add(new Border { Height = 1, Background = (Brush)FindResource("Line"), Margin = new Thickness(10, 4, 10, 2) });
            var btn = new Button { Style = (Style)FindResource("MenuRow"), Height = double.NaN, MinHeight = 40, Padding = new Thickness(0, 8, 0, 8), Content = content, Focusable = false };
            btn.Click += (_, _) => { FillPopup.IsOpen = false; r.Act(); };
            panel.Children.Add(btn);
            if (r.Chips != null)
            {
                var wrap = new WrapPanel { Margin = new Thickness(10, 2, 10, 8) };
                wrap.Children.Add(new TextBlock { Text = "адрес:", FontSize = 12, Foreground = (Brush)FindResource("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
                foreach (var (label, act) in r.Chips)
                {
                    var a = act;
                    var chip = MakeChip(label, null);
                    chip.Height = 28;
                    chip.Margin = new Thickness(0, 3, 8, 3);
                    chip.Click += (_, _) => { FillPopup.IsOpen = false; a(); };
                    wrap.Children.Add(chip);
                }
                panel.Children.Add(wrap);
            }
        }
        FillPanel.Child = panel;
        double zoom = 1;
        try { zoom = tab.View?.ZoomFactor ?? 1; } catch { }
        FillPopup.PlacementTarget = tab.View;
        FillPopup.Placement = PlacementMode.Relative;
        FillPopup.HorizontalOffset = rect.GetProperty("x").GetDouble() * zoom - 14;
        FillPopup.VerticalOffset = rect.GetProperty("y").GetDouble() * zoom - 2;
        FillPopup.IsOpen = false;
        FillPopup.IsOpen = true;
    }

    void FillLogin(BrowserTab tab, CoreWebView2 core, PasswordEntry entry)
    {
        try
        {
            var host = UrlTools.HostOf(core.Source);
            // пароль подставляется только на тот сайт, для которого сохранён
            if (!host.Equals(entry.Host, StringComparison.OrdinalIgnoreCase) && UrlTools.RegistrableDomain(host) != UrlTools.RegistrableDomain(entry.Host)) return;
            _ = core.ExecuteScriptAsync($"window.__dymkaForms && window.__dymkaForms.fillLogin({JsonSerializer.Serialize(entry.Username)},{JsonSerializer.Serialize(entry.Password)})");
            entry.LastUsed = DateTime.Now;
            TheVault.Save();
        }
        catch (Exception ex) { App.Log("Подстановка пароля: " + ex.Message); }
    }

    void FillCard(BrowserTab tab, CoreWebView2 core, Card card, int? address)
    {
        if (_fillFields == null) return;
        var pairs = FormFill.Build(card, _fillFields, address).Select(x => new { i = x.Index, v = x.Value }).ToList();
        if (pairs.Count == 0) return;
        try { _ = core.ExecuteScriptAsync($"window.__dymkaForms && window.__dymkaForms.fill({JsonSerializer.Serialize(pairs)})"); }
        catch (Exception ex) { App.Log("Автозаполнение: " + ex.Message); }
    }

    /// <summary>«Заполнить карточкой» из меню правой кнопки - даже если поле не распознано.</summary>
    async Task FillFromContextMenuAsync(BrowserTab tab, CoreWebView2 core)
    {
        try
        {
            var raw = await core.ExecuteScriptAsync("window.__dymkaForms ? window.__dymkaForms.describeFocused() : ''");
            var json = JsonSerializer.Deserialize<string>(raw);
            if (string.IsNullOrEmpty(json)) return;
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var fields = root.GetProperty("fields").EnumerateArray().Select(f => new FieldInfo(
                f.GetProperty("i").GetInt32(), Str(f, "type"), Str(f, "name"), Str(f, "id"), Str(f, "ac"), Str(f, "label"), Str(f, "ph"))).ToList();
            ShowCardHints(tab, core, root.GetProperty("rect").Clone(), root.GetProperty("focus").GetInt32(), fields, forced: true);
        }
        catch (Exception ex) { App.Log("Заполнить карточкой: " + ex.Message); }
    }

    public IReadOnlyList<string> FillRowsForTest => FillPopup.IsOpen ? _fillRows.Select(r => r.Title).ToList() : new List<string>();
    public void ClickFillRowForTest(int i) { FillPopup.IsOpen = false; _fillRows[i].Act(); }
    public void ClickFillChipForTest(int row, int chip) { FillPopup.IsOpen = false; _fillRows[row].Chips![chip].act(); }

    // ===================================================================== сохранение пароля

    (BrowserTab tab, string url, string pass)? _pendingSave;

    void OnPasswordSubmitted(BrowserTab tab, CoreWebView2 core, string user, string pass)
    {
        if (!_settings.OfferSavePasswords || pass.Length == 0) return;
        var url = core.Source;
        var host = UrlTools.HostOf(url).ToLowerInvariant();
        if (host.Length == 0 || host == UrlTools.StartHost) return;
        var v = TheVault;
        bool update = false;
        if (v.Exists && (v.IsUnlocked || !v.HasMasterPassword))
        {
            var data = v.Data!;
            if (data.NeverSave.Contains(host)) return;
            var ex = data.Passwords.FirstOrDefault(e => e.Host == host && e.Username == user);
            if (ex != null && ex.Password == pass) return;
            update = ex != null;
        }
        _pendingSave = (tab, url, pass);
        SpTitle.Text = (update ? "Обновить пароль для " : "Сохранить пароль для ") + host + "?";
        SpUser.Text = user;
        SpPass.Text = new string('•', Math.Min(pass.Length, 14));
        SavePwPopup.IsOpen = true;
    }

    public bool SavePromptOpen => SavePwPopup.IsOpen;

    void SpSave_Click(object sender, RoutedEventArgs e) => ConfirmSavePassword();

    public void ConfirmSavePassword()
    {
        SavePwPopup.IsOpen = false;
        if (_pendingSave is not { } p) return;
        _pendingSave = null;
        if (!EnsureVaultOpen()) return;
        var r = Vault.Remember(TheVault.Data!, p.url, SpUser.Text.Trim(), p.pass);
        TheVault.Save();
        ShowToast(r == "updated" ? "Пароль обновлён" : "Пароль сохранён");
    }

    void SpLater_Click(object sender, RoutedEventArgs e)
    {
        SavePwPopup.IsOpen = false;
        _pendingSave = null;
    }

    void SpNever_Click(object sender, RoutedEventArgs e)
    {
        SavePwPopup.IsOpen = false;
        if (_pendingSave is not { } p) return;
        _pendingSave = null;
        if (!EnsureVaultOpen()) return;
        var host = UrlTools.HostOf(p.url).ToLowerInvariant();
        if (!TheVault.Data!.NeverSave.Contains(host)) TheVault.Data!.NeverSave.Add(host);
        TheVault.Save();
        ShowToast("Для " + host + " пароль больше предлагаться не будет");
    }

    // ===================================================================== окно паролей и карточек

    void Vault_Click(object sender, RoutedEventArgs e)
    {
        ClosePopups();
        OpenVaultWindow("passwords");
    }

    public VaultWindow? VaultWindowForTest { get; private set; }

    public void OpenVaultWindow(string page)
    {
        if (!EnsureVaultOpen()) return;
        var w = new VaultWindow(TheVault, _settings, page, this) { Owner = this };
        VaultWindowForTest = w;
        w.Closed += (_, _) => VaultWindowForTest = null;
        if (_selfTestDir != null) w.Show(); else w.ShowDialog();
    }
}
