using System.Windows;
using System.Windows.Controls;

namespace Dymka;

/// <summary>Панель вкладок как в Chrome: вкладки одинаковой ширины, сжимаются, когда их много.</summary>
public sealed class ShrinkTabPanel : Panel
{
    public double MaxTabWidth { get; set; } = 220;
    public double MinTabWidth { get; set; } = 56;
    /// <summary>Место справа, которое нужно оставить под кнопку «+».</summary>
    public double Reserve { get; set; } = 40;

    public double PinnedWidth { get; set; } = 42;

    double _tabWidth;

    static bool IsPinned(UIElement c) => c is FrameworkElement fe && fe.DataContext is BrowserTab { IsPinned: true };

    protected override Size MeasureOverride(Size availableSize)
    {
        int n = InternalChildren.Count;
        if (n == 0) return new Size(0, 0);
        int pinned = InternalChildren.Cast<UIElement>().Count(IsPinned);
        int normal = n - pinned;
        double pinnedTotal = pinned * PinnedWidth;
        double avail = double.IsInfinity(availableSize.Width)
            ? MaxTabWidth * normal + pinnedTotal
            : Math.Max(0, availableSize.Width - Reserve);
        _tabWidth = normal == 0 ? 0 : Math.Max(MinTabWidth, Math.Min(MaxTabWidth, (avail - pinnedTotal) / normal));
        double h = 0;
        foreach (UIElement c in InternalChildren)
        {
            c.Measure(new Size(IsPinned(c) ? PinnedWidth : _tabWidth, availableSize.Height));
            h = Math.Max(h, c.DesiredSize.Height);
        }
        double total = pinnedTotal + _tabWidth * normal;
        if (!double.IsInfinity(availableSize.Width)) total = Math.Min(total, Math.Max(0, availableSize.Width - Reserve));
        return new Size(total, h);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        foreach (UIElement c in InternalChildren)
        {
            double w = IsPinned(c) ? PinnedWidth : _tabWidth;
            c.Arrange(new Rect(x, 0, w, finalSize.Height));
            x += w;
        }
        return finalSize;
    }
}
