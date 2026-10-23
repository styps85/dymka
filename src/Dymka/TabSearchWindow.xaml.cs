using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Dymka;

/// <summary>Окошко поиска по вкладкам (Ctrl+Shift+A). Закрывается при уходе фокуса, Esc или выборе вкладки.</summary>
public partial class TabSearchWindow : Window
{
    readonly Func<string, List<MainWindow.TabSearchItem>> _search;
    readonly Action<MainWindow.TabSearchItem?> _go;
    bool _closing;

    public TabSearchWindow(Func<string, List<MainWindow.TabSearchItem>> search, Action<MainWindow.TabSearchItem?> go)
    {
        _search = search;
        _go = go;
        InitializeComponent();
        Fill();
        Loaded += (_, _) => { TabSearchBox.Focus(); Keyboard.Focus(TabSearchBox); };
        Deactivated += (_, _) => SafeClose();
        Closing += (_, _) => _closing = true;
    }

    void SafeClose()
    {
        if (_closing) return;
        _closing = true;
        Dispatcher.BeginInvoke(new Action(Close));
    }

    public string Text { get => TabSearchBox.Text; set => TabSearchBox.Text = value; }

    public List<MainWindow.TabSearchItem> Results => (TabSearchList.ItemsSource as List<MainWindow.TabSearchItem>) ?? new();

    void Fill()
    {
        var items = _search(TabSearchBox.Text);
        TabSearchList.ItemsSource = items;
        if (items.Count > 0) TabSearchList.SelectedIndex = 0;
        TabSearchEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TabSearchHint.Visibility = TabSearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void TabSearchBox_TextChanged(object sender, TextChangedEventArgs e) => Fill();

    void TabSearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        int n = TabSearchList.Items.Count;
        switch (e.Key)
        {
            case Key.Down:
                if (n > 0) TabSearchList.SelectedIndex = (TabSearchList.SelectedIndex + 1) % n;
                TabSearchList.ScrollIntoView(TabSearchList.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up:
                if (n > 0) TabSearchList.SelectedIndex = (TabSearchList.SelectedIndex - 1 + n) % n;
                TabSearchList.ScrollIntoView(TabSearchList.SelectedItem);
                e.Handled = true;
                break;
            case Key.Enter:
                Choose(TabSearchList.SelectedItem as MainWindow.TabSearchItem);
                e.Handled = true;
                break;
            case Key.Escape:
                SafeClose();
                e.Handled = true;
                break;
        }
    }

    void TabSearchList_Click(object sender, MouseButtonEventArgs e)
    {
        var d = e.OriginalSource as DependencyObject;
        while (d != null && d is not ListBoxItem) d = VisualTreeHelper.GetParent(d);
        if (d is ListBoxItem { DataContext: MainWindow.TabSearchItem item }) Choose(item);
    }

    void Choose(MainWindow.TabSearchItem? item)
    {
        _closing = true;
        Close();
        _go(item);
    }
}
