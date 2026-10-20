using System.Runtime.InteropServices;
using DropFlow.Hooks;

namespace DropFlow.Spike;

/// <summary>Win32 helpers for placing the wheel under the cursor on the right monitor at the right pixel size.</summary>
internal static class WindowNative
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_NOACTIVATE = 0x08000000;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int MDT_EFFECTIVE_DPI = 0;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_HIDEWINDOW = 0x0080;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private static readonly nint HWND_TOPMOST = -1;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(Point pt, uint dwFlags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hWnd, out Rect lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    /// <summary>Makes the window a tool window that never takes focus, so showing it cannot cancel the drag.</summary>
    public static void MakeNonActivating(nint hwnd)
    {
        var style = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (nint)((long)style | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
    }

    public static uint DpiAt(ScreenPoint p)
    {
        var monitor = MonitorFromPoint(new Point { X = p.X, Y = p.Y }, MONITOR_DEFAULTTONEAREST);
        return GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 ? dpiX : 96;
    }

    /// <summary>Centres a window of <paramref name="dips"/> device-independent pixels on <paramref name="p"/> (physical pixels).</summary>
    public static (int X, int Y, int Size, uint Dpi) ShowTopmostCentered(nint hwnd, ScreenPoint p, double dips)
    {
        var dpi = DpiAt(p);
        var size = (int)Math.Round(dips * dpi / 96.0);
        var x = p.X - size / 2;
        var y = p.Y - size / 2;
        SetWindowPos(hwnd, HWND_TOPMOST, x, y, size, size, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        return (x, y, size, dpi);
    }

    public static void HideNoActivate(nint hwnd) =>
        SetWindowPos(hwnd, 0, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_HIDEWINDOW);

    public static string Describe(nint hwnd) =>
        GetWindowRect(hwnd, out var r) ? $"{r.Left},{r.Top} {r.Right - r.Left}x{r.Bottom - r.Top}" : "?";
}
