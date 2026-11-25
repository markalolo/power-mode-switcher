using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;
using PowerModeSwitcher.Native;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Power;
using Windows.Win32.UI.WindowsAndMessaging;

namespace PowerModeSwitcher.Services;

public enum PowerSource
{
    Unknown,
    OnBattery,
    PluggedIn,
}

/// <summary>
/// A snapshot of the power state.
/// </summary>
/// <param name="Mode">The user's chosen power mode for the current power source, or null if it can't be read (see Error).</param>
/// <param name="PluggedInMode">The mode chosen for when the PC is plugged in.</param>
/// <param name="OnBatteryMode">The mode chosen for when the PC is on battery.</param>
/// <param name="Source">Plugged in or on battery.</param>
/// <param name="EnergySaverOn">
/// Windows Energy Saver (called Battery Saver before Windows 11 24H2) is on. While it is on, Windows
/// runs at best power efficiency, but Mode still shows the mode you chose, which Windows goes back to.
/// </param>
/// <param name="BalancedPlan">
/// False when the active power plan is not Balanced or based on it. Windows ignores Power Mode then.
/// </param>
/// <param name="Error">Why the mode could not be read, or null.</param>
public sealed record PowerModeReading(
    Guid? Mode,
    Guid? PluggedInMode,
    Guid? OnBatteryMode,
    PowerSource Source,
    bool EnergySaverOn,
    bool BalancedPlan,
    string? Error);

/// <summary>
/// Reads and sets the Windows Power Mode, and raises Changed whenever it changes anywhere
/// (Settings, battery flyout, plugging in, etc.). This is the only place that calls the power APIs
/// (diagnostics readers are in PowerModeService.Details.cs).
/// <para>
/// Windows 11 documents PowerGet/SetUserConfiguredAC/DCPowerMode, which are used first. The
/// undocumented overlay functions (Native/PowerOverlayApi.cs) are the fallback for older Windows,
/// and the only way to read or set 3af9b8d9, which the documented API does not support.
/// </para>
/// Never use "powercfg /setactive" here: that changes the power plan, which is the wrong layer.
/// </summary>
public sealed partial class PowerModeService : IDisposable
{
    private static readonly TimeSpan SafetyPollInterval = TimeSpan.FromSeconds(5);

    /// <summary>Where Windows keeps the chosen power mode per power source (read only).</summary>
    internal const string PowerSchemesKeyPath = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";

    // The documented functions list "Windows 11" with no build number. If an early Windows 11 build
    // lacks them, this is set once and the app uses the undocumented functions from then on.
    private static volatile bool s_documentedApiMissing;

    private SynchronizationContext? _uiContext;

    // Effective power mode notification. The delegate must stay alive while Windows holds it.
    private EFFECTIVE_POWER_MODE_CALLBACK? _effectiveModeCallback;
    private nint _effectiveModeRegistration;
    private volatile int _effectivePowerMode = -1; // last EFFECTIVE_POWER_MODE value, for diagnostics

    // Power setting notifications (Energy Saver, power plan personality). Windows is given a pointer to
    // the subscribe parameters, so they live in unmanaged memory (with the delegate kept alive) until
    // every notification is unregistered.
    private PDEVICE_NOTIFY_CALLBACK_ROUTINE? _powerSettingCallback;
    private nint _subscribeParameters;
    private readonly List<HPOWERNOTIFY> _powerSettingRegistrations = new();
    private volatile int _energySaverStatus; // from GUID_ENERGY_SAVER_STATUS: 0 = off

    // The active plan's personality (GUID_POWERSCHEME_PERSONALITY), written on a Windows thread.
    private readonly object _planLock = new();
    private Guid? _planPersonality;

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
        RegisterPowerSettingNotifications();

        // Plugging in, unplugging, and resume from sleep.
        SystemEvents.PowerModeChanged += OnSystemPowerModeChanged;

        // Safety net in case a notification is missed (and the only way to see a change to the other
        // power source's mode). Refresh() only raises Changed if something changed.
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

    /// <summary>Reads the Power Mode, both power sources' modes and the power source right now.</summary>
    public PowerModeReading Read()
    {
        (PowerSource source, bool batterySaverFlag) = ReadPowerSource();
        bool energySaverOn = _energySaverStatus != 0 || batterySaverFlag;

        // The user's chosen mode for the current power source. Both values stay the same while
        // Energy Saver overrides the mode, so they are the right values for the slider.
        Guid? configured = TryGetUserConfiguredMode(source);
        (Guid? overlay, string? overlayError) = TryGetActualOverlay();

        // Documented value first. Only the undocumented one can report 3af9b8d9.
        Guid? mode = overlay == KnownPowerModes.BetterPerformance ? overlay : configured ?? overlay;

        (Guid? pluggedIn, Guid? onBattery) = ReadConfiguredModes();

        return new PowerModeReading(
            mode, pluggedIn, onBattery, source, energySaverOn, IsBalancedPlan(), mode is null ? overlayError : null);
    }

    /// <summary>
    /// Sets the Power Mode for the current power source, then refreshes Current.
    /// Returns true on success; on failure, error holds a short message. Call on the UI thread.
    /// </summary>
    public bool TrySetMode(Guid mode, out string? error)
    {
        // Documented Windows 11 API for the three modes it supports. 3af9b8d9 is not one of them, so
        // it goes straight to the undocumented function, which is also the fallback for older Windows,
        // an unknown power source, or a failure.
        WIN32_ERROR? documented = TrySetUserConfiguredMode(mode, ReadPowerSource().Source);
        if (documented == WIN32_ERROR.ERROR_SUCCESS)
        {
            error = null;
        }
        else
        {
            try
            {
                var result = (WIN32_ERROR)PowerOverlayApi.PowerSetActiveOverlayScheme(in mode);
                error = result == WIN32_ERROR.ERROR_SUCCESS ? null : DescribeError(result);
            }
            catch (EntryPointNotFoundException)
            {
                error = documented is WIN32_ERROR code
                    ? DescribeError(code)
                    : "This version of Windows does not support setting the power mode.";
            }
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

    // ---- Reading and setting through the documented API, with the undocumented fallback ----

    private static bool CanUseDocumentedApi(PowerSource source) =>
        !s_documentedApiMissing && source != PowerSource.Unknown && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    /// <summary>
    /// The user's chosen mode for the given power source, from the documented Windows 11 API.
    /// Null when that API can't be used here or the call fails.
    /// </summary>
    private static Guid? TryGetUserConfiguredMode(PowerSource source)
    {
        if (!CanUseDocumentedApi(source)) return null;
        try
        {
            Guid mode;
            uint result = source == PowerSource.PluggedIn
                ? PInvoke.PowerGetUserConfiguredACPowerMode(out mode)
                : PInvoke.PowerGetUserConfiguredDCPowerMode(out mode);
            return result == (uint)WIN32_ERROR.ERROR_SUCCESS ? mode : null;
        }
        catch (EntryPointNotFoundException)
        {
            s_documentedApiMissing = true;
            return null;
        }
    }

    /// <summary>
    /// Sets the user's chosen mode for the given power source with the documented Windows 11 API.
    /// Null when that API can't be used (older Windows, unknown power source, or a mode it does not support).
    /// </summary>
    private static WIN32_ERROR? TrySetUserConfiguredMode(Guid mode, PowerSource source)
    {
        if (!CanUseDocumentedApi(source) || !KnownPowerModes.IsDocumented(mode)) return null;
        try
        {
            uint result = source == PowerSource.PluggedIn
                ? PInvoke.PowerSetUserConfiguredACPowerMode(in mode)
                : PInvoke.PowerSetUserConfiguredDCPowerMode(in mode);
            return (WIN32_ERROR)result;
        }
        catch (EntryPointNotFoundException)
        {
            s_documentedApiMissing = true;
            return null;
        }
    }

    /// <summary>The active mode from the undocumented overlay function, or an error message.</summary>
    private static (Guid? Overlay, string? Error) TryGetActualOverlay()
    {
        try
        {
            var result = (WIN32_ERROR)PowerOverlayApi.PowerGetActualOverlayScheme(out Guid overlay);
            return result == WIN32_ERROR.ERROR_SUCCESS ? (overlay, null) : (null, DescribeError(result));
        }
        catch (EntryPointNotFoundException)
        {
            return (null, "This version of Windows does not support reading the power mode.");
        }
    }

    /// <summary>
    /// The mode chosen for each power source. The registry values Windows Settings writes come first,
    /// because they also show 3af9b8d9; the documented API is the fallback.
    /// </summary>
    private static (Guid? PluggedIn, Guid? OnBattery) ReadConfiguredModes()
    {
        (Guid? pluggedIn, Guid? onBattery) = ReadOverlayRegistry();
        pluggedIn ??= TryGetUserConfiguredMode(PowerSource.PluggedIn);
        onBattery ??= TryGetUserConfiguredMode(PowerSource.OnBattery);
        return (pluggedIn, onBattery);
    }

    /// <summary>
    /// ActiveOverlayAcPowerScheme / ActiveOverlayDcPowerScheme from the registry. Reading them needs no
    /// administrator rights; the app never writes them.
    /// </summary>
    private static (Guid? PluggedIn, Guid? OnBattery) ReadOverlayRegistry()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(PowerSchemesKeyPath);
            return (ParseGuid(key?.GetValue("ActiveOverlayAcPowerScheme")), ParseGuid(key?.GetValue("ActiveOverlayDcPowerScheme")));
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return (null, null);
        }
    }

    private static Guid? ParseGuid(object? value) => value is string text && Guid.TryParse(text, out Guid guid) ? guid : null;

    private bool IsBalancedPlan()
    {
        lock (_planLock)
        {
            // Unknown until Windows reports it: assume Balanced rather than warn for no reason.
            return _planPersonality is not Guid personality || personality == PInvoke.GUID_TYPICAL_POWER_SAVINGS;
        }
    }

    private void OnSystemPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode is PowerModes.StatusChange or PowerModes.Resume) RequestRefresh();
    }

    // ---- Windows notifications ----

    /// <summary>
    /// Documented API (Windows 10 1809+). Windows calls back when the effective power mode changes.
    /// It is a trigger to re-read (and the value is kept for diagnostics).
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
    /// Documented power setting notifications. Windows sends each current value right away, then again on
    /// every change.
    /// <list type="bullet">
    /// <item>GUID_ENERGY_SAVER_STATUS (Windows 11 24H2+): Energy Saver can be on even when plugged in, and
    /// the older Battery Saver flag does not report it. On older Windows the flag is used instead.</item>
    /// <item>GUID_POWERSCHEME_PERSONALITY: which kind of plan is active. Power Mode only works on
    /// Balanced or a plan based on it; PC maker plan GUIDs differ per install, but their personality does not.</item>
    /// </list>
    /// </summary>
    private unsafe void RegisterPowerSettingNotifications()
    {
        _powerSettingCallback = OnPowerSettingChanged;
        try
        {
            var parameters = new DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS { Callback = _powerSettingCallback };
            _subscribeParameters = Marshal.AllocHGlobal(Marshal.SizeOf<DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS>());
            Marshal.StructureToPtr(parameters, _subscribeParameters, fDeleteOld: false);

            foreach (Guid settingGuid in new[] { PInvoke.GUID_ENERGY_SAVER_STATUS, PInvoke.GUID_POWERSCHEME_PERSONALITY })
            {
                Guid setting = settingGuid;
                void* registration;
                WIN32_ERROR result = PInvoke.PowerSettingRegisterNotification(
                    &setting, REGISTER_NOTIFICATION_FLAGS.DEVICE_NOTIFY_CALLBACK, new HANDLE((void*)_subscribeParameters), &registration);
                if (result == WIN32_ERROR.ERROR_SUCCESS) _powerSettingRegistrations.Add(new HPOWERNOTIFY((nint)registration));
            }
        }
        catch (EntryPointNotFoundException)
        {
            // Very old Windows: no notifications; the safety poll and Battery Saver flag still work.
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

        foreach (HPOWERNOTIFY registration in _powerSettingRegistrations)
        {
            PInvoke.PowerSettingUnregisterNotification(registration);
        }
        _powerSettingRegistrations.Clear();
        if (_subscribeParameters != 0)
        {
            Marshal.FreeHGlobal(_subscribeParameters);
            _subscribeParameters = 0;
        }
        GC.KeepAlive(_powerSettingCallback);
        _powerSettingCallback = null;
    }

    // Called by Windows on its own thread.
    private unsafe void OnEffectivePowerModeChanged(EFFECTIVE_POWER_MODE mode, void* context)
    {
        _effectivePowerMode = (int)mode;
        RequestRefresh();
    }

    // Called by Windows on its own thread with a POWERBROADCAST_SETTING.
    private unsafe uint OnPowerSettingChanged(void* context, uint type, void* setting)
    {
        if (type != PInvoke.PBT_POWERSETTINGCHANGE || setting is null) return 0;

        var broadcast = (POWERBROADCAST_SETTING*)setting;
        if (broadcast->PowerSetting == PInvoke.GUID_ENERGY_SAVER_STATUS && broadcast->DataLength >= sizeof(uint))
        {
            // A DWORD: 0 = Energy Saver off, otherwise on.
            _energySaverStatus = (int)*(uint*)&broadcast->Data;
        }
        else if (broadcast->PowerSetting == PInvoke.GUID_POWERSCHEME_PERSONALITY && broadcast->DataLength >= sizeof(Guid))
        {
            // A GUID: the personality of the active plan (Balanced, High performance or Power saver).
            Guid personality = *(Guid*)&broadcast->Data;
            lock (_planLock) _planPersonality = personality;
        }
        else
        {
            return 0;
        }

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
