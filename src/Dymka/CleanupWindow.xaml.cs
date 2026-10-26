using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Dymka.Core;

namespace Dymka;

/// <summary>Окно очистки вкладок: группы (дубликаты, пустые, с ошибкой, давно не открывавшиеся) с галочками.</summary>
public partial class CleanupWindow : Window
{
    readonly AppSettings _s;
    readonly Func<int, List<CleanupItem>> _find;
    readonly Dictionary<int, CheckBox> _boxes = new();
    readonly Dictionary<CleanupGroup, (CheckBox header, List<CheckBox> items)> _groups = new();
    bool _syncing;

    static readonly int[] DayChoices = { 3, 7, 14, 30 };

    /// <summary>Отмеченные вкладки (Id) - что закрыть.</summary>
    public List<int> SelectedIds => _boxes.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToList();

    public int FoundCount => _boxes.Count;

    public CleanupWindow(AppSettings settings, Func<int, List<CleanupItem>> find)
    {
        _s = settings;
        _find = find;
        InitializeComponent();
        Build();
    }

    void Build()
    {
        // отметки, которые пользователь уже поставил, сохраняются при смене срока «давно не открывались»
        var previous = _boxes.ToDictionary(kv => kv.Key, kv => kv.Value.IsChecked == true);
        _boxes.Clear();
        _groups.Clear();
        Groups.Children.Clear();
        var items = _find(_s.CleanupOldDays);

        foreach (var g in Enum.GetValues<CleanupGroup>())
        {
            var list = items.Where(i => i.Group == g).ToList();
            if (list.Count == 0 && g != CleanupGroup.Old) continue;
            Groups.Children.Add(MakeGroup(g, list, previous));
        }
        if (items.Count == 0)
            Groups.Children.Insert(0, new Border
            {
                Background = (Brush)FindResource("Surface"), CornerRadius = new CornerRadius(16), Padding = new Thickness(20, 16, 20, 16),
                Margin = new Thickness(0, 0, 0, 12), BorderBrush = (Brush)FindResource("Line"), BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = "Дубликатов, пустых вкладок и страниц с ошибкой нет - закрывать нечего.",
                    TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("Ink2"),
                },
            });
        UpdateSummary();
    }

    Border MakeGroup(CleanupGroup g, List<CleanupItem> list, Dictionary<int, bool> previous)
    {
        var tick = (Style)FindResource("Tick");
        var stack = new StackPanel();
        var header = new CheckBox
        {
            Style = tick,
            Content = new TextBlock
            {
                Text = $"{TabCleanup.GroupTitle(g)} ({list.Count})", FontSize = 14.5, FontWeight = FontWeights.Bold,
            },
            IsEnabled = list.Count > 0,
        };
        stack.Children.Add(header);
        if (g == CleanupGroup.Old)
        {
            // срок «давно» можно поменять прямо здесь
            var chips = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(30, 8, 0, 0) };
            chips.Children.Add(new TextBlock
            {
                Text = "не открывались больше", FontSize = 12, Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            foreach (var d in DayChoices)
            {
                var rb = new RadioButton
                {
                    Style = (Style)FindResource("Chip"), GroupName = "days", Content = DaysText(d), Tag = d, IsChecked = d == _s.CleanupOldDays,
                };
                rb.Checked += (_, _) =>
                {
                    if (_syncing || (int)rb.Tag == _s.CleanupOldDays) return;
                    _s.CleanupOldDays = (int)rb.Tag;
                    _s.Save();
                    Dispatcher.BeginInvoke(new Action(Build));
                };
                chips.Children.Add(rb);
            }
            stack.Children.Add(chips);
        }

        var boxes = new List<CheckBox>();
        foreach (var item in list)
        {
            var t = item.Tab;
            var c = AppSettings.ContainerById(t.Container);
            string sub = t.IsStart ? "Стартовая страница" : Uri.UnescapeDataString(t.Url);
            if (g == CleanupGroup.Old || g == CleanupGroup.Duplicates) sub = Ago(t.LastActive) + " · " + sub;
            var content = new DockPanel();
            var chip = new Border
            {
                CornerRadius = new CornerRadius(8), Background = (Brush)FindResource("Field"), Padding = new Thickness(7, 2, 7, 2),
                Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        new System.Windows.Shapes.Ellipse
                        {
                            Width = 7, Height = 7, Fill = BrushCache.Get(c.Color), Margin = new Thickness(0, 0, 5, 0),
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                        new TextBlock { Text = c.Name, FontSize = 11.5, Foreground = (Brush)FindResource("Ink2") },
                    },
                },
            };
            DockPanel.SetDock(chip, Dock.Right);
            content.Children.Add(chip);
            content.Children.Add(new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = t.Title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                    new TextBlock
                    {
                        Text = sub, FontSize = 12, Foreground = (Brush)FindResource("Muted"), TextTrimming = TextTrimming.CharacterEllipsis,
                    },
                },
            });
            var cb = new CheckBox
            {
                Style = tick, Content = content, Margin = new Thickness(0, 10, 0, 0), ToolTip = t.Url,
                IsChecked = previous.TryGetValue(t.Id, out var was) ? was : TabCleanup.CheckedByDefault(g),
            };
            cb.Checked += (_, _) => SyncHeader(g);
            cb.Unchecked += (_, _) => SyncHeader(g);
            boxes.Add(cb);
            _boxes[t.Id] = cb;
            stack.Children.Add(cb);
        }
        if (list.Count == 0)
            stack.Children.Add(new TextBlock
            {
                Text = "Таких вкладок нет.", FontSize = 12.5, Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(30, 8, 0, 0),
            });

        header.Click += (_, _) =>
        {
            bool on = boxes.Any(b => b.IsChecked != true);
            _syncing = true;
            foreach (var b in boxes) b.IsChecked = on;
            _syncing = false;
            SyncHeader(g);
        };
        _groups[g] = (header, boxes);
        SyncHeader(g);

        return new Border
        {
            Background = (Brush)FindResource("Surface"), CornerRadius = new CornerRadius(16), Padding = new Thickness(20, 16, 20, 16),
            Margin = new Thickness(0, 0, 0, 12), BorderBrush = (Brush)FindResource("Line"), BorderThickness = new Thickness(1),
            Child = stack,
        };
    }

    void SyncHeader(CleanupGroup g)
    {
        if (_syncing || !_groups.TryGetValue(g, out var grp)) return;
        int on = grp.items.Count(b => b.IsChecked == true);
        grp.header.IsChecked = on == 0 ? false : on == grp.items.Count ? true : null;
        UpdateSummary();
    }

    void UpdateSummary()
    {
        int n = SelectedIds.Count;
        CloseBtn.Content = n == 0 ? "Закрыть отмеченные" : $"Закрыть отмеченные ({n})";
        CloseBtn.IsEnabled = n > 0;
        Summary.Text = _boxes.Count == 0 ? "" : $"Найдено вкладок: {_boxes.Count}";
    }

    static string DaysText(int d) => d switch { 3 => "3 дней", 7 => "недели", 14 => "2 недель", _ => "месяца" };

    static string Ago(DateTime t)
    {
        var span = DateTime.Now - t;
        if (span.TotalHours < 1) return "только что";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours} ч назад";
        int d = (int)span.TotalDays;
        return d == 1 ? "вчера" : $"{d} {Plural(d, "день", "дня", "дней")} назад";
    }

    static string Plural(int n, string one, string few, string many)
    {
        int m10 = n % 10, m100 = n % 100;
        if (m10 == 1 && m100 != 11) return one;
        if (m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14)) return few;
        return many;
    }

    void CloseChecked_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedIds.Count == 0) return;
        try { DialogResult = true; }
        catch (InvalidOperationException) { Close(); }   // окно открыто не модально (самопроверка)
    }
}
