using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dymka.Core;

public sealed class Shortcut
{
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
}

public sealed class PinnedSite
{
    public string Container { get; set; } = "work";
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
}

public sealed class ExtensionEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Source { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public sealed record ContainerInfo(string Id, string Name, string Color);

public sealed record SearchEngine(string Id, string Name, string Template);

/// <summary>Настройки браузера. Хранятся в %LOCALAPPDATA%\Dymka\settings.json.</summary>
public sealed class AppSettings
{
    public static readonly ContainerInfo[] Containers =
    {
        new("work", "Работа", "#3D63DD"),
        new("personal", "Личное", "#1C7F6E"),
        new("bank", "Банк", "#C2552E"),
    };

    public static readonly SearchEngine[] Engines =
    {
        new("ddg", "DuckDuckGo", "https://duckduckgo.com/?q={0}"),
        new("startpage", "Startpage", "https://www.startpage.com/do/search?query={0}"),
        new("yandex", "Яндекс", "https://yandex.ru/search/?text={0}"),
        new("google", "Google", "https://www.google.com/search?q={0}"),
    };

    /// <summary>"top" - вкладки сверху, "side" - вкладки сбоку.</summary>
    public string Layout { get; set; } = "top";
    public string SearchEngineId { get; set; } = "ddg";
    public string LastContainer { get; set; } = "work";

    /// <summary>Через сколько минут неактивная вкладка засыпает (0 - никогда).</summary>
    public int SleepMinutes { get; set; } = 15;

    public bool ClearCookiesOnExit { get; set; } = true;

    /// <summary>SmartScreen (предупреждения о мошеннических сайтах и опасных файлах): "off", "bank" - только
    /// в контейнере «Банк», "all" - везде. SmartScreen отправляет адреса страниц в Microsoft.</summary>
    public string SmartScreenMode { get; set; } = "bank";

    public bool CheckUpdates { get; set; } = true;

    /// <summary>При запуске восстанавливать вкладки прошлого сеанса (спящими) или начинать с новой вкладки.</summary>
    public bool RestoreSession { get; set; } = true;
    /// <summary>Открытые вкладки по окнам (закреплённые хранятся отдельно, в Pinned).</summary>
    public List<SessionWindow> Session { get; set; } = new();
    /// <summary>Очистка вкладок: «давно не открывались» - больше скольких дней.</summary>
    public int CleanupOldDays { get; set; } = TabCleanup.DefaultOldDays;

    /// <summary>Перевод видео (встроенное расширение voice-over-translation, сервис Яндекса). По умолчанию выключен.</summary>
    public bool VideoTranslation { get; set; }

    /// <summary>Предлагать перевод страниц не на русском (кнопка «Перевести» в адресной строке). Перевод - Яндекс Переводчик.</summary>
    public bool OfferTranslation { get; set; } = true;

    /// <summary>Видео со звуком при переходе на другую вкладку продолжается в мини-окне поверх всех окон («картинка в картинке»).</summary>
    public bool AutoPip { get; set; } = true;

    /// <summary>Оформление: "light", "dark" или "system" (как в Windows).</summary>
    public string Theme { get; set; } = "light";

    /// <summary>Мини-плеер для музыки: включён ли, оформление ("dark", "light", "theme" - как у браузера) и где стоит.</summary>
    public bool MiniPlayer { get; set; } = true;
    public string MiniPlayerStyle { get; set; } = "dark";
    /// <summary>Край экрана, к которому прилип мини-плеер: "" (угол страницы), "left", "right", "top", "bottom".</summary>
    public string MiniPlayerEdge { get; set; } = "";
    /// <summary>Положение вдоль края (0..1) или, если не прилип, - доля от ширины и высоты экрана.</summary>
    public double MiniPlayerX { get; set; } = -1;
    public double MiniPlayerY { get; set; } = -1;

    /// <summary>Скачивание: спрашивать, куда сохранить каждый файл; папка по умолчанию ("" - «Загрузки» Windows).</summary>
    public bool AskDownloadPath { get; set; } = true;
    public string DownloadFolder { get; set; } = "";
    public string LastDownloadFolder { get; set; } = "";

    /// <summary>Фон новой вкладки: путь внутри папки ui, например "wallpapers/dymka-1-sky-waves.jpg" (пусто - первый стандартный).</summary>
    public string Wallpaper { get; set; } = "";
    /// <summary>Показывать фон на новой вкладке.</summary>
    public bool WallpaperOn { get; set; } = true;
    /// <summary>Каждый день новый фон.</summary>
    public bool WallpaperDaily { get; set; }
    /// <summary>Фоны по временам года и к праздникам (для фона дня).</summary>
    public bool WallpaperSeasonal { get; set; }
    /// <summary>День, когда фон дня выбирался последний раз (yyyy-MM-dd).</summary>
    public string WallpaperDay { get; set; } = "";
    /// <summary>Часы и дата на новой вкладке.</summary>
    public bool ShowClock { get; set; } = true;
    /// <summary>Сведения о защите на новой вкладке (трекеры, спящие вкладки, cookie).</summary>
    public bool ShowStartStats { get; set; } = true;

    /// <summary>Доверять российским сертификатам Минцифры (встроены в браузер) - только для .ru, .рф, .su, .рус и сайтов из списка ниже.</summary>
    public bool TrustRussianCa { get; set; } = true;
    public List<string> RussianCaSites { get; set; } = new();

    /// <summary>Подсказки поисковой системы при вводе в адресную строку (то, что вы набираете, отправляется поисковику).</summary>
    public bool SearchSuggestions { get; set; } = true;

    /// <summary>Предлагать сохранить пароль после входа на сайт.</summary>
    public bool OfferSavePasswords { get; set; } = true;
    /// <summary>Предлагать сохранить банковскую карту, введённую на сайте (без кода CVC).</summary>
    public bool OfferSaveCards { get; set; } = true;
    /// <summary>Подсказка с карточками при входе в поле анкеты (ФИО, ИНН, паспорт, адрес…).</summary>
    public bool OfferAutofill { get; set; } = true;
    /// <summary>Через сколько минут простоя закрывать хранилище с главным паролем (0 - только при закрытии браузера).</summary>
    public int VaultLockMinutes { get; set; } = 15;
    public string BuiltinVotFolder { get; set; } = "";

    // --- печать: запоминаем последний выбор
    public string PrintPrinter { get; set; } = "";
    public bool PrintLandscape { get; set; }
    public int PrintPagesPerSheet { get; set; } = 1;
    public bool PrintColor { get; set; } = true;
    public string PrintDuplex { get; set; } = "none";
    public string PrintMargins { get; set; } = "default";
    public int PrintScale { get; set; } = 100;
    public bool PrintHeaders { get; set; }
    public bool PrintBackgrounds { get; set; }
    public bool BlockThirdPartyCookies { get; set; } = true;
    public bool AutoUpdateLists { get; set; } = true;
    public DateTime LastListUpdate { get; set; } = DateTime.MinValue;

    /// <summary>Сайты, cookie которых сохраняются после закрытия.</summary>
    public List<string> CookieWhitelist { get; set; } = new()
    {
        "gosuslugi.ru", "esia.gosuslugi.ru", "nalog.gov.ru", "my.arbitr.ru",
    };

    /// <summary>Сайты, на которых блокировка трекеров выключена.</summary>
    public List<string> TrackerExceptions { get; set; } = new();

    /// <summary>Решения по разрешениям: "site.ru|camera" -> true/false.</summary>
    public Dictionary<string, bool> SitePermissions { get; set; } = new();

    /// <summary>Старый общий набор плиток (до версии с контейнерами) - переносится в «Работу».</summary>
    public List<Shortcut>? Shortcuts { get; set; }

    /// <summary>Плитки стартовой страницы - у каждого контейнера свои.</summary>
    public Dictionary<string, List<Shortcut>> ShortcutsByContainer { get; set; } = new();

    static List<Shortcut> DefaultWorkShortcuts() => new()
    {
        new() { Title = "Картотека", Url = "https://kad.arbitr.ru/" },
        new() { Title = "Госуслуги", Url = "https://www.gosuslugi.ru/" },
        new() { Title = "ЕФРСБ", Url = "https://fedresurs.ru/" },
        new() { Title = "Dropbox", Url = "https://www.dropbox.com/home" },
    };

    /// <summary>Новый порядок плиток после перетаскивания: по списку адресов; плитки, которых нет в списке, - в конце.</summary>
    public static void ReorderShortcuts(List<Shortcut> list, IList<string> urls)
    {
        var rank = new Dictionary<string, int>();
        for (int i = 0; i < urls.Count; i++) rank.TryAdd(urls[i], i);
        var sorted = list.Select((s, i) => (s, i))
            .OrderBy(x => rank.TryGetValue(x.s.Url, out var r) ? r : urls.Count + x.i)
            .Select(x => x.s).ToList();
        list.Clear();
        list.AddRange(sorted);
    }

    public List<Shortcut> ShortcutsFor(string container)
    {
        container = ContainerById(container).Id;
        if (!ShortcutsByContainer.TryGetValue(container, out var list))
        {
            list = new List<Shortcut>();
            ShortcutsByContainer[container] = list;
        }
        return list;
    }

    public List<PinnedSite> Pinned { get; set; } = new();
    public List<ExtensionEntry> Extensions { get; set; } = new();
    /// <summary>Расширения, которые поставила сама «Дымка» (удалять можно только их, а не встроенные компоненты движка).</summary>
    public List<string> ManagedExtensionIds { get; set; } = new();

    /// <summary>Контейнеры, данные которых ещё не очищены (браузер закрылся аварийно).</summary>
    public List<string> PendingCleanup { get; set; } = new();

    public DateTime StatsDate { get; set; } = DateTime.Today;
    public int BlockedToday { get; set; }

    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 820;
    public bool WindowMaximized { get; set; }

    [JsonIgnore] public string Path { get; private set; } = "";

    public SearchEngine Engine => Engines.FirstOrDefault(e => e.Id == SearchEngineId) ?? Engines[0];

    public static ContainerInfo ContainerById(string? id) =>
        Containers.FirstOrDefault(c => c.Id == id) ?? Containers[0];

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static AppSettings Load(string path)
    {
        AppSettings s;
        try
        {
            s = File.Exists(path)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOpts) ?? new AppSettings()
                : new AppSettings();
        }
        catch
        {
            s = new AppSettings();
        }
        s.Path = path;
        if (s.Layout != "top" && s.Layout != "side") s.Layout = "top";
        if (s.ShortcutsByContainer.Count == 0)
        {
            // первый запуск или переход со старой версии: прежние плитки - в «Работу», остальные контейнеры пустые
            s.ShortcutsByContainer["work"] = s.Shortcuts ?? DefaultWorkShortcuts();
            s.ShortcutsByContainer["personal"] = new List<Shortcut>();
            s.ShortcutsByContainer["bank"] = new List<Shortcut>();
        }
        s.Shortcuts = null;
        if (s.SmartScreenMode is not ("off" or "bank" or "all")) s.SmartScreenMode = "bank";
        if (s.PrintPagesPerSheet is not (1 or 2 or 4 or 6 or 9 or 16)) s.PrintPagesPerSheet = 1;
        s.PrintScale = Math.Clamp(s.PrintScale, 10, 200);
        s.CleanupOldDays = Math.Clamp(s.CleanupOldDays, 1, 365);
        s.Session ??= new List<SessionWindow>();
        if (s.StatsDate.Date != DateTime.Today) { s.StatsDate = DateTime.Today; s.BlockedToday = 0; }
        return s;
    }

    public void Save()
    {
        if (string.IsNullOrEmpty(Path)) return;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var tmp = Path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOpts));
            File.Move(tmp, Path, true);
        }
        catch
        {
            // настройки не критичны: при ошибке записи браузер продолжает работу
        }
    }

    public void CountBlocked(int n = 1)
    {
        if (StatsDate.Date != DateTime.Today) { StatsDate = DateTime.Today; BlockedToday = 0; }
        BlockedToday += n;
    }

    public bool UseSmartScreen(string container) =>
        SmartScreenMode == "all" || (SmartScreenMode == "bank" && container == "bank");

    public bool IsShortcut(string url, string container)
    {
        string Key(string u) => UrlTools.NormalizeDomain(u) + (Uri.TryCreate(u, UriKind.Absolute, out var x) ? x.AbsolutePath.TrimEnd('/') : "");
        var k = Key(url);
        return ShortcutsFor(container).Any(s => Key(s.Url) == k);
    }

    public bool KeepsCookies(string host) => UrlTools.HostMatches(host, CookieWhitelist);

    public bool IsTrackerException(string host) => UrlTools.HostMatches(host, TrackerExceptions);

    public void SetListMembership(List<string> list, string host, bool member)
    {
        var domain = UrlTools.RegistrableDomain(host);
        list.RemoveAll(d => UrlTools.HostMatches(host, new[] { d }));
        if (member) list.Add(domain);
    }

    public static string PermissionKey(string host, string kind) => UrlTools.RegistrableDomain(host) + "|" + kind;
}
