using Windows.Win32;

namespace PowerModeSwitcher.Services;

/// <summary>The three positions on the slider, from most battery saving to most performance.</summary>
public enum Stop
{
    Bottom,
    Middle,
    Top,
}

/// <summary>The two sets of power modes Windows 11 offers (PLAN.md section 3, "Mode sets").</summary>
public enum ModeSetKind
{
    Standard,
    Classic,
}

/// <summary>
/// The four power mode GUIDs Windows has. Source: docs/power-mode-guids.md, section 1.
/// Windows used to call power modes "overlays".
/// </summary>
public static class KnownPowerModes
{
    public static readonly Guid BestPowerEfficiency = PInvoke.GUID_POWER_MODE_BEST_EFFICIENCY; // 961cc777
    public static readonly Guid Balanced = PInvoke.GUID_POWER_MODE_NONE;                       // 00000000
    public static readonly Guid BestPerformance = PInvoke.GUID_POWER_MODE_BEST_PERFORMANCE;     // ded574b5

    /// <summary>"Better performance". Not in the documented list, but offered by Classic PCs.</summary>
    public static readonly Guid BetterPerformance = new("3af9b8d9-7c97-431d-ad78-34a8bfea439f");

    /// <summary>The documented Windows 11 API only accepts these three.</summary>
    public static bool IsDocumented(Guid mode) =>
        mode == BestPowerEfficiency || mode == Balanced || mode == BestPerformance;

    /// <summary>Windows' internal name for a power mode (as stored in the registry), for diagnostics.</summary>
    public static string InternalName(Guid mode) =>
        mode == BestPowerEfficiency ? "Better Battery-life Overlay"
        : mode == Balanced ? "No overlay"
        : mode == BetterPerformance ? "High Performance Overlay"
        : mode == BestPerformance ? "Max Performance Overlay"
        : "unknown";
}

/// <summary>One stop on the slider: the power mode it sets and how it is labelled.</summary>
/// <param name="Mode">The power mode GUID.</param>
/// <param name="Label">The wording Windows Settings uses (menu and tray tooltip).</param>
/// <param name="WidgetLabel">The shorter label next to the track on the widget.</param>
public sealed record ModeStop(Guid Mode, string Label, string WidgetLabel);

/// <summary>
/// A set of three power modes, one per stop, as Windows Settings offers them on a given PC.
/// The same GUID can sit at a different stop in each set: Balanced (00000000) is the middle stop
/// in Standard and the bottom stop ("Recommended") in Classic.
/// </summary>
public sealed class ModeSet
{
    /// <summary>Seen on the development laptop (Dell, Windows 11).</summary>
    public static readonly ModeSet Standard = new(
        ModeSetKind.Standard,
        bottom: new ModeStop(KnownPowerModes.BestPowerEfficiency, "Best power efficiency", "Battery"),
        middle: new ModeStop(KnownPowerModes.Balanced, "Balanced", "Balanced"),
        top: new ModeStop(KnownPowerModes.BestPerformance, "Best performance", "Performance"));

    /// <summary>Seen on a Surface Laptop (Windows 11). Does not offer 961cc777 at all.</summary>
    public static readonly ModeSet Classic = new(
        ModeSetKind.Classic,
        bottom: new ModeStop(KnownPowerModes.Balanced, "Recommended", "Recommended"),
        middle: new ModeStop(KnownPowerModes.BetterPerformance, "Better performance", "Better performance"),
        top: new ModeStop(KnownPowerModes.BestPerformance, "Best performance", "Performance"));

    private readonly ModeStop _bottom;
    private readonly ModeStop _middle;
    private readonly ModeStop _top;

    private ModeSet(ModeSetKind kind, ModeStop bottom, ModeStop middle, ModeStop top)
    {
        Kind = kind;
        _bottom = bottom;
        _middle = middle;
        _top = top;
    }

    public ModeSetKind Kind { get; }

    public string Name => Kind.ToString();

    /// <summary>The three Windows labels from bottom to top, for example "Best power efficiency / Balanced / Best performance".</summary>
    public string Summary => $"{_bottom.Label} / {_middle.Label} / {_top.Label}";

    public ModeSet Other => Kind == ModeSetKind.Standard ? Classic : Standard;

    public ModeStop this[Stop stop] => stop switch
    {
        Stop.Bottom => _bottom,
        Stop.Middle => _middle,
        _ => _top,
    };

    public static ModeSet For(ModeSetKind kind) => kind == ModeSetKind.Classic ? Classic : Standard;

    /// <summary>The stop that sets this mode in this set, or null if the set does not offer it.</summary>
    public Stop? StopOf(Guid mode) =>
        mode == _bottom.Mode ? Stop.Bottom
        : mode == _middle.Mode ? Stop.Middle
        : mode == _top.Mode ? Stop.Top
        : null;

    /// <summary>
    /// The label for any mode: this set's wording first, then the other set's,
    /// so a mode this set does not offer is still named correctly.
    /// </summary>
    public string LabelFor(Guid? mode)
    {
        if (mode is not Guid known) return "Unknown power mode";
        if (StopOf(known) is Stop stop) return this[stop].Label;
        if (Other.StopOf(known) is Stop otherStop) return Other[otherStop].Label;
        return "Unknown power mode";
    }

    /// <summary>True if either set offers this mode.</summary>
    public static bool IsKnown(Guid mode) => Standard.StopOf(mode) is not null || Classic.StopOf(mode) is not null;
}
