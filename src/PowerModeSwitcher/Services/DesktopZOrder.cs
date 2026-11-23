using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using PowerModeSwitcher.Native;

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
        IntPtr hwnd = new WindowInteropHelper(window).Handle;

        // Tool window: not in Alt+Tab. No-activate: clicking it leaves focus where it was.
        long exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        exStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
        exStyle &= ~NativeMethods.WS_EX_APPWINDOW;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));

        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        SendToBottom(hwnd);
    }

    /// <summary>Puts the window at the bottom of the z-order (just above the desktop).</summary>
    public static void SendToBottom(IntPtr hwnd) =>
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_BOTTOM, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

    /// <summary>
    /// Lets a right-click menu opened from the window take focus, so the menu closes
    /// when you click somewhere else. The window itself still stays at the bottom.
    /// </summary>
    public static void AllowMenuFocus(Window window) =>
        NativeMethods.SetForegroundWindow(new WindowInteropHelper(window).Handle);

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case NativeMethods.WM_MOUSEACTIVATE:
                // Clicking the widget must not activate it.
                handled = true;
                return new IntPtr(NativeMethods.MA_NOACTIVATE);

            case NativeMethods.WM_WINDOWPOSCHANGING:
                // Whatever tries to change our place in the z-order, send us to the bottom instead.
                var pos = Marshal.PtrToStructure<NativeMethods.WINDOWPOS>(lParam);
                if ((pos.flags & NativeMethods.SWP_NOZORDER) == 0 && pos.hwndInsertAfter != NativeMethods.HWND_BOTTOM)
                {
                    pos.hwndInsertAfter = NativeMethods.HWND_BOTTOM;
                    Marshal.StructureToPtr(pos, lParam, fDeleteOld: false);
                }
                break;
        }

        return IntPtr.Zero;
    }
}
