using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using PowerModeSwitcher.Native;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Power;
using Windows.Win32.UI.WindowsAndMessaging;

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

    // Effective power mode notification. The delegate must stay alive while Windows holds it.
    private EFFECTIVE_POWER_MODE_CALLBACK? _effectiveModeCallback;
    private nint _effectiveModeRegistration;

    // Energy Saver notification. Windows is given a pointer to the subscribe parameters, so they live
    // in unmanaged memory (with the delegate kept alive) until the notification is unregistered.
    private PDEVICE_NOTIFY_CALLBACK_ROUTINE? _energySaverCallback;
    private nint _energySaverParameters;
    private HPOWERNOTIFY _energySaverRegistration;
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

        RegisterEffectivePowerModeNotification();
        RegisterEnergySaverNotification();

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
            var result = (WIN32_ERROR)PowerOverlayApi.PowerGetActualOverlayScheme(out Guid overlay);
            if (result != WIN32_ERROR.ERROR_SUCCESS)
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
            var result = (WIN32_ERROR)PowerOverlayApi.PowerSetActiveOverlayScheme(OverlayFromMode(mode));
            error = result == WIN32_ERROR.ERROR_SUCCESS ? null : DescribeError(result);
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
        UnregisterNotifications();
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

    // ---- Windows notifications ----

    /// <summary>
    /// Documented API (Windows 10 1809+). Windows calls back when the effective power mode changes.
    /// It is only a trigger: the slider always re-reads the actual overlay.
    /// If registration fails, the 5-second safety poll still keeps the widget in sync.
    /// </summary>
    private unsafe void RegisterEffectivePowerModeNotification()
    {
        // Windows 10 1809 (build 17763) and later. Older Windows relies on the safety poll.
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return;

        _effectiveModeCallback = OnEffectivePowerModeChanged;
        HRESULT hr = PInvoke.PowerRegisterForEffectivePowerModeNotifications(
            PInvoke.EFFECTIVE_POWER_MODE_V2, _effectiveModeCallback, null, out void* registration);
        if (hr.Value >= 0) _effectiveModeRegistration = (nint)registration;
    }

    /// <summary>
    /// Documented API (Windows 11 24H2+). Energy Saver can be on even when plugged in, and the older
    /// Battery Saver flag does not report it. Windows sends the current value right away, then again
    /// on every change. On older Windows this fails and the Battery Saver flag is used instead.
    /// </summary>
    private unsafe void RegisterEnergySaverNotification()
    {
        _energySaverCallback = OnPowerSettingChanged;
        try
        {
            var parameters = new DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS { Callback = _energySaverCallback };
            _energySaverParameters = Marshal.AllocHGlobal(Marshal.SizeOf<DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS>());
            Marshal.StructureToPtr(parameters, _energySaverParameters, fDeleteOld: false);

            Guid setting = PInvoke.GUID_ENERGY_SAVER_STATUS;
            void* registration;
            WIN32_ERROR result = PInvoke.PowerSettingRegisterNotification(
                &setting, REGISTER_NOTIFICATION_FLAGS.DEVICE_NOTIFY_CALLBACK, new HANDLE((void*)_energySaverParameters), &registration);
            if (result == WIN32_ERROR.ERROR_SUCCESS) _energySaverRegistration = new HPOWERNOTIFY((nint)registration);
        }
        catch (EntryPointNotFoundException)
        {
            // Windows without Energy Saver: the Battery Saver flag is used instead.
        }
    }

    private unsafe void UnregisterNotifications()
    {
        // Registration only happens on build 17763+, so the version check never skips a real unregister.
        if (_effectiveModeRegistration != 0 && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            PInvoke.PowerUnregisterFromEffectivePowerModeNotifications((void*)_effectiveModeRegistration);
            _effectiveModeRegistration = 0;
        }
        GC.KeepAlive(_effectiveModeCallback);
        _effectiveModeCallback = null;

        if (!_energySaverRegistration.IsNull)
        {
            PInvoke.PowerSettingUnregisterNotification(_energySaverRegistration);
            _energySaverRegistration = default;
        }
        if (_energySaverParameters != 0)
        {
            Marshal.FreeHGlobal(_energySaverParameters);
            _energySaverParameters = 0;
        }
        GC.KeepAlive(_energySaverCallback);
        _energySaverCallback = null;
    }

    // Called by Windows on its own thread.
    private unsafe void OnEffectivePowerModeChanged(EFFECTIVE_POWER_MODE mode, void* context) => RequestRefresh();

    // Called by Windows on its own thread with a POWERBROADCAST_SETTING for GUID_ENERGY_SAVER_STATUS.
    private unsafe uint OnPowerSettingChanged(void* context, uint type, void* setting)
    {
        if (type != PInvoke.PBT_POWERSETTINGCHANGE || setting is null) return 0;

        var broadcast = (POWERBROADCAST_SETTING*)setting;
        if (broadcast->PowerSetting != PInvoke.GUID_ENERGY_SAVER_STATUS || broadcast->DataLength < sizeof(uint)) return 0;

        // The data is a DWORD: 0 = Energy Saver off, otherwise on.
        _energySaverStatus = (int)*(uint*)&broadcast->Data;
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
        if (!PInvoke.GetSystemPowerStatus(out SYSTEM_POWER_STATUS status))
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

    private static string DescribeError(WIN32_ERROR code) =>
        $"{new Win32Exception((int)code).Message} (error {(uint)code})";
}
