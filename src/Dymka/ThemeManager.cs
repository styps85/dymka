using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Dymka;

/// <summary>Оформление браузера: светлое, тёмное или как в Windows. Цвета - ресурсы приложения (DynamicResource),
/// поэтому окна перекрашиваются сразу, без перезапуска.</summary>
static class ThemeManager
{
    // ключ, светлый, тёмный
    static readonly (string Key, string Light, string Dark)[] Palette =
    {
        ("Accent", "#3D63DD", "#4F75EE"),
        ("AccentSoft", "#EAF0FF", "#243154"),
        ("AccentSoft2", "#DCE5FF", "#2D3C66"),
        ("Ink", "#1B2230", "#E6EAF0"),
        ("Ink2", "#3D4656", "#C2C9D4"),
        ("Muted", "#5B6475", "#97A1B2"),
        ("Faint", "#7A8394", "#858E9E"),
        ("Line", "#E8ECF2", "#2D333D"),
        ("LineStrong", "#D7DDE6", "#3B424E"),
        ("PopupLine", "#E3E7EE", "#363D48"),
        ("Ground", "#F7F9FC", "#16191F"),
        ("Strip", "#EEF1F6", "#101216"),
        ("Surface", "#FFFFFF", "#1E222A"),
        ("Field", "#F1F4F8", "#272C35"),
        ("FieldHover", "#F8F9FB", "#2C323C"),
        ("Hover", "#E6EAF0", "#2E343E"),
        ("Pressed", "#DCE1E9", "#383F4B"),
        ("Segment", "#E3E8EF", "#22262E"),
        ("TabHover", "#E2E7EE", "#262A33"),
        ("TabBorder", "#E1E6ED", "#343A45"),
        ("Thumb", "#C3CAD5", "#4A5260"),
        ("SwitchOff", "#C9D0DB", "#4A5260"),
        ("Green", "#1C7F53", "#45C083"),
        ("GreenSoft", "#E3F3EA", "#1D3B2D"),
        ("GreenPale", "#F2F9F5", "#17261F"),
        ("GreenHover", "#D3EDDF", "#24493A"),
        ("Danger", "#C42B1C", "#FF6A5C"),
        ("WarnSoft", "#FFF8E6", "#3A3220"),
        ("WinHover", "#14000000", "#1FFFFFFF"),
        ("WinPressed", "#22000000", "#33FFFFFF"),
        ("PrintGround", "#E9EDF3", "#121418"),
        ("BusyOverlay", "#CCE9EDF3", "#CC121418"),
    };

    /// <summary>"light", "dark", "system".</summary>
    public static string Mode { get; private set; } = "light";
    public static bool IsDark { get; private set; }
    public static event Action? Changed;
    static bool _hooked;

    /// <summary>Фон страниц до загрузки (чтобы в тёмной теме не мелькал белый).</summary>
    public static System.Drawing.Color PageBackground => IsDark ? System.Drawing.Color.FromArgb(0x16, 0x19, 0x1F) : System.Drawing.Color.White;

    public static void Apply(string mode)
    {
        Mode = mode is "dark" or "system" ? mode : "light";
        bool dark = Mode == "dark" || (Mode == "system" && SystemIsDark());
        bool changed = dark != IsDark;
        IsDark = dark;
        var res = Application.Current.Resources;
        foreach (var (key, light, darkHex) in Palette)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? darkHex : light)!);
            b.Freeze();
            res[key] = b;
        }
        if (!_hooked)
        {
            _hooked = true;
            // тема Windows сменилась - перекрашиваемся, если выбрано «Как в Windows»
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category == UserPreferenceCategory.General && Mode == "system")
                    Application.Current.Dispatcher.BeginInvoke(new Action(() => Apply("system")));
            };
        }
        App.Log("Оформление: " + Mode + (Mode == "system" ? (dark ? " (сейчас тёмное)" : " (сейчас светлое)") : ""));
        Changed?.Invoke();
        _ = changed;
    }

    /// <summary>В Windows выбран тёмный режим для приложений.</summary>
    public static bool SystemIsDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    /// <summary>Цвет схемы для сайтов (prefers-color-scheme).</summary>
    public static Microsoft.Web.WebView2.Core.CoreWebView2PreferredColorScheme WebScheme => Mode switch
    {
        "dark" => Microsoft.Web.WebView2.Core.CoreWebView2PreferredColorScheme.Dark,
        "system" => Microsoft.Web.WebView2.Core.CoreWebView2PreferredColorScheme.Auto,
        _ => Microsoft.Web.WebView2.Core.CoreWebView2PreferredColorScheme.Light,
    };
}
