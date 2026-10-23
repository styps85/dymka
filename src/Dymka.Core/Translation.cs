using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dymka.Core;

/// <summary>Бесплатный Яндекс Переводчик (тот же сервис, что у виджета перевода сайтов и прокси-перевода страниц).
/// Здесь только логика без сети: пакеты запросов, разбор ответа, определение языка страницы.</summary>
public static class Translation
{
    /// <summary>Разделитель кусков текста внутри одного сегмента (кусочки абзаца между ссылками, жирным и т.п.).</summary>
    public const string Separator = "<wbr>";

    /// <summary>Сколько символов исходного текста отправлять одним запросом.</summary>
    public const int MaxChars = 800;

    public const string SidPageUrl = "https://translated.turbopages.org/proxy_u/en-ru.en/https/example.com/";
    public const string SidWidgetUrl = "https://translate.yandex.net/website-widget/v1/widget.js?widgetId=ytWidget&pageLang=en&widgetTheme=light&autoMode=false";
    public const string ApiUrl = "https://translate.yandex.net/api/v1/tr.json/translate";

    static readonly Regex SidRe = new(@"sid\s*:\s*'([0-9a-f\.]+)'?", RegexOptions.Compiled);
    static readonly Regex SepRe = new(@"<wbr\s*/?>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Извлекает идентификатор сессии из страницы прокси-перевода или скрипта виджета.</summary>
    public static string? ExtractSid(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var m = SidRe.Match(text);
        return m.Success && m.Groups[1].Value.Length > 7 ? m.Groups[1].Value : null;
    }

    /// <summary>Длинный sid выдаёт мобильная версия сервиса - для него другой параметр srv.</summary>
    public static string ServiceFor(string sid) => sid.Length > 40 ? "tr-touch-url" : "tr-url-widget";

    public static string BuildUrl(string sid, string targetLang, string? sourceLang = null) =>
        $"{ApiUrl}?srv={ServiceFor(sid)}&id={sid}-0-0&format=html&lang=" +
        (string.IsNullOrEmpty(sourceLang) || sourceLang == "auto" ? "" : sourceLang + "-") + targetLang;

    public static string BuildBody(IEnumerable<string> segments)
    {
        var sb = new StringBuilder();
        foreach (var s in segments)
        {
            if (sb.Length > 0) sb.Append('&');
            sb.Append("text=").Append(Uri.EscapeDataString(s));
        }
        return sb.ToString();
    }

    /// <summary>Делит сегменты на запросы не длиннее MaxChars (сегмент не режется: длинный уходит отдельным запросом).</summary>
    public static List<List<int>> Batch(IReadOnlyList<string> segments, int maxChars = MaxChars)
    {
        var result = new List<List<int>>();
        var cur = new List<int>();
        int len = 0;
        for (int i = 0; i < segments.Count; i++)
        {
            int l = segments[i].Length;
            if (cur.Count > 0 && len + l > maxChars)
            {
                result.Add(cur);
                cur = new List<int>();
                len = 0;
            }
            cur.Add(i);
            len += l;
        }
        if (cur.Count > 0) result.Add(cur);
        return result;
    }

    /// <summary>Ответ сервиса: {"code":200,"lang":"en-ru","text":["…","…"]}.</summary>
    public static (int code, string lang, List<string> texts) ParseResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        int code = r.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
        string lang = r.TryGetProperty("lang", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() ?? "" : "";
        var texts = new List<string>();
        if (r.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.Array)
            foreach (var x in t.EnumerateArray()) texts.Add(x.GetString() ?? "");
        return (code, lang, texts);
    }

    /// <summary>Переведённый сегмент - снова отдельные куски обычного текста.</summary>
    public static string[] SplitPieces(string translatedHtml) =>
        SepRe.Split(translatedHtml).Select(p => WebUtility.HtmlDecode(p)).ToArray();

    public static string EscapePiece(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>Язык, с которого перевели, из поля lang ответа ("en-ru" -> "en").</summary>
    public static string SourceOf(string lang) => lang.Split('-')[0];

    static readonly string[] CyrillicForeign = { "uk", "be", "kk", "bg", "sr", "mk", "ky", "tg", "mn" };

    /// <summary>Стоит ли предлагать перевод: язык страницы по атрибуту lang и по доле русских букв в тексте.</summary>
    public static bool IsForeign(string? htmlLang, int cyrillicLetters, int allLetters)
    {
        var lang = (htmlLang ?? "").Trim().ToLowerInvariant();
        var primary = lang.Split('-', '_')[0];
        if (allLetters < 40)
            return primary.Length > 0 && primary != "ru";   // текста мало - верим атрибуту
        double share = (double)cyrillicLetters / allLetters;
        if (share >= 0.3)
            return CyrillicForeign.Contains(primary);      // кириллица, но, например, украинский
        return true;
    }

    /// <summary>Перевод выделенного: русский текст - на английский, остальное - на русский.</summary>
    public static string TargetForSelection(string text)
    {
        int cyr = text.Count(ch => ch >= 'Ѐ' && ch <= 'ӿ');
        int letters = text.Count(char.IsLetter);
        return letters > 0 && (double)cyr / letters >= 0.5 ? "en" : "ru";
    }

    public static string LanguageName(string code) => code switch
    {
        "en" => "английского",
        "de" => "немецкого",
        "fr" => "французского",
        "es" => "испанского",
        "it" => "итальянского",
        "pt" => "португальского",
        "zh" => "китайского",
        "ja" => "японского",
        "ko" => "корейского",
        "tr" => "турецкого",
        "pl" => "польского",
        "uk" => "украинского",
        "kk" => "казахского",
        "be" => "белорусского",
        "ar" => "арабского",
        _ => code,
    };
}
