using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Dymka.Core;

namespace Dymka;

/// <summary>Меню вкладки (правый щелчок по вкладке): закрыть вкладки слева/справа (выше/ниже), остальные,
/// перенести в новое окно или другой контейнер, дублировать, закрепить, выключить звук, усыпить.</summary>
public partial class MainWindow
{
    Popup? _tabMenu;

    void Tab_RightUp(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not BrowserTab tab) return;
        e.Handled = true;
        ShowTabMenu(tab);
    }

    /// <summary>Вкладки текущего контейнера в том порядке, в каком они видны (без закреплённых).</summary>
    List<BrowserTab> VisibleTabOrder()
    {
        bool side = _settings.Layout == "side";
        var list = side
            ? _awakeView.Cast<BrowserTab>().Concat(_sleepView.Cast<BrowserTab>()).ToList()
            : _topView.Cast<BrowserTab>().ToList();
        return list;
    }

    /// <summary>Для самопроверки: вкладки по порядку, как их видно.</summary>
    public List<BrowserTab> VisibleTabOrderForTest => VisibleTabOrder();

    public void ShowTabMenu(BrowserTab tab)
    {
        ClosePopups();
        bool side = _settings.Layout == "side";
        var order = VisibleTabOrder();
        int idx = order.IndexOf(tab);
        var before = idx > 0 ? order.Take(idx).ToList() : new List<BrowserTab>();
        var after = idx >= 0 ? order.Skip(idx + 1).ToList() : new List<BrowserTab>();
        var others = order.Where(t => t != tab).ToList();

        var panel = new StackPanel();
        void Sep() => panel.Children.Add(new Border { Height = 1, Background = (Brush)FindResource("Line"), Margin = new Thickness(8, 4, 8, 4) });
        void Row(Geometry icon, string text, Action act, string? keys = null, bool enabled = true)
        {
            var dock = new DockPanel();
            if (keys != null)
            {
                var k = new TextBlock { Text = keys, Foreground = (Brush)FindResource("Muted"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
                DockPanel.SetDock(k, Dock.Right);
                dock.Children.Add(k);
            }
            var g = new Glyph { Data = icon, Size = 16, Foreground = (Brush)FindResource("Ink2"), Margin = new Thickness(0, 0, 12, 0) };
            DockPanel.SetDock(g, Dock.Left);
            dock.Children.Add(g);
            dock.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            var b = new Button { Style = (Style)FindResource("MenuRow"), Content = dock, IsEnabled = enabled };
            b.Click += (_, _) =>
            {
                CloseTabMenu();
                try { act(); } catch (Exception ex) { App.Log("Меню вкладки: " + ex.Message); }
            };
            panel.Children.Add(b);
        }
        Geometry G(string key) => (Geometry)FindResource(key);

        // заголовок - название вкладки
        panel.Children.Add(new TextBlock
        {
            Text = tab.Title, FontWeight = FontWeights.SemiBold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(10, 6, 10, 6), Foreground = (Brush)FindResource("Ink2"),
        });
        Sep();
        Row(G("IcReload"), "Обновить", () => ReloadTab(tab), "F5");
        Row(G("IcCopy"), "Дублировать вкладку", () => DuplicateTab(tab));
        Row(G("IcPin"), tab.IsPinned ? "Открепить вкладку" : "Закрепить вкладку", () => SetPinned(tab, !tab.IsPinned));
        if (tab.ShowAudio)
            Row(G(tab.IsMuted ? "IcSpeaker" : "IcSpeakerOff"), tab.IsMuted ? "Включить звук вкладки" : "Выключить звук вкладки", () => ToggleMute(tab));
        if (tab.View != null && tab != _active && !InSplit(tab))
            Row(G("IcMoon"), "Усыпить вкладку (освободить память)", () => { SleepTab(tab); RefreshViews(); UpdateMemory(); });
        Sep();
        Row(G("IcNewWindow"), "Перенести в новое окно", () => MoveTabToNewWindow(tab));
        var conts = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in AppSettings.Containers.Where(c => c.Id != tab.Container))
        {
            var id = c.Id;
            var chip = MakeChip(c.Name, BrushCache.Get(c.Color));
            chip.Click += async (_, _) => { CloseTabMenu(); await MoveToContainerAsync(tab, id); };
            conts.Children.Add(chip);
        }
        var moveRow = new DockPanel { Margin = new Thickness(10, 6, 10, 6) };
        var mg = new Glyph { Data = G("IcSwap"), Size = 16, Foreground = (Brush)FindResource("Ink2"), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(mg, Dock.Left);
        moveRow.Children.Add(mg);
        var mt = new TextBlock { Text = "Перенести в", VerticalAlignment = VerticalAlignment.Center, FontSize = 13.5, Margin = new Thickness(0, 0, 10, 0) };
        DockPanel.SetDock(mt, Dock.Left);
        moveRow.Children.Add(mt);
        moveRow.Children.Add(conts);
        panel.Children.Add(moveRow);
        Sep();
        Row(G("IcClose"), "Закрыть вкладку", () => CloseTab(tab), "Ctrl+W");
        Row(G(side ? "IcChevronDown" : "IcChevronRight"), side ? $"Закрыть вкладки ниже ({after.Count})" : $"Закрыть вкладки справа ({after.Count})",
            () => CloseTabsGroup(after, tab), enabled: after.Count > 0);
        Row(G(side ? "IcChevronUp" : "IcBack"), side ? $"Закрыть вкладки выше ({before.Count})" : $"Закрыть вкладки слева ({before.Count})",
            () => CloseTabsGroup(before, tab), enabled: before.Count > 0);
        Row(G("IcClose"), $"Закрыть другие вкладки ({others.Count})", () => CloseTabsGroup(others, tab), enabled: others.Count > 0);
        Row(G("IcUndo"), "Вернуть закрытую вкладку", ReopenClosed, "Ctrl+Shift+T", enabled: _closed.Count > 0);

        _tabMenu ??= new Popup { StaysOpen = false, AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade, Placement = PlacementMode.MousePoint };
        _tabMenu.Child = new Border { Style = (Style)FindResource("PopupCard"), Width = 330, Padding = new Thickness(6), Child = panel };
        _tabMenu.PlacementTarget = this;
        _tabMenu.IsOpen = true;
    }

    public bool TabMenuOpenForTest => _tabMenu?.IsOpen == true;

    void CloseTabMenu()
    {
        if (_tabMenu != null) _tabMenu.IsOpen = false;
    }

    void ReloadTab(BrowserTab tab)
    {
        if (tab.Core != null) { try { tab.Core.Reload(); } catch { } }
        else Activate(tab);
    }

    void DuplicateTab(BrowserTab tab)
    {
        if (string.IsNullOrEmpty(tab.Url) || tab.IsStart) { _ = NewTabAsync(null, activate: true, container: tab.Container); return; }
        if (tab != _active) Activate(tab);
        _ = NewTabAsync(tab.Url, activate: true, container: tab.Container);
    }

    public void MoveTabToNewWindow(BrowserTab tab)
    {
        if (!_tabs.Contains(tab)) return;
        OpenWindow(tab.IsStart ? null : tab.Url, tab.Container);
        CloseTab(tab, remember: false);
    }

    /// <summary>Закрывает группу вкладок; вкладка keep становится активной. Закрытые можно вернуть кнопкой в уведомлении.</summary>
    public void CloseTabsGroup(List<BrowserTab> list, BrowserTab keep)
    {
        list = list.Where(t => _tabs.Contains(t) && t != keep && !t.IsPinned).ToList();
        if (list.Count == 0) return;
        if (_tabs.Contains(keep) && _active != keep) Activate(keep);
        int remembered = 0;
        foreach (var t in list)
        {
            bool willRemember = !t.IsStart && !string.IsNullOrEmpty(t.Url);
            CloseTab(t);
            if (willRemember) remembered++;
        }
        App.Log("Закрыто вкладок из меню вкладки: " + list.Count);
        if (remembered > 0)
            ShowToast($"Закрыто вкладок: {list.Count}", "Вернуть", () =>
            {
                for (int i = 0; i < remembered && _closed.Count > 0; i++)
                {
                    var (container, url) = _closed.Pop();
                    _ = NewTabAsync(url, activate: false, container: container);
                }
                RefreshViews();
            }, seconds: 5);
    }
}
