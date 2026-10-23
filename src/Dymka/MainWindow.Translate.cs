using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Dymka.Core;
using Microsoft.Web.WebView2.Core;

namespace Dymka;

/// <summary>Перевод страниц и выделенного текста (бесплатный Яндекс Переводчик).</summary>
public partial class MainWindow
{
    static readonly YandexTranslator Translator = new();
    public static YandexTranslator TranslatorForTest => Translator;

    static string? _translateScript;
    static string TranslateScript
    {
        get
        {
            if (_translateScript != null) return _translateScript;
            using var s = typeof(MainWindow).Assembly.GetManifestResourceStream("Dymka.Web.translate.js");
            using var r = new StreamReader(s!);
            return _translateScript = r.ReadToEnd();
        }
    }

    /// <summary>Язык страницы: атрибут lang и сколько в начале текста русских букв из всех букв.</summary>
    const string ProbeScript =
        "(function(){try{var t=(document.body&&document.body.innerText||'').slice(0,4000);" +
        "var all=(t.match(/\\p{L}/gu)||[]).length,cyr=(t.match(/[\\u0400-\\u04FF]/g)||[]).length;" +
        "return JSON.stringify({lang:document.documentElement.lang||'',all:all,cyr:cyr});}catch(e){return '{}';}})()";

    async Task ProbeLanguageAsync(BrowserTab tab, CoreWebView2 core, string url)
    {
        try
        {
            await Task.Delay(1200);   // у многих сайтов текст появляется чуть позже окончания загрузки
            if (tab.Url != url || tab.Core != core || tab.TranslateState != TranslateState.None) return;
            var raw = await core.ExecuteScriptAsync(ProbeScript);
            var json = JsonSerializer.Deserialize<string>(raw);
            if (string.IsNullOrEmpty(json)) return;
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            string lang = r.TryGetProperty("lang", out var l) ? l.GetString() ?? "" : "";
            int all = r.TryGetProperty("all", out var a) ? a.GetInt32() : 0;
            int cyr = r.TryGetProperty("cyr", out var c) ? c.GetInt32() : 0;
            tab.IsForeign = Translation.IsForeign(lang, cyr, all);
            if (tab == _active) UpdateTranslateUi();
            if (tab.IsForeign && tab.TranslateSticky.Length > 0 &&
                tab.TranslateSticky == UrlTools.RegistrableDomain(UrlTools.HostOf(tab.Url)))
                await TranslatePageAsync(tab);
        }
        catch (Exception ex)
        {
            App.Log("Язык страницы: " + ex.Message);
        }
    }

    /// <summary>Переводит страницу вкладки на русский. Текст страницы уходит в Яндекс Переводчик.</summary>
    public async Task TranslatePageAsync(BrowserTab tab)
    {
        var core = tab.Core;
        if (core == null || tab.IsStart || tab.TranslateState != TranslateState.None) return;
        if (!tab.Url.StartsWith("http://") && !tab.Url.StartsWith("https://")) return;
        tab.TranslateState = TranslateState.Working;
        tab.TranslateError = "";
        tab.TranslatedBatches = 0;
        tab.SourceLang = "";
        tab.TranslateSticky = UrlTools.RegistrableDomain(UrlTools.HostOf(tab.Url));
        UpdateTranslateUi();
        try
        {
            await core.ExecuteScriptAsync(TranslateScript);
            var raw = await core.ExecuteScriptAsync("window.__dymkaTr ? window.__dymkaTr.start() : '{}'");
            var json = JsonSerializer.Deserialize<string>(raw) ?? "{}";
            using var doc = JsonDocument.Parse(json);
            int groups = doc.RootElement.TryGetProperty("groups", out var g) ? g.GetInt32() : 0;
            App.Log($"Перевод страницы {UrlTools.HostOf(tab.Url)}: абзацев {groups}");
            if (groups == 0 && tab.TranslatePending == 0)
            {
                tab.TranslateState = TranslateState.None;
                if (tab == _active) ShowToast("На странице нечего переводить");
            }
        }
        catch (Exception ex)
        {
            App.Log("Перевод страницы: " + ex.Message);
            tab.TranslateState = TranslateState.None;
            if (tab == _active) ShowToast("Не удалось перевести страницу");
        }
        UpdateTranslateUi();
    }

    public void RestoreOriginal(BrowserTab tab)
    {
        tab.TranslateSticky = "";
        if (tab.TranslateState == TranslateState.None) return;
        tab.TranslateState = TranslateState.None;
        try { _ = tab.Core?.ExecuteScriptAsync("window.__dymkaTr && window.__dymkaTr.restore()"); } catch { }
        UpdateTranslateUi();
    }

    /// <summary>Страница прислала очередную порцию абзацев: переводим и возвращаем.</summary>
    async Task TranslateBatchAsync(BrowserTab tab, CoreWebView2 core, int id, List<string> items)
    {
        tab.TranslatePending++;
        try
        {
            var (pieces, src) = await Translator.TranslateSegmentsAsync(items, "ru");
            if (src.Length > 0) tab.SourceLang = src;
            if (tab.Core != core || tab.TranslateState == TranslateState.None) return;
            await core.ExecuteScriptAsync($"window.__dymkaTr && window.__dymkaTr.apply({id},{JsonSerializer.Serialize(pieces)})");
            tab.TranslatedBatches++;
        }
        catch (Exception ex)
        {
            tab.TranslateError = ex.Message;
            App.Log("Перевод: " + ex.Message);
        }
        finally
        {
            tab.TranslatePending--;
            if (tab.TranslatePending == 0 && tab.TranslateState == TranslateState.Working) FinishTranslation(tab);
        }
    }

    void FinishTranslation(BrowserTab tab)
    {
        if (tab.TranslatedBatches == 0)
        {
            RestoreOriginal(tab);
            if (tab == _active)
                ShowToast("Не удалось перевести: " + (tab.TranslateError.Length > 0 ? tab.TranslateError : "сервис Яндекса не ответил"), seconds: 5);
            return;
        }
        tab.TranslateState = TranslateState.Done;
        UpdateTranslateUi();
        if (tab != _active) return;
        if (tab.SourceLang == "ru") ShowToast("Страница уже на русском");
        else if (tab.SourceLang.Length > 0) ShowToast("Переведено с " + Translation.LanguageName(tab.SourceLang) + " (Яндекс Переводчик)");
    }

    void UpdateTranslateUi()
    {
        var t = _active;
        bool web = t != null && !t.IsStart && (t.Url.StartsWith("http://") || t.Url.StartsWith("https://"));
        var state = t?.TranslateState ?? TranslateState.None;
        bool show = web && (state != TranslateState.None || (t!.IsForeign && _settings.OfferTranslation));
        TranslateBtn.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        TranslateText.Text = state switch
        {
            TranslateState.Working => "Перевожу…",
            TranslateState.Done => "Оригинал",
            _ => "Перевести",
        };
        TranslateBtn.ToolTip = state == TranslateState.Done
            ? "Показать страницу на языке оригинала"
            : "Перевести страницу на русский. Текст страницы отправляется в Яндекс Переводчик";
        TranslateMenuText.Text = state == TranslateState.None ? "Перевести страницу на русский" : "Показать оригинал";
        TranslateMenuItem.IsEnabled = web;
    }

    void Translate_Click(object sender, RoutedEventArgs e)
    {
        ClosePopups();
        var t = _active;
        if (t == null) return;
        if (t.TranslateState == TranslateState.None) _ = TranslatePageAsync(t);
        else RestoreOriginal(t);
    }

    public bool TranslateButtonVisible => TranslateBtn.Visibility == Visibility.Visible;

    // ===================================================================== перевод выделенного

    public async Task ShowSelectionTranslationAsync(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        if (text.Length > 5000) text = text[..5000];
        SelTrSource.Text = text.Length > 300 ? text[..299] + "…" : text;
        SelTrResult.Text = "Перевожу…";
        SelTrLang.Text = "Яндекс Переводчик";
        SelTrPopup.IsOpen = true;
        try
        {
            var target = Translation.TargetForSelection(text);
            var (res, src) = await Translator.TranslateTextAsync(text, target);
            SelTrResult.Text = res;
            string to = target == "ru" ? "на русский" : "на английский";
            SelTrLang.Text = (src.Length > 0 && src != target ? "С " + Translation.LanguageName(src) + " " : "Перевод ") + to + " · Яндекс Переводчик";
        }
        catch (Exception ex)
        {
            SelTrResult.Text = "Не удалось перевести: " + ex.Message;
        }
    }

    public string SelectionTranslationForTest => SelTrPopup.IsOpen ? SelTrResult.Text : "";

    void SelTrCopy_Click(object sender, RoutedEventArgs e)
    {
        if (TrySetClipboard(SelTrResult.Text)) ShowToast("Перевод скопирован");
        SelTrPopup.IsOpen = false;
    }

    void SelTrClose_Click(object sender, RoutedEventArgs e) => SelTrPopup.IsOpen = false;

    /// <summary>Пункты перевода в контекстном меню страницы.</summary>
    void AddTranslateMenuItems(BrowserTab source, CoreWebView2ContextMenuRequestedEventArgs e)
    {
        var items = e.MenuItems;
        var target = e.ContextMenuTarget;
        CoreWebView2ContextMenuItem Item(string label, Action act)
        {
            var it = _env!.CreateContextMenuItem(label, null, CoreWebView2ContextMenuItemKind.Command);
            it.CustomItemSelected += (_, _) => Dispatcher.BeginInvoke(act);
            return it;
        }

        if (target.HasSelection && !string.IsNullOrWhiteSpace(target.SelectionText))
        {
            var sel = target.SelectionText.Trim();
            var shortSel = sel.Replace('\n', ' ');
            if (shortSel.Length > 24) shortSel = shortSel[..23] + "…";
            int at = 0;
            for (int i = 0; i < items.Count; i++)
                if (items[i].Name == "copy") { at = i + 1; break; }
            items.Insert(at, Item($"Перевести «{shortSel}»", () => _ = ShowSelectionTranslationAsync(sel)));
        }

        if (target.IsEditable && Shared.Vault.Exists && source.Url.StartsWith("http"))
        {
            var core = source.Core;
            if (core != null)
                items.Insert(0, Item("Заполнить карточкой…", () => _ = FillFromContextMenuAsync(source, core)));
        }

        var url = source.Url;
        if (target.Kind == CoreWebView2ContextMenuTargetKind.Page && !target.HasLinkUri && !target.HasSelection &&
            (url.StartsWith("http://") || url.StartsWith("https://")) && !source.IsStart)
        {
            int at = items.Count;
            for (int i = 0; i < items.Count; i++)
                if (items[i].Name == "print") { at = i + 1; break; }
            items.Insert(at, source.TranslateState == TranslateState.None
                ? Item("Перевести страницу на русский", () => _ = TranslatePageAsync(source))
                : Item("Показать оригинал", () => RestoreOriginal(source)));
        }
    }
}
