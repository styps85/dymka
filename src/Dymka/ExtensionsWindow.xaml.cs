using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Dymka.Core;

namespace Dymka;

public partial class ExtensionsWindow : Window
{
    readonly AppSettings _s;
    readonly string _dir;
    List<FoundExtension> _found = new();
    bool _scanned;

    public bool Changed { get; private set; }

    public ExtensionsWindow(AppSettings settings, string extensionsDir)
    {
        _s = settings;
        _dir = extensionsDir;
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            _found = await Task.Run(() => ExtensionScanner.ScanInstalledBrowsers(local, roaming));
            _scanned = true;
            Render();
        };
        Render();
    }

    void Render()
    {
        InstalledList.Children.Clear();
        InstalledHint.Text = _s.Extensions.Count == 0
            ? "Пока ни одного. Добавьте нужные из списка ниже."
            : "Отметьте контейнеры, где расширение нужно: в остальных оно не загружается, и новые вкладки там открываются быстрее. " +
              "Выключенное расширение остаётся в «Дымке», но не работает нигде.";
        foreach (var e in _s.Extensions.ToList())
        {
            var entry = e;
            var icon = FindIcon(Path.Combine(_dir, entry.Id));
            var toggle = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = entry.Enabled, VerticalAlignment = VerticalAlignment.Center };
            var places = PlacesFor(entry);
            toggle.Click += (_, _) => { entry.Enabled = toggle.IsChecked == true; Changed = true; places.IsEnabled = entry.Enabled; };
            var remove = new Button
            {
                Content = "Удалить", Style = (Style)FindResource("SecondaryButton"), Height = 32, Padding = new Thickness(12, 0, 12, 0),
                Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            remove.Click += (_, _) =>
            {
                _s.Extensions.Remove(entry);
                try { Directory.Delete(Path.Combine(_dir, entry.Id), true); } catch { }
                Changed = true;
                Render();
            };
            bool older = ExtensionEntry.HasNewerCopy(entry, _s.Extensions);
            InstalledList.Children.Add(Row(icon, entry.Name, $"{entry.Source} · версия {entry.Version}" +
                (older ? " · есть более новая версия этого расширения, эту копию можно удалить" : ""), null, remove, toggle, extra: places));
        }

        FoundList.Children.Clear();
        if (_found.Count == 0)
        {
            FoundList.Children.Add(new TextBlock
            {
                Text = _scanned ? "В других браузерах на этом компьютере расширений не найдено." : "Ищу расширения в Яндекс Браузере, Chrome, Edge, Opera…",
                Foreground = (Brush)FindResource("Muted"), FontSize = 12.5,
            });
            return;
        }
        foreach (var f in _found)
        {
            var ext = f;
            bool added = _s.Extensions.Any(x => x.Id == ext.Id);
            var btn = new Button
            {
                Content = added ? "Добавлено" : "Добавить",
                Style = (Style)FindResource(added ? "SecondaryButton" : "PrimaryButton"),
                Height = 32, Padding = new Thickness(14, 0, 14, 0), IsEnabled = !added, VerticalAlignment = VerticalAlignment.Center,
            };
            btn.Click += (_, _) =>
            {
                try
                {
                    Directory.CreateDirectory(_dir);
                    ExtensionScanner.CopyTo(ext, _dir);
                    _s.Extensions.Add(new ExtensionEntry { Id = ext.Id, Name = ext.Name, Version = ext.Version, Source = ext.Source });
                    Changed = true;
                    Render();
                }
                catch (Exception ex)
                {
                    Dialogs.Show(this, "Не удалось добавить расширение", ex.Message, new[] { "Закрыть" });
                }
            };
            string meta = $"{ext.Source} · версия {ext.Version}" + (ext.UsesNativeMessaging ? " · работает с программой на компьютере" : "");
            FoundList.Children.Add(Row(LoadIcon(ext.IconPath), ext.Name, meta, ext.Description, btn, null, ext.ForSignature));
        }
    }

    /// <summary>«Работает в:» и отметки контейнеров. Хотя бы один контейнер остаётся отмеченным.</summary>
    WrapPanel PlacesFor(ExtensionEntry entry)
    {
        var wrap = new WrapPanel { Margin = new Thickness(0, 7, 0, 0), IsEnabled = entry.Enabled };
        wrap.Children.Add(new TextBlock { Text = "Работает в:", FontSize = 12, Foreground = (Brush)FindResource("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        var boxes = new List<(CheckBox box, string id)>();
        foreach (var c in AppSettings.Containers)
        {
            var label = new StackPanel { Orientation = Orientation.Horizontal };
            label.Children.Add(new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = BrushCache.Get(c.Color), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            label.Children.Add(new TextBlock { Text = c.Name, VerticalAlignment = VerticalAlignment.Center });
            var box = new CheckBox { Style = (Style)FindResource("PlaceChip"), Content = label, IsChecked = entry.Containers == null || entry.Containers.Contains(c.Id), Tag = c.Id };
            boxes.Add((box, c.Id));
            box.Click += (_, _) =>
            {
                var on = boxes.Where(b => b.box.IsChecked == true).Select(b => b.id).ToList();
                if (on.Count == 0) { box.IsChecked = true; return; }   // совсем без контейнеров - это просто «выключено»
                entry.SetContainers(on);
                Changed = true;
            };
            wrap.Children.Add(box);
        }
        return wrap;
    }

    /// <summary>Для самопроверки: оставить расширение только в указанных контейнерах.</summary>
    public void SetPlacesForTest(string id, params string[] containers)
    {
        var e = _s.Extensions.First(x => x.Id == id);
        e.SetContainers(containers);
        Changed = true;
        Render();
    }

    UIElement Row(ImageSource? icon, string name, string meta, string? desc, UIElement action, UIElement? toggle, bool highlight = false, UIElement? extra = null)
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var iconBox = new Border
        {
            Width = 36, Height = 36, CornerRadius = new CornerRadius(9), Background = (Brush)FindResource("Field"),
            VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left,
            Child = icon != null ? new Image { Source = icon, Width = 28, Height = 28 } : null,
        };
        grid.Children.Add(iconBox);

        var text = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        if (highlight)
            title.Children.Add(new Border
            {
                Background = (Brush)FindResource("AccentSoft"), CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 1, 6, 1),
                Margin = new Thickness(8, 0, 0, 0),
                Child = new TextBlock { Text = "подпись", FontSize = 11, Foreground = (Brush)FindResource("Accent") },
            });
        text.Children.Add(title);
        text.Children.Add(new TextBlock { Text = meta, FontSize = 12, Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrWhiteSpace(desc))
            text.Children.Add(new TextBlock
            {
                Text = desc.Length > 160 ? desc[..159] + "…" : desc, FontSize = 12.5, Foreground = (Brush)FindResource("Ink2"),
                Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap,
            });
        if (extra != null) text.Children.Add(extra);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(action);
        if (toggle != null) actions.Children.Add(toggle);
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);
        return grid;
    }

    static ImageSource? FindIcon(string folder)
    {
        var info = ExtensionScanner.ReadManifest(folder, "", Path.GetFileName(folder));
        return LoadIcon(info?.IconPath);
    }

    static ImageSource? LoadIcon(string? path)
    {
        if (path == null || !File.Exists(path)) return null;
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.UriSource = new Uri(path);
            img.DecodePixelWidth = 56;
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch { return null; }
    }

    void Done_Click(object sender, RoutedEventArgs e) => Close();
}
