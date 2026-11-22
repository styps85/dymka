using System.Windows;
using System.Windows.Media;
using System.Windows.Shell;

namespace Dymka;

/// <summary>Полоса вкладок сверху: прокрутка стрелками и колёсиком, когда вкладки не помещаются,
/// и режим «несколько рядов» (кнопка со стрелкой рядом с кнопками окна).</summary>
public partial class MainWindow
{
    ShrinkTabPanel? _topPanel;
    int _stripRows = 1;

    const double StripBase = 46, StripRow = 40;
    const double ReserveNormal = 80, ReserveScroll = 108;

    double StripHeight => StripBase + Math.Max(0, _stripRows - 1) * StripRow;

    void TopTabsPanel_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ShrinkTabPanel p || ReferenceEquals(p, _topPanel)) return;
        if (_topPanel != null) _topPanel.StateChanged -= OnTopTabsState;
        _topPanel = p;
        p.MultiRow = _settings.TabRows;
        p.StateChanged += OnTopTabsState;
        UpdateTabRowsButton();
    }

    void OnTopTabsState(bool overflow, bool canLeft, bool canRight, int rows)
    {
        var vis = overflow ? Visibility.Visible : Visibility.Collapsed;
        TabsLeftBtn.Visibility = vis;
        TabsRightBtn.Visibility = vis;
        TabsLeftBtn.IsEnabled = canLeft;
        TabsRightBtn.IsEnabled = canRight;
        TabsLeftBtn.Opacity = canLeft ? 1 : 0.35;
        TabsRightBtn.Opacity = canRight ? 1 : 0.35;
        if (_topPanel != null)
        {
            // стрелка справа занимает место - оставляем его, пока вкладки прокручиваются
            double reserve = overflow ? ReserveScroll : ReserveNormal;
            if (Math.Abs(_topPanel.Reserve - reserve) > 0.5) { _topPanel.Reserve = reserve; _topPanel.InvalidateMeasure(); }
        }
        if (rows != _stripRows)
        {
            _stripRows = rows;
            if (!_fullscreen && _settings.Layout != "side")
            {
                TopRow.Height = new GridLength(StripHeight);
                WindowChrome.GetWindowChrome(this).CaptionHeight = StripHeight;
                Dispatcher.BeginInvoke(new Action(PositionMediaBar), System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }
    }

    void TabsLeft_Click(object sender, RoutedEventArgs e) => _topPanel?.ScrollBy(-1);
    void TabsRight_Click(object sender, RoutedEventArgs e) => _topPanel?.ScrollBy(1);

    void TabRows_Click(object sender, RoutedEventArgs e) => SetTabRows(!_settings.TabRows);

    public void SetTabRows(bool on)
    {
        _settings.TabRows = on;
        _settings.Save();
        if (_topPanel != null) _topPanel.MultiRow = on;
        UpdateTabRowsButton();
        // в других окнах - так же
        foreach (var w in Shared.Windows.Where(w => w != this)) w.ApplyTabRowsFromSettings();
    }

    void ApplyTabRowsFromSettings()
    {
        if (_topPanel != null) _topPanel.MultiRow = _settings.TabRows;
        UpdateTabRowsButton();
    }

    void UpdateTabRowsButton()
    {
        TabRowsGlyph.Data = (Geometry)FindResource(_settings.TabRows ? "IcChevronUp" : "IcChevronDown");
        TabRowsBtn.ToolTip = _settings.TabRows
            ? "Вкладки в один ряд (лишние - прокруткой)"
            : "Вкладки в несколько рядов, если не помещаются в один";
    }

    // ===================================================================== для самопроверки

    public (bool overflow, bool canLeft, bool canRight, int rows, double offset, double tabWidth, double stripHeight) TopTabsStateForTest()
    {
        var p = _topPanel;
        if (p == null) return (false, false, false, 0, 0, 0, TopRow.ActualHeight);
        return (p.Overflow, TabsLeftBtn.IsVisible && TabsLeftBtn.IsEnabled, TabsRightBtn.IsVisible && TabsRightBtn.IsEnabled,
            p.Rows, p.Offset, p.TabWidth, TopRow.ActualHeight);
    }

    public void ScrollTopTabsForTest(double pages) => _topPanel?.ScrollBy(pages);

    /// <summary>Видна ли вкладка целиком в полосе сверху.</summary>
    public bool TopTabVisibleForTest(BrowserTab tab)
    {
        if (_topPanel == null) return false;
        foreach (var c in _topPanel.Children.OfType<FrameworkElement>())
        {
            if (c.DataContext != tab) continue;
            var r = c.TransformToAncestor(_topPanel).TransformBounds(new Rect(0, 0, c.ActualWidth, c.ActualHeight));
            return r.Left >= -0.5 && r.Right <= _topPanel.ActualWidth + 0.5;
        }
        return false;
    }

    /// <summary>Крестик у неактивной вкладки без мыши скрыт и места не занимает.</summary>
    public bool InactiveCloseCollapsedForTest()
    {
        if (_topPanel == null) return false;
        foreach (var c in _topPanel.Children.OfType<FrameworkElement>())
        {
            if (c.DataContext is not BrowserTab { IsActive: false } || c.IsMouseOver) continue;
            var btn = FindNamed(c, "CloseBtn");
            return btn != null && btn.Visibility == Visibility.Collapsed;
        }
        return false;
    }

    static FrameworkElement? FindNamed(DependencyObject root, string name)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var ch = VisualTreeHelper.GetChild(root, i);
            if (ch is FrameworkElement fe && fe.Name == name) return fe;
            var r = FindNamed(ch, name);
            if (r != null) return r;
        }
        return null;
    }

    /// <summary>Добавить спящие вкладки (без движка) - проверить полосу, когда вкладок много.</summary>
    public List<BrowserTab> AddSleepingTabsForTest(int count, string container)
    {
        var list = new List<BrowserTab>();
        for (int i = 0; i < count; i++)
        {
            var t = new BrowserTab(container)
            {
                Url = $"https://example{i + 1}.dymka-test.org/",
                Title = $"Тестовая вкладка номер {i + 1}",
                IsSleeping = true,
                LastActive = DateTime.Now.AddMinutes(-i - 1),
            };
            t.PropertyChanged += OnTabPropertyChanged;
            _tabs.Add(t);
            list.Add(t);
        }
        return list;
    }
}
