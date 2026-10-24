using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Dymka;

/// <summary>Перетаскивание вкладок мышью - меняет их порядок (в полосе сверху и в боковой панели).</summary>
public partial class MainWindow
{
    BrowserTab? _dragTab;
    ItemsControl? _dragHost;
    Point _dragStart;
    bool _dragging, _dragHooked;

    /// <summary>Вызывается из Tab_MouseDown: запоминаем, откуда может начаться перетаскивание.</summary>
    void BeginTabDrag(BrowserTab tab, object sender, MouseButtonEventArgs e)
    {
        if (!_dragHooked)
        {
            _dragHooked = true;
            PreviewMouseMove += OnTabDragMove;
            PreviewMouseLeftButtonUp += (_, _) => EndTabDrag();
            LostMouseCapture += (_, _) => { if (_dragging && Mouse.Captured == null) EndTabDrag(); };
        }
        _dragTab = tab;
        _dragHost = FindAncestor<ItemsControl>(sender as DependencyObject);
        _dragStart = e.GetPosition(this);
        _dragging = false;
    }

    void OnTabDragMove(object sender, MouseEventArgs e)
    {
        if (_dragTab == null || _dragHost == null) return;
        if (e.LeftButton != MouseButtonState.Pressed) { EndTabDrag(); return; }
        var pos = e.GetPosition(this);
        if (!_dragging)
        {
            if (Math.Abs(pos.X - _dragStart.X) < 6 && Math.Abs(pos.Y - _dragStart.Y) < 6) return;
            _dragging = true;
            Mouse.Capture(_dragHost, CaptureMode.SubTree);
            Mouse.OverrideCursor = Cursors.SizeAll;
        }
        var target = TabAt(_dragHost, e.GetPosition(_dragHost));
        if (target != null && target != _dragTab) MoveTabTo(_dragTab, target);
    }

    void EndTabDrag()
    {
        if (_dragging)
        {
            _dragging = false;
            Mouse.OverrideCursor = null;
            if (Mouse.Captured == _dragHost) Mouse.Capture(null);
            if (_dragTab?.IsPinned == true) SavePinned();
            ScheduleSessionSave();
        }
        _dragTab = null;
        _dragHost = null;
    }

    static BrowserTab? TabAt(ItemsControl host, Point p)
    {
        if (p.X < 0 || p.Y < 0 || p.X > host.ActualWidth || p.Y > host.ActualHeight) return null;
        var hit = host.InputHitTest(p) as DependencyObject;
        while (hit != null && hit != host)
        {
            if (hit is FrameworkElement fe && fe.DataContext is BrowserTab t) return t;
            hit = VisualTreeHelper.GetParent(hit);
        }
        return null;
    }

    /// <summary>Ставит вкладку на место другой (в пределах одного списка: закреплённые с закреплёнными).</summary>
    public void MoveTabTo(BrowserTab tab, BrowserTab target)
    {
        if (tab == target || tab.IsPinned != target.IsPinned || tab.Container != target.Container) return;
        int from = _tabs.IndexOf(tab), to = _tabs.IndexOf(target);
        if (from < 0 || to < 0) return;
        _tabs.Move(from, to);
    }

    static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T) d = VisualTreeHelper.GetParent(d);
        return d as T;
    }
}
