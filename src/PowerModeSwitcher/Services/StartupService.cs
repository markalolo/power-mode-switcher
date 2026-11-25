using System.IO;
using System.Security;
using Microsoft.Win32;

namespace PowerModeSwitcher.Services;

/// <summary>
/// "Start with Windows" through the per-user Run registry key (no administrator rights needed).
/// </summary>
internal static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    // One entry whatever the .exe is called (PowerModeSwitcher.exe, PowerModeSwitcher-x64.exe,
    // PowerModeSwitcher-x64-standalone.exe, ...), so switching files replaces it instead of adding one.
    private const string ValueName = "PowerModeSwitcher";

    /// <summary>True if the Run key has an entry for this app.</summary>
    public static bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Turns autostart on or off. When on, writes the path of the .exe that is running now,
    /// so if the .exe has been moved, the entry is fixed the next time it starts.
    /// </summary>
    public static bool TryApply(bool enabled, out string? error)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                error = null;
                return true;
            }

            // The app's own .exe, whatever its file name. Running as "dotnet PowerModeSwitcher.dll"
            // reports dotnet.exe, which must never be registered.
            string? exePath = Environment.ProcessPath;
            if (exePath is null
                || !string.Equals(Path.GetExtension(exePath), ".exe", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileName(exePath), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
            {
                error = "Can't find the app's .exe to start with Windows.";
                return false;
            }

            string command = $"\"{exePath}\"";
            if (key.GetValue(ValueName) as string != command) key.SetValue(ValueName, command);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            error = ex.Message;
            return false;
        }
    }
}
