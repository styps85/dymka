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
Eq(UrlTools.IsLocalFile("file:///C:/Users/User/Dropbox/Дела.html") && !UrlTools.IsLocalFile("https://file.ru/") && !UrlTools.IsLocalFile(null), true, "адрес файла на компьютере распознаётся");
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

// --- отбор вариантов подсказки по введённому
{
    var c1 = new Card { Title = "Должник Ёлкин", LastName = "Ёлкин", FirstName = "Пётр", MiddleName = "Иванович", Inn = "661234567890", Snils = "123-456-789 01" };
    c1.Addresses.Add(new CardAddress { Label = "Домашний", Postcode = "620000", Text = "г. Екатеринбург, ул. Ленина, 1" });
    var t1 = FillFilter.SearchText(c1);
    Eq(FillFilter.Matches("", t1), true, "фильтр: пустой ввод - все варианты");
    Eq(FillFilter.Matches("елк", t1), true, "фильтр: часть фамилии, «е» вместо «ё»");
    Eq(FillFilter.Matches("ЛКИН", t1), true, "фильтр: середина слова, без учёта регистра");
    Eq(FillFilter.Matches("пётр елкин", t1), true, "фильтр: несколько слов в любом порядке");
    Eq(FillFilter.Matches("иванов", t1), true, "фильтр: часть отчества");
    Eq(FillFilter.Matches("петров", t1), false, "фильтр: нет такого слова - вариант скрыт");
    Eq(FillFilter.Matches("6612 3456", t1), true, "фильтр: цифры ИНН с пробелом");
    Eq(FillFilter.Matches("12345678901", t1), true, "фильтр: СНИЛС без дефисов");
    Eq(FillFilter.Matches("ленина", t1), true, "фильтр: по адресу");
    Eq(FillFilter.Matches("  Ёлкин   П ", t1), true, "фильтр: лишние пробелы");
    Eq(FillFilter.Matches("9999", t1), false, "фильтр: чужие цифры");
    Eq(FillFilter.Matches("7890123", t1), false, "фильтр: цифры соседних полей (ИНН и СНИЛС) не склеиваются");
    Eq(FillFilter.Matches("Ив", FillFilter.SearchText(new PasswordEntry { Username = "ivanov@mail.ru", Host = "mail.ru" })), false, "фильтр: кириллица не путается с латиницей");
    Eq(FillFilter.Matches("ivan", FillFilter.SearchText(new PasswordEntry { Username = "ivanov@mail.ru", Host = "mail.ru" })), true, "фильтр: логин");
}

// --- расширения по контейнерам
{
    var e = new ExtensionEntry { Id = "a", Name = "Рутокен", Version = "1.0.5.0" };
    Eq(e.WorksIn("bank"), true, "расширение по умолчанию работает во всех контейнерах");
    e.SetContainers(new[] { "work" });
    Eq(e.WorksIn("work") && !e.WorksIn("bank") && !e.WorksIn("personal"), true, "расширение только в «Работе»");
    e.SetContainers(AppSettings.Containers.Select(c => c.Id));
    Eq(e.Containers == null, true, "все контейнеры - без списка");
    e.Enabled = false;
    Eq(e.WorksIn("work"), false, "выключенное не работает нигде");
    var newer = new ExtensionEntry { Id = "b", Name = "Рутокен", Version = "1.1.0.0" };
    Eq(ExtensionEntry.HasNewerCopy(e, new[] { e, newer }), true, "старая копия расширения распознаётся");
    Eq(ExtensionEntry.HasNewerCopy(newer, new[] { e, newer }), false, "новая копия - не дубль");
    var ftmp = Path.Combine(Path.GetTempPath(), "dymka-ext-" + Guid.NewGuid() + ".json");
    var es = AppSettings.Load(ftmp);
    es.Extensions.Add(new ExtensionEntry { Id = "x", Containers = new() { "work" } });
    es.Save();
    var back = AppSettings.Load(ftmp);
    File.Delete(ftmp);
    Eq(back.Extensions[0].Containers?.SingleOrDefault(), "work", "контейнеры расширения сохраняются в настройках");
    Eq(back.PreloadNewTab, true, "заготовка новой вкладки включена по умолчанию");
}

// --- обновление: манифест, подпись, докачка
{
    var data = new byte[300_000];
    new Random(5).NextBytes(data);
    var dir = Path.Combine(Path.GetTempPath(), "dymka-dl-" + Guid.NewGuid());
    Directory.CreateDirectory(dir);
    var src = Path.Combine(dir, "src.bin");
    File.WriteAllBytes(src, data);
    var desc = UpdateManifest.Describe(src);
    Eq(desc.Size, 300_000L, "манифест: размер файла");
    var m = new UpdateManifest { Version = "1.0.70", Files = { desc, new UpdateFile("a.zip", 5, "AB") } };
    using (var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256))
    {
        var pub = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        m.Sign(key);
        var back = UpdateManifest.Parse(m.ToJson())!;
        Eq(back.Verify(pub), true, "манифест: подпись проверяется после сохранения");
        back.Files[0] = back.Files[0] with { Sha256 = new string('0', 64) };
        Eq(back.Verify(pub), false, "манифест: подменённая сумма - подпись не сходится");
        using var other = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        Eq(m.Verify(Convert.ToBase64String(other.ExportSubjectPublicKeyInfo())), false, "манифест: чужой ключ не подходит");
    }
    Eq(UpdateManifest.Parse("{\"Format\":1,\"Version\":\"1.0.70\",\"Files\":[{\"Name\":\"Dymka-update.zip\",\"Size\":10,\"Sha256\":\"ab\"}],\"Signature\":\"\"}")?.File("Dymka-update.zip")?.Size, 10L,
        "манифест из сборки (PowerShell) читается");

    // обрыв на середине -> докачка с того же места
    var srv = new FlakyServer(data, dropAfter: 120_000, supportsRange: true);
    var http = new HttpClient(srv);
    var outFile = Path.Combine(dir, "out.bin");
    var logs = new List<string>();
    var err = ResumableDownload.RunAsync(http, "https://x/f", outFile, data.Length, desc.Sha256, log: logs.Add,
        options: new ResumableDownload.Options { Backoff = _ => TimeSpan.Zero }).GetAwaiter().GetResult();
    Eq(err, null, "докачка: файл скачан после обрыва");
    Eq(File.Exists(outFile) && File.ReadAllBytes(outFile).AsSpan().SequenceEqual(data), true, "докачка: файл целый");
    Eq(srv.RangeStarts.Contains(120_000), true, "докачка: второй запрос продолжил с места обрыва: " + string.Join(",", srv.RangeStarts));
    // сервер без докачки - скачивает заново
    File.Delete(outFile);
    var srv2 = new FlakyServer(data, dropAfter: 50_000, supportsRange: false);
    err = ResumableDownload.RunAsync(new HttpClient(srv2), "https://x/f", outFile, data.Length, desc.Sha256,
        options: new ResumableDownload.Options { Backoff = _ => TimeSpan.Zero }).GetAwaiter().GetResult();
    Eq(err == null && File.ReadAllBytes(outFile).AsSpan().SequenceEqual(data), true, "сервер без докачки: файл скачан заново целиком");
    // неверная сумма - файл не принимается
    File.Delete(outFile);
    err = ResumableDownload.RunAsync(new HttpClient(new FlakyServer(data, 0, true)), "https://x/f", outFile, data.Length, new string('1', 64),
        options: new ResumableDownload.Options { Backoff = _ => TimeSpan.Zero }).GetAwaiter().GetResult();
    Eq(err != null && err.Contains("контрольная сумма") && !File.Exists(outFile), true, "чужая контрольная сумма - обновление не принимается");
    Eq(ResumableDownload.Describe(new HttpRequestException("The SSL connection could not be established, see inner exception.",
        new System.Security.Authentication.AuthenticationException("Authentication failed because the remote party sent a TLS alert: 'HandshakeFailure'."))).Contains("HandshakeFailure"),
        true, "ошибка соединения записывается вместе с внутренней причиной");
    Directory.Delete(dir, true);
}

// --- синхронизация: шифрование и слияние данных компьютеров
{
    var t0 = new DateTime(2026, 11, 1, 10, 0, 0);
    Eq(SyncCrypto.CheckPassword("короткий") != null, true, "синхронизация: короткий пароль не принимается");
    Eq(SyncCrypto.CheckPassword("Длинный-пароль-2026"), null, "синхронизация: длинный пароль подходит");
    var (info, key) = SyncCrypto.CreateSet("Длинный-пароль-2026", t0);
    var infoBack = System.Text.Json.JsonSerializer.Deserialize<SyncSetInfo>(System.Text.Json.JsonSerializer.Serialize(info))!;
    Eq(SyncCrypto.OpenSet(infoBack, "Длинный-пароль-2026")?.SequenceEqual(key), true, "синхронизация: свой пароль открывает набор");
    Eq(SyncCrypto.OpenSet(infoBack, "Чужой-пароль-коллеги"), null, "синхронизация: чужой пароль набор не открывает");
    var sealedDoc = SyncCrypto.Seal(key, new SyncDocument { Computer = "Кабинет", Records = { new SyncRecord { Id = "pw:1", Kind = "password", Json = "{\"p\":\"секрет\"}" } } });
    Eq(System.Text.Encoding.UTF8.GetString(sealedDoc).Contains("секрет"), false, "синхронизация: в файле нет открытого текста");
    Eq(SyncCrypto.Open(key, sealedDoc).Records[0].Json.Contains("секрет"), true, "синхронизация: файл расшифровывается своим ключом");
    var tampered = (byte[])sealedDoc.Clone(); tampered[^1] ^= 1;
    bool rejected = false; try { SyncCrypto.Open(key, tampered); } catch (System.Security.Cryptography.CryptographicException) { rejected = true; }
    Eq(rejected, true, "синхронизация: изменённый файл не принимается");
    var otherKey = SyncCrypto.CreateSet("Пароль-Пигильцева-1", t0).key;
    rejected = false; try { SyncCrypto.Open(otherKey, sealedDoc); } catch (System.Security.Cryptography.CryptographicException) { rejected = true; }
    Eq(rejected, true, "синхронизация: ключом чужого набора файл не открыть");
    Eq(SyncCrypto.SafeName("Семенов/../Г.В.:"), "Семенов_.._Г.В._", "синхронизация: имя набора без опасных символов");

    string Mac(string j) => SyncCrypto.Mac(key, j);
    Dictionary<string, (string kind, string json)> L(params (string id, string json)[] r) => r.ToDictionary(x => x.id, x => ("k", x.json));
    // компьютер A: два пароля, первая синхронизация
    var aLocal = L(("pw:1", "a1"), ("pw:2", "b1"));
    var s_ra = SyncMerge.Merge(aLocal, new SyncState(), Array.Empty<SyncDocument>(), Mac, t0, "A");
    Eq(s_ra.Document.Records.Count == 2 && s_ra.Apply.Count == 0, true, "слияние: первая синхронизация - свои записи в файл");
    var aState = SyncMerge.Remember(s_ra, aLocal, Mac, "S");
    // компьютер B: пусто, получает записи A
    var s_rb = SyncMerge.Merge(L(), new SyncState(), new[] { s_ra.Document }, Mac, t0.AddMinutes(1), "B");
    Eq(s_rb.Apply.Select(x => x.Id).OrderBy(x => x).SequenceEqual(new[] { "pw:1", "pw:2" }), true, "слияние: новый компьютер получает все записи");
    var bLocal = L(("pw:1", "a1"), ("pw:2", "b1"));
    var bState = SyncMerge.Remember(s_rb, bLocal, Mac, "S");
    // на B правят pw:1 и удаляют pw:2; на A ничего не меняется
    var bLocal2 = L(("pw:1", "a2"));
    var s_rb2 = SyncMerge.Merge(bLocal2, bState, new[] { s_ra.Document }, Mac, t0.AddMinutes(5), "B");
    Eq(s_rb2.Apply.Count == 0 && s_rb2.Remove.Count == 0 && s_rb2.LocalChanges == 2, true, "слияние: правка и удаление на B не откатываются старым файлом A");
    // A получает правку и удаление
    var s_ra2 = SyncMerge.Merge(aLocal, aState, new[] { s_rb2.Document }, Mac, t0.AddMinutes(6), "A");
    Eq(s_ra2.Apply.SingleOrDefault()?.Json == "a2" && s_ra2.Remove.SequenceEqual(new[] { "pw:2" }), true, "слияние: A получает правку и удаление с B");
    // одновременная правка: побеждает более поздняя
    var s_ra3 = SyncMerge.Merge(L(("pw:1", "a3")), SyncMerge.Remember(s_ra2, L(("pw:1", "a2")), Mac, "S"), new[] { s_rb2.Document }, Mac, t0.AddMinutes(10), "A");
    Eq(s_ra3.Document.Records.Single(r => r.Id == "pw:1").Json, "a3", "слияние: более поздняя правка побеждает");
    // запись, изменённая после удаления, восстанавливается
    var revived = new SyncDocument { Records = { new SyncRecord { Id = "pw:2", Json = "b9", Stamp = t0.AddMinutes(20) } } };
    var s_ra4 = SyncMerge.Merge(L(("pw:1", "a3")), SyncMerge.Remember(s_ra3, L(("pw:1", "a3")), Mac, "S"), new[] { s_rb2.Document, revived }, Mac, t0.AddMinutes(21), "A");
    Eq(s_ra4.Apply.Any(x => x.Id == "pw:2" && x.Json == "b9"), true, "слияние: запись, изменённая после удаления, возвращается");
    // повторное слияние без изменений ничего не делает
    var aState4 = SyncMerge.Remember(s_ra4, L(("pw:1", "a3"), ("pw:2", "b9")), Mac, "S");
    var s_ra5 = SyncMerge.Merge(L(("pw:1", "a3"), ("pw:2", "b9")), aState4, new[] { s_ra4.Document }, Mac, t0.AddMinutes(30), "A");
    Eq(s_ra5.Apply.Count == 0 && s_ra5.Remove.Count == 0 && s_ra5.LocalChanges == 0, true, "слияние: без изменений - ничего не применяется");
}

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
Eq(UrlTools.IsStartPage("https://dymka.start/start.html") && UrlTools.IsStartPage("https://dymka.start/"), true, "стартовая страница");
Eq(UrlTools.IsStartPage(UrlTools.GalleryUrl), false, "галерея фонов - не стартовая страница");
Eq(UrlTools.IsGalleryPage(UrlTools.GalleryUrl) && UrlTools.IsInternalPage(UrlTools.GalleryUrl), true, "галерея фонов - служебная страница");
// ---- фоны новой вкладки
var wps = Wallpapers.Order(new[] { "custom-moi.jpg", "gallery-зима-1.jpg", "dymka-2.jpg", "dymka-1.jpg", "gallery-новый год-1.png", "readme.txt", "gallery-весна.webp" });
Eq(string.Join(",", wps), "dymka-1.jpg,dymka-2.jpg,gallery-весна.webp,gallery-зима-1.jpg,gallery-новый год-1.png,custom-moi.jpg", "фоны по порядку: стандартные, галерея, свои");
Eq(Wallpapers.Step(wps, "dymka-1.jpg", 1), "dymka-2.jpg", "стрелка вперёд");
Eq(Wallpapers.Step(wps, "dymka-1.jpg", -1), "custom-moi.jpg", "стрелка назад по кругу");
Eq(Wallpapers.Step(wps, "нет-такого.jpg", 1), "dymka-1.jpg", "неизвестный фон - начинаем с первого");
Eq(Wallpapers.ThemeOf("gallery-Новый Год-2.jpg"), "newyear", "тема по имени: новый год");
Eq(Wallpapers.ThemeOf("winter-snow.jpg") + Wallpapers.ThemeOf("8 марта-1.jpg") + Wallpapers.ThemeOf("осень.png"), "wintermarch8autumn", "темы: зима, 8 марта, осень");
Eq(Wallpapers.ThemeOf("dymka-1-sky-waves.jpg"), "", "стандартный фон - круглый год");
Eq(Wallpapers.HolidayOf(new DateTime(2026, 12, 31)) + Wallpapers.HolidayOf(new DateTime(2027, 1, 5)) + Wallpapers.HolidayOf(new DateTime(2027, 3, 7)), "newyearnewyearmarch8", "праздники по датам");
Eq(Wallpapers.SeasonOf(new DateTime(2026, 10, 25)), "autumn", "октябрь - осень");
Eq(string.Join(",", Wallpapers.Pool(wps, new DateTime(2026, 12, 31), true)), "gallery-новый год-1.png", "в Новый год - новогодние фоны");
Eq(string.Join(",", Wallpapers.Pool(wps, new DateTime(2027, 1, 20), true)), "gallery-зима-1.jpg", "зимой - зимние фоны");
Eq(string.Join(",", Wallpapers.Pool(wps, new DateTime(2026, 10, 25), true)), "dymka-1.jpg,dymka-2.jpg,custom-moi.jpg", "осенью без осенних - фоны без темы");
Eq(Wallpapers.Pool(wps, new DateTime(2026, 12, 31), false).Contains("gallery-новый год-1.png"), false, "без времён года праздничные фоны не выпадают как фон дня");
var d1 = Wallpapers.OfTheDay(wps, new DateTime(2026, 10, 25), false);
var d2 = Wallpapers.OfTheDay(wps, new DateTime(2026, 10, 26), false);
Eq(d1 != d2 && d1 == Wallpapers.OfTheDay(wps, new DateTime(2026, 10, 25, 23, 0, 0), false), true, "фон дня меняется раз в сутки");
Eq(Wallpapers.SafeName("gallery-", "../../evil name?.PNG"), "gallery-evil-name-.png", "безопасное имя файла фона");

// --- порядок плиток после перетаскивания
var sc = new List<Shortcut> { new() { Title = "A", Url = "a" }, new() { Title = "B", Url = "b" }, new() { Title = "C", Url = "c" }, new() { Title = "D", Url = "d" } };
AppSettings.ReorderShortcuts(sc, new[] { "c", "a", "b", "d" });
Eq(string.Join(",", sc.Select(x => x.Url)), "c,a,b,d", "перетаскивание: C на первое место");
AppSettings.ReorderShortcuts(sc, new[] { "b", "zzz" });
Eq(string.Join(",", sc.Select(x => x.Url)), "b,c,a,d", "неизвестный адрес не мешает, остальные сохраняют порядок");

// --- мини-плеер: ответ страницы
var ms1 = MediaState.Parse("{\"has\":true,\"playing\":true,\"title\":\" Трек \",\"artist\":\"Исполнитель\",\"art\":\"https://x.ru/a.jpg\",\"video\":false,\"prev\":true,\"next\":true}");
Eq(ms1?.Title, "Трек", "мини-плеер: название без пробелов");
Eq(ms1?.WantsBar, true, "мини-плеер: звук без видео - показываем");
Eq(MediaState.Parse("\"{\\\"has\\\":true,\\\"video\\\":true}\"")?.WantsBar, false, "мини-плеер: строка в кавычках, видео - не показываем");
Eq(MediaState.Parse("{\"has\":true,\"art\":\"data:image/png;base64,AAA\"}")?.Art, "", "мини-плеер: картинку data: не загружаем");
Eq(MediaState.Parse("null"), null, "мини-плеер: пустой ответ");
Eq(MediaState.Parse("{oops"), null, "мини-плеер: мусор");

// --- отметка запуска (диагностика аварийного закрытия)
var rf = new RunFlag(new DateTime(2026, 10, 27, 0, 13, 40), 19780, "https://site.ru/page", new DateTime(2026, 10, 27, 1, 2, 3));
Eq(RunFlag.Parse(rf.Format()), rf, "отметка запуска: запись и чтение");
Eq(RunFlag.Parse("2026-10-25 23:57:24")?.Pid, 0, "отметка запуска старого вида");
Eq(RunFlag.Parse("мусор"), null, "отметка запуска: мусор");
Eq(RunFlag.SafeUrl("https://yandex.ru/search/?text=секрет#x"), "https://yandex.ru/search/", "в журнал - адрес без запроса");

// --- скачивание: свободное имя файла
var taken = new HashSet<string> { Path.Combine("D", "акт.pdf"), Path.Combine("D", "акт (1).pdf") };
Eq(DownloadPaths.Unique("D", "акт.pdf", taken.Contains), Path.Combine("D", "акт (2).pdf"), "скачивание: занятое имя - «акт (2).pdf»");
Eq(DownloadPaths.Unique("D", "новый.docx", taken.Contains), Path.Combine("D", "новый.docx"), "скачивание: свободное имя не меняется");
Eq(DownloadPaths.Safe("a:b?.txt"), "a_b_.txt", "скачивание: недопустимые знаки заменяются");
Eq(DownloadPaths.Safe(""), "download", "скачивание: пустое имя");

// --- мини-плеер: по умолчанию светлый, выбор вручную сохраняется
var tmpS = Path.Combine(Path.GetTempPath(), "dymka-test-settings.json");
File.WriteAllText(tmpS, "{\"MiniPlayerStyle\":\"dark\"}");
Eq(AppSettings.Load(tmpS).MiniPlayerStyle, "light", "мини-плеер: прежний тёмный по умолчанию становится светлым");
File.WriteAllText(tmpS, "{\"MiniPlayerStyle\":\"dark\",\"MiniPlayerStyleChosen\":true}");
Eq(AppSettings.Load(tmpS).MiniPlayerStyle, "dark", "мини-плеер: тёмный, выбранный вручную, остаётся");
File.Delete(tmpS);

// --- проверка обновлений
Eq(UpdateCheck.ParseTag("v1.0.54"), new Version(1, 0, 54), "тег v1.0.54");
Eq(UpdateCheck.ParseTag("1.2"), new Version(1, 2, 0), "тег без третьей части");
Eq(UpdateCheck.ParseTag("selftest"), null, "служебный тег selftest");
Eq(UpdateCheck.ParseTag("certs-fetch"), null, "служебный тег certs-fetch");
Eq(UpdateCheck.ParseTag(""), null, "пустой тег");
Eq(UpdateCheck.FromReleaseUrl("https://github.com/styps85/dymka/releases/tag/v1.0.54"), new Version(1, 0, 54), "адрес после перенаправления");
Eq(UpdateCheck.FromReleaseUrl("https://github.com/styps85/dymka/releases"), null, "адрес без тега");
Eq(UpdateCheck.FromReleaseUrl("https://github.com/styps85/dymka/releases/tag/selftest"), null, "адрес служебного выпуска");
Eq(UpdateCheck.Normalize(new Version(1, 0, 46, 0)) < new Version(1, 0, 54), true, "1.0.46.0 старше 1.0.54");
Eq(UpdateCheck.Normalize(new Version(1, 0, 54, 0)) == new Version(1, 0, 54), true, "четвёртая часть не мешает сравнению");
Eq(UpdateCheck.DownloadUrl(new Version(1, 0, 54), "Dymka.exe"), "https://github.com/styps85/dymka/releases/download/v1.0.54/Dymka.exe", "ссылка на exe");
var nowU = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
Eq(UpdateCheck.DescribeHttpFailure(403, "0", "1800001200", nowU).Contains("60 в час") , true, "лимит: причина");
Eq(UpdateCheck.DescribeHttpFailure(403, "0", "1800001200", nowU).Contains("через 20 мин"), true, "лимит: когда снимется");
Eq(UpdateCheck.DescribeHttpFailure(403, "57", null, nowU).Contains("провайдер"), true, "403 без лимита - блокировка");
Eq(UpdateCheck.DescribeHttpFailure(502, null, null, nowU).Contains("502"), true, "ошибка сервера");

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

/// <summary>Сервер для проверки докачки: отдаёт файл, рвёт соединение после dropAfter байт (один раз), умеет Range.</summary>
sealed class FlakyServer(byte[] data, int dropAfter, bool supportsRange) : HttpMessageHandler
{
    public readonly List<long> RangeStarts = new();
    bool _dropped;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        long from = req.Headers.Range?.Ranges.FirstOrDefault()?.From ?? 0;
        RangeStarts.Add(from);
        if (!supportsRange) from = 0;
        var slice = data.AsSpan((int)from).ToArray();
        Stream body = new MemoryStream(slice);
        if (!_dropped && dropAfter > 0) { _dropped = true; body = new DropStream(body, dropAfter - (int)from); }
        var resp = new HttpResponseMessage(from > 0 ? System.Net.HttpStatusCode.PartialContent : System.Net.HttpStatusCode.OK) { Content = new StreamContent(body) };
        resp.Content.Headers.ContentLength = slice.Length;
        return Task.FromResult(resp);
    }
}

sealed class DropStream(Stream inner, int limit) : Stream
{
    int _read;
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_read >= limit) throw new IOException("Соединение разорвано удалённой стороной");
        int n = inner.Read(buffer, offset, Math.Min(count, limit - _read));
        _read += n;
        return n;
    }
    public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
    public override long Length => inner.Length; public override long Position { get => _read; set => throw new NotSupportedException(); }
    public override void Flush() { } public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
    public override void SetLength(long v) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
}
