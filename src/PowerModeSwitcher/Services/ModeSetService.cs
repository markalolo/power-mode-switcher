namespace PowerModeSwitcher.Services;

/// <summary>The "Power mode options" setting.</summary>
public enum ModeSetChoice
{
    /// <summary>Detect the set from modes picked in Windows, or ask once.</summary>
    Auto,
    Standard,
    Classic,
}

/// <summary>How an explicit Standard or Classic choice was made.</summary>
public enum ModeSetOrigin
{
    Menu,
    Question,
}

/// <summary>
/// Decides which mode set (Standard or Classic, PLAN.md section 3) the widget uses, and sets power
/// modes by stop through that set (PLAN.md Phase 8c).
/// <para>
/// Auto detection uses only modes picked outside the app: 3af9b8d9 on either power source means
/// Classic, 961cc777 means Standard, and 00000000 or ded574b5 alone decide nothing. Reads within a few
/// seconds of the app's own change, and the mode the app itself last set for each power source (saved,
/// so this survives restarts, until Windows reports a different mode there), are ignored, so the app
/// cannot fool itself. While undecided, Auto keeps the set that was in use when Automatic was picked in
/// the menu. On first run nothing is known yet, so it uses Standard and may ask the user once.
/// </para>
/// </summary>
public sealed class ModeSetService
{
    // Reads this soon after the app sets a mode are not used for detection.
    private static readonly TimeSpan OwnChangeWindow = TimeSpan.FromSeconds(5);

    private readonly SettingsService _settings;
    private readonly PowerModeService _power;
    private readonly HashSet<Guid> _reportedUnknownModes = new();
    private DateTime _lastOwnChangeUtc = DateTime.MinValue;
    private bool _started;

    public ModeSetService(SettingsService settings, PowerModeService power)
    {
        _settings = settings;
        _power = power;
    }

    /// <summary>Raised on the UI thread when the set in use, or how it was chosen, changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised with a short message worth showing as a tray notification.</summary>
    public event EventHandler<string>? Notice;

    /// <summary>
    /// The set in use. While Auto is undecided this is the set kept from before Automatic was picked,
    /// or Standard if there is none (as before Phase 8).
    /// </summary>
    public ModeSet Current => Settings.ModeSet switch
    {
        ModeSetChoice.Standard => ModeSet.Standard,
        ModeSetChoice.Classic => ModeSet.Classic,
        _ => ModeSet.For(Settings.DetectedModeSet ?? Settings.ModeSetUntilDetected ?? ModeSetKind.Standard),
    };

    public ModeSetChoice Choice => Settings.ModeSet;

    public ModeSetKind? Detected => Settings.DetectedModeSet;

    /// <summary>The set Auto keeps until it detects one (see AppSettings.ModeSetUntilDetected).</summary>
    public ModeSetKind? UntilDetected => Settings.ModeSetUntilDetected;

    public bool IsUndecided => Settings.ModeSet == ModeSetChoice.Auto && Settings.DetectedModeSet is null;

    /// <summary>True while nothing at all is known about the set (first run), so the question can help.</summary>
    public bool NeedsAnswer => IsUndecided && Settings.ModeSetUntilDetected is null;

    /// <summary>True when the one-time question should be shown now.</summary>
    public bool ShouldAskQuestion => NeedsAnswer && !Settings.ModeSetQuestionShown;

    /// <summary>How the current set was chosen, in plain words (menu and diagnostics).</summary>
    public string HowChosen => Settings.ModeSet switch
    {
        ModeSetChoice.Auto when Settings.DetectedModeSet is not null => "detected automatically",
        ModeSetChoice.Auto when Settings.ModeSetUntilDetected is not null => "not detected yet, so keeping the set in use when Automatic was picked",
        ModeSetChoice.Auto => "not decided yet, so using Standard",
        _ when Settings.ModeSetChosenBy == ModeSetOrigin.Question => "chosen in the one-time question",
        _ => "chosen in the menu",
    };

    private AppSettings Settings => _settings.Settings;

    /// <summary>
    /// Starts following power changes and runs detection on the current reading.
    /// Call after everything that listens to Changed and Notice is wired up.
    /// </summary>
    public void Start()
    {
        if (_started) return;
        _started = true;
        _power.Changed += (_, _) => OnPowerChanged(notify: true);
        OnPowerChanged(notify: false);
    }

    /// <summary>Sets the "Power mode options" choice (from the menu or the one-time question).</summary>
    public void Choose(ModeSetChoice choice, ModeSetOrigin origin)
    {
        if (choice != ModeSetChoice.Auto)
        {
            Settings.ModeSet = choice;
            Settings.ModeSetChosenBy = origin;
        }
        else if (Settings.ModeSet != ModeSetChoice.Auto)
        {
            // Coming from Standard or Classic: keep that set until Windows shows which one it offers,
            // instead of falling back to Standard and asking the one-time question again.
            Settings.ModeSetUntilDetected = Current.Kind;
            Settings.DetectedModeSet = null;
            Settings.ModeSetChosenBy = null;
            Settings.ModeSet = ModeSetChoice.Auto;
        }

        if (choice == ModeSetChoice.Auto) Detect(_power.Current, notify: false);

        _settings.Save();
        Changed?.Invoke(this, EventArgs.Empty);

        // Picking Automatic often changes nothing visible, so say what it decided.
        if (choice == ModeSetChoice.Auto)
        {
            Notice?.Invoke(this, Settings.DetectedModeSet is null
                ? $"Automatic will decide once you pick Better performance or Best power efficiency in Windows Settings. Until then it keeps {Current.Summary}."
                : $"Automatic found the power modes this PC shows: {Current.Summary}.");
        }
    }

    /// <summary>Records that the one-time question has appeared, so it is not shown again.</summary>
    public void MarkQuestionShown()
    {
        Settings.ModeSetQuestionShown = true;
        _settings.Save();
    }

    /// <summary>Sets the power mode at this stop of the current set.</summary>
    public bool TrySetStop(Stop stop, out string? error)
    {
        Guid mode = Current[stop].Mode;
        RememberOwnChange(mode);
        return _power.TrySetMode(mode, out error);
    }

    private void RememberOwnChange(Guid mode)
    {
        _lastOwnChangeUtc = DateTime.UtcNow;
        switch (_power.Current.Source)
        {
            case PowerSource.PluggedIn: Settings.LastAppModePluggedIn = mode; break;
            case PowerSource.OnBattery: Settings.LastAppModeOnBattery = mode; break;
        }
        _settings.Save();
    }

    private void OnPowerChanged(bool notify)
    {
        PowerModeReading reading = _power.Current;
        bool forgot = ForgetReplacedOwnModes(reading);
        bool detected = Detect(reading, notify);
        if (forgot || detected) _settings.Save();
        if (detected) Changed?.Invoke(this, EventArgs.Empty);
        ReportUnknownMode(reading);
    }

    /// <summary>
    /// Once Windows reports a different mode for a power source than the one the app last set there, the
    /// mode was changed outside the app, so the app's old choice no longer needs ignoring. Without this,
    /// picking that same mode in Windows Settings later would never count as a sighting.
    /// Returns true if anything was cleared.
    /// </summary>
    private bool ForgetReplacedOwnModes(PowerModeReading reading)
    {
        if (DateTime.UtcNow - _lastOwnChangeUtc < OwnChangeWindow) return false;

        bool changed = false;
        if (IsReplaced(Settings.LastAppModePluggedIn, reading.PluggedInMode,
                reading.Source == PowerSource.PluggedIn ? reading.Mode : null))
        {
            Settings.LastAppModePluggedIn = null;
            changed = true;
        }
        if (IsReplaced(Settings.LastAppModeOnBattery, reading.OnBatteryMode,
                reading.Source == PowerSource.OnBattery ? reading.Mode : null))
        {
            Settings.LastAppModeOnBattery = null;
            changed = true;
        }
        return changed;
    }

    // For the current power source the live reading must differ too: it can show 3af9b8d9 (the reading
    // prefers the overlay) while the saved value still shows something else.
    private static bool IsReplaced(Guid? lastSetByApp, Guid? sourceMode, Guid? currentMode) =>
        lastSetByApp is Guid last && sourceMode is Guid now && now != last && currentMode != last;

    /// <summary>Updates the detected set from this reading. Returns true if it changed.</summary>
    private bool Detect(PowerModeReading reading, bool notify)
    {
        if (Settings.ModeSet != ModeSetChoice.Auto) return false;
        if (DateTime.UtcNow - _lastOwnChangeUtc < OwnChangeWindow) return false;

        Guid? lastOwnForCurrent = reading.Source switch
        {
            PowerSource.PluggedIn => Settings.LastAppModePluggedIn,
            PowerSource.OnBattery => Settings.LastAppModeOnBattery,
            _ => null,
        };

        var seen = new List<Guid>();
        AddSighting(seen, reading.PluggedInMode, Settings.LastAppModePluggedIn);
        AddSighting(seen, reading.OnBatteryMode, Settings.LastAppModeOnBattery);
        AddSighting(seen, reading.Mode, lastOwnForCurrent);

        // If both signs turn up, Classic wins: version 1.1.0 assumed Standard everywhere and may have
        // left 961cc777 behind on a Classic PC, while the app only ever sets 3af9b8d9 when Classic is
        // already in use.
        ModeSetKind? found = seen.Contains(KnownPowerModes.BetterPerformance) ? ModeSetKind.Classic
            : seen.Contains(KnownPowerModes.BestPowerEfficiency) ? ModeSetKind.Standard
            : null;
        if (found is null || found == Settings.DetectedModeSet) return false;

        ModeSetKind before = Current.Kind;
        Settings.DetectedModeSet = found;
        if (notify && Current.Kind != before)
        {
            Notice?.Invoke(this, $"Switched to the power mode options this PC shows: {Current.Summary}.");
        }
        return true;
    }

    // The app's own last choice for a power source says nothing about which set Windows offers.
    private static void AddSighting(List<Guid> seen, Guid? mode, Guid? lastSetByApp)
    {
        if (mode is Guid known && known != lastSetByApp) seen.Add(known);
    }

    /// <summary>A mode in neither set: say so once (the widget shows its neutral state; it never guesses).</summary>
    private void ReportUnknownMode(PowerModeReading reading)
    {
        if (reading.Mode is Guid mode && !ModeSet.IsKnown(mode) && _reportedUnknownModes.Add(mode))
        {
            Notice?.Invoke(this,
                "Windows is using a power mode this app doesn't recognize. Right-click the widget and choose Copy diagnostics to report it.");
        }
    }
}
