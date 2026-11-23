using System.Runtime.InteropServices;

namespace PowerModeSwitcher.Native;

/// <summary>
/// P/Invoke declarations. Only PowerModeService should call the power functions.
/// </summary>
internal static class NativeMethods
{
    private const string PowrProf = "powrprof.dll";
    private const string Kernel32 = "kernel32.dll";

    internal const uint ERROR_SUCCESS = 0;

    // ---- Power Mode (overlay schemes) ----

    /// <summary>
    /// Undocumented. Sets the active power overlay scheme, which is what the
    /// Windows 11 "Power mode" setting controls. Applies to the current power
    /// source (plugged in or on battery). Returns 0 on success, otherwise a Win32 error code.
    /// </summary>
    [DllImport(PowrProf, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern uint PowerSetActiveOverlayScheme(Guid overlaySchemeGuid);

    /// <summary>
    /// Undocumented. Reads the overlay scheme that is actually in effect right now.
    /// Returns 0 on success, otherwise a Win32 error code.
    /// </summary>
    [DllImport(PowrProf, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern uint PowerGetActualOverlayScheme(out Guid actualOverlayGuid);

    // ---- Effective power mode notifications (documented, Windows 10 1809+) ----

    internal const uint EFFECTIVE_POWER_MODE_V2 = 2;

    /// <summary>Windows calls this on a thread pool thread when the effective power mode changes.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void EffectivePowerModeCallback(int effectivePowerMode, IntPtr context);

    /// <summary>Returns an HRESULT (0 on success).</summary>
    [DllImport(PowrProf, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int PowerRegisterForEffectivePowerModeNotifications(
        uint version, EffectivePowerModeCallback callback, IntPtr context, out IntPtr registrationHandle);

    /// <summary>Returns an HRESULT (0 on success).</summary>
    [DllImport(PowrProf, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int PowerUnregisterFromEffectivePowerModeNotifications(IntPtr registrationHandle);

    // ---- Energy Saver notifications (documented, Windows 11 24H2+) ----

    /// <summary>
    /// GUID_ENERGY_SAVER_STATUS from the Windows SDK. It looks like a sample GUID, but it is the
    /// real value (confirmed on Windows 11 25H2: it reports 1 when Energy Saver is on, 0 when off).
    /// </summary>
    internal static readonly Guid GUID_ENERGY_SAVER_STATUS = new("550E8400-E29B-41D4-A716-446655440000");

    internal const uint DEVICE_NOTIFY_CALLBACK = 2;
    internal const uint PBT_POWERSETTINGCHANGE = 0x8013;

    // POWERBROADCAST_SETTING layout: GUID PowerSetting (16 bytes), DWORD DataLength, then the data.
    internal const int POWERBROADCAST_SETTING_LENGTH_OFFSET = 16;
    internal const int POWERBROADCAST_SETTING_DATA_OFFSET = 20;

    /// <summary>Windows calls this on its own thread when a registered power setting changes.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate uint DeviceNotifyCallback(IntPtr context, uint type, IntPtr setting);

    [StructLayout(LayoutKind.Sequential)]
    internal struct DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS
    {
        public DeviceNotifyCallback Callback;
        public IntPtr Context;
    }

    /// <summary>Returns 0 on success, otherwise a Win32 error code.</summary>
    [DllImport(PowrProf, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern uint PowerSettingRegisterNotification(
        ref Guid settingGuid, uint flags, ref DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS recipient, out IntPtr registrationHandle);

    [DllImport(PowrProf, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern uint PowerSettingUnregisterNotification(IntPtr registrationHandle);

    // ---- Power source and (older) Battery Saver ----

    [StructLayout(LayoutKind.Sequential)]
    internal struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;      // 0 = on battery, 1 = plugged in, 255 = unknown
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;  // 1 = Battery Saver is on
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport(Kernel32, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    // ---- Windows, z-order and monitors (used by DesktopZOrder and ScreenPlacement) ----

    private const string User32 = "user32.dll";

    internal const int GWL_EXSTYLE = -20;
    internal const long WS_EX_TOOLWINDOW = 0x00000080;   // not in Alt+Tab
    internal const long WS_EX_APPWINDOW = 0x00040000;    // forces a taskbar button (we remove it)
    internal const long WS_EX_NOACTIVATE = 0x08000000;   // clicking does not take focus

    internal static readonly IntPtr HWND_BOTTOM = new(1);

    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_NOACTIVATE = 0x0010;

    internal const int WM_MOUSEACTIVATE = 0x0021;
    internal const int WM_WINDOWPOSCHANGING = 0x0046;
    internal const int MA_NOACTIVATE = 3;

    internal const uint MONITOR_DEFAULTTONULL = 0;
    internal const uint MONITOR_DEFAULTTOPRIMARY = 1;
    internal const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WINDOWPOS
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x;
        public int y;
        public int cx;
        public int cy;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport(User32, EntryPoint = "GetWindowLongPtrW", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport(User32, EntryPoint = "SetWindowLongPtrW", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport(User32, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport(User32, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport(User32, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out POINT point);

    [DllImport(User32, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport(User32, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern IntPtr MonitorFromPoint(POINT point, uint flags);

    [DllImport(User32, EntryPoint = "GetMonitorInfoW", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport(User32, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern uint GetDpiForWindow(IntPtr hWnd);

    // ---- Rounded corners for the menu (Windows 11; ignored on Windows 10) ----

    internal const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    internal const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
