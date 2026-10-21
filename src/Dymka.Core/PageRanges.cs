namespace Dymka.Core;

/// <summary>Разбор и сборка диапазонов страниц для печати: «1-3, 5, 8-».</summary>
public static class PageRanges
{
    /// <summary>Номера страниц (с 1) из строки вида «1-3, 5, 8-». null - строка с ошибкой.</summary>
    public static List<int>? Parse(string? text, int total)
    {
        var result = new SortedSet<int>();
        if (string.IsNullOrWhiteSpace(text) || total <= 0) return null;
        foreach (var raw in text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var part = raw.Replace('–', '-').Replace('—', '-').Trim();
            int dash = part.IndexOf('-');
            int a, b;
            if (dash < 0)
            {
                if (!int.TryParse(part, out a)) return null;
                b = a;
            }
            else
            {
                var left = part[..dash].Trim();
                var right = part[(dash + 1)..].Trim();
                a = left.Length == 0 ? 1 : int.TryParse(left, out var l) ? l : -1;
                b = right.Length == 0 ? total : int.TryParse(right, out var r) ? r : -1;
                if (a < 0 || b < 0) return null;
            }
            if (a < 1 || b < a) return null;
            for (int i = a; i <= Math.Min(b, total); i++) result.Add(i);
        }
        return result.Count == 0 ? null : result.ToList();
    }

    /// <summary>Все страницы, кроме последней (если страница одна - она и печатается).</summary>
    public static List<int> AllButLast(int total) =>
        Enumerable.Range(1, total <= 1 ? Math.Max(total, 0) : total - 1).ToList();

    /// <summary>Сжатая запись для принтера: [1,2,3,5,7,8] -> «1-3,5,7-8».</summary>
    public static string ToRangeString(IReadOnlyList<int> pages)
    {
        var parts = new List<string>();
        int i = 0;
        while (i < pages.Count)
        {
            int start = pages[i], end = start;
            while (i + 1 < pages.Count && pages[i + 1] == end + 1) { i++; end = pages[i]; }
            parts.Add(start == end ? start.ToString() : $"{start}-{end}");
            i++;
        }
        return string.Join(",", parts);
    }

    /// <summary>Сетка «страниц на листе»: столбцы, строки и ориентация листа.</summary>
    public static (int cols, int rows, bool sheetLandscape) Grid(int perSheet, bool pageLandscape)
    {
        var (c, r, flip) = perSheet switch
        {
            2 => (2, 1, true),
            4 => (2, 2, false),
            6 => (3, 2, true),
            9 => (3, 3, false),
            16 => (4, 4, false),
            _ => (1, 1, false),
        };
        bool sheetLandscape = flip ? !pageLandscape : pageLandscape;
        // на книжном листе «2 и 6 на листе» идут друг под другом
        if (flip && !sheetLandscape) (c, r) = (r, c);
        return (c, r, sheetLandscape);
    }
}
