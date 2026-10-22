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

    /// <summary>Профили (контейнеры), в которых расширения уже сверены в этом сеансе.</summary>
    public static readonly HashSet<string> ExtensionsSynced = new();
}
