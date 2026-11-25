using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Microsoft.Win32;
using Windows.Win32;

namespace PowerModeSwitcher.Services;

/// <summary>
/// Builds the plain-text report for "Copy diagnostics" (PLAN.md Phase 8a). It only reads, and contains
/// no user names, serial numbers or machine names: just Windows, device model and power details.
/// </summary>
internal static class DiagnosticsService
{
    private static readonly string[] EffectivePowerModeNames =
    {
        "Battery Saver", "Better Battery", "Balanced", "High Performance", "Max Performance", "Game Mode", "Mixed Reality",
    };

    public static string Build(PowerModeService power, ModeSetService modeSets, SettingsService settings)
    {
        PowerModeReading reading = power.Current;
        PowerDetails details = power.CollectDetails();
        ModeSet set = modeSets.Current;
        AppSettings s = settings.Settings;

        var text = new StringBuilder();
        text.AppendLine("Power Mode Switcher diagnostics");
        text.AppendLine($"Created: {DateTime.Now:yyyy-MM-dd HH:mm}");
        text.AppendLine();

        text.AppendLine($"App:      version {AppVersion()}, {RuntimeInformation.ProcessArchitecture} process");
        text.AppendLine($"Windows:  {WindowsDescription()}, {RuntimeInformation.OSArchitecture}");
        text.AppendLine($"Device:   {DeviceDescription()}");
        text.AppendLine();

        text.AppendLine($"Power source:   {SourceText(reading.Source)}");
        text.AppendLine($"Energy Saver:   {(reading.EnergySaverOn ? "on" : "off")}");
        text.AppendLine($"Mode set:       {set.Name} ({set.Summary}), {modeSets.HowChosen}");
        text.AppendLine($"  Setting: {s.ModeSet}; detected: {s.DetectedModeSet?.ToString() ?? "none"}; question shown: {(s.ModeSetQuestionShown ? "yes" : "no")}");
        text.AppendLine($"  Last set by this app: plugged in {Guid(s.LastAppModePluggedIn)}, on battery {Guid(s.LastAppModeOnBattery)}");
        text.AppendLine($"Widget shows:   {WidgetShows(reading, set)}");
        text.AppendLine();

        text.AppendLine("Chosen power mode, plugged in:");
        text.AppendLine($"  documented API: {Mode(details.DocumentedPluggedIn)}");
        text.AppendLine($"  registry:       {Mode(details.RegistryPluggedIn)}");
        text.AppendLine("Chosen power mode, on battery:");
        text.AppendLine($"  documented API: {Mode(details.DocumentedOnBattery)}");
        text.AppendLine($"  registry:       {Mode(details.RegistryOnBattery)}");
        text.AppendLine($"Actual overlay:     {Mode(details.ActualOverlay)}");
        text.AppendLine($"Effective overlay:  {Mode(details.EffectiveOverlay)}");
        text.AppendLine($"Effective power mode: {EffectivePowerMode(details.EffectivePowerMode)}");
        text.AppendLine();

        text.AppendLine($"Active power plan:  {Guid(details.ActivePlan)} {details.ActivePlanName}");
        text.AppendLine($"Plan personality:   {Guid(details.PlanPersonality)} {PersonalityName(details.PlanPersonality)}"
            + (reading.BalancedPlan ? "" : "  <- Power Mode only works on Balanced or a plan based on it"));
        text.AppendLine("All power plans:");
        foreach (PowerPlan plan in details.Plans) text.AppendLine($"  {plan.Guid} {plan.Name}");
        text.AppendLine($@"Subkeys under HKLM\{PowerModeService.PowerSchemesKeyPath}:");
        foreach (PowerSchemeEntry entry in details.PowerSchemesSubkeys) text.AppendLine($"  {entry.Key} {entry.FriendlyName}");

        return text.ToString();
    }

    private static string WidgetShows(PowerModeReading reading, ModeSet set)
    {
        if (reading.Error is not null) return $"error: {reading.Error}";
        if (reading.Mode is not Guid mode) return "nothing (mode unreadable)";
        return set.StopOf(mode) is Stop stop
            ? $"{set[stop].Label} ({stop.ToString().ToLowerInvariant()} stop)"
            : $"{set.LabelFor(mode)} (no stop in {set.Name}: neutral state)";
    }

    private static string Mode(Guid? mode) =>
        mode is Guid known ? $"{known} ({KnownPowerModes.InternalName(known)})" : "not available";

    private static string Guid(Guid? value) => value?.ToString() ?? "none";

    private static string SourceText(PowerSource source) => source switch
    {
        PowerSource.PluggedIn => "plugged in",
        PowerSource.OnBattery => "on battery",
        _ => "unknown",
    };

    private static string EffectivePowerMode(int value) =>
        value < 0 ? "not reported yet"
        : value < EffectivePowerModeNames.Length ? $"{value} ({EffectivePowerModeNames[value]})"
        : value.ToString();

    private static string PersonalityName(Guid? personality) =>
        personality == PInvoke.GUID_TYPICAL_POWER_SAVINGS ? "(Balanced)"
        : personality == PInvoke.GUID_MIN_POWER_SAVINGS ? "(High performance)"
        : personality == PInvoke.GUID_MAX_POWER_SAVINGS ? "(Power saver)"
        : personality is null ? "(not reported yet)" : "(unknown)";

    private static string AppVersion() =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "unknown";

    /// <summary>
    /// For example "Windows 11 Pro, version 25H2 (build 26200.9448)". Windows 11 still says
    /// "Windows 10" in ProductName, so the build number decides which it is.
    /// </summary>
    private static string WindowsDescription()
    {
        int build = Environment.OSVersion.Version.Build;
        string product = ReadString(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName") ?? "Windows";
        if (build >= 22000) product = product.Replace("Windows 10", "Windows 11");
        string? version = ReadString(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion");
        object? ubr = ReadValue(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "UBR");
        return $"{product}{(version is null ? "" : $", version {version}")} (build {build}{(ubr is int u ? $".{u}" : "")})";
    }

    /// <summary>Manufacturer and model only (never the serial number).</summary>
    private static string DeviceDescription()
    {
        string? maker = ReadString(@"HARDWARE\DESCRIPTION\System\BIOS", "SystemManufacturer");
        string? model = ReadString(@"HARDWARE\DESCRIPTION\System\BIOS", "SystemProductName");
        return maker is null && model is null ? "unknown" : $"{maker} {model}".Trim();
    }

    private static string? ReadString(string keyPath, string name) => ReadValue(keyPath, name) as string;

    private static object? ReadValue(string keyPath, string name)
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(keyPath);
            return key?.GetValue(name);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
