using Dymka.Core;

int failed = 0, passed = 0;
void Eq<T>(T actual, T expected, string what)
{
    if (EqualityComparer<T>.Default.Equals(actual, expected)) { passed++; return; }
    failed++;
    Console.WriteLine($"ОШИБКА: {what}\n   ожидалось: {expected}\n   получено:  {actual}");
}

const string ddg = "https://duckduckgo.com/?q={0}";

// --- адресная строка
Eq(UrlTools.ToNavigable("kad.arbitr.ru", ddg), "https://kad.arbitr.ru", "домен без схемы");
Eq(UrlTools.ToNavigable("  https://example.com/a?b=1 ", ddg), "https://example.com/a?b=1", "полный адрес");
Eq(UrlTools.ToNavigable("госуслуги.рф", ddg), "https://госуслуги.рф", "кириллический домен");
Eq(UrlTools.ToNavigable("localhost:8080/x", ddg), "http://localhost:8080/x", "localhost");
Eq(UrlTools.ToNavigable("192.168.1.1", ddg), "https://192.168.1.1", "IP-адрес");
Eq(UrlTools.ToNavigable("ст. 213.25 закона о банкротстве", ddg),
   "https://duckduckgo.com/?q=" + Uri.EscapeDataString("ст. 213.25 закона о банкротстве"), "поиск с пробелами");
Eq(UrlTools.ToNavigable("банкротство", ddg), "https://duckduckgo.com/?q=" + Uri.EscapeDataString("банкротство"), "одно слово - поиск");
Eq(UrlTools.ToNavigable("about:blank", ddg), "about:blank", "about:");
Eq(UrlTools.ToNavigable("", ddg), null, "пусто");
Eq(UrlTools.ToNavigable("213.25", ddg), "https://duckduckgo.com/?q=213.25", "число с точкой - поиск");

// --- очистка ссылок
Eq(UrlTools.CleanUrl("https://site.ru/page?utm_source=x&id=5&yclid=123#top"), "https://site.ru/page?id=5#top", "utm и yclid");
Eq(UrlTools.CleanUrl("https://site.ru/page?id=5"), null, "нечего чистить");
Eq(UrlTools.CleanUrl("https://site.ru/?fbclid=abc"), "https://site.ru/", "только метка");
Eq(UrlTools.CleanUrl("https://site.ru/?UTM_Medium=a&q=b"), "https://site.ru/?q=b", "регистр");
Eq(UrlTools.CleanUrl("ftp://site.ru/?utm_x=1"), null, "не http");

// --- домены
Eq(UrlTools.RegistrableDomain("www.kad.arbitr.ru"), "arbitr.ru", "arbitr.ru");
Eq(UrlTools.RegistrableDomain("news.bbc.co.uk"), "bbc.co.uk", "co.uk");
Eq(UrlTools.RegistrableDomain("minjust.gov.ru"), "minjust.gov.ru", "gov.ru");
Eq(UrlTools.RegistrableDomain("10.0.0.1"), "10.0.0.1", "IP");
Eq(UrlTools.IsThirdParty("mc.yandex.ru", "kad.arbitr.ru"), true, "сторонний");
Eq(UrlTools.IsThirdParty("static.arbitr.ru", "kad.arbitr.ru"), false, "свой поддомен");
Eq(UrlTools.HostMatches("esia.gosuslugi.ru", new[] { "gosuslugi.ru" }), true, "белый список поддомен");
Eq(UrlTools.HostMatches("notgosuslugi.ru", new[] { "gosuslugi.ru" }), false, "белый список чужой");
Eq(UrlTools.NormalizeDomain("https://www.Site.ru/path?x"), "site.ru", "нормализация");
Eq(UrlTools.SplitForDisplay("https://kad.arbitr.ru/Card/abc"), ("kad.arbitr.ru", "/Card/abc"), "показ адреса");
Eq(UrlTools.SplitForDisplay(UrlTools.StartUrl), ("", ""), "стартовая скрыта");
Eq(UrlTools.InitialFor("Картотека арбитражных дел", "https://kad.arbitr.ru"), "К", "буква");
Eq(UrlTools.InitialFor("", "https://www.dropbox.com"), "D", "буква из домена");

// --- блок-лист
var bl = new Blocklist();
bl.AddLine("mc.yandex.ru");
bl.AddLine("0.0.0.0 ads.example.com");
bl.AddLine("||tracker.net^");
bl.AddLine("||thirdparty.org^$third-party");
bl.AddLine("||onlyscripts.com^$script");
bl.AddLine("||path.com/ads/");
bl.AddLine("@@||good.tracker.net^");
bl.AddLine("! комментарий");
Eq(bl.IsBlocked("mc.yandex.ru"), true, "точное совпадение");
Eq(bl.IsBlocked("x.mc.yandex.ru"), true, "поддомен");
Eq(bl.IsBlocked("yandex.ru"), false, "родительский домен не блокируется");
Eq(bl.IsBlocked("ads.example.com"), true, "формат hosts");
Eq(bl.IsBlocked("cdn.tracker.net"), true, "формат ABP");
Eq(bl.IsBlocked("good.tracker.net"), false, "исключение @@");
Eq(bl.IsBlocked("thirdparty.org"), true, "$third-party");
Eq(bl.IsBlocked("onlyscripts.com"), false, "правило с типом пропущено");
Eq(bl.IsBlocked("path.com"), false, "правило с путём пропущено");
Eq(bl.Count, 4, "число доменов");

var full = Blocklist.Build(null);
Eq(full.Count > 100, true, "встроенный список загружен");
Eq(full.IsBlocked("www.google-analytics.com"), true, "google-analytics");
Eq(full.IsBlocked("mc.yandex.ru"), true, "Метрика");
Eq(full.IsBlocked("kad.arbitr.ru"), false, "картотека не блокируется");
Eq(full.IsBlocked("www.gosuslugi.ru"), false, "госуслуги не блокируются");

// --- настройки
var tmp = Path.Combine(Path.GetTempPath(), "dymka-test-" + Guid.NewGuid() + ".json");
var s = AppSettings.Load(tmp);
Eq(s.Layout, "top", "по умолчанию вкладки сверху");
s.Layout = "side";
s.SetListMembership(s.TrackerExceptions, "www.kad.arbitr.ru", true);
s.Save();
var s2 = AppSettings.Load(tmp);
Eq(s2.Layout, "side", "сохранение вида");
Eq(s2.IsTrackerException("kad.arbitr.ru"), true, "исключение сохранено");
s2.SetListMembership(s2.TrackerExceptions, "kad.arbitr.ru", false);
Eq(s2.IsTrackerException("kad.arbitr.ru"), false, "исключение снято");
Eq(s2.KeepsCookies("esia.gosuslugi.ru"), true, "белый список cookie");
File.Delete(tmp);
File.WriteAllText(tmp, "{ это не json");
Eq(AppSettings.Load(tmp).Layout, "top", "битый файл настроек");
File.Delete(tmp);

// --- печать: диапазоны страниц
Eq(string.Join(",", PageRanges.Parse("1-3, 5", 10)!), "1,2,3,5", "диапазон 1-3, 5");
Eq(string.Join(",", PageRanges.Parse("8-", 10)!), "8,9,10", "диапазон до конца");
Eq(string.Join(",", PageRanges.Parse("2–4", 10)!), "2,3,4", "длинное тире в диапазоне");
Eq(PageRanges.Parse("5-2", 10), null, "обратный диапазон - ошибка");
Eq(PageRanges.Parse("abc", 10), null, "буквы - ошибка");
Eq(PageRanges.Parse("12", 10), null, "страницы нет в документе");
Eq(string.Join(",", PageRanges.AllButLast(5)), "1,2,3,4", "все, кроме последней");
Eq(string.Join(",", PageRanges.AllButLast(1)), "1", "одна страница печатается");
Eq(PageRanges.ToRangeString(new[] { 1, 2, 3, 5, 7, 8 }), "1-3,5,7-8", "сжатая запись диапазона");
Eq(PageRanges.Grid(2, false), (2, 1, true), "2 на листе: альбомный лист, рядом");
Eq(PageRanges.Grid(2, true), (1, 2, false), "2 на листе для альбомных страниц: книжный лист, друг под другом");
Eq(PageRanges.Grid(4, false), (2, 2, false), "4 на листе");
Eq(new AppSettings().UseSmartScreen("bank"), true, "SmartScreen в «Банке» по умолчанию");
Eq(new AppSettings().UseSmartScreen("work"), false, "SmartScreen в «Работе» выключен по умолчанию");

// --- поиск расширений в других браузерах
var la = Path.Combine(Path.GetTempPath(), "dymka-la-" + Guid.NewGuid());
var ra = Path.Combine(Path.GetTempPath(), "dymka-ra-" + Guid.NewGuid());
var extDir = Path.Combine(la, "Yandex", "YandexBrowser", "User Data", "Default", "Extensions", "iifchhfnnmpdbibifmljnfjhpififfog");
foreach (var ver in new[] { "1.2.8_0", "1.2.13_0" })
{
    var vd = Path.Combine(extDir, ver);
    Directory.CreateDirectory(Path.Combine(vd, "_locales", "ru"));
    Directory.CreateDirectory(Path.Combine(vd, "_metadata"));
    File.WriteAllText(Path.Combine(vd, "manifest.json"),
        "{ \"manifest_version\": 3, \"name\": \"__MSG_extName__\", \"default_locale\": \"en\", \"version\": \"" + ver.Replace("_0", "") +
        "\", \"permissions\": [\"nativeMessaging\"], \"description\": \"CAdES\" }");
    File.WriteAllText(Path.Combine(vd, "_locales", "ru", "messages.json"), "{ \"extName\": { \"message\": \"КриптоПро ЭЦП\" } }");
    File.WriteAllText(Path.Combine(vd, "_metadata", "verified_contents.json"), "{}");
}
var themeDir = Path.Combine(la, "Google", "Chrome", "User Data", "Profile 1", "Extensions", "themeid", "1.0");
Directory.CreateDirectory(themeDir);
File.WriteAllText(Path.Combine(themeDir, "manifest.json"), "{ \"name\": \"Тема\", \"version\": \"1\", \"theme\": {} }");
var found = ExtensionScanner.ScanInstalledBrowsers(la, ra);
Eq(found.Count, 1, "найдено одно расширение (тема пропущена)");
Eq(found.FirstOrDefault()?.Name, "КриптоПро ЭЦП", "название из _locales");
Eq(found.FirstOrDefault()?.Version, "1.2.13", "берётся последняя версия");
Eq(found.FirstOrDefault()?.ForSignature, true, "распознано как плагин подписи");
Eq(found.FirstOrDefault()?.UsesNativeMessaging, true, "использует связь с программой на компьютере");
Eq(found.FirstOrDefault()?.Source, "Яндекс Браузер", "источник - Яндекс Браузер");
var copied = ExtensionScanner.CopyTo(found[0], Path.Combine(la, "dymka-ext"));
Eq(File.Exists(Path.Combine(copied, "manifest.json")), true, "расширение скопировано");
Eq(Directory.Exists(Path.Combine(copied, "_metadata")), false, "служебная папка _metadata не копируется");
Eq(ExtensionScanner.CompareVersions("1.10.0", "1.9.9") > 0, true, "сравнение версий");
Directory.Delete(la, true);

var tmp2 = Path.Combine(Path.GetTempPath(), "dymka-test-" + Guid.NewGuid() + ".json");
File.WriteAllText(tmp2, "{ \"Shortcuts\": [ { \"Title\": \"Картотека\", \"Url\": \"https://kad.arbitr.ru/\" }, { \"Title\": \"Почта\", \"Url\": \"https://mail.yandex.ru/\" } ] }");
var st = AppSettings.Load(tmp2);
Eq(st.ShortcutsFor("work").Count, 2, "старые плитки перенесены в «Работу»");
Eq(st.ShortcutsFor("personal").Count, 0, "«Личное» начинается пустым");
Eq(st.IsShortcut("https://kad.arbitr.ru", "work"), true, "картотека уже на стартовой «Работы»");
Eq(st.IsShortcut("https://kad.arbitr.ru", "bank"), false, "в «Банке» картотеки нет");
Eq(st.Shortcuts, null, "старый общий список больше не хранится");
File.Delete(tmp2);
Eq(AppSettings.Load(Path.Combine(Path.GetTempPath(), "nonexistent-" + Guid.NewGuid() + ".json")).ShortcutsFor("work").Count, 4, "первый запуск: 4 плитки в «Работе»");

Console.WriteLine($"Тестов пройдено: {passed}, ошибок: {failed}");
return failed == 0 ? 0 : 1;
