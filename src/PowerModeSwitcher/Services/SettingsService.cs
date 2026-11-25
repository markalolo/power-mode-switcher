using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace PowerModeSwitcher.Services;

/// <summary>Everything saved in settings.json.</summary>
public sealed class AppSettings
{
    /// <summary>Widget's left edge in screen pixels. Null until the widget has been moved.</summary>
    public int? WindowLeft { get; set; }

    /// <summary>Widget's top edge in screen pixels. Null until the widget has been moved.</summary>
    public int? WindowTop { get; set; }

    public bool PositionLocked { get; set; }

    public bool WidgetVisible { get; set; } = true;

    /// <summary>Show "Performance", "Balanced" and "Battery" next to the track.</summary>
    public bool ShowModeNames { get; set; } = true;

    /// <summary>Draw the whole widget at half size.</summary>
    public bool HalfSize { get; set; }

    public bool StartWithWindows { get; set; } = true;
}

/// <summary>
/// Loads and saves %APPDATA%\PowerModeSwitcher\settings.json, using the source-generated
/// serializer in AppJsonContext. Fields missing from an older file keep their defaults above.
/// </summary>
public sealed class SettingsService
{
    public SettingsService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PowerModeSwitcher",
            "settings.json"))
    {
    }

    public SettingsService(string filePath)
    {
        FilePath = filePath;
    }

    public string FilePath { get; }

    public AppSettings Settings { get; private set; } = new();

    /// <summary>Loads settings. A missing or damaged file just means defaults.</summary>
    public void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                Settings = JsonSerializer.Deserialize(File.ReadAllText(FilePath), AppJsonContext.Default.AppSettings) ?? new AppSettings();
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine($"Could not read settings, using defaults: {ex.Message}");
        }

        Settings = new AppSettings();
    }

    /// <summary>Saves settings. Failing to save is not fatal, so errors are ignored.</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

            // Write to a temp file first so a crash mid-write can't leave a half-written file.
            string tempPath = FilePath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(Settings, AppJsonContext.Default.AppSettings));
            File.Move(tempPath, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Could not save settings: {ex.Message}");
        }
    }
}
