using Dymka.Core;
using Microsoft.Web.WebView2.Core;

namespace Dymka;

/// <summary>Общее для всех окон браузера: движок, списки блокировки, открытые окна.</summary>
static class Shared
{
    public static Task<CoreWebView2Environment>? EnvTask;
    public static volatile Blocklist Blocklist = new();
    public static bool BlocklistLoading;
    public static readonly HashSet<string> UsedContainers = new();
    public static readonly List<MainWindow> Windows = new();
    public static bool StartupDone;
    /// <summary>Последнее окно закрывается: сеанс уже сохранён и больше не перезаписывается.</summary>
    public static bool SessionFrozen;
    /// <summary>Движки, процесс которых уже завершился.</summary>
    public static readonly HashSet<CoreWebView2Environment> ExitedEnvs = new();

    /// <summary>Хранилище паролей и карточек (одно на все окна).</summary>
    public static Vault Vault = null!;

    /// <summary>Сайты, открытые по сертификату Минцифры в этом сеансе.</summary>
    public static readonly HashSet<string> RussianCaHosts = new();

    /// <summary>Профили (контейнеры), в которых расширения уже сверены в этом сеансе.</summary>
    public static readonly HashSet<string> ExtensionsSynced = new();
}
