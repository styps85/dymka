using System.Windows;
using Dymka.Core;

namespace Dymka;

/// <summary>Окно инкогнито: страницы открываются в закрытом режиме движка (InPrivate). История, cookie, кэш и данные
/// сайтов живут только в памяти и исчезают, когда закрыто последнее окно инкогнито. Вкладки не попадают в сеанс,
/// пароли и карточки можно подставлять, но новые не сохраняются, расширения не загружаются.</summary>
public partial class MainWindow
{
    public const string IncognitoId = "incognito";

    /// <summary>Сайт для журнала: адреса из окна инкогнито в журнал не попадают.</summary>
    static string LogSite(BrowserTab? tab, string? url) =>
        tab?.Container == IncognitoId ? "сайт в окне инкогнито" : UrlTools.HostOf(url ?? "");

    static string LogUrl(BrowserTab? tab, string? url) =>
        tab?.Container == IncognitoId ? "сайт в окне инкогнито" : RunFlag.SafeUrl(url);
    bool _incognito;

    public bool Incognito
    {
        get => _incognito;
        init
        {
            _incognito = value;
            if (!value) return;
            _container = IncognitoId;
            Title = "Дымка - инкогнито";
            Resources.MergedDictionaries.Add(ThemeManager.IncognitoPalette());
            ContainerSegBox.Visibility = Visibility.Collapsed;
            IncognitoBadge.Visibility = Visibility.Visible;
            UpdateContainerUi();
        }
    }

    public MainWindow OpenIncognito()
    {
        var w = OpenWindow(null, null, incognito: true);
        App.Log("Открыто окно инкогнито");
        return w;
    }

    void Incognito_Click(object sender, RoutedEventArgs e)
    {
        ClosePopups();
        OpenIncognito();
    }

    /// <summary>Сообщение, когда в окне инкогнито пытаются сменить контейнер.</summary>
    bool RefuseInIncognito()
    {
        if (!_incognito) return false;
        ShowToast("В окне инкогнито контейнеров нет: все его вкладки отдельные и удаляются при закрытии окна", seconds: 4);
        return true;
    }
}
