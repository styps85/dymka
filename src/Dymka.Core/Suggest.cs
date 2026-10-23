using System.Text.Json;

namespace Dymka.Core;

/// <summary>Подсказка при вводе в адресную строку: перейти на сайт (nav) или искать (search).</summary>
public sealed record Suggestion(string Kind, string Text, string Url, string Hint);

/// <summary>Подсказки при вводе. Переход на сайт предлагается только из надёжных источников на вашем компьютере
/// (плитки, сохранённые пароли, открытые вкладки, встроенный список официальных сайтов) - никогда из ответа поисковика.</summary>
public static class Suggest
{
    public static readonly (string Title, string Url, string Keywords)[] KnownSites =
    {
        ("Картотека арбитражных дел", "https://kad.arbitr.ru/", "кад kad арбитраж суд дела картотека"),
        ("Мой арбитр - подача документов", "https://my.arbitr.ru/", "мой арбитр подача документов my arbitr"),
        ("Федресурс (ЕФРСБ)", "https://fedresurs.ru/", "ефрсб федресурс банкротство сообщения fedresurs"),
        ("ЕФРСБ - банкротство", "https://bankrot.fedresurs.ru/", "ефрсб банкрот bankrot fedresurs"),
        ("Госуслуги", "https://www.gosuslugi.ru/", "госуслуги gosuslugi"),
        ("ФНС России", "https://www.nalog.gov.ru/", "фнс налоговая налог nalog"),
        ("Выписка из ЕГРЮЛ и ЕГРИП", "https://egrul.nalog.ru/", "егрюл егрип выписка egrul"),
        ("Банк данных исполнительных производств", "https://fssp.gov.ru/iss/ip", "фссп приставы исполнительные производства fssp"),
        ("Росреестр", "https://rosreestr.gov.ru/", "росреестр егрн rosreestr"),
        ("ГАС «Правосудие»", "https://sudrf.ru/", "гас правосудие суды общей юрисдикции sudrf"),
        ("Верховный суд РФ", "https://vsrf.ru/", "верховный суд вс рф vsrf"),
        ("Реестр уведомлений о залоге", "https://www.reestr-zalogov.ru/", "реестр залогов нотариат залог"),
        ("КонсультантПлюс", "https://www.consultant.ru/", "консультант консультантплюс consultant"),
        ("Гарант", "https://www.garant.ru/", "гарант garant"),
        ("Пенсионный и социальный фонд (СФР)", "https://sfr.gov.ru/", "сфр пенсионный фонд социальный фонд пфр"),
        ("Почта России - отслеживание", "https://www.pochta.ru/tracking", "почта россии отслеживание трек pochta"),
        ("Яндекс Почта", "https://mail.yandex.ru/", "яндекс почта mail yandex"),
        ("Сбербанк Онлайн", "https://online.sberbank.ru/", "сбер сбербанк онлайн sberbank"),
        ("Альфа-Банк", "https://alfabank.ru/", "альфа альфабанк alfabank"),
        ("ВТБ Онлайн", "https://online.vtb.ru/", "втб vtb онлайн"),
        ("Т-Банк", "https://www.tbank.ru/", "т-банк тинькофф tbank"),
        ("Совкомбанк", "https://sovcombank.ru/", "совкомбанк sovcombank"),
    };

    static string Norm(string s) => s.ToLowerInvariant().Replace('ё', 'е');

    static bool Matches(string input, params string[] hay)
    {
        var words = Norm(input).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return false;
        var tokens = hay.SelectMany(h => Norm(h).Split(new[] { ' ', '.', '/', '-', ':', '«', '»', '(', ')', ',' }, StringSplitOptions.RemoveEmptyEntries)).ToList();
        // каждое слово ввода должно быть началом какого-то слова (последнее слово - ещё набирается)
        return words.All(w => tokens.Any(t => t.StartsWith(w)));
    }

    /// <summary>Переходы на сайты: сначала ваши (плитки, пароли, вкладки), потом встроенный список.</summary>
    public static List<Suggestion> Local(string input, IEnumerable<(string title, string url)> userSites, int max = 3)
    {
        input = input.Trim();
        var result = new List<Suggestion>();
        if (input.Length < 2) return result;
        var seen = new HashSet<string>();
        void Add(string title, string url)
        {
            var host = UrlTools.HostOf(url);
            if (host.Length == 0 || !seen.Add(host + UrlTools.SplitForDisplay(url).Item2)) return;
            result.Add(new Suggestion("nav", title, url, host.StartsWith("www.") ? host[4..] : host));
        }
        foreach (var (title, url) in userSites)
            if (result.Count < max && Matches(input, title, UrlTools.HostOf(url))) Add(title, url);
        foreach (var (title, url, kw) in KnownSites)
            if (result.Count < max && Matches(input, title, UrlTools.HostOf(url), kw)) Add(title, url);
        return result;
    }

    /// <summary>Адрес подсказок поисковой системы. Startpage своих подсказок не даёт - используется DuckDuckGo.</summary>
    public static string RemoteUrl(string engineId, string q) => engineId switch
    {
        "yandex" => "https://suggest.yandex.ru/suggest-ff.cgi?uil=ru&part=" + Uri.EscapeDataString(q),
        "google" => "https://suggestqueries.google.com/complete/search?client=firefox&hl=ru&q=" + Uri.EscapeDataString(q),
        _ => "https://duckduckgo.com/ac/?type=list&kl=ru-ru&q=" + Uri.EscapeDataString(q),
    };

    /// <summary>Ответ в формате OpenSearch: ["запрос", ["подсказка 1", "подсказка 2", …]].</summary>
    public static List<string> ParseRemote(string json, int max = 6)
    {
        var list = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind == JsonValueKind.Array && r.GetArrayLength() > 1 && r[1].ValueKind == JsonValueKind.Array)
                foreach (var x in r[1].EnumerateArray())
                {
                    var s = x.ValueKind == JsonValueKind.String ? x.GetString() : x.ValueKind == JsonValueKind.Array && x.GetArrayLength() > 0 ? x[0].GetString() : null;
                    if (!string.IsNullOrWhiteSpace(s) && !list.Contains(s.Trim())) list.Add(s.Trim());
                    if (list.Count >= max) break;
                }
        }
        catch (JsonException) { }
        return list;
    }

    /// <summary>Итоговый список: переходы на сайты, затем поисковые подсказки (без повторов и без самого ввода).</summary>
    public static List<Suggestion> Merge(string input, List<Suggestion> nav, List<string> remote, string engineName, int max = 8)
    {
        var res = new List<Suggestion>(nav);
        var typed = Norm(input.Trim());
        foreach (var s in remote)
        {
            if (res.Count >= max) break;
            if (Norm(s) == typed) continue;
            res.Add(new Suggestion("search", s, "", engineName));
        }
        return res;
    }
}
