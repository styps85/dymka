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

    double _tabWidth;

    protected override Size MeasureOverride(Size availableSize)
    {
        int n = InternalChildren.Count;
        if (n == 0) return new Size(0, 0);
        double avail = double.IsInfinity(availableSize.Width) ? MaxTabWidth * n : Math.Max(0, availableSize.Width - Reserve);
        _tabWidth = Math.Max(MinTabWidth, Math.Min(MaxTabWidth, avail / n));
        double h = 0;
        foreach (UIElement c in InternalChildren)
        {
            c.Measure(new Size(_tabWidth, availableSize.Height));
            h = Math.Max(h, c.DesiredSize.Height);
        }
        return new Size(Math.Min(_tabWidth * n, double.IsInfinity(availableSize.Width) ? _tabWidth * n : Math.Max(0, availableSize.Width - Reserve)), h);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        foreach (UIElement c in InternalChildren)
        {
            c.Arrange(new Rect(x, 0, _tabWidth, finalSize.Height));
            x += _tabWidth;
        }
        return finalSize;
    }
}
