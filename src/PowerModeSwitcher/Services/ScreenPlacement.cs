using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;
using Point = System.Drawing.Point;

namespace PowerModeSwitcher.Services;

/// <summary>
/// Moves the widget and keeps it on a screen. Works in physical screen pixels so positions
/// stay correct across monitors with different display scaling.
/// </summary>
internal static class ScreenPlacement
{
    // Gap from the screen edges for the default bottom-right spot, at 100% scaling.
    private const int EdgeMargin = 24;

    public static (int X, int Y) GetWindowPosition(nint hwnd)
    {
        PInvoke.GetWindowRect(new HWND(hwnd), out RECT rect);
        return (rect.left, rect.top);
    }

    public static (int X, int Y) GetCursorPosition()
    {
        PInvoke.GetCursorPos(out Point point);
        return (point.X, point.Y);
    }

    public static void MoveWindow(nint hwnd, int x, int y) =>
        PInvoke.SetWindowPos(new HWND(hwnd), default, x, y, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);

    /// <summary>
    /// Puts the window at the saved spot if that spot is on a connected screen (nudged so the
    /// whole widget is visible). Otherwise puts it at the bottom-right of the primary screen.
    /// </summary>
    public static void PlaceOnScreen(nint hwnd, int? savedLeft, int? savedTop)
    {
        PInvoke.GetWindowRect(new HWND(hwnd), out RECT rect);
        int width = rect.right - rect.left;
        int height = rect.bottom - rect.top;

        if (savedLeft is int left && savedTop is int top)
        {
            var center = new Point(left + width / 2, top + height / 2);
            HMONITOR monitor = PInvoke.MonitorFromPoint(center, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONULL);
            if (!monitor.IsNull && TryGetWorkArea(monitor, out RECT work))
            {
                left = Math.Clamp(left, work.left, Math.Max(work.left, work.right - width));
                top = Math.Clamp(top, work.top, Math.Max(work.top, work.bottom - height));
                MoveWindow(hwnd, left, top);
                return;
            }
        }

        HMONITOR primary = PInvoke.MonitorFromPoint(default, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY);
        if (!TryGetWorkArea(primary, out RECT primaryWork)) return;

        int margin = (int)(EdgeMargin * DpiOf(hwnd) / 96);
        MoveWindow(hwnd, primaryWork.right - width - margin, primaryWork.bottom - height - margin);
    }

    /// <summary>
    /// Where the window's top-left corner should go when it changes to a new size (in DIPs), so that
    /// the corner nearest the screen edge stays put. A widget in the bottom-right quarter of the
    /// screen keeps its bottom-right corner; one in the top-left quarter keeps its top-left; and so on.
    /// </summary>
    public static (int X, int Y) PositionKeepingNearestCorner(nint hwnd, double newWidthDips, double newHeightDips)
    {
        PInvoke.GetWindowRect(new HWND(hwnd), out RECT rect);
        double scale = DpiOf(hwnd) / 96.0;
        int newWidth = (int)Math.Round(newWidthDips * scale);
        int newHeight = (int)Math.Round(newHeightDips * scale);

        var center = new Point((rect.left + rect.right) / 2, (rect.top + rect.bottom) / 2);
        HMONITOR monitor = PInvoke.MonitorFromPoint(center, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        if (!TryGetWorkArea(monitor, out RECT work)) return (rect.left, rect.top);

        bool nearRight = center.X > (work.left + work.right) / 2;
        bool nearBottom = center.Y > (work.top + work.bottom) / 2;
        return (nearRight ? rect.right - newWidth : rect.left,
                nearBottom ? rect.bottom - newHeight : rect.top);
    }

    /// <summary>If the window is no longer on a screen (for example a monitor was unplugged), bring it back.</summary>
    public static void EnsureOnScreen(nint hwnd)
    {
        (int x, int y) = GetWindowPosition(hwnd);
        PlaceOnScreen(hwnd, x, y);
    }

    private static uint DpiOf(nint hwnd)
    {
        // GetDpiForWindow needs Windows 10 1607 (build 14393); assume 100% scaling before that.
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 14393)) return 96;

        uint dpi = PInvoke.GetDpiForWindow(new HWND(hwnd));
        return dpi == 0 ? 96 : dpi;
    }

    private static unsafe bool TryGetWorkArea(HMONITOR monitor, out RECT workArea)
    {
        var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        bool ok = PInvoke.GetMonitorInfo(monitor, ref info);
        workArea = info.rcWork;
        return ok;
    }
}
