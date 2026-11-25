using System.Windows;
using System.Windows.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace PowerModeSwitcher.Services;

/// <summary>
/// Keeps a window at desktop level: below every normal app window, never brought to the
/// front, never takes focus when clicked, and not shown in the taskbar or Alt+Tab.
/// </summary>
internal static class DesktopZOrder
{
    /// <summary>Call from OnSourceInitialized, before the window is first shown.</summary>
    public static void Attach(Window window)
    {
        var hwnd = new HWND(new WindowInteropHelper(window).Handle);

        // Tool window: not in Alt+Tab. No-activate: clicking it leaves focus where it was.
        var exStyle = (WINDOW_EX_STYLE)PInvoke.GetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        exStyle |= WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_NOACTIVATE;
        exStyle &= ~WINDOW_EX_STYLE.WS_EX_APPWINDOW;
        PInvoke.SetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (nint)exStyle);

        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        SendToBottom(hwnd);
    }

    /// <summary>Puts the window at the bottom of the z-order (just above the desktop).</summary>
    public static void SendToBottom(HWND hwnd) =>
        PInvoke.SetWindowPos(hwnd, HWND.HWND_BOTTOM, 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);

    /// <summary>
    /// Lets a right-click menu opened from the window take focus, so the menu closes
    /// when you click somewhere else. The window itself still stays at the bottom.
    /// </summary>
    public static void AllowMenuFocus(Window window) =>
        PInvoke.SetForegroundWindow(new HWND(new WindowInteropHelper(window).Handle));

    private static unsafe nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        switch ((uint)msg)
        {
            case PInvoke.WM_MOUSEACTIVATE:
                // Clicking the widget must not activate it.
                handled = true;
                return (nint)PInvoke.MA_NOACTIVATE;

            case PInvoke.WM_WINDOWPOSCHANGING:
                // Whatever tries to change our place in the z-order, send us to the bottom instead.
                var pos = (WINDOWPOS*)lParam;
                if ((pos->flags & SET_WINDOW_POS_FLAGS.SWP_NOZORDER) == 0 && pos->hwndInsertAfter != HWND.HWND_BOTTOM)
                {
                    pos->hwndInsertAfter = HWND.HWND_BOTTOM;
                }
                break;
        }

        return 0;
    }
}
