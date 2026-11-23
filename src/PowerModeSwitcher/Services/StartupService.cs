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
    private const string ValueName = "PowerModeSwitcher";
    private const string ExeName = "PowerModeSwitcher.exe";

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

            string? exePath = Environment.ProcessPath;
            if (exePath is null || !string.Equals(Path.GetFileName(exePath), ExeName, StringComparison.OrdinalIgnoreCase))
            {
                error = "Can't find PowerModeSwitcher.exe to start with Windows.";
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
