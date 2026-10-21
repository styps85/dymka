using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Dymka.Core;
using Microsoft.Web.WebView2.Core;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace Dymka;

public sealed class PreviewPage
{
    public ImageSource? Image { get; init; }
    public Thickness Frame { get; init; }
}

public sealed class PreviewSheet
{
    public double Width { get; init; }
    public double Height { get; init; }
    public Thickness Pad { get; init; }
    public int Cols { get; init; }
    public int Rows { get; init; }
    public List<PreviewPage> Pages { get; init; } = new();
    public string Caption { get; init; } = "";
}

/// <summary>Окно печати «Дымки»: предпросмотр, все основные параметры на виду.</summary>
public partial class PrintWindow : Window
{
    const double A4W = 8.27, A4H = 11.69;   // дюймы
    const int MaxPreviewPages = 80;

    readonly CoreWebView2 _core;
    readonly CoreWebView2Environment _env;
    readonly AppSettings _s;
    readonly string _title;
    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(350) };

    readonly List<BitmapSource> _pageImages = new();
    int _total;
    bool _ready;
    int _generation;
    bool _printing;

    static readonly int[] PerSheetChoices = { 1, 2, 4, 6, 9, 16 };

    public int TotalPages => _total;
    public bool PreviewReady { get; private set; }

    public PrintWindow(CoreWebView2 core, CoreWebView2Environment env, AppSettings settings, string title)
    {
        _core = core;
        _env = env;
        _s = settings;
        _title = title;
        InitializeComponent();

        var chip = (Style)FindResource("Chip");
        foreach (var n in PerSheetChoices)
        {
            var rb = new RadioButton { Content = n.ToString(), Tag = n, GroupName = "persheet", Style = chip, IsChecked = n == _s.PrintPagesPerSheet };
            rb.Checked += Selection_Changed;
            PerSheetPanel.Children.Add(rb);
        }

        LoadPrinters();
        Portrait.IsChecked = !_s.PrintLandscape;
        Landscape.IsChecked = _s.PrintLandscape;
        PagesAll.IsChecked = true;
        ColorOn.IsChecked = _s.PrintColor;
        ColorOff.IsChecked = !_s.PrintColor;
        DuplexNone.IsChecked = _s.PrintDuplex == "none";
        DuplexLong.IsChecked = _s.PrintDuplex == "long";
        DuplexShort.IsChecked = _s.PrintDuplex == "short";
        MarginsDefault.IsChecked = _s.PrintMargins == "default";
        MarginsNarrow.IsChecked = _s.PrintMargins == "narrow";
        MarginsNone.IsChecked = _s.PrintMargins == "none";
        ScaleBox.Text = _s.PrintScale.ToString();
        HeadersBox.IsChecked = _s.PrintHeaders;
        BackgroundsBox.IsChecked = _s.PrintBackgrounds;

        _debounce.Tick += async (_, _) => { _debounce.Stop(); await GenerateAsync(); };
        Loaded += async (_, _) => { _ready = true; await GenerateAsync(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    void LoadPrinters()
    {
        var names = new List<string>();
        string def = "";
        try
        {
            using var server = new LocalPrintServer();
            foreach (var q in server.GetPrintQueues(new[] { EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections }))
            {
                names.Add(q.FullName);
                q.Dispose();
            }
            using var d = LocalPrintServer.GetDefaultPrintQueue();
            def = d.FullName;
        }
        catch (Exception ex)
        {
            App.Log("Список принтеров: " + ex.Message);
        }
        names = names.Distinct().OrderBy(n => n).ToList();
        PrinterBox.ItemsSource = names;
        var pick = names.Contains(_s.PrintPrinter) ? _s.PrintPrinter : names.Contains(def) ? def : names.FirstOrDefault();
        PrinterBox.SelectedItem = pick;
        if (names.Count == 0)
        {
            ShowError("В Windows не найдено ни одного принтера.");
            PrintBtn.IsEnabled = false;
        }
    }

    // ------------------------------------------------------------------ параметры

    bool IsLandscape => Landscape.IsChecked == true;

    int PerSheet => PerSheetPanel.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as int? ?? 1;

    int Scale
    {
        get
        {
            if (!int.TryParse(ScaleBox.Text.Trim().TrimEnd('%'), out var v)) v = 100;
            return Math.Clamp(v, 10, 200);
        }
    }

    (double top, double side) Margins =>
        MarginsNone.IsChecked == true ? (0, 0) : MarginsNarrow.IsChecked == true ? (0.25, 0.25) : (0.4, 0.4);

    CoreWebView2PrintSettings BuildLayoutSettings()
    {
        var ps = _env.CreatePrintSettings();
        ps.Orientation = IsLandscape ? CoreWebView2PrintOrientation.Landscape : CoreWebView2PrintOrientation.Portrait;
        ps.ScaleFactor = Scale / 100.0;
        ps.PageWidth = A4W;
        ps.PageHeight = A4H;
        var (top, side) = Margins;
        ps.MarginTop = top; ps.MarginBottom = top; ps.MarginLeft = side; ps.MarginRight = side;
        ps.ShouldPrintHeaderAndFooter = HeadersBox.IsChecked == true;
        ps.ShouldPrintBackgrounds = BackgroundsBox.IsChecked == true;
        ps.HeaderTitle = _title;
        return ps;
    }

    /// <summary>Выбранные страницы (номера с 1) или null, если диапазон введён с ошибкой.</summary>
    public List<int>? SelectedPages()
    {
        if (_total <= 0) return new List<int>();
        if (PagesButLast.IsChecked == true) return PageRanges.AllButLast(_total);
        if (PagesCustom.IsChecked == true) return PageRanges.Parse(RangeBox.Text, _total);
        return Enumerable.Range(1, _total).ToList();
    }

    public void SelectAllButLastForTest() => PagesButLast.IsChecked = true;

    public void SetPerSheetForTest(int n)
    {
        foreach (var r in PerSheetPanel.Children.OfType<RadioButton>())
            if ((int)r.Tag == n) r.IsChecked = true;
    }

    // ------------------------------------------------------------------ предпросмотр

    void Layout_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _debounce.Stop();
        _debounce.Start();
    }

    void Selection_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        RebuildSheets();
    }

    void Range_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        if (RangeBox.Text.Length > 0) PagesCustom.IsChecked = true;
        RebuildSheets();
    }

    void Range_GotFocus(object sender, KeyboardFocusChangedEventArgs e) => PagesCustom.IsChecked = true;

    async Task GenerateAsync()
    {
        int gen = ++_generation;
        Busy.Visibility = Visibility.Visible;
        PreviewReady = false;
        HideError();
        try
        {
            using var pdf = await _core.PrintToPdfStreamAsync(BuildLayoutSettings());
            var ms = new MemoryStream();
            await pdf.CopyToAsync(ms);
            ms.Position = 0;
            if (gen != _generation) return;

            var doc = await PdfDocument.LoadFromStreamAsync(ms.AsRandomAccessStream());
            _total = (int)doc.PageCount;
            _pageImages.Clear();
            int render = Math.Min(_total, MaxPreviewPages);
            for (uint i = 0; i < render; i++)
            {
                using var page = doc.GetPage(i);
                var ras = new InMemoryRandomAccessStream();
                await page.RenderToStreamAsync(ras, new PdfPageRenderOptions { DestinationWidth = 420 });
                ras.Seek(0);
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.StreamSource = ras.AsStreamForRead();
                img.EndInit();
                img.Freeze();
                if (gen != _generation) return;
                _pageImages.Add(img);
            }
            TotalInfo.Text = $"всего {_total} {Plural(_total, "страница", "страницы", "страниц")}";
            RebuildSheets();
            PreviewReady = true;
        }
        catch (Exception ex)
        {
            App.Log("Предпросмотр печати: " + ex);
            ShowError("Не удалось подготовить предпросмотр. Печатать всё равно можно.");
            PreviewReady = true;
        }
        finally
        {
            if (gen == _generation) Busy.Visibility = Visibility.Collapsed;
        }
    }

    void RebuildSheets()
    {
        var pages = SelectedPages();
        bool rangeOk = pages != null && (pages.Count > 0 || _total == 0);
        RangeBox.BorderBrush = PagesCustom.IsChecked == true && !rangeOk ? (Brush)FindResource("Danger") : Brushes.Transparent;
        PrintBtn.IsEnabled = rangeOk && PrinterBox.Items.Count > 0 && !_printing;
        if (!rangeOk || pages == null) { Sheets.ItemsSource = null; SheetsInfo.Text = "диапазон страниц указан с ошибкой"; return; }

        int per = PerSheet;
        var (cols, rows, sheetLandscape) = PageRanges.Grid(per, IsLandscape);
        double w = sheetLandscape ? 420 : 297, h = sheetLandscape ? 297 : 420;
        var sheets = new List<PreviewSheet>();
        for (int i = 0; i < pages.Count; i += per)
        {
            var chunk = pages.Skip(i).Take(per).ToList();
            var items = chunk.Select(n => new PreviewPage
            {
                Image = n - 1 < _pageImages.Count ? _pageImages[n - 1] : null,
                Frame = new Thickness(per > 1 ? 1 : 0),
            }).ToList();
            while (items.Count < cols * rows && per > 1) items.Add(new PreviewPage());
            string cap = chunk.Count == 1 ? $"стр. {chunk[0]}" : $"стр. {chunk[0]}-{chunk[^1]}";
            sheets.Add(new PreviewSheet
            {
                Width = w, Height = h, Cols = cols, Rows = rows, Pages = items,
                Pad = new Thickness(per > 1 ? 10 : 0), Caption = $"Лист {sheets.Count + 1} · {cap}",
            });
            if (sheets.Count >= 60) break;
        }
        Sheets.ItemsSource = sheets;
        int sheetCount = (pages.Count + per - 1) / per;
        SheetsInfo.Text = $"{sheetCount} {Plural(sheetCount, "лист", "листа", "листов")}" +
                          (pages.Count != _total ? $" · {pages.Count} из {_total} стр." : "");
    }

    static string Plural(int n, string one, string few, string many)
    {
        int m10 = n % 10, m100 = n % 100;
        if (m10 == 1 && m100 != 11) return one;
        if (m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14)) return few;
        return many;
    }

    void ShowError(string text) { ErrorText.Text = text; ErrorText.Visibility = Visibility.Visible; }
    void HideError() => ErrorText.Visibility = Visibility.Collapsed;

    // ------------------------------------------------------------------ кнопки

    void CopiesMinus_Click(object sender, RoutedEventArgs e) => CopiesBox.Text = Math.Max(1, Copies - 1).ToString();
    void CopiesPlus_Click(object sender, RoutedEventArgs e) => CopiesBox.Text = Math.Min(99, Copies + 1).ToString();
    int Copies => int.TryParse(CopiesBox.Text.Trim(), out var c) ? Math.Clamp(c, 1, 99) : 1;

    void ScaleMinus_Click(object sender, RoutedEventArgs e) { ScaleBox.Text = Math.Max(10, Scale - 10).ToString(); Layout_Changed(sender, e); }
    void ScalePlus_Click(object sender, RoutedEventArgs e) { ScaleBox.Text = Math.Min(200, Scale + 10).ToString(); Layout_Changed(sender, e); }
    void Scale_LostFocus(object sender, KeyboardFocusChangedEventArgs e) { ScaleBox.Text = Scale.ToString(); Layout_Changed(sender, e); }
    void Scale_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ScaleBox.Text = Scale.ToString();
        Layout_Changed(sender, e);
        e.Handled = true;
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    async void Print_Click(object sender, RoutedEventArgs e)
    {
        var pages = SelectedPages();
        var printer = PrinterBox.SelectedItem as string;
        if (pages == null || string.IsNullOrEmpty(printer)) return;

        var ps = BuildLayoutSettings();
        ps.MediaSize = CoreWebView2PrintMediaSize.Default;
        ps.PrinterName = printer;
        ps.Copies = Copies;
        ps.Collation = CoreWebView2PrintCollation.Collated;
        ps.PagesPerSide = PerSheet;
        ps.ColorMode = ColorOn.IsChecked == true ? CoreWebView2PrintColorMode.Color : CoreWebView2PrintColorMode.Grayscale;
        ps.Duplex = DuplexLong.IsChecked == true ? CoreWebView2PrintDuplex.TwoSidedLongEdge
            : DuplexShort.IsChecked == true ? CoreWebView2PrintDuplex.TwoSidedShortEdge
            : CoreWebView2PrintDuplex.OneSided;
        if (_total > 0 && pages.Count < _total) ps.PageRanges = PageRanges.ToRangeString(pages);

        _s.PrintPrinter = printer;
        _s.PrintLandscape = IsLandscape;
        _s.PrintPagesPerSheet = PerSheet;
        _s.PrintColor = ColorOn.IsChecked == true;
        _s.PrintDuplex = DuplexLong.IsChecked == true ? "long" : DuplexShort.IsChecked == true ? "short" : "none";
        _s.PrintMargins = MarginsNone.IsChecked == true ? "none" : MarginsNarrow.IsChecked == true ? "narrow" : "default";
        _s.PrintScale = Scale;
        _s.PrintHeaders = HeadersBox.IsChecked == true;
        _s.PrintBackgrounds = BackgroundsBox.IsChecked == true;
        _s.Save();

        _printing = true;
        PrintBtn.IsEnabled = false;
        PrintBtn.Content = "Отправляю…";
        HideError();
        try
        {
            var status = await _core.PrintAsync(ps);
            if (status == CoreWebView2PrintStatus.Succeeded)
            {
                Close();
                return;
            }
            ShowError(status == CoreWebView2PrintStatus.PrinterUnavailable
                ? "Принтер недоступен. Проверьте, что он включён и подключён."
                : "Не удалось отправить документ на печать.");
        }
        catch (Exception ex)
        {
            App.Log("Печать: " + ex);
            ShowError("Не удалось отправить документ на печать: " + ex.Message);
        }
        _printing = false;
        PrintBtn.Content = "Печать";
        RebuildSheets();
    }
}
