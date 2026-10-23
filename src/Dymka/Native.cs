using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Dymka;

static class Native
{
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);
    [DllImport("user32.dll")] static extern short GetKeyState(int key);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("psapi.dll", SetLastError = true)] static extern bool GetProcessMemoryInfo(IntPtr process, ref PROCESS_MEMORY_COUNTERS_EX2 counters, int size);

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_MEMORY_COUNTERS_EX2
    {
        public int cb;
        public int PageFaultCount;
        public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
            QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage,
            PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }

    /// <summary>Частный рабочий набор процесса - то же число, что показывает диспетчер задач в столбце «Память».</summary>
    public static long PrivateWorkingSet(int pid)
    {
        var h = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (h == IntPtr.Zero) return -1;
        try
        {
            var c = new PROCESS_MEMORY_COUNTERS_EX2 { cb = Marshal.SizeOf<PROCESS_MEMORY_COUNTERS_EX2>() };
            if (!GetProcessMemoryInfo(h, ref c, c.cb)) return -1;
            long v = (long)c.PrivateWorkingSetSize.ToUInt64();
            return v > 0 ? v : (long)c.WorkingSetSize.ToUInt64();
        }
        finally { CloseHandle(h); }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    const int SRCCOPY = 0x00CC0020;
    const int CAPTUREBLT = 0x40000000;

    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, int flags, UIntPtr extra);

    /// <summary>Нажать сочетание Ctrl+клавиша (для самопроверки горячих клавиш).</summary>
    public static void PressCtrl(byte vk)
    {
        keybd_event(0x11, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, 2, UIntPtr.Zero);
        keybd_event(0x11, 0, 2, UIntPtr.Zero);
    }

    /// <summary>Нажать Ctrl+Shift+клавиша.</summary>
    public static void PressCtrlShift(byte vk)
    {
        keybd_event(0x11, 0, 0, UIntPtr.Zero);
        keybd_event(0x10, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, 2, UIntPtr.Zero);
        keybd_event(0x10, 0, 2, UIntPtr.Zero);
        keybd_event(0x11, 0, 2, UIntPtr.Zero);
    }

    /// <summary>Набрать латинские буквы и цифры, как с клавиатуры (для самопроверки).</summary>
    public static void TypeLatin(string text)
    {
        foreach (var ch in text.ToUpperInvariant())
        {
            byte vk = ch == ' ' ? (byte)0x20 : (byte)ch;
            keybd_event(vk, 0, 0, UIntPtr.Zero);
            keybd_event(vk, 0, 2, UIntPtr.Zero);
            Thread.Sleep(30);
        }
    }

    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern void mouse_event(int flags, int dx, int dy, int data, UIntPtr extra);

    /// <summary>Настоящий щелчок мышью по середине элемента (для самопроверки).</summary>
    public static void ClickCenter(FrameworkElement el)
    {
        var p = el.PointToScreen(new Point(el.ActualWidth / 2, el.ActualHeight / 2));
        SetCursorPos((int)p.X, (int)p.Y);
        Thread.Sleep(80);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); // левая кнопка вниз
        Thread.Sleep(60);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); // вверх
    }

    /// <summary>Настоящий щелчок мышью в точке экрана (в физических пикселях).</summary>
    public static void ClickAt(Point screen)
    {
        SetCursorPos((int)screen.X, (int)screen.Y);
        Thread.Sleep(80);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(60);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
    }

    public static bool IsCtrlDown() => GetKeyState(0x11) < 0;
    public static bool IsShiftDown() => GetKeyState(0x10) < 0;

    /// <summary>Скругление углов окна в Windows 11 (в Windows 10 вызов просто ничего не делает).</summary>
    public static void RoundCorners(Window w)
    {
        try
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            int pref = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, 33, ref pref, sizeof(int));
        }
        catch { }
    }

    /// <summary>Насколько развёрнутое окно с WindowChrome вылезает за край экрана (в единицах WPF).</summary>
    public static Thickness MaximizedOverhang(Window w)
    {
        double scale = VisualTreeHelper.GetDpi(w).DpiScaleX;
        int frame = GetSystemMetrics(32) + GetSystemMetrics(92); // SM_CXSIZEFRAME + SM_CXPADDEDBORDER
        double t = frame / (scale <= 0 ? 1 : scale);
        if (t <= 0 || t > 20) t = 7;
        return new Thickness(t);
    }

    /// <summary>Границы монитора, на котором окно (в единицах WPF).</summary>
    public static Rect MonitorBounds(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        var mon = MonitorFromWindow(hwnd, 2);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (mon == IntPtr.Zero || !GetMonitorInfo(mon, ref info))
            return new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
        var dpi = VisualTreeHelper.GetDpi(w);
        return new Rect(info.rcMonitor.Left / dpi.DpiScaleX, info.rcMonitor.Top / dpi.DpiScaleY,
            (info.rcMonitor.Right - info.rcMonitor.Left) / dpi.DpiScaleX,
            (info.rcMonitor.Bottom - info.rcMonitor.Top) / dpi.DpiScaleY);
    }

    /// <summary>Снимок области экрана, занятой окном (вместе с содержимым WebView2 и всплывающими панелями).</summary>
    public static BitmapSource? CaptureWindow(Window w, int extraBottom = 0)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (!GetWindowRect(hwnd, out var r)) return null;
        int width = r.Right - r.Left, height = r.Bottom - r.Top + extraBottom;
        if (width <= 0 || height <= 0) return null;
        var screen = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        var bmp = CreateCompatibleBitmap(screen, width, height);
        var old = SelectObject(mem, bmp);
        try
        {
            BitBlt(mem, 0, 0, width, height, screen, r.Left, r.Top, SRCCOPY | CAPTUREBLT);
            var src = Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(bmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    public static void SavePng(BitmapSource src, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
