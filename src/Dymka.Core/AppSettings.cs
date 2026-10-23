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
