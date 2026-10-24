using System.Globalization;
using System.Windows.Data;

namespace Dymka;

/// <summary>Цвет «#RRGGBB» из данных - в кисть для привязок в разметке.</summary>
public sealed class HexBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string s && s.StartsWith('#') ? BrushCache.Get(s) : System.Windows.Media.Brushes.Gray;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
