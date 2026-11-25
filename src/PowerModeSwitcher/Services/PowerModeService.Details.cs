using System.IO;
using System.Security;
using System.Text;
using Microsoft.Win32;
using PowerModeSwitcher.Native;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Power;

namespace PowerModeSwitcher.Services;

/// <summary>A power plan and its display name.</summary>
public sealed record PowerPlan(Guid Guid, string Name);

/// <summary>A subkey under the PowerSchemes registry key and its friendly name.</summary>
public sealed record PowerSchemeEntry(string Key, string? FriendlyName);

/// <summary>Raw power details for Copy diagnostics. Nulls mean "could not be read here".</summary>
public sealed record PowerDetails(
    Guid? DocumentedPluggedIn,
    Guid? DocumentedOnBattery,
    Guid? RegistryPluggedIn,
    Guid? RegistryOnBattery,
    Guid? ActualOverlay,
    Guid? EffectiveOverlay,
    int EffectivePowerMode,
    Guid? ActivePlan,
    string? ActivePlanName,
    Guid? PlanPersonality,
    IReadOnlyList<PowerPlan> Plans,
    IReadOnlyList<PowerSchemeEntry> PowerSchemesSubkeys);

// Readers used only by Copy diagnostics. Everything here only reads; nothing is ever written.
public sealed partial class PowerModeService
{
    /// <summary>Collects every power value Copy diagnostics reports. Call on the UI thread.</summary>
    public PowerDetails CollectDetails()
    {
        (Guid? registryPluggedIn, Guid? registryOnBattery) = ReadOverlayRegistry();
        Guid? activePlan = TryGetActivePlan();
        Guid? personality;
        lock (_planLock) personality = _planPersonality;

        return new PowerDetails(
            DocumentedPluggedIn: TryGetUserConfiguredMode(PowerSource.PluggedIn),
            DocumentedOnBattery: TryGetUserConfiguredMode(PowerSource.OnBattery),
            RegistryPluggedIn: registryPluggedIn,
            RegistryOnBattery: registryOnBattery,
            ActualOverlay: TryGetActualOverlay().Overlay,
            EffectiveOverlay: TryGetEffectiveOverlay(),
            EffectivePowerMode: _effectivePowerMode,
            ActivePlan: activePlan,
            ActivePlanName: activePlan is Guid plan ? ReadPlanName(plan) : null,
            PlanPersonality: personality,
            Plans: EnumeratePlans(),
            PowerSchemesSubkeys: ReadPowerSchemesSubkeys());
    }

    private static Guid? TryGetEffectiveOverlay()
    {
        try
        {
            return PowerOverlayApi.PowerGetEffectiveOverlayScheme(out Guid overlay) == (uint)WIN32_ERROR.ERROR_SUCCESS ? overlay : null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>The active power plan. Windows allocates the GUID; it is freed with LocalFree.</summary>
    private static unsafe Guid? TryGetActivePlan()
    {
        Guid* active = null;
        if (PInvoke.PowerGetActiveScheme(default, &active) != WIN32_ERROR.ERROR_SUCCESS || active is null) return null;
        try
        {
            return *active;
        }
        finally
        {
            PInvoke.LocalFree(new HLOCAL(active));
        }
    }

    /// <summary>A plan's name as Windows shows it. The first call asks for the size, the second reads it.</summary>
    private static unsafe string? ReadPlanName(Guid plan)
    {
        uint size = 0;
        if (PInvoke.PowerReadFriendlyName(default, &plan, null, null, null, &size) != WIN32_ERROR.ERROR_SUCCESS || size == 0)
        {
            return null;
        }

        byte[] buffer = new byte[size];
        fixed (byte* bytes = buffer)
        {
            if (PInvoke.PowerReadFriendlyName(default, &plan, null, null, bytes, &size) != WIN32_ERROR.ERROR_SUCCESS) return null;
        }
        return Encoding.Unicode.GetString(buffer).TrimEnd('\0');
    }

    /// <summary>Every power plan on this PC (PC maker plans get different GUIDs on each install).</summary>
    private static unsafe List<PowerPlan> EnumeratePlans()
    {
        var plans = new List<PowerPlan>();
        for (uint index = 0; index < 64; index++)
        {
            Guid plan;
            uint size = (uint)sizeof(Guid);
            if (PInvoke.PowerEnumerate(default, null, null, POWER_DATA_ACCESSOR.ACCESS_SCHEME, index, (byte*)&plan, &size)
                != WIN32_ERROR.ERROR_SUCCESS)
            {
                break; // ERROR_NO_MORE_ITEMS after the last plan
            }
            plans.Add(new PowerPlan(plan, ReadPlanName(plan) ?? "(no name)"));
        }
        return plans;
    }

    /// <summary>The subkeys under the PowerSchemes key: plans and power mode overlays this PC knows.</summary>
    private static List<PowerSchemeEntry> ReadPowerSchemesSubkeys()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(PowerSchemesKeyPath);
            if (key is null) return new List<PowerSchemeEntry>();

            var entries = new List<PowerSchemeEntry>();
            foreach (string name in key.GetSubKeyNames())
            {
                using RegistryKey? subkey = key.OpenSubKey(name);
                // FriendlyName looks like "@%SystemRoot%\system32\powrprof.dll,-14,Balanced": keep the part after the last comma.
                string? friendly = subkey?.GetValue("FriendlyName") as string;
                entries.Add(new PowerSchemeEntry(name, friendly is null ? null : friendly[(friendly.LastIndexOf(',') + 1)..]));
            }
            return entries;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return new List<PowerSchemeEntry>();
        }
    }
}
