using System.Runtime.InteropServices;

namespace PowerModeSwitcher.Native;

/// <summary>
/// The two undocumented Power Mode overlay functions in powrprof.dll.
/// <para>
/// Hand-written on purpose: Microsoft does not document these functions, so they are not in the
/// Windows metadata that CsWin32 generates every other declaration from (see NativeMethods.txt).
/// They are widely used and have been stable since Windows 10. [LibraryImport] generates the
/// marshalling code at build time; both signatures are blittable (a Guid and a DWORD).
/// </para>
/// <para>
/// On Windows 11 the app uses the documented PowerGet/SetUserConfiguredAC/DCPowerMode first (generated
/// by CsWin32). These are the fallback for older Windows, and the only way to read 3af9b8d9
/// ("Better performance"), which the documented API does not support. See docs/power-mode-guids.md.
/// </para>
/// Both return a Win32 error code (cast to WIN32_ERROR): 0 means success.
/// Only PowerModeService should call these.
/// </summary>
internal static partial class PowerOverlayApi
{
    private const string PowrProf = "powrprof.dll";

    /// <summary>
    /// Sets the active power overlay scheme, which is what the Windows 11 "Power mode" setting
    /// controls, for the current power source (plugged in or on battery).
    /// <para>
    /// The native function takes a pointer to the GUID, so it is passed by reference (<c>in</c>).
    /// Passing it by value happened to work on x64, where a 16-byte struct goes by hidden reference,
    /// but on ARM64 the GUID's bytes land in registers and are read as an address: error 87 or a crash.
    /// </para>
    /// </summary>
    [LibraryImport(PowrProf)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial uint PowerSetActiveOverlayScheme(in Guid overlaySchemeGuid);

    /// <summary>
    /// Reads the overlay scheme the user chose for the current power source. This stays the same
    /// while Energy Saver temporarily overrides it, so it is the right value for the slider.
    /// </summary>
    [LibraryImport(PowrProf)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial uint PowerGetActualOverlayScheme(out Guid actualOverlayGuid);
}
