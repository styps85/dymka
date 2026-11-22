using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Dymka;

/// <summary>Панель вкладок как в Chrome: вкладки одинаковой ширины, сжимаются, когда их много.
/// Не поместились в один ряд - прокручиваются стрелками и колёсиком мыши (активная всегда видна);
/// в режиме «несколько рядов» переносятся во второй-третий ряд. Между вкладками - тонкие разделители.</summary>
public sealed class ShrinkTabPanel : Panel
{
    public double MaxTabWidth { get; set; } = 220;
    public double MinTabWidth { get; set; } = 56;
    /// <summary>В режиме рядов вкладка уже этого - повод начать следующий ряд (чтобы название читалось).</summary>
    public double MinRowTabWidth { get; set; } = 130;
    public int MaxRows { get; set; } = 3;
    public double RowHeight { get; set; } = 40;
    /// <summary>Место справа, которое нужно оставить под кнопку «+».</summary>
    public double Reserve { get; set; } = 40;

    public double PinnedWidth { get; set; } = 42;

    /// <summary>Вкладки в несколько рядов (иначе один ряд с прокруткой).</summary>
    public bool MultiRow
    {
        get => _multiRow;
        set { if (_multiRow == value) return; _multiRow = value; _offset = 0; _lastActive = null; InvalidateMeasure(); }
    }

    /// <summary>Изменилось состояние: (есть скрытые вкладки, можно листать влево, вправо, рядов).</summary>
    public event Action<bool, bool, bool, int>? StateChanged;

    bool _multiRow;
    double _tabWidth, _offset, _content, _visible;
    int _rows = 1;
    object? _lastActive;
    readonly List<Rect> _slots = new();
    (bool, bool, bool, int) _lastState = (false, false, false, 1);

    public ShrinkTabPanel()
    {
        ClipToBounds = true;
        Background = Brushes.Transparent;   // колесо мыши ловится и между вкладками
        Loaded += (_, _) => ThemeManager.Changed += OnTheme;
        Unloaded += (_, _) => ThemeManager.Changed -= OnTheme;
    }

    void OnTheme() => Dispatcher.BeginInvoke(new Action(InvalidateVisual));

    static bool IsPinned(UIElement c) => c is FrameworkElement fe && fe.DataContext is BrowserTab { IsPinned: true };
    static BrowserTab? TabOf(UIElement c) => (c as FrameworkElement)?.DataContext as BrowserTab;

    public bool Overflow => !_multiRow && _content > _visible + 0.5;
    public int Rows => _rows;
    public double Offset => _offset;
    public double TabWidth => _tabWidth;

    protected override Size MeasureOverride(Size availableSize)
    {
        int n = InternalChildren.Count;
        if (n == 0) { _content = 0; _rows = 1; return new Size(0, 0); }
        int pinned = InternalChildren.Cast<UIElement>().Count(IsPinned);
        int normal = n - pinned;
        double pinnedTotal = pinned * PinnedWidth;
        bool inf = double.IsInfinity(availableSize.Width);
        double avail = inf ? MaxTabWidth * normal + pinnedTotal : Math.Max(0, availableSize.Width - Reserve);
        _visible = avail;
        _rows = 1;
        if (normal == 0) _tabWidth = 0;
        else if (!_multiRow || inf)
            _tabWidth = Math.Max(MinTabWidth, Math.Min(MaxTabWidth, (avail - pinnedTotal) / normal));
        else
        {
            // наименьшее число рядов, при котором название вкладки ещё читается
            double w = 0;
            int r;
            for (r = 1; r <= MaxRows; r++)
            {
                int k = (int)Math.Ceiling((normal + pinned * PinnedWidth / Math.Max(1, MinRowTabWidth)) / r);
                w = Math.Min(MaxTabWidth, avail / Math.Max(1, k));
                if (w >= MinRowTabWidth) break;
            }
            if (r > MaxRows)
            {
                int perRow = Math.Max(1, (int)(avail / MinTabWidth));
                r = Math.Max(MaxRows, (int)Math.Ceiling((double)n / perRow));
                int k = (int)Math.Ceiling((double)n / r);
                w = Math.Max(MinTabWidth, Math.Min(MaxTabWidth, avail / k));
            }
            _tabWidth = Math.Floor(w);
        }
        foreach (UIElement c in InternalChildren)
            c.Measure(new Size(IsPinned(c) ? PinnedWidth : _tabWidth, RowHeight));
        Layout(avail);
        double h = _multiRow ? _rows * RowHeight : InternalChildren.Cast<UIElement>().Max(c => c.DesiredSize.Height);
        double width = _multiRow ? avail : Math.Min(_content, avail);
        if (inf) width = _content;
        return new Size(width, h);
    }

    /// <summary>Места вкладок: в один ряд подряд, в режиме рядов - с переносом.</summary>
    void Layout(double avail)
    {
        _slots.Clear();
        double x = 0;
        int row = 0;
        foreach (UIElement c in InternalChildren)
        {
            double w = IsPinned(c) ? PinnedWidth : _tabWidth;
            if (_multiRow && x > 0 && x + w > avail + 0.5) { row++; x = 0; }
            _slots.Add(new Rect(x, row * RowHeight, w, RowHeight));
            x += w;
        }
        _rows = _multiRow ? row + 1 : 1;
        _content = _multiRow ? avail : _slots.Count == 0 ? 0 : _slots[^1].Right;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_slots.Count != InternalChildren.Count) Layout(_visible);
        _visible = Math.Min(_visible, finalSize.Width);
        if (!_multiRow) KeepActiveVisible();
        _offset = Math.Max(0, Math.Min(_offset, Math.Max(0, _content - _visible)));
        double h = _multiRow ? RowHeight : finalSize.Height;
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            var c = InternalChildren[i];
            var s = _slots[i];
            bool last = !_multiRow || (int)Math.Round(s.Top / RowHeight) == _rows - 1;
            if (TabOf(c) is { } t && t.InUpperRow == last) t.InUpperRow = !last;
            c.Arrange(new Rect(s.X - (_multiRow ? 0 : _offset), _multiRow ? s.Y : 0, s.Width, h));
        }
        InvalidateVisual();
        ReportState();
        return finalSize;
    }

    /// <summary>Сменилась активная вкладка - прокрутить так, чтобы её было видно.</summary>
    void KeepActiveVisible()
    {
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            var t = TabOf(InternalChildren[i]);
            if (t is not { IsActive: true }) continue;
            if (!ReferenceEquals(t, _lastActive))
            {
                _lastActive = t;
                var s = _slots[i];
                if (s.Left < _offset) _offset = s.Left;
                else if (s.Right > _offset + _visible) _offset = s.Right - _visible;
            }
            return;
        }
    }

    void ReportState()
    {
        var st = (Overflow, Overflow && _offset > 0.5, Overflow && _offset < _content - _visible - 0.5, _rows);
        if (st == _lastState) return;
        _lastState = st;
        Dispatcher.BeginInvoke(new Action(() => StateChanged?.Invoke(st.Item1, st.Item2, st.Item3, st.Item4)));
    }

    /// <summary>Пролистать на долю видимой ширины (знак - направление).</summary>
    public void ScrollBy(double pages)
    {
        if (!Overflow) return;
        double step = Math.Max(_tabWidth, _visible * 0.6) * pages;
        _offset = Math.Max(0, Math.Min(_offset + step, _content - _visible));
        InvalidateArrange();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!Overflow) return;
        _offset = Math.Max(0, Math.Min(_offset - e.Delta / 120.0 * Math.Max(_tabWidth, 60), _content - _visible));
        InvalidateArrange();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); InvalidateVisual(); }
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); InvalidateVisual(); }

    /// <summary>Разделители - тонкие бледные линии между соседними вкладками (рядом с активной и под мышью их нет).</summary>
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (TryFindResource("TabSeparator") is not Brush brush) return;
        var pen = new Pen(brush, 1);
        pen.Freeze();
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        for (int i = 1; i < InternalChildren.Count && i < _slots.Count; i++)
        {
            var a = InternalChildren[i - 1];
            var b = InternalChildren[i];
            var sa = _slots[i - 1];
            var sb = _slots[i];
            if (Math.Abs(sa.Top - sb.Top) > 0.5) continue;   // перенос на следующий ряд
            if (Hidden(a) || Hidden(b)) continue;
            double x = sb.Left - (_multiRow ? 0 : _offset) - 1.5;
            x = Math.Round(x * dpi) / dpi + 0.5 / dpi;
            double bottom = (_multiRow ? sb.Top + RowHeight : ActualHeight) - 4;
            double mid = bottom - 16;
            dc.DrawLine(pen, new Point(x, mid - 8), new Point(x, mid + 8));
        }
    }

    static bool Hidden(UIElement c) => c.IsMouseOver || TabOf(c) is { IsActive: true };
}
