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

    /// <summary>Документ, приславший сообщение: сама страница или встроенное окно (фрейм) - например, форма оплаты банка.</summary>
    public sealed class FormTarget
    {
        public required string Source { get; init; }
        public required Func<string, Task<string>> Exec { get; init; }
        public bool InFrame { get; init; }
        public CoreWebView2Frame? Frame { get; init; }
        public string Host => UrlTools.HostOf(Source);
    }

    /// <summary>Встроенные окна (фреймы) каждой вкладки, включая вложенные, - чтобы заполнять формы оплаты,
    /// у которых поля разнесены по разным окнам.</summary>
    readonly Dictionary<BrowserTab, List<CoreWebView2Frame>> _frames = new();
    static bool? _nestedFrames;

    readonly DispatcherTimer _fillBlurTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    bool _fillBlurHooked;
    List<FieldInfo>? _fillFields;

    /// <summary>Строка подсказки. Search - текст для отбора по введённому (null - строка видна всегда).</summary>
    sealed record FillRow(string Title, string Sub, Action Act, List<(string label, Action act)>? Chips = null, bool Muted = false,
        string? Badge = null, string? BadgeColor = null, string? Right = null, string? BadgeFore = null, string? Search = null);

    /// <summary>Строки, видимые сейчас (после отбора по введённому).</summary>
    readonly List<FillRow> _fillRows = new();
    /// <summary>Все строки подсказки для текущего поля.</summary>
    List<FillRow> _fillAll = new();
    string _fillHeaderText = "", _fillQuery = "";
    BrowserTab? _fillTab;
    /// <summary>Подсказка скрыта отбором (ничего не нашлось) - вернётся, если стереть лишнее.</summary>
    bool _fillFilteredOut;
    /// <summary>Подсказка скрыта крестиком - на этой странице сама не появляется.</summary>
    (BrowserTab tab, string url)? _fillHiddenOn;
    /// <summary>Куда вы перетащили подсказку на этой странице.</summary>
    (BrowserTab tab, string url, double x, double y)? _fillMovedTo;

    bool FillHiddenHere(BrowserTab tab) => _fillHiddenOn is { } h && h.tab == tab && h.url == tab.Url;

    static List<FieldInfo> ParseFields(JsonElement root) =>
        root.GetProperty("fields").EnumerateArray().Select(f => new FieldInfo(
            f.GetProperty("i").GetInt32(), Str(f, "type"), Str(f, "name"), Str(f, "id"), Str(f, "ac"), Str(f, "label"), Str(f, "ph"))
        {
            MaxLength = f.TryGetProperty("ml", out var ml) && ml.ValueKind == JsonValueKind.Number ? ml.GetInt32() : 0,
        }).ToList();

    void HookFrame(BrowserTab tab, CoreWebView2Frame frame, int depth = 1)
    {
        try
        {
            if (!_frames.TryGetValue(tab, out var list)) _frames[tab] = list = new List<CoreWebView2Frame>();
            list.Add(frame);
            frame.Destroyed += (_, _) => { if (_frames.TryGetValue(tab, out var l)) l.Remove(frame); };
            frame.NavigationStarting += (_, e) =>
            {
                var h = UrlTools.HostOf(e.Uri);
                if (h.Length > 0 && h != "about") App.Log($"Фрейм (уровень {depth}): {h}");
            };
            // вложенные фреймы (окно в окне - так устроены формы оплаты Яндекс Пэй и банков);
            // событие есть не во всех версиях движка, поэтому подписка через отражение
            var ev = typeof(CoreWebView2Frame).GetEvent("FrameCreated");
            if (_nestedFrames == null)
            {
                _nestedFrames = ev != null;
                App.Log("Вложенные фреймы: " + (ev != null ? "поддерживаются" : "не поддерживаются этой версией движка"));
            }
            if (ev != null && depth < 4)
                ev.AddEventHandler(frame, new EventHandler<CoreWebView2FrameCreatedEventArgs>((_, fe) => HookFrame(tab, fe.Frame, depth + 1)));
            frame.WebMessageReceived += (_, e) =>
            {
                try
                {
                    using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("__dymka", out var k)) return;
                    var kind = k.GetString();
                    if (kind is "pw-focus" or "card-focus" or "form-blur" or "pw-submit" or "card-submit" or "form-input" or "form-esc")
                        OnFormMessage(tab, new FormTarget { Source = e.Source, Exec = frame.ExecuteScriptAsync, InFrame = true, Frame = frame }, kind, root);
                }
                catch (Exception ex) { App.Log("Сообщение из фрейма: " + ex.Message); }
            };
        }
        catch (Exception ex) { App.Log("Фрейм: " + ex.Message); }
    }

    void OnFormMessage(BrowserTab tab, FormTarget target, string kind, JsonElement root)
    {
        if (tab != _active) return;
        switch (kind)
        {
            case "pw-focus":
                ShowPasswordHints(tab, target, root.GetProperty("rect").Clone());
                break;
            case "card-focus":
                ShowCardHints(tab, target, root.GetProperty("rect").Clone(), root.GetProperty("focus").GetInt32(), ParseFields(root));
                break;
            case "form-blur":
                if (!_fillBlurHooked)
                {
                    _fillBlurHooked = true;
                    _fillBlurTimer.Tick += (_, _) =>
                    {
                        _fillBlurTimer.Stop();
                        if (FillPopup.Child is UIElement c && c.IsMouseOver) return;
                        if (FillHeader.IsMouseCaptured) return;
                        FillPopup.IsOpen = false;
                        _fillFilteredOut = false;
                    };
                }
                _fillBlurTimer.Stop();
                _fillBlurTimer.Start();
                break;
            case "form-input":
                // вы вводите текст в поле: в подсказке остаются варианты, где есть введённое
                if (tab == _fillTab && (FillPopup.IsOpen || _fillFilteredOut))
                {
                    _fillQuery = Str(root, "v");
                    RenderFillPopup();
                }
                break;
            case "form-esc":
                if (FillPopup.IsOpen) FillPopup.IsOpen = false;
                _fillFilteredOut = false;
                break;
            case "pw-submit":
                OnPasswordSubmitted(tab, target, Str(root, "user"), Str(root, "pass"));
                break;
            case "card-submit":
                var fl = ParseFields(root);
                var vals = root.GetProperty("fields").EnumerateArray().Select(f => Str(f, "v")).ToList();
                OnCardSubmitted(target, fl, vals);
                break;
        }
    }

    static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    void ShowPasswordHints(BrowserTab tab, FormTarget target, JsonElement rect)
    {
        var v = TheVault;
        if (!v.Exists || FillHiddenHere(tab)) return;
        var host = target.Host;
        if (host.Length == 0) return;
        _fillRows.Clear();
        if (v.HasMasterPassword && !v.IsUnlocked)
        {
            _fillRows.Add(new FillRow("Пароли закрыты главным паролем", "Нажмите, чтобы открыть", () =>
            {
                FillPopup.IsOpen = false;
                if (EnsureVaultOpen()) ShowPasswordHints(tab, target, rect);
            }));
        }
        else
        {
            var entries = Vault.ForHost(v.Data!.Passwords, host);
            if (entries.Count == 0) return;
            foreach (var e in entries)
            {
                var entry = e;
                _fillRows.Add(new FillRow(entry.Username.Length > 0 ? entry.Username : "(без логина)",
                    "••••••••" + (entry.Host != host ? "  ·  " + entry.Host : ""), () => FillLogin(target, entry), Search: FillFilter.SearchText(entry)));
            }
        }
        _fillRows.Add(new FillRow("Управление паролями…", "", () => OpenVaultWindow("passwords"), Muted: true));
        OpenFillPopup(tab, rect, target.InFrame, "Сохранённые пароли");
    }

    void ShowCardHints(BrowserTab tab, FormTarget target, JsonElement rect, int focus, List<FieldInfo> fields, bool forced = false)
    {
        var v = TheVault;
        if ((!_settings.OfferAutofill && !forced) || !v.Exists) return;
        if (forced) _fillHiddenOn = null;
        else if (FillHiddenHere(tab)) return;
        var kinds = FormFill.ClassifyAll(fields);
        // поле оплаты - подсказка с банковскими картами
        if (focus >= 0 && focus < kinds.Length && FormFill.IsBankCardField(kinds[focus]))
        {
            ShowBankCardHints(tab, target, rect, fields, forced);
            return;
        }
        if (!forced && (focus < 0 || focus >= kinds.Length || kinds[focus] == FieldKind.None)) { FillPopup.IsOpen = false; return; }
        if (forced && !kinds.Any(k => k != FieldKind.None))
        {
            ShowToast("В этой форме не нашлось знакомых полей (ФИО, ИНН, паспорт, адрес, карта…)", seconds: 4);
            return;
        }
        if (forced && !kinds.Any(k => k != FieldKind.None && !FormFill.IsBankCardField(k)))
        {
            ShowBankCardHints(tab, target, rect, fields, forced);
            return;
        }
        _fillFields = fields;
        _fillRows.Clear();
        if (v.HasMasterPassword && !v.IsUnlocked)
        {
            _fillRows.Add(new FillRow("Карточки закрыты главным паролем", "Нажмите, чтобы открыть", () =>
            {
                FillPopup.IsOpen = false;
                if (EnsureVaultOpen()) ShowCardHints(tab, target, rect, focus, fields, forced);
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
                        chips.Add((card.Addresses[i].Label, () => FillCard(target, card, idx)));
                    }
                }
                _fillRows.Add(new FillRow(card.Title, sub, () => FillCard(target, card, null), chips, Search: FillFilter.SearchText(card)));
            }
        }
        _fillRows.Add(new FillRow("Управление карточками…", "", () => OpenVaultWindow("cards"), Muted: true));
        OpenFillPopup(tab, rect, target.InFrame, "Заполнить из карточки");
    }

    void ShowBankCardHints(BrowserTab tab, FormTarget target, JsonElement rect, List<FieldInfo> fields, bool forced)
    {
        var v = TheVault;
        _fillFields = fields;
        _fillRows.Clear();
        if (v.HasMasterPassword && !v.IsUnlocked)
        {
            _fillRows.Add(new FillRow("Банковские карты закрыты главным паролем", "Нажмите, чтобы открыть", () =>
            {
                FillPopup.IsOpen = false;
                if (EnsureVaultOpen()) ShowBankCardHints(tab, target, rect, fields, forced);
            }));
        }
        else
        {
            var cards = v.Data!.BankCards;
            if (cards.Count == 0)
            {
                if (forced) ShowToast("Банковских карт пока нет: добавьте их в «⋯» - «Пароли и автозаполнение» - «Банковские карты»", seconds: 5);
                return;
            }
            foreach (var c in cards)
            {
                var card = c;
                _fillRows.Add(new FillRow(card.Title, card.Sub, () => FillBankCard(tab, target, card),
                    Badge: card.BadgeText, BadgeColor: card.BadgeColor, Right: "•• " + card.Last4, BadgeFore: card.BadgeFore,
                    Search: FillFilter.SearchText(card)));
            }
        }
        _fillRows.Add(new FillRow("Управление банковскими картами…", "", () => OpenVaultWindow("bankcards"), Muted: true));
        OpenFillPopup(tab, rect, target.InFrame, "Банковские карты");
    }

    void OpenFillPopup(BrowserTab tab, JsonElement rect, bool inFrame, string header)
    {
        _fillBlurTimer.Stop();
        _fillAll = _fillRows.ToList();
        _fillHeaderText = header;
        _fillQuery = "";
        _fillTab = tab;
        _fillFilteredOut = false;
        FillPopup.PlacementTarget = tab.View;
        FillPopup.Placement = PlacementMode.Relative;
        if (inFrame && tab.View != null)
        {
            // во фрейме координаты поля - внутри фрейма; ставим подсказку у указателя мыши
            var pt = System.Windows.Input.Mouse.GetPosition(tab.View);
            bool inside = pt.X >= 0 && pt.Y >= 0 && pt.X <= tab.View.ActualWidth && pt.Y <= tab.View.ActualHeight;
            FillPopup.HorizontalOffset = inside ? pt.X - 30 : 40;
            FillPopup.VerticalOffset = inside ? pt.Y + 16 : 40;
        }
        else
        {
            double zoom = 1;
            try { zoom = tab.View?.ZoomFactor ?? 1; } catch { }
            FillPopup.HorizontalOffset = rect.GetProperty("x").GetDouble() * zoom - 14;
            FillPopup.VerticalOffset = rect.GetProperty("y").GetDouble() * zoom - 2;
        }
        if (_fillMovedTo is { } m && m.tab == tab && m.url == tab.Url)
        {
            // вы перетащили подсказку на этой странице - она остаётся там, куда её поставили
            FillPopup.HorizontalOffset = m.x;
            FillPopup.VerticalOffset = m.y;
        }
        FillPopup.IsOpen = false;
        RenderFillPopup();
    }

    /// <summary>Строит подсказку из вариантов, подходящих к введённому в поле.</summary>
    void RenderFillPopup()
    {
        var q = _fillQuery;
        var shown = _fillAll.Where(r => r.Search == null || r.Muted || FillFilter.Matches(q, r.Search)).ToList();
        bool anyChoice = shown.Any(r => !r.Muted && r.Search != null);
        bool hasChoices = _fillAll.Any(r => !r.Muted && r.Search != null);
        _fillRows.Clear();
        if (hasChoices && !anyChoice)
        {
            // ничего не подходит к введённому - подсказка не мешает; сотрёте лишнее - вернётся
            FillPopup.IsOpen = false;
            _fillFilteredOut = true;
            return;
        }
        _fillFilteredOut = false;
        _fillRows.AddRange(shown);
        int total = _fillAll.Count(r => !r.Muted && r.Search != null), found = shown.Count(r => !r.Muted && r.Search != null);
        FillHeaderText.Text = _fillHeaderText + (FillFilter.Normalize(q).Length > 0 && found < total ? $"  ·  {found} из {total}" : "");
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
            object rowContent = content;
            if (r.Badge != null)
            {
                // значок карты: буквы банка на цветной плашке и последние цифры справа
                var dock = new DockPanel();
                var badge = new Border
                {
                    Width = 34, Height = 34, CornerRadius = new CornerRadius(9), Background = BrushCache.Get(r.BadgeColor ?? "#3D63DD"),
                    Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = r.Badge, Foreground = BrushCache.Get(r.BadgeFore ?? "#FFFFFF"), FontWeight = FontWeights.Bold, FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                };
                DockPanel.SetDock(badge, Dock.Left);
                dock.Children.Add(badge);
                if (r.Right != null)
                {
                    var right = new TextBlock { Text = r.Right, FontWeight = FontWeights.SemiBold, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
                    DockPanel.SetDock(right, Dock.Right);
                    dock.Children.Add(right);
                }
                content.VerticalAlignment = VerticalAlignment.Center;
                dock.Children.Add(content);
                rowContent = dock;
            }
            var btn = new Button { Style = (Style)FindResource("MenuRow"), Height = double.NaN, MinHeight = 40, Padding = new Thickness(0, 8, 0, 8), Content = rowContent, Focusable = false };
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
        FillScroll.ScrollToTop();
        if (!FillPopup.IsOpen) FillPopup.IsOpen = true;
    }

    // перетаскивание подсказки за заголовок
    Point _fillDragStart;
    double _fillDragX, _fillDragY;

    void FillHeader_Down(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _fillDragStart = e.GetPosition(this);
        _fillDragX = FillPopup.HorizontalOffset;
        _fillDragY = FillPopup.VerticalOffset;
        FillHeader.CaptureMouse();
        e.Handled = true;
    }

    void FillHeader_Move(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!FillHeader.IsMouseCaptured) return;
        var p = e.GetPosition(this);
        double w = _fillTab?.View?.ActualWidth ?? ActualWidth, h = _fillTab?.View?.ActualHeight ?? ActualHeight;
        FillPopup.HorizontalOffset = Math.Clamp(_fillDragX + p.X - _fillDragStart.X, -20, Math.Max(0, w - 80));
        FillPopup.VerticalOffset = Math.Clamp(_fillDragY + p.Y - _fillDragStart.Y, -20, Math.Max(0, h - 50));
    }

    void FillHeader_Up(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (FillHeader.IsMouseCaptured) FillHeader.ReleaseMouseCapture();
    }

    void FillHeader_LostCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_fillTab != null && (Math.Abs(FillPopup.HorizontalOffset - _fillDragX) > 2 || Math.Abs(FillPopup.VerticalOffset - _fillDragY) > 2))
            _fillMovedTo = (_fillTab, _fillTab.Url, FillPopup.HorizontalOffset, FillPopup.VerticalOffset);
    }

    void FillClose_Click(object sender, RoutedEventArgs e) => HideFillOnPage();

    /// <summary>Крестик: подсказка больше не появляется сама на этой странице (до перехода на другую).</summary>
    void HideFillOnPage()
    {
        FillPopup.IsOpen = false;
        _fillFilteredOut = false;
        if (_fillTab == null) return;
        _fillHiddenOn = (_fillTab, _fillTab.Url);
        ShowToast("Подсказка скрыта на этой странице. Вернуть: правый щелчок по полю - «Заполнить карточкой…»", seconds: 5);
    }

    // для самопроверки
    public void FillTypeForTest(string text) { _fillQuery = text; RenderFillPopup(); }
    public bool FillOpenForTest => FillPopup.IsOpen;
    public double FillOpacityForTest => FillBox.Opacity;
    public void FillMoveForTest(double dx, double dy)
    {
        _fillDragX = FillPopup.HorizontalOffset; _fillDragY = FillPopup.VerticalOffset;
        FillPopup.HorizontalOffset += dx; FillPopup.VerticalOffset += dy;
        FillHeader_LostCapture(this, null!);
    }
    public (double x, double y) FillOffsetForTest => (FillPopup.HorizontalOffset, FillPopup.VerticalOffset);
    public void FillHideForTest() => HideFillOnPage();


    void FillLogin(FormTarget target, PasswordEntry entry)
    {
        try
        {
            var host = target.Host;
            // пароль подставляется только на тот сайт, для которого сохранён
            if (!host.Equals(entry.Host, StringComparison.OrdinalIgnoreCase) && UrlTools.RegistrableDomain(host) != UrlTools.RegistrableDomain(entry.Host)) return;
            _ = target.Exec($"window.__dymkaForms && window.__dymkaForms.fillLogin({JsonSerializer.Serialize(entry.Username)},{JsonSerializer.Serialize(entry.Password)})");
            entry.LastUsed = DateTime.Now;
            TheVault.Save();
        }
        catch (Exception ex) { App.Log("Подстановка пароля: " + ex.Message); }
    }

    void FillCard(FormTarget target, Card card, int? address)
    {
        if (_fillFields == null) return;
        var pairs = FormFill.Build(card, _fillFields, address).Select(x => new { i = x.Index, v = x.Value }).ToList();
        if (pairs.Count == 0) return;
        try { _ = target.Exec($"window.__dymkaForms && window.__dymkaForms.fill({JsonSerializer.Serialize(pairs)})"); }
        catch (Exception ex) { App.Log("Автозаполнение: " + ex.Message); }
    }

    void FillBankCard(BrowserTab tab, FormTarget target, BankCard card)
    {
        if (_fillFields == null) return;
        var pairs = FormFill.BuildBankCard(card, _fillFields).Select(x => new { i = x.Index, v = x.Value }).ToList();
        int cvc = FormFill.CvcIndex(_fillFields);
        if (pairs.Count > 0)
        {
            try { _ = target.Exec($"window.__dymkaForms && window.__dymkaForms.fill({JsonSerializer.Serialize(pairs)},{cvc})"); }
            catch (Exception ex) { App.Log("Банковская карта: " + ex.Message); }
        }
        App.Log($"Банковская карта •••• {card.Last4} подставлена на {target.Host}" + (target.InFrame ? " (форма оплаты во фрейме)" : ""));
        // остальные поля оплаты могут быть в других встроенных окнах - заполняем и их
        _ = FillCardInOtherDocumentsAsync(tab, target, card, cvc < 0);
        ShowToast("Введите код CVC с обратной стороны карты - он не хранится", seconds: 4);
    }

    async Task FillCardInOtherDocumentsAsync(BrowserTab tab, FormTarget origin, BankCard card, bool focusCvc)
    {
        var docs = new List<(string name, Func<string, Task<string>> exec)>();
        if (origin.InFrame && tab.Core is { } top) docs.Add(("страница", top.ExecuteScriptAsync));
        if (_frames.TryGetValue(tab, out var frames))
            foreach (var f in frames.ToList())
                if (f != origin.Frame) docs.Add(("фрейм", f.ExecuteScriptAsync));
        int filled = 0;
        foreach (var (name, exec) in docs)
        {
            try
            {
                var raw = await exec("window.__dymkaForms ? window.__dymkaForms.describeAll() : ''");
                var json = raw.Length > 2 ? JsonSerializer.Deserialize<string>(raw) : null;
                if (string.IsNullOrEmpty(json)) continue;
                using var doc = JsonDocument.Parse(json);
                var fields = ParseFields(doc.RootElement);
                var pairs = FormFill.BuildBankCard(card, fields).Select(x => new { i = x.Index, v = x.Value }).ToList();
                int cvc = focusCvc ? FormFill.CvcIndex(fields) : -1;
                if (pairs.Count == 0 && cvc < 0) continue;
                await exec($"window.__dymkaForms && window.__dymkaForms.fill({JsonSerializer.Serialize(pairs)},{cvc})");
                if (cvc >= 0) focusCvc = false;
                filled += pairs.Count;
            }
            catch (Exception ex) { App.Log($"Карта в другом окне ({name}): {ex.Message}"); }
        }
        if (filled > 0) App.Log($"Банковская карта: заполнено полей в других окнах формы - {filled}");
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
            var target = new FormTarget { Source = core.Source, Exec = core.ExecuteScriptAsync };
            ShowCardHints(tab, target, root.GetProperty("rect").Clone(), root.GetProperty("focus").GetInt32(), ParseFields(root), forced: true);
        }
        catch (Exception ex) { App.Log("Заполнить карточкой: " + ex.Message); }
    }

    public IReadOnlyList<string> FillRowsForTest => FillPopup.IsOpen ? _fillRows.Select(r => r.Title).ToList() : new List<string>();
    public void ClickFillRowForTest(int i) { FillPopup.IsOpen = false; _fillRows[i].Act(); }
    public void ClickFillChipForTest(int row, int chip) { FillPopup.IsOpen = false; _fillRows[row].Chips![chip].act(); }

    // ===================================================================== сохранение пароля

    (BrowserTab tab, string url, string pass)? _pendingSave;

    void OnPasswordSubmitted(BrowserTab tab, FormTarget target, string user, string pass)
    {
        if (!_settings.OfferSavePasswords || pass.Length == 0) return;
        var url = target.Source;
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

    // ===================================================================== сохранить карту?

    BankCard? _pendingCard;

    void OnCardSubmitted(FormTarget target, List<FieldInfo> fields, List<string> values)
    {
        if (!_settings.OfferSaveCards) return;
        var card = FormFill.CardFromForm(fields, values);
        if (card == null) return;
        var v = TheVault;
        if (v.Exists && (v.IsUnlocked || !v.HasMasterPassword) && v.Data!.BankCards.Any(c => c.Number == card.Number)) return;
        _pendingCard = card;
        ScTitle.Text = "Сохранить карту в «Дымке»?";
        ScCard.Text = card.Summary + (card.Holder.Length > 0 ? " · " + card.Holder : "");
        ScName.Text = card.Title;
        ScBank.Items.Clear();
        ScBank.Items.Add("Банк не указан");
        foreach (var b in CardTools.Banks) ScBank.Items.Add(b.Name);
        ScBank.SelectedIndex = 0;
        SaveCardPopup.IsOpen = true;
        App.Log($"Предложено сохранить карту •••• {card.Last4} ({target.Host})");
    }

    public bool SaveCardPromptOpen => SaveCardPopup.IsOpen;

    void ScSave_Click(object sender, RoutedEventArgs e) => ConfirmSaveCard();

    public void ConfirmSaveCard()
    {
        SaveCardPopup.IsOpen = false;
        var c = _pendingCard;
        _pendingCard = null;
        if (c == null || !EnsureVaultOpen()) return;
        var bank = ScBank.SelectedItem as string;
        c.Bank = bank == null || bank == "Банк не указан" ? "" : bank;
        c.Title = string.IsNullOrWhiteSpace(ScName.Text) ? (c.Bank.Length > 0 ? c.Bank : c.Title) : ScName.Text.Trim();
        TheVault.Data!.BankCards.Add(c);
        TheVault.Save();
        ShowToast("Карта сохранена (без кода CVC)");
    }

    void ScLater_Click(object sender, RoutedEventArgs e)
    {
        SaveCardPopup.IsOpen = false;
        _pendingCard = null;
    }

    void ScNever_Click(object sender, RoutedEventArgs e)
    {
        SaveCardPopup.IsOpen = false;
        _pendingCard = null;
        _settings.OfferSaveCards = false;
        _settings.Save();
        ShowToast("Больше не буду предлагать. Включить снова: «Пароли и автозаполнение» - «Защита»", seconds: 5);
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
