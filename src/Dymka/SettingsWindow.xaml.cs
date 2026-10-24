using System.Windows;
using System.Windows.Controls;
using Dymka.Core;

namespace Dymka;

public partial class SettingsWindow : Window
{
    readonly AppSettings _s;
    bool _resetPermissions;

    public bool UpdateListsRequested { get; private set; }

    /// <summary>Папка ui с текущими обоями (для предпросмотра).</summary>
    public string? UiDir { get; init; }
    /// <summary>Выбор обоев: null - не меняли, "" - убрать, иначе путь к новому файлу.</summary>
    public string? WallpaperChoice { get; private set; }
    public bool VideoTranslationChanged { get; private set; }

    static readonly (int minutes, string text)[] SleepChoices =
    {
        (5, "5 минут"), (15, "15 минут"), (30, "30 минут"), (60, "1 час"), (0, "Никогда"),
    };

    /// <summary>Раздел, к которому прокрутить при открытии ("video").</summary>
    public string? StartSection { get; init; }

    public SettingsWindow(AppSettings settings, int blocklistCount)
    {
        _s = settings;
        InitializeComponent();
        Loaded += (_, _) => ShowWallpaperPreview(!string.IsNullOrEmpty(_s.Wallpaper) && UiDir != null ? Path.Combine(UiDir, _s.Wallpaper) : null);
        Loaded += (_, _) =>
        {
            if (StartSection == "video") VideoCard.BringIntoView();
        };

        LayoutTop.IsChecked = _s.Layout != "side";
        LayoutSide.IsChecked = _s.Layout == "side";

        var chip = (Style)FindResource("Chip");
        foreach (var e in AppSettings.Engines)
            EnginePanel.Children.Add(new RadioButton
            {
                Content = e.Name, Tag = e.Id, GroupName = "engine", Style = chip, IsChecked = e.Id == _s.Engine.Id,
            });

        bool any = false;
        foreach (var (m, text) in SleepChoices)
        {
            bool on = m == _s.SleepMinutes;
            any |= on;
            SleepPanel.Children.Add(new RadioButton { Content = text, Tag = m, GroupName = "sleep", Style = chip, IsChecked = on });
        }
        if (!any) ((RadioButton)SleepPanel.Children[1]).IsChecked = true;

        foreach (var (id, text) in new[] { ("off", "Выключена"), ("bank", "Только в «Банке»"), ("all", "Везде") })
            SmartScreenPanel.Children.Add(new RadioButton
            {
                Content = text, Tag = id, GroupName = "smartscreen", Style = chip, IsChecked = _s.SmartScreenMode == id,
            });
        CheckUpdatesBox.IsChecked = _s.CheckUpdates;
        StartRestore.IsChecked = _s.RestoreSession;
        StartNew.IsChecked = !_s.RestoreSession;
        VideoTranslationBox.IsChecked = _s.VideoTranslation;
        OfferTranslationBox.IsChecked = _s.OfferTranslation;
        AutoPipBox.IsChecked = _s.AutoPip;
        SuggestBox.IsChecked = _s.SearchSuggestions;
        RuCaBox.IsChecked = _s.TrustRussianCa;
        RuCaSites.Text = string.Join(Environment.NewLine, _s.RussianCaSites);
        VersionInfo.Text = $"Установлена версия {App.VersionText}. Новая версия скачивается и устанавливается по вашему согласию.";

        ClearOnExit.IsChecked = _s.ClearCookiesOnExit;
        Block3p.IsChecked = _s.BlockThirdPartyCookies;
        AutoLists.IsChecked = _s.AutoUpdateLists;
        ListsInfo.Text = $"Сейчас в списке {blocklistCount:N0} доменов трекеров и рекламы. " +
                         (_s.LastListUpdate == DateTime.MinValue
                             ? "Полные списки ещё не скачивались."
                             : $"Последнее обновление: {_s.LastListUpdate:dd.MM.yyyy}.");

        Whitelist.Text = string.Join(Environment.NewLine, _s.CookieWhitelist);
        Exceptions.Text = string.Join(Environment.NewLine, _s.TrackerExceptions);

        int perms = _s.SitePermissions.Count;
        PermInfo.Text = perms == 0
            ? "Сохранённых решений нет. Когда сайт просит доступ к камере, микрофону или местоположению, браузер спрашивает вас."
            : $"Сохранено решений: {perms}. Изменить их для отдельного сайта можно в панели защиты (значок щита в адресной строке).";
        ResetPermBtn.IsEnabled = perms > 0;
    }

    static List<string> ParseDomains(string text) =>
        text.Split(new[] { '\r', '\n', ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(UrlTools.NormalizeDomain)
            .Where(d => d.Length > 0)
            .Distinct()
            .ToList();

    void ShowWallpaperPreview(string? file)
    {
        WallpaperPreview.Source = null;
        if (file != null && File.Exists(file))
        {
            try
            {
                var img = new System.Windows.Media.Imaging.BitmapImage();
                img.BeginInit();
                img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                img.DecodePixelWidth = 256;
                img.UriSource = new Uri(file);
                img.EndInit();
                img.Freeze();
                WallpaperPreview.Source = img;
            }
            catch { }
        }
        bool has = WallpaperPreview.Source != null;
        WallpaperNone.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        WallpaperRemoveBtn.IsEnabled = has || (file != null && WallpaperChoice != "");
    }

    void WallpaperPick_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Обои новой вкладки",
            Filter = "Изображения (*.jpg;*.jpeg;*.png;*.webp;*.bmp)|*.jpg;*.jpeg;*.png;*.webp;*.bmp",
        };
        if (dlg.ShowDialog(this) != true) return;
        WallpaperChoice = dlg.FileName;
        ShowWallpaperPreview(dlg.FileName);
    }

    void WallpaperRemove_Click(object sender, RoutedEventArgs e)
    {
        WallpaperChoice = "";
        ShowWallpaperPreview(null);
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        _s.Layout = LayoutSide.IsChecked == true ? "side" : "top";
        foreach (RadioButton r in EnginePanel.Children)
            if (r.IsChecked == true) _s.SearchEngineId = (string)r.Tag;
        foreach (RadioButton r in SleepPanel.Children)
            if (r.IsChecked == true) _s.SleepMinutes = (int)r.Tag;
        foreach (RadioButton r in SmartScreenPanel.Children)
            if (r.IsChecked == true) _s.SmartScreenMode = (string)r.Tag;
        _s.CheckUpdates = CheckUpdatesBox.IsChecked == true;
        _s.RestoreSession = StartRestore.IsChecked == true;
        VideoTranslationChanged = _s.VideoTranslation != (VideoTranslationBox.IsChecked == true);
        _s.VideoTranslation = VideoTranslationBox.IsChecked == true;
        _s.OfferTranslation = OfferTranslationBox.IsChecked == true;
        _s.AutoPip = AutoPipBox.IsChecked == true;
        _s.SearchSuggestions = SuggestBox.IsChecked == true;
        _s.TrustRussianCa = RuCaBox.IsChecked == true;
        _s.RussianCaSites = ParseDomains(RuCaSites.Text);
        _s.ClearCookiesOnExit = ClearOnExit.IsChecked == true;
        _s.BlockThirdPartyCookies = Block3p.IsChecked == true;
        _s.AutoUpdateLists = AutoLists.IsChecked == true;
        _s.CookieWhitelist = ParseDomains(Whitelist.Text);
        _s.TrackerExceptions = ParseDomains(Exceptions.Text);
        if (_resetPermissions) _s.SitePermissions.Clear();
        DialogResult = true;
    }

    void UpdateLists_Click(object sender, RoutedEventArgs e)
    {
        UpdateListsRequested = true;
        UpdateListsBtn.IsEnabled = false;
        UpdateListsBtn.Content = "Обновятся после сохранения";
    }

    void ResetPermissions_Click(object sender, RoutedEventArgs e)
    {
        _resetPermissions = true;
        ResetPermBtn.IsEnabled = false;
        PermInfo.Text = "Разрешения будут сброшены после сохранения.";
    }
}
