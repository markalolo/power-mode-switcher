using PowerModeSwitcher.Native;

namespace PowerModeSwitcher.Services;

/// <summary>
/// Moves the widget and keeps it on a screen. Works in physical screen pixels so positions
/// stay correct across monitors with different display scaling.
/// </summary>
internal static class ScreenPlacement
{
    // Gap from the screen edges for the default bottom-right spot, at 100% scaling.
    private const int EdgeMargin = 24;

    public static (int X, int Y) GetWindowPosition(IntPtr hwnd)
    {
        NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT rect);
        return (rect.Left, rect.Top);
    }

    public static (int X, int Y) GetCursorPosition()
    {
        NativeMethods.GetCursorPos(out NativeMethods.POINT point);
        return (point.X, point.Y);
    }

    public static void MoveWindow(IntPtr hwnd, int x, int y) =>
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

    /// <summary>
    /// Puts the window at the saved spot if that spot is on a connected screen (nudged so the
    /// whole widget is visible). Otherwise puts it at the bottom-right of the primary screen.
    /// </summary>
    public static void PlaceOnScreen(IntPtr hwnd, int? savedLeft, int? savedTop)
    {
        NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT rect);
        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;

        if (savedLeft is int left && savedTop is int top)
        {
            var center = new NativeMethods.POINT { X = left + width / 2, Y = top + height / 2 };
            IntPtr monitor = NativeMethods.MonitorFromPoint(center, NativeMethods.MONITOR_DEFAULTTONULL);
            if (monitor != IntPtr.Zero && TryGetWorkArea(monitor, out NativeMethods.RECT work))
            {
                left = Math.Clamp(left, work.Left, Math.Max(work.Left, work.Right - width));
                top = Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - height));
                MoveWindow(hwnd, left, top);
                return;
            }
        }

        IntPtr primary = NativeMethods.MonitorFromPoint(default, NativeMethods.MONITOR_DEFAULTTOPRIMARY);
        if (!TryGetWorkArea(primary, out NativeMethods.RECT primaryWork)) return;

        uint dpi = NativeMethods.GetDpiForWindow(hwnd);
        int margin = (int)(EdgeMargin * (dpi == 0 ? 96 : dpi) / 96);
        MoveWindow(hwnd, primaryWork.Right - width - margin, primaryWork.Bottom - height - margin);
    }

    /// <summary>
    /// Where the window's top-left corner should go when it changes to a new size (in DIPs), so that
    /// the corner nearest the screen edge stays put. A widget in the bottom-right quarter of the
    /// screen keeps its bottom-right corner; one in the top-left quarter keeps its top-left; and so on.
    /// </summary>
    public static (int X, int Y) PositionKeepingNearestCorner(IntPtr hwnd, double newWidthDips, double newHeightDips)
    {
        NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT rect);
        uint dpi = NativeMethods.GetDpiForWindow(hwnd);
        double scale = (dpi == 0 ? 96 : dpi) / 96.0;
        int newWidth = (int)Math.Round(newWidthDips * scale);
        int newHeight = (int)Math.Round(newHeightDips * scale);

        var center = new NativeMethods.POINT { X = (rect.Left + rect.Right) / 2, Y = (rect.Top + rect.Bottom) / 2 };
        IntPtr monitor = NativeMethods.MonitorFromPoint(center, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (!TryGetWorkArea(monitor, out NativeMethods.RECT work)) return (rect.Left, rect.Top);

        bool nearRight = center.X > (work.Left + work.Right) / 2;
        bool nearBottom = center.Y > (work.Top + work.Bottom) / 2;
        return (nearRight ? rect.Right - newWidth : rect.Left,
                nearBottom ? rect.Bottom - newHeight : rect.Top);
    }

    /// <summary>If the window is no longer on a screen (for example a monitor was unplugged), bring it back.</summary>
    public static void EnsureOnScreen(IntPtr hwnd)
    {
        (int x, int y) = GetWindowPosition(hwnd);
        PlaceOnScreen(hwnd, x, y);
    }

    private static bool TryGetWorkArea(IntPtr monitor, out NativeMethods.RECT workArea)
    {
        var info = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        bool ok = NativeMethods.GetMonitorInfo(monitor, ref info);
        workArea = info.rcWork;
        return ok;
    }
}
