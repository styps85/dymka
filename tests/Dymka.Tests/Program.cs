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

// --- очистка вкладок и поиск
var now0 = new DateTime(2026, 10, 23, 5, 0, 0);
TabInfo T(int id, string url, string c = "work", bool start = false, bool pinned = false, bool active = false, bool err = false, double daysAgo = 0, string title = "") =>
    new(id, c, url, title.Length > 0 ? title : url, start, pinned, active, err, now0.AddDays(-daysAgo));
var tabsC = new List<TabInfo>
{
    T(1, "https://kad.arbitr.ru/", daysAgo: 2),
    T(2, "https://kad.arbitr.ru/", daysAgo: 0.1),            // самая свежая - остаётся
    T(3, "https://kad.arbitr.ru/", c: "personal"),           // другой контейнер - не дубликат
    T(4, "https://dymka.start/start.html", start: true),
    T(5, "https://dymka.start/start.html", start: true, active: true),   // активная - не трогаем
    T(6, "https://broken.example/", err: true),
    T(7, "https://old.example/", daysAgo: 30),
    T(8, "https://pinned.example/", pinned: true, daysAgo: 60),          // закреплённая - не трогаем
    T(9, "https://fedresurs.ru/", daysAgo: 12),
    T(10, "https://fedresurs.ru/", daysAgo: 20),             // дубликат и старая - только в «Дубликатах»
};
var found2 = TabCleanup.Find(tabsC, now0, 7);
string Ids(CleanupGroup g) => string.Join(",", found2.Where(x => x.Group == g).Select(x => x.Tab.Id).OrderBy(i => i));
Eq(Ids(CleanupGroup.Duplicates), "1,10", "дубликаты: остаются самые свежие, другой контейнер не считается");
Eq(Ids(CleanupGroup.Empty), "4", "пустые вкладки, кроме активной");
Eq(Ids(CleanupGroup.Errors), "6", "страницы с ошибкой");
Eq(Ids(CleanupGroup.Old), "7,9", "давно не открывались (больше 7 дней), закреплённая не трогается");
Eq(found2.Select(x => x.Tab.Id).Distinct().Count(), found2.Count, "каждая вкладка - не больше чем в одной группе");
Eq(TabCleanup.CheckedByDefault(CleanupGroup.Old), false, "«давно не открывались» по умолчанию не отмечены");
var dupPinned = TabCleanup.Find(new[] { T(1, "https://a.ru/", daysAgo: 0), T(2, "https://a.ru/", pinned: true, daysAgo: 5) }, now0);
Eq(string.Join(",", dupPinned.Select(x => x.Tab.Id)), "1", "дубликат закреплённой вкладки закрывается, закреплённая остаётся");
var sr = TabCleanup.Search(tabsC.Append(T(11, "https://example.com/%D0%B4%D0%B5%D0%BB%D0%BE", title: "Карточка дела")), "дело");
Eq(sr.FirstOrDefault()?.Id, 11, "поиск по адресу с русскими буквами (раскодированный адрес)");
Eq(TabCleanup.Search(tabsC, "FEDRES").Count, 2, "поиск без учёта регистра");
Eq(TabCleanup.Search(tabsC, "kad personal").Count, 0, "все слова должны встретиться (контейнер не ищется)");
Eq(TabCleanup.Search(tabsC, "").Any(x => x.IsStart), false, "пустой запрос: стартовые страницы не показываются");
var tmp3 = Path.Combine(Path.GetTempPath(), "dymka-test-" + Guid.NewGuid() + ".json");
var s3 = AppSettings.Load(tmp3);
Eq(s3.RestoreSession, true, "восстановление сеанса включено по умолчанию");
s3.Session.Add(new SessionWindow { Active = 1, Container = "personal", Tabs = { new SessionTab { Url = "https://a.ru/", Title = "А", Container = "personal" }, new SessionTab { Url = "https://b.ru/", Container = "work" } } });
s3.Save();
var s4 = AppSettings.Load(tmp3);
Eq(s4.Session.Count == 1 && s4.Session[0].Tabs.Count == 2 && s4.Session[0].Active == 1 && s4.Session[0].Tabs[0].Title == "А", true, "сеанс сохраняется и читается");
File.Delete(tmp3);

// --- перевод страниц
Eq(Translation.ExtractSid("var a = {sid: '0a1b2c3d.65f2e1a0.4f1c', x: 1}"), "0a1b2c3d.65f2e1a0.4f1c", "sid из страницы сервиса");
Eq(Translation.ExtractSid("нет ничего"), null, "sid не найден");
Eq(Translation.ServiceFor("0a1b2c3d.65f2e1a0"), "tr-url-widget", "короткий sid - виджет");
Eq(Translation.BuildUrl("abc12345", "ru"), "https://translate.yandex.net/api/v1/tr.json/translate?srv=tr-url-widget&id=abc12345-0-0&format=html&lang=ru", "адрес запроса (язык определяется сам)");
Eq(Translation.BuildBody(new[] { "Hello<wbr>world", "A & B" }), "text=Hello%3Cwbr%3Eworld&text=A%20%26%20B", "тело запроса");
var bt = Translation.Batch(new[] { new string('a', 500), new string('b', 200), new string('c', 200), new string('d', 1000), "e" }, 800);
Eq(string.Join("|", bt.Select(b => string.Join(",", b))), "0,1|2|3|4", "пакеты по 800 символов, длинный сегмент - отдельно");
var pr = Translation.ParseResponse("{\"code\":200,\"lang\":\"en-ru\",\"text\":[\"Привет<wbr>мир\",\"A &amp; B\"]}");
Eq(pr.code == 200 && pr.lang == "en-ru" && pr.texts.Count == 2, true, "разбор ответа сервиса");
Eq(string.Join("|", Translation.SplitPieces(pr.texts[0])), "Привет|мир", "куски абзаца разделяются обратно");
Eq(Translation.SplitPieces("A &amp; B<wbr/>C")[0], "A & B", "HTML-сущности раскодируются");
Eq(Translation.SourceOf("en-ru"), "en", "язык оригинала");
Eq(Translation.IsForeign("en", 0, 500), true, "английская страница - предложить перевод");
Eq(Translation.IsForeign("en", 400, 500), false, "русский текст при ошибочном lang=en - не предлагать");
Eq(Translation.IsForeign("ru", 0, 10), false, "мало текста и lang=ru - не предлагать");
Eq(Translation.IsForeign("", 0, 10), false, "мало текста и нет lang - не предлагать");
Eq(Translation.IsForeign("uk", 450, 500), true, "украинский (кириллица) - предложить");
Eq(Translation.IsForeign("ru-RU", 300, 500), false, "русская страница");
Eq(Translation.TargetForSelection("Доброе утро"), "en", "выделенный русский текст - на английский");
Eq(Translation.TargetForSelection("Good morning"), "ru", "выделенный английский - на русский");

var fake = new FakeYandex();
var ytr = new YandexTranslator(fake);
var (pieces, srcLang) = ytr.TranslateSegmentsAsync(new[] { "Hello<wbr>world", "Bye" }, "ru").GetAwaiter().GetResult();
Eq(srcLang, "en", "переводчик: язык оригинала");
Eq(string.Join("|", pieces[0]) + "/" + string.Join("|", pieces[1]), "[ru]Hello|[ru]world/[ru]Bye", "переводчик: куски на своих местах");
Eq(fake.SidRequests, 1, "sid запрашивается один раз и кешируется");
var (txt, _) = ytr.TranslateTextAsync("Line one\n\nLine two", "ru").GetAwaiter().GetResult();
Eq(txt, "[ru]Line one\n\n[ru]Line two", "перевод выделенного: строки сохраняются");
Eq(ytr.LastMethod, "GET", "запрос перевода - GET");
fake.FailGet = true;
var (p2, _) = ytr.TranslateSegmentsAsync(new[] { "Hi" }, "ru").GetAwaiter().GetResult();
Eq(p2[0][0] == "[ru]Hi" && ytr.LastMethod == "POST", true, "если GET не принят - запасной POST");

// --- хранилище паролей
var vdir = Path.Combine(Path.GetTempPath(), "dymka-vault-" + Guid.NewGuid());
var vpath = Path.Combine(vdir, "vault.dat");
var prot = new FakeProtector();
var vault = new Vault(vpath, prot);
Eq(vault.State, VaultState.Empty, "хранилища ещё нет");
var vdata = vault.Data!;
Eq(vault.Exists && !vault.HasMasterPassword, true, "хранилище создаётся при первом обращении, без главного пароля");
Eq(Vault.Remember(vdata, "https://kad.arbitr.ru/login", "semenov", "Секрет-1"), "added", "пароль сохранён");
Eq(Vault.Remember(vdata, "https://kad.arbitr.ru/", "semenov", "Секрет-1"), "same", "тот же пароль - без изменений");
Eq(Vault.Remember(vdata, "https://kad.arbitr.ru/", "semenov", "Секрет-2"), "updated", "новый пароль обновляет старый");
vdata.Cards.Add(new Card { Title = "Я", LastName = "Иванов", FirstName = "Иван", MiddleName = "Петрович",
    Addresses = { new CardAddress { Label = "Домашний", Text = "г. Екатеринбург, ул. Ленина, 1" }, new CardAddress { Label = "Почтовый", Postcode = "620000", Text = "а/я 24" } } });
vault.Save();
var raw = File.ReadAllText(vpath);
Eq(raw.Contains("Секрет") || raw.Contains("semenov") || raw.Contains("Ленина"), false, "в файле хранилища нет открытого текста");
var v2 = new Vault(vpath, prot);
Eq(v2.State, VaultState.Locked, "после перезапуска хранилище закрыто");
Eq(v2.Data?.Passwords.FirstOrDefault()?.Password, "Секрет-2", "без главного пароля открывается само (защита Windows)");
Eq(v2.Data?.Cards[0].ShortName, "Иванов И.П.", "карточка с инициалами");
v2.SetMasterPassword("пароль-мастер");
var v3 = new Vault(vpath, prot);
Eq(v3.HasMasterPassword && v3.Data == null, true, "с главным паролем само не открывается");
bool wrong = false;
try { v3.Unlock("неверный"); } catch (WrongPasswordException) { wrong = true; }
Eq(wrong && !v3.IsUnlocked, true, "неверный главный пароль не подходит");
v3.Unlock("пароль-мастер");
Eq(v3.Data?.Passwords.Count, 1, "верный главный пароль открывает хранилище");
Eq(v3.CheckMasterPassword("пароль-мастер") && !v3.CheckMasterPassword("x"), true, "проверка главного пароля");
v3.Lock();
Eq(v3.IsUnlocked, false, "хранилище закрывается (автоблокировка)");
bool otherUser = false;
try { new Vault(vpath, new FakeProtector(0x33)).Unlock("пароль-мастер"); } catch (Exception) { otherUser = true; }
Eq(otherUser, true, "на другой учётной записи Windows файл не открыть даже с главным паролем");
v3.Unlock("пароль-мастер");
v3.SetMasterPassword(null);
Eq(new Vault(vpath, prot).Data?.Passwords.Count, 1, "главный пароль снят - снова открывается само");
var fh = Vault.ForHost(new[] { new PasswordEntry { Host = "esia.gosuslugi.ru", Username = "a" }, new PasswordEntry { Host = "www.gosuslugi.ru", Username = "b" }, new PasswordEntry { Host = "nalog.ru", Username = "c" } }, "www.gosuslugi.ru");
Eq(string.Join(",", fh.Select(x => x.Username)), "b,a", "пароли сайта: сначала точный адрес, потом тот же домен, чужие не показываются");
Directory.Delete(vdir, true);

// --- импорт CSV
var chromeCsv = "name,url,username,password,note\r\nkad.arbitr.ru,https://kad.arbitr.ru/,semenov,\"p,a\"\"ss\",\r\nМой банк,https://online.bank.ru/login,ivan@mail.ru,qwerty ,заметка\r\n,android://abc@com.app/,u,p,\r\n";
var (ce, cskip, cerr) = PasswordCsv.Parse(chromeCsv);
Eq(cerr == null && ce.Count == 2 && cskip == 1, true, "CSV Chrome: 2 пароля, приложение Android пропущено");
Eq(ce[0].Password, "p,a\"ss", "пароль с запятой и кавычкой");
Eq(ce[1].Password, "qwerty ", "пробелы в пароле сохраняются");
Eq(ce[1].Host + "|" + ce[1].Note, "online.bank.ru|заметка", "сайт и примечание");
var (ye, _, _) = PasswordCsv.Parse("\uFEFFurl,login,password\nhttps://fedresurs.ru,user1,pw1\n");
Eq(ye.Count == 1 && ye[0].Username == "user1", true, "CSV Яндекс Браузера (столбец login)");
var (fe, _, _) = PasswordCsv.Parse("\"url\",\"username\",\"password\",\"httpRealm\"\n\"https://a.ru\",\"x\",\"y\",\"\"\n");
Eq(fe.Count == 1 && fe[0].Password == "y", true, "CSV Firefox (все поля в кавычках)");
Eq(PasswordCsv.Parse("a,b\n1,2").error != null, true, "не выгрузка паролей - понятная ошибка");
var mdata = new VaultData();
PasswordCsv.Merge(mdata, ce);
var (ma, mu, ms) = PasswordCsv.Merge(mdata, PasswordCsv.Parse(chromeCsv.Replace("qwerty ", "new")).entries);
Eq((ma, mu, ms), (0, 1, 1), "повторный импорт: новых нет, один пароль обновлён");

// --- распознавание полей и автозаполнение
FieldInfo F(int i, string label, string type = "text", string name = "", string ph = "", string ac = "") => new(i, type, name, "", ac, label, ph);
FieldKind K(string label, string type = "text", string name = "", string ph = "", string ac = "") => FormFill.Classify(F(0, label, type, name, ph, ac));
Eq(K("Фамилия"), FieldKind.LastName, "поле «Фамилия»");
Eq(K("Имя"), FieldKind.FirstName, "поле «Имя»");
Eq(K("Отчество (при наличии)"), FieldKind.MiddleName, "поле «Отчество»");
Eq(K("Фамилия, имя, отчество"), FieldKind.FullName, "поле ФИО");
Eq(K("", name: "lastName"), FieldKind.LastName, "name=lastName");
Eq(K("", name: "first_name"), FieldKind.FirstName, "name=first_name");
Eq(K("First name"), FieldKind.FirstName, "First name - не полное имя");
Eq(K("ИНН"), FieldKind.Inn, "ИНН");
Eq(K("СНИЛС"), FieldKind.Snils, "СНИЛС");
Eq(K("Серия паспорта"), FieldKind.PassportSeries, "серия паспорта");
Eq(K("Номер паспорта"), FieldKind.PassportNumber, "номер паспорта");
Eq(K("Серия и номер паспорта"), FieldKind.PassportSeriesNumber, "серия и номер одним полем");
Eq(K("Кем выдан"), FieldKind.PassportIssuedBy, "кем выдан");
Eq(K("Дата выдачи", "date"), FieldKind.PassportIssueDate, "дата выдачи");
Eq(K("Код подразделения"), FieldKind.PassportCode, "код подразделения");
Eq(K("Дата рождения"), FieldKind.BirthDate, "дата рождения");
Eq(K("Место рождения"), FieldKind.None, "место рождения не путается с датой");
Eq(K("Адрес электронной почты"), FieldKind.Email, "адрес электронной почты - это почта, не адрес");
Eq(K("Почтовый адрес"), FieldKind.Address, "почтовый адрес - это адрес");
Eq(K("Почтовый индекс"), FieldKind.Postcode, "индекс");
Eq(K("Контактный телефон"), FieldKind.Phone, "телефон");
Eq(K("", "tel"), FieldKind.Phone, "type=tel");
Eq(K("Регистрационный номер арбитражного управляющего"), FieldKind.RegNumber, "рег. номер АУ");
Eq(K("Наименование СРО"), FieldKind.Sro, "СРО");
Eq(K("Срок оплаты"), FieldKind.None, "«срок» - не СРО");
Eq(K("Сумма"), FieldKind.None, "неизвестное поле не заполняется");
Eq(FormFill.ClassifyAll(new[] { F(0, "Серия"), F(1, "Номер") })[1], FieldKind.PassportNumber, "«Номер» после «Серии» - номер паспорта");
var card = new Card { LastName = "Иванов", FirstName = "Иван", MiddleName = "Петрович", BirthDate = "01.02.1980", Inn = "123456789012", PassportIssueDate = "05.06.2010",
    Addresses = { new CardAddress { Label = "Домашний", Postcode = "620001", Text = "г. Екатеринбург, ул. Ленина, 1" },
                  new CardAddress { Label = "Рабочий", Postcode = "620002", Text = "г. Екатеринбург, ул. Мира, 2" },
                  new CardAddress { Label = "Почтовый", Postcode = "620000", Text = "Главпочтамт, а/я 24" } } };
var form = new[] { F(0, "Фамилия"), F(1, "Имя"), F(2, "Отчество"), F(3, "Дата рождения", "date"), F(4, "ИНН"), F(5, "Адрес регистрации"),
    F(6, "Индекс"), F(7, "Почтовый адрес"), F(8, "Адрес места работы"), F(9, "Дата выдачи"), F(10, "Комментарий") };
var fill = FormFill.Build(card, form).ToDictionary(x => x.Index, x => x.Value);
Eq(fill[0] + " " + fill[1] + " " + fill[2], "Иванов Иван Петрович", "ФИО по полям");
Eq(fill[3], "1980-02-01", "дата в поле type=date - в формате гггг-мм-дд");
Eq(fill[9], "05.06.2010", "дата в обычном поле - как в карточке");
Eq(fill[5], "г. Екатеринбург, ул. Ленина, 1", "адрес регистрации - домашний адрес");
Eq(fill[6], "620001", "индекс после адреса регистрации - индекс домашнего адреса");
Eq(fill[7], "Главпочтамт, а/я 24", "почтовый адрес - почтовый");
Eq(fill[8], "г. Екатеринбург, ул. Мира, 2", "адрес работы - рабочий");
Eq(fill.ContainsKey(10), false, "неизвестное поле не трогается");
var fill2 = FormFill.Build(card, new[] { F(0, "Адрес") }, chosenAddress: 1);
Eq(fill2[0].Value, "г. Екатеринбург, ул. Мира, 2", "для поля «Адрес» без уточнения - адрес, выбранный в подсказке");

// --- поля поиска и подсказки при вводе
Eq(K("", ph: "Искать в Яндекс или ввести адрес"), FieldKind.None, "поле поиска стартовой страницы - не адрес");
Eq(K("Поиск по сайту"), FieldKind.None, "поле «Поиск» не анкета");
Eq(K("", "search", name: "q"), FieldKind.None, "type=search не анкета");
var sg = Suggest.Local("картотека", new[] { ("Почта", "https://mail.yandex.ru/") });
Eq(sg.FirstOrDefault()?.Url, "https://kad.arbitr.ru/", "«картотека» - предложение перейти в картотеку арбитражных дел");
Eq(Suggest.Local("кар", new[] { ("Картотека", "https://kad.arbitr.ru/") }).Count, 1, "плитка и встроенный сайт с тем же адресом - одна подсказка");
Eq(Suggest.Local("ефрсб", Array.Empty<(string, string)>()).Any(x => x.Url.Contains("fedresurs")), true, "«ефрсб» - Федресурс");
Eq(Suggest.Local("мой ар", Array.Empty<(string, string)>()).FirstOrDefault()?.Url, "https://my.arbitr.ru/", "несколько слов, последнее не дописано");
Eq(Suggest.Local("к", Array.Empty<(string, string)>()).Count, 0, "одна буква - без подсказок");
Eq(Suggest.Local("рецепт борща", Array.Empty<(string, string)>()).Count, 0, "посторонний запрос - переходов нет");
Eq(string.Join("|", Suggest.ParseRemote("[\"картотека\",[\"картотека арбитражных дел\",\"картотека дел\"]]")), "картотека арбитражных дел|картотека дел", "подсказки поисковика (OpenSearch)");
Eq(Suggest.ParseRemote("не json").Count, 0, "плохой ответ поисковика - без подсказок");
var mg = Suggest.Merge("картотека", sg, new List<string> { "картотека", "картотека арбитражных дел" }, "Яндекс");
Eq(mg.Count == 2 && mg[0].Kind == "nav" && mg[1].Text == "картотека арбитражных дел", true, "сначала переход на сайт, потом поиск, без повтора ввода");
Eq(Suggest.RemoteUrl("startpage", "a b").StartsWith("https://duckduckgo.com/ac/"), true, "Startpage - подсказки DuckDuckGo");

// --- сертификаты Минцифры (проверка на тестовом удостоверяющем центре)
Eq(RussianCa.IsAllowedHost("online.alfabank.ru", null), true, ".ru - разрешено");
Eq(RussianCa.IsAllowedHost("госуслуги.рф", null), true, ".рф - разрешено");
Eq(RussianCa.IsAllowedHost("example.com", null), false, ".com - нет");
Eq(RussianCa.IsAllowedHost("bank.example.com", new[] { "example.com" }), true, "сайт из вашего списка - разрешено");
using (var rootKey = System.Security.Cryptography.RSA.Create(2048))
using (var leafKey = System.Security.Cryptography.RSA.Create(2048))
using (var otherKey = System.Security.Cryptography.RSA.Create(2048))
{
    System.Security.Cryptography.X509Certificates.X509Certificate2 MakeRoot(System.Security.Cryptography.RSA key, string name)
    {
        var rq = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=" + name, key, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        rq.CertificateExtensions.Add(new System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension(true, false, 0, true));
        rq.CertificateExtensions.Add(new System.Security.Cryptography.X509Certificates.X509KeyUsageExtension(System.Security.Cryptography.X509Certificates.X509KeyUsageFlags.KeyCertSign | System.Security.Cryptography.X509Certificates.X509KeyUsageFlags.CrlSign, true));
        return rq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
    }
    var root = MakeRoot(rootKey, "Test Russian Root");
    var other = MakeRoot(otherKey, "Other Root");
    var lrq = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=bank.test.ru", leafKey, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
    var san = new System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder();
    san.AddDnsName("bank.test.ru");
    lrq.CertificateExtensions.Add(san.Build());
    lrq.CertificateExtensions.Add(new System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension(new System.Security.Cryptography.OidCollection { new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1") }, false));
    var leaf = lrq.Create(root, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1), new byte[] { 1, 2, 3, 4 });
    var leafPem = leaf.ExportCertificatePem();
    Eq(RussianCa.Verify(leafPem, new[] { leafPem }, "bank.test.ru", new[] { root }, null, out _), true, "сертификат, выданный корневым центром, принимается");
    Eq(RussianCa.Verify(leafPem, null, "evil.test.ru", new[] { root }, null, out var why1), false, "сертификат другого сайта не принимается (" + why1 + ")");
    Eq(RussianCa.Verify(leafPem, null, "bank.test.ru", new[] { other }, null, out _), false, "сертификат чужого центра не принимается");
}
var bundled = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Dymka/Bundled"));
var rootFile = Path.Combine(bundled, "russian_trusted_root_ca.crt");
if (File.Exists(rootFile))
{
    var rr = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(File.ReadAllText(rootFile));
    var rs = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(File.ReadAllText(Path.Combine(bundled, "russian_trusted_sub_ca.crt")));
    Eq(rr.Subject.Contains("Russian Trusted Root CA") && rs.Issuer == rr.Subject, true, "встроенные сертификаты Минцифры: корневой и промежуточный");
    using var ch = new System.Security.Cryptography.X509Certificates.X509Chain();
    ch.ChainPolicy.TrustMode = System.Security.Cryptography.X509Certificates.X509ChainTrustMode.CustomRootTrust;
    ch.ChainPolicy.CustomTrustStore.Add(rr);
    ch.ChainPolicy.RevocationMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck;
    Eq(ch.Build(rs), true, "промежуточный сертификат подписан корневым");
}
else Console.WriteLine("(встроенных сертификатов ещё нет: " + rootFile + ")");

// --- банковские карты
Eq(CardTools.Luhn("4111111111111111"), true, "номер карты: контрольная сумма верна");
Eq(CardTools.Luhn("4111111111111112"), false, "опечатка в номере карты ловится");
Eq(CardTools.Brand("2202200012345678"), "Мир", "карта «Мир»");
Eq(CardTools.Brand("4111111111111111"), "Visa", "Visa");
Eq(CardTools.Brand("5500000000000004"), "Mastercard", "Mastercard");
Eq(CardTools.Format("2202200012345678"), "2202 2000 1234 5678", "номер группами по 4");
Eq(CardTools.TryParseExp("05/28", out var em, out var ey) && em == 5 && ey == 2028, true, "срок «05/28»");
Eq(CardTools.TryParseExp("13/28", out _, out _), false, "13-й месяц - ошибка");
var bc = new BankCard { Title = "Сбер", Number = "2202200012345678", Holder = "Ivan Ivanov", ExpMonth = 5, ExpYear = 2028 };
Eq(bc.Summary, "Мир •••• 5678 · до 05/28", "карта в подсказке: только последние цифры");
Eq(new BankCard { Bank = "Т-Банк" }.BadgeColor + new BankCard { Bank = "Т-Банк" }.BadgeFore, "#FFDD2D#333333", "значок Т-Банка: жёлтый, буквы тёмные");
Eq(new BankCard { Bank = "СберБанк" }.BadgeColor, "#21A038", "значок Сбера зелёный");
Eq(CardTools.Banks.Select(b => b.Color + b.Fore).Distinct().Count(), CardTools.Banks.Length, "значки банков не повторяются по цвету");
Eq(K("Номер карты", "tel"), FieldKind.CardNumber, "«Номер карты» (type=tel) - номер карты, не телефон");
Eq(K("", ac: "cc-number"), FieldKind.CardNumber, "autocomplete=cc-number");
Eq(K("Срок действия", ph: "ММ/ГГ"), FieldKind.CardExp, "срок действия");
Eq(K("Имя владельца карты"), FieldKind.CardHolder, "владелец карты");
Eq(K("CVC/CVV"), FieldKind.CardCvc, "CVC");
Eq(K("Номер паспорта"), FieldKind.PassportNumber, "номер паспорта по-прежнему паспорт");
var pay = new[] { F(0, "Номер карты", "tel"), F(1, "Срок действия", ph: "ММ/ГГ"), F(2, "Имя владельца карты"), F(3, "CVC") };
var pf = FormFill.BuildBankCard(bc, pay).ToDictionary(x => x.Index, x => x.Value);
Eq(pf[0] + "|" + pf[1] + "|" + pf[2], "2202200012345678|05/28|IVAN IVANOV", "оплата: номер, срок, владелец");
Eq(pf.ContainsKey(3), false, "код CVC не подставляется никогда");
Eq(FormFill.CvcIndex(pay), 3, "после подстановки курсор - в поле CVC");
var pay2 = new[] { F(0, "Месяц", "select", name: "exp-month"), F(1, "Год", "select", name: "exp-year"), F(2, "", "text", name: "cc-exp", ph: "ММ/ГГГГ") };
var pf2 = FormFill.BuildBankCard(bc, pay2).ToDictionary(x => x.Index, x => x.Value);
Eq(pf2[0] + "|" + pf2[1] + "|" + pf2[2], "05|2028|05/2028", "месяц и год отдельно, срок с годом из 4 цифр");
Eq(FormFill.Build(new Card { LastName = "Иванов" }, pay).Count, 0, "карточка человека не заполняет поля оплаты");

Eq(FormFill.Classify(new FieldInfo(0, "password", "cvc", "", "", "CVC", "")), FieldKind.CardCvc, "CVC в виде поля пароля - это CVC");
Eq(FormFill.Classify(new FieldInfo(0, "password", "password", "", "", "Пароль", "")), FieldKind.None, "обычный пароль - не поле анкеты");
Eq(K("Действует до"), FieldKind.CardExp, "«Действует до» - срок карты (как у Яндекс Пэй)");
Eq(FormFill.ClassifyAll(new[] { F(0, "Номер карты"), F(1, "Действует до"), F(2, "Код") })[2], FieldKind.CardCvc, "«Код» в форме карты - CVC");
Eq(FormFill.ClassifyAll(new[] { F(0, "Фамилия"), F(1, "Код") })[1], FieldKind.None, "«Код» в анкете - не CVC");
var saved = FormFill.CardFromForm(new[] { F(0, "Номер карты"), F(1, "Действует до"), F(2, "Код"), F(3, "Имя владельца карты") },
    new[] { "4111 1111 1111 1111", "1229", "123", "petr petrov" });
Eq(saved != null && saved.Number == "4111111111111111" && saved.ExpMonth == 12 && saved.ExpYear == 2029 && saved.Holder == "PETR PETROV", true, "карта, введённая на сайте: номер, срок, владелец");
Eq(FormFill.CardFromForm(new[] { F(0, "Номер карты") }, new[] { "4111 1111 1111 1112" }), null, "номер с ошибкой - не предлагается");
var noLabel = FormFill.CardFromForm(new[] { F(0, ""), F(1, "") }, new[] { "2202 2000 1234 5672", "что-то" });
Eq(noLabel?.Number, "2202200012345672", "номер без подписи узнаётся по контрольной сумме");
Console.WriteLine($"Тестов пройдено: {passed}, ошибок: {failed}");
return failed == 0 ? 0 : 1;

sealed class FakeYandex : HttpMessageHandler
{
    public int SidRequests;
    public bool FailGet;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        var url = req.RequestUri!.ToString();
        if (url.Contains("turbopages"))
        {
            SidRequests++;
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("... sid: 'a1b2c3d4.e5f6a7b8.c9d0', ...") };
        }
        if (req.Method == HttpMethod.Get && FailGet) return new HttpResponseMessage(System.Net.HttpStatusCode.MethodNotAllowed);
        string query = req.Method == HttpMethod.Post ? await req.Content!.ReadAsStringAsync(ct) : req.RequestUri.Query;
        var texts = query.TrimStart('?').Split('&').Where(p => p.StartsWith("text=")).Select(p => Uri.UnescapeDataString(p[5..]))
            .Select(s => string.Join("<wbr>", s.Split("<wbr>").Select(x => "[ru]" + x)));
        var json = System.Text.Json.JsonSerializer.Serialize(new { code = 200, lang = "en-ru", text = texts });
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json) };
    }
}

sealed class FakeProtector(byte k = 0x5A) : IKeyProtector
{
    public byte[] Protect(byte[] d) => d.Select(b => (byte)(b ^ k)).Prepend(k).ToArray();
    public byte[] Unprotect(byte[] d) => d[0] != k ? throw new System.Security.Cryptography.CryptographicException("чужая учётная запись") : d.Skip(1).Select(b => (byte)(b ^ k)).ToArray();
}
