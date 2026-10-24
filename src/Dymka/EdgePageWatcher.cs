using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Dymka;

/// <summary>Следит за окнами, которые движок может открыть сам, минуя «Дымку», со служебными страницами Edge
/// (например, шестерёнка в мини-окне с видео открывает edge://settings). Такое окно сразу закрывается,
/// а вместо него открываются настройки «Дымки».</summary>
static class EdgePageWatcher
{
    delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);

    const uint EVENT_OBJECT_SHOW = 0x8002, EVENT_OBJECT_NAMECHANGE = 0x800C, WINEVENT_OUTOFCONTEXT = 0, GA_ROOT = 2, WM_CLOSE = 0x0010;

    static WinEventProc? _proc;   // держим ссылку, иначе сборщик мусора уберёт делегат
    static IntPtr _hook;
    static Action<string>? _onPage;
    static DateTime _lastHandled;

    public static void Start(Action<string> onPage)
    {
        if (_hook != IntPtr.Zero) return;
        _onPage = onPage;
        _proc = OnEvent;
        _hook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_NAMECHANGE, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT);
    }

    public static void Stop()
    {
        if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
    }

    static void OnEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            if (idObject != 0 || hwnd == IntPtr.Zero || GetAncestor(hwnd, GA_ROOT) != hwnd) return;
            var sb = new StringBuilder(256);
            if (GetWindowText(hwnd, sb, sb.Capacity) <= 0) return;
            var title = sb.ToString();
            if (!title.StartsWith("edge://", StringComparison.OrdinalIgnoreCase)) return;
            GetWindowThreadProcessId(hwnd, out var pid);
            string name;
            try { using var p = Process.GetProcessById((int)pid); name = p.ProcessName; } catch { return; }
            if (!name.StartsWith("msedgewebview2", StringComparison.OrdinalIgnoreCase)) return;
            PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            if (DateTime.Now - _lastHandled < TimeSpan.FromSeconds(2)) return;
            _lastHandled = DateTime.Now;
            App.Log("Закрыто окно служебной страницы движка: " + title);
            _onPage?.Invoke(title);
        }
        catch { }
    }
}
