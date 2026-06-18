using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace AutoCode.Desktop;

// Window chrome: custom min/max/close, borderless-maximize work-area clamping, Win32 interop.
public partial class MainWindow
{
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeRestore_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        var maximized = WindowState == WindowState.Maximized;
        MaxIcon.Geometry = (Geometry)FindResource(maximized ? "IconRestore" : "IconMax");
    }

    // Constrain the maximized borderless window to the monitor work area, so it does not
    // cover (and get clipped behind) the taskbar. Without this, WPF maximizes a
    // WindowStyle=None window to the full monitor + ~7px overflow on every edge.
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var source = (HwndSource)PresentationSource.FromVisual(this)!;
        source.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_GETMINMAXINFO = 0x0024;
        if (msg == WM_GETMINMAXINFO)
        {
            WmGetMinMaxInfo(hwnd, lParam);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void WmGetMinMaxInfo(IntPtr hwnd, IntPtr lParam)
    {
        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);

        const int MONITOR_DEFAULTTONEAREST = 0x00000002;
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor != IntPtr.Zero)
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                var work = info.rcWork;       // physical px (app is PerMonitorV2)
                var bounds = info.rcMonitor;  // physical px

                // Maximized rect = work area, positioned relative to the monitor origin.
                mmi.ptMaxPosition.X = work.Left - bounds.Left;
                mmi.ptMaxPosition.Y = work.Top - bounds.Top;
                mmi.ptMaxSize.X = work.Right - work.Left;
                mmi.ptMaxSize.Y = work.Bottom - work.Top;

                // handled=true suppresses WPF's own MinWidth/MinHeight enforcement, so we must
                // re-apply it here. Convert DIP minimums to physical px for THIS monitor's DPI;
                // recomputed every message so dragging across monitors of differing DPI stays correct.
                var scale = GetDpiForWindow(hwnd) / 96.0;
                mmi.ptMinTrackSize.X = (int)Math.Ceiling(MinWidth * scale);
                mmi.ptMinTrackSize.Y = (int)Math.Ceiling(MinHeight * scale);
            }
        }

        Marshal.StructureToPtr(mmi, lParam, true);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern ExecutionState SetThreadExecutionState(ExecutionState esFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}

[Flags]
internal enum ExecutionState : uint
{
    EsContinuous = 0x80000000,
    EsSystemRequired = 0x00000001,
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINT { public int X; public int Y; }

[StructLayout(LayoutKind.Sequential)]
internal struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

[StructLayout(LayoutKind.Sequential)]
internal struct MINMAXINFO
{
    public POINT ptReserved;
    public POINT ptMaxSize;
    public POINT ptMaxPosition;
    public POINT ptMinTrackSize;
    public POINT ptMaxTrackSize;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MONITORINFO
{
    public int cbSize;
    public RECT rcMonitor;
    public RECT rcWork;
    public uint dwFlags;
}
