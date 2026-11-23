using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using PowerModeSwitcher.Native;

namespace PowerModeSwitcher.Services;

/// <summary>The three slider positions, plus Unknown for an overlay we do not recognize.</summary>
public enum PowerMode
{
    Unknown,
    BestPowerEfficiency,
    Balanced,
    BestPerformance,
}

public enum PowerSource
{
    Unknown,
    OnBattery,
    PluggedIn,
}

/// <summary>
/// A snapshot of the power state. Error is null when the mode was read successfully.
/// EnergySaverOn means Windows Energy Saver (called Battery Saver before Windows 11 24H2) is on.
/// While it is on, Windows runs at best power efficiency, but Mode still shows the mode you chose,
/// which Windows goes back to when Energy Saver turns off.
/// </summary>
public sealed record PowerModeReading(
    PowerMode Mode,
    Guid Overlay,
    PowerSource Source,
    bool EnergySaverOn,
    string? Error);

/// <summary>
/// Reads and sets the Windows 11 Power Mode through power overlay schemes in powrprof.dll,
/// and raises Changed whenever it changes anywhere (Settings, battery flyout, plugging in, etc.).
/// This is the only place that calls the power APIs, so the undocumented parts are easy to swap.
/// Never use "powercfg /setactive" here: that changes the power plan, which is the wrong layer.
/// </summary>
public sealed class PowerModeService : IDisposable
{
    public static readonly Guid BestPowerEfficiencyOverlay = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    public static readonly Guid BalancedOverlay = Guid.Empty;
    public static readonly Guid BestPerformanceOverlay = new("ded574b5-45a0-4f42-8737-46345c09c238");

    // Older "Better battery" overlay. Recognized when reading, never written.
    private static readonly Guid LegacyBetterBatteryOverlay = new("3af9b8d9-7c97-431d-ad78-34a8bfea439f");

    private static readonly TimeSpan SafetyPollInterval = TimeSpan.FromSeconds(5);

    private SynchronizationContext? _uiContext;
    private NativeMethods.EffectivePowerModeCallback? _effectiveModeCallback; // kept alive while registered
    private IntPtr _effectiveModeRegistration;
    private NativeMethods.DeviceNotifyCallback? _energySaverCallback; // kept alive while registered
    private IntPtr _energySaverRegistration;
    private volatile int _energySaverStatus; // from GUID_ENERGY_SAVER_STATUS: 0 = off
    private Timer? _safetyPoll;
    private volatile bool _disposed;

    public PowerModeService()
    {
        Current = Read();
    }

    /// <summary>The latest reading. Only changes on the UI thread.</summary>
    public PowerModeReading Current { get; private set; }

    /// <summary>Raised on the UI thread when Current changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Starts watching for changes. Call once, on the UI thread.</summary>
    public void Start()
    {
        if (_uiContext is not null || _disposed) return;
        _uiContext = SynchronizationContext.Current
            ?? throw new InvalidOperationException("PowerModeService.Start must be called on the UI thread.");

        // Documented API. Windows calls back when the effective power mode changes. We only use it
        // as a trigger and then re-read the overlay, which is the source of truth for the slider.
        _effectiveModeCallback = (_, _) => RequestRefresh();
        try
        {
            int hr = NativeMethods.PowerRegisterForEffectivePowerModeNotifications(
                NativeMethods.EFFECTIVE_POWER_MODE_V2, _effectiveModeCallback, IntPtr.Zero, out _effectiveModeRegistration);
            if (hr != 0) _effectiveModeRegistration = IntPtr.Zero; // the safety poll still keeps us in sync
        }
        catch (EntryPointNotFoundException)
        {
            _effectiveModeRegistration = IntPtr.Zero;
        }

        // Documented API (Windows 11 24H2+). Energy Saver can be on even when plugged in, and the
        // older Battery Saver flag does not report it. Windows sends the current value right away,
        // then again on every change. On older Windows this fails and the Battery Saver flag is used.
        _energySaverCallback = OnPowerSettingChanged;
        try
        {
            Guid setting = NativeMethods.GUID_ENERGY_SAVER_STATUS;
            var recipient = new NativeMethods.DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS { Callback = _energySaverCallback, Context = IntPtr.Zero };
            uint result = NativeMethods.PowerSettingRegisterNotification(
                ref setting, NativeMethods.DEVICE_NOTIFY_CALLBACK, ref recipient, out _energySaverRegistration);
            if (result != NativeMethods.ERROR_SUCCESS) _energySaverRegistration = IntPtr.Zero;
        }
        catch (EntryPointNotFoundException)
        {
            _energySaverRegistration = IntPtr.Zero;
        }

        // Plugging in, unplugging, and resume from sleep.
        SystemEvents.PowerModeChanged += OnSystemPowerModeChanged;

        // Safety net in case a notification is missed. Refresh() only raises Changed if something changed.
        _safetyPoll = new Timer(_ => RequestRefresh(), null, SafetyPollInterval, SafetyPollInterval);
    }

    /// <summary>Re-reads the power state and raises Changed if it differs. Call on the UI thread.</summary>
    public void Refresh()
    {
        if (_disposed) return;

        PowerModeReading reading = Read();
        if (reading == Current) return;

        Current = reading;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Reads the Power Mode and power source that Windows reports right now.</summary>
    public PowerModeReading Read()
    {
        (PowerSource source, bool batterySaverFlag) = ReadPowerSource();
        bool energySaverOn = _energySaverStatus != 0 || batterySaverFlag;

        try
        {
            uint result = NativeMethods.PowerGetActualOverlayScheme(out Guid overlay);
            if (result != NativeMethods.ERROR_SUCCESS)
            {
                return new PowerModeReading(PowerMode.Unknown, Guid.Empty, source, energySaverOn, DescribeError(result));
            }

            return new PowerModeReading(ModeFromOverlay(overlay), overlay, source, energySaverOn, null);
        }
        catch (EntryPointNotFoundException)
        {
            return new PowerModeReading(PowerMode.Unknown, Guid.Empty, source, energySaverOn,
                "This version of Windows does not support reading the power mode.");
        }
    }

    /// <summary>
    /// Sets the Power Mode for the current power source, then refreshes Current.
    /// Returns true on success; on failure, error holds a short message. Call on the UI thread.
    /// </summary>
    public bool TrySetMode(PowerMode mode, out string? error)
    {
        if (mode == PowerMode.Unknown)
        {
            error = "Cannot set an unknown power mode.";
            return false;
        }

        try
        {
            uint result = NativeMethods.PowerSetActiveOverlayScheme(OverlayFromMode(mode));
            error = result == NativeMethods.ERROR_SUCCESS ? null : DescribeError(result);
        }
        catch (EntryPointNotFoundException)
        {
            error = "This version of Windows does not support setting the power mode.";
        }

        Refresh();
        return error is null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _safetyPoll?.Dispose();
        _safetyPoll = null;

        SystemEvents.PowerModeChanged -= OnSystemPowerModeChanged;

        if (_effectiveModeRegistration != IntPtr.Zero)
        {
            NativeMethods.PowerUnregisterFromEffectivePowerModeNotifications(_effectiveModeRegistration);
            _effectiveModeRegistration = IntPtr.Zero;
        }
        GC.KeepAlive(_effectiveModeCallback);
        _effectiveModeCallback = null;

        if (_energySaverRegistration != IntPtr.Zero)
        {
            NativeMethods.PowerSettingUnregisterNotification(_energySaverRegistration);
            _energySaverRegistration = IntPtr.Zero;
        }
        GC.KeepAlive(_energySaverCallback);
        _energySaverCallback = null;
    }

    public static PowerMode ModeFromOverlay(Guid overlay)
    {
        if (overlay == BestPowerEfficiencyOverlay || overlay == LegacyBetterBatteryOverlay) return PowerMode.BestPowerEfficiency;
        if (overlay == BalancedOverlay) return PowerMode.Balanced;
        if (overlay == BestPerformanceOverlay) return PowerMode.BestPerformance;
        return PowerMode.Unknown;
    }

    public static Guid OverlayFromMode(PowerMode mode) => mode switch
    {
        PowerMode.BestPowerEfficiency => BestPowerEfficiencyOverlay,
        PowerMode.Balanced => BalancedOverlay,
        PowerMode.BestPerformance => BestPerformanceOverlay,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "No overlay for this mode."),
    };

    /// <summary>The same wording Windows Settings uses.</summary>
    public static string DisplayName(PowerMode mode) => mode switch
    {
        PowerMode.BestPowerEfficiency => "Best power efficiency",
        PowerMode.Balanced => "Balanced",
        PowerMode.BestPerformance => "Best performance",
        _ => "Unknown",
    };

    private void OnSystemPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode is PowerModes.StatusChange or PowerModes.Resume) RequestRefresh();
    }

    // Called by Windows on its own thread with a POWERBROADCAST_SETTING for GUID_ENERGY_SAVER_STATUS.
    private uint OnPowerSettingChanged(IntPtr context, uint type, IntPtr setting)
    {
        if (type != NativeMethods.PBT_POWERSETTINGCHANGE || setting == IntPtr.Zero) return 0;
        if (Marshal.PtrToStructure<Guid>(setting) != NativeMethods.GUID_ENERGY_SAVER_STATUS) return 0;
        if (Marshal.ReadInt32(setting, NativeMethods.POWERBROADCAST_SETTING_LENGTH_OFFSET) < sizeof(int)) return 0;

        _energySaverStatus = Marshal.ReadInt32(setting, NativeMethods.POWERBROADCAST_SETTING_DATA_OFFSET);
        RequestRefresh();
        return 0;
    }

    // Called from any thread. Hops to the UI thread before reading.
    private void RequestRefresh()
    {
        if (_disposed) return;
        _uiContext?.Post(_ => Refresh(), null);
    }

    // BatterySaverFlag is the older Battery Saver signal. On Windows 11 24H2+ it stays 0 when
    // Energy Saver is turned on, so Energy Saver comes from the notification above instead.
    private static (PowerSource Source, bool BatterySaverFlag) ReadPowerSource()
    {
        if (!NativeMethods.GetSystemPowerStatus(out NativeMethods.SYSTEM_POWER_STATUS status))
        {
            return (PowerSource.Unknown, false);
        }

        PowerSource source = status.ACLineStatus switch
        {
            0 => PowerSource.OnBattery,
            1 => PowerSource.PluggedIn,
            _ => PowerSource.Unknown,
        };
        return (source, status.SystemStatusFlag == 1);
    }

    private static string DescribeError(uint code) =>
        $"{new Win32Exception((int)code).Message} (error {code})";
}
