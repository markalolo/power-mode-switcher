using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using PowerModeSwitcher.Services;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Forms = System.Windows.Forms;
using DrawingIcon = System.Drawing.Icon;

namespace PowerModeSwitcher;

/// <summary>
/// The notification area (tray) icon: shows the current stop as ice cube, sailboat or rocket,
/// and owns the app's right-click menu (opened from the tray icon or from the widget).
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const string AppName = "Power Mode Switcher";

    private readonly PowerModeService _powerMode;
    private readonly ModeSetService _modeSets;
    private readonly SettingsService _settings;
    private readonly WidgetWindow _widget;

    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Dictionary<Stop, DrawingIcon> _icons;

    // One renderer per Windows color mode; picked each time the menu opens.
    private readonly ThemedMenuRenderer _lightRenderer = new(MenuPalette.Light);
    private readonly ThemedMenuRenderer _darkRenderer = new(MenuPalette.Dark);

    // Mode items, bottom stop first, labelled from the current mode set.
    private readonly Dictionary<Stop, Forms.ToolStripMenuItem> _stopItems = new();

    private readonly Forms.ToolStripMenuItem _modeOptionsItem;
    private readonly Forms.ToolStripMenuItem _autoSetItem;
    private readonly Forms.ToolStripMenuItem _standardSetItem;
    private readonly Forms.ToolStripMenuItem _classicSetItem;
    private readonly Forms.ToolStripMenuItem _showHideItem;
    private readonly Forms.ToolStripMenuItem _lockItem;
    private readonly Forms.ToolStripMenuItem _modeNamesItem;
    private readonly Forms.ToolStripMenuItem _halfSizeItem;
    private readonly Forms.ToolStripMenuItem _startupItem;

    public TrayIcon(PowerModeService powerMode, ModeSetService modeSets, SettingsService settings, WidgetWindow widget)
    {
        _powerMode = powerMode;
        _modeSets = modeSets;
        _settings = settings;
        _widget = widget;

        // The pictures belong to positions in every set: ice cube, sailboat, rocket from bottom to top.
        _icons = new Dictionary<Stop, DrawingIcon>
        {
            [Stop.Bottom] = LoadIcon("knob-battery-ice.ico"),
            [Stop.Middle] = LoadIcon("knob-balanced-sailboat.ico"),
            [Stop.Top] = LoadIcon("knob-performance-rocket.ico"),
        };

        _menu = new Forms.ContextMenuStrip { Padding = new Forms.Padding(0, 4, 0, 4) };
        _menu.HandleCreated += (_, _) => RoundCorners(_menu.Handle);

        // Header with the version, so it's easy to tell which release is running.
        _menu.Items.Add(new Forms.ToolStripMenuItem($"{AppName} {AppVersion()}") { Enabled = false });
        _menu.Items.Add(new Forms.ToolStripSeparator());

        foreach (Stop stop in new[] { Stop.Bottom, Stop.Middle, Stop.Top })
        {
            _stopItems[stop] = AddItem(_menu, "", () => SetStop(stop));
        }

        // Power mode options: which set of modes this PC's Windows Settings offers.
        _modeOptionsItem = new Forms.ToolStripMenuItem("Power mode options");
        _autoSetItem = AddItem(_modeOptionsItem.DropDown, "Automatic", () => _modeSets.Choose(ModeSetChoice.Auto, ModeSetOrigin.Menu));
        _standardSetItem = AddItem(_modeOptionsItem.DropDown, $"Standard: {ModeSet.Standard.Summary}",
            () => _modeSets.Choose(ModeSetChoice.Standard, ModeSetOrigin.Menu));
        _classicSetItem = AddItem(_modeOptionsItem.DropDown, $"Classic: {ModeSet.Classic.Summary}",
            () => _modeSets.Choose(ModeSetChoice.Classic, ModeSetOrigin.Menu));
        _modeOptionsItem.DropDown.Padding = new Forms.Padding(0, 4, 0, 4);
        _modeOptionsItem.DropDown.HandleCreated += (_, _) => RoundCorners(_modeOptionsItem.DropDown.Handle);
        _menu.Items.Add(_modeOptionsItem);
        _menu.Items.Add(new Forms.ToolStripSeparator());

        _showHideItem = AddItem(_menu, "Hide widget", ToggleWidget);
        _lockItem = AddItem(_menu, "Lock widget position", ToggleLock);
        _modeNamesItem = AddItem(_menu, "Show mode names", () => _widget.SetModeNamesVisible(!_settings.Settings.ShowModeNames));
        _halfSizeItem = AddItem(_menu, "Half-size widget", () => _widget.SetHalfSize(!_settings.Settings.HalfSize));
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _startupItem = AddItem(_menu, "Start with Windows", ToggleStartWithWindows);
        AddItem(_menu, "Copy diagnostics", CopyDiagnostics);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        AddItem(_menu, "Exit", () => Application.Current.Shutdown());
        _menu.Opening += (_, _) =>
        {
            ApplyTheme();
            RefreshMenu();
        };

        _notifyIcon = new Forms.NotifyIcon { ContextMenuStrip = _menu };
        _notifyIcon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) ToggleWidget();
        };

        _powerMode.Changed += PowerOrModeSet_Changed;
        _modeSets.Changed += PowerOrModeSet_Changed;
        _modeSets.Notice += ModeSets_Notice;
        UpdateIconAndTooltip();
        _notifyIcon.Visible = true;
    }

    public void Dispose()
    {
        _powerMode.Changed -= PowerOrModeSet_Changed;
        _modeSets.Changed -= PowerOrModeSet_Changed;
        _modeSets.Notice -= ModeSets_Notice;

        // Hide first so no "ghost" icon is left in the tray.
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        foreach (DrawingIcon icon in _icons.Values) icon.Dispose();
    }

    /// <summary>Opens the same menu at the mouse pointer (used when the widget is right-clicked).</summary>
    public void ShowMenuAtCursor() => _menu.Show(Forms.Cursor.Position);

    private void PowerOrModeSet_Changed(object? sender, EventArgs e) => UpdateIconAndTooltip();

    private void ModeSets_Notice(object? sender, string message) => ShowInfo(message);

    /// <summary>Light or dark menu, matching the Windows app color mode right now.</summary>
    private void ApplyTheme()
    {
        ThemedMenuRenderer renderer = MenuPalette.WindowsUsesDarkMode() ? _darkRenderer : _lightRenderer;
        if (_menu.Renderer == renderer) return;

        foreach (Forms.ToolStrip strip in new Forms.ToolStrip[] { _menu, _modeOptionsItem.DropDown })
        {
            strip.Renderer = renderer;
            strip.BackColor = renderer.Palette.Background;
            strip.ForeColor = renderer.Palette.Text;
        }
    }

    /// <summary>Windows 11 style rounded corners on a menu (ignored on older Windows).</summary>
    private static unsafe void RoundCorners(nint hwnd)
    {
        DWM_WINDOW_CORNER_PREFERENCE preference = DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_ROUND;
        PInvoke.DwmSetWindowAttribute(new HWND(hwnd), DWMWINDOWATTRIBUTE.DWMWA_WINDOW_CORNER_PREFERENCE,
            &preference, (uint)sizeof(DWM_WINDOW_CORNER_PREFERENCE));
    }

    private static string AppVersion() =>
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "";

    /// <summary>The current stop in the current set, or null for the neutral state.</summary>
    private Stop? CurrentStop()
    {
        PowerModeReading reading = _powerMode.Current;
        return reading.Error is null && reading.Mode is Guid mode ? _modeSets.Current.StopOf(mode) : null;
    }

    private void UpdateIconAndTooltip()
    {
        // The neutral state (a mode with no stop in this set, or unreadable) uses the sailboat;
        // the tooltip names the mode.
        _notifyIcon.Icon = _icons[CurrentStop() ?? Stop.Middle];
        _notifyIcon.Text = DescribeForTooltip(_powerMode.Current, _modeSets.Current);
    }

    private static string DescribeForTooltip(PowerModeReading reading, ModeSet set)
    {
        var details = new List<string>();
        if (reading.Source == PowerSource.PluggedIn) details.Add("plugged in");
        if (reading.Source == PowerSource.OnBattery) details.Add("on battery");
        if (reading.EnergySaverOn) details.Add("Energy Saver on");

        string label = reading.Error is null && reading.Mode is Guid mode && ModeSet.IsKnown(mode) ? set.LabelFor(mode) : "unknown";
        string text = $"Power mode: {label}";
        if (details.Count > 0) text += $" ({string.Join(", ", details)})";
        if (!reading.BalancedPlan) text += "\nPower mode needs the Balanced power plan";
        return text;
    }

    private void RefreshMenu()
    {
        ModeSet set = _modeSets.Current;
        Stop? current = CurrentStop();
        foreach ((Stop stop, Forms.ToolStripMenuItem item) in _stopItems)
        {
            item.Text = set[stop].Label;
            item.Checked = current == stop;
        }

        _autoSetItem.Text = _modeSets.Choice != ModeSetChoice.Auto ? "Automatic"
            : _modeSets.Detected is ModeSetKind detected ? $"Automatic ({detected}, detected)"
            : _modeSets.UntilDetected is ModeSetKind kept ? $"Automatic ({kept} for now)"
            : "Automatic (not decided yet)";
        _autoSetItem.Checked = _modeSets.Choice == ModeSetChoice.Auto;
        _standardSetItem.Checked = _modeSets.Choice == ModeSetChoice.Standard;
        _classicSetItem.Checked = _modeSets.Choice == ModeSetChoice.Classic;

        _showHideItem.Text = _widget.IsVisible ? "Hide widget" : "Show widget";
        _lockItem.Checked = _settings.Settings.PositionLocked;
        _modeNamesItem.Checked = _settings.Settings.ShowModeNames;
        _halfSizeItem.Checked = _settings.Settings.HalfSize;
        _startupItem.Checked = StartupService.IsEnabled();
    }

    private void SetStop(Stop stop)
    {
        if (!_modeSets.TrySetStop(stop, out string? error))
        {
            ShowWarning($"Couldn't switch to {_modeSets.Current[stop].Label}. {error}");
        }
    }

    private void CopyDiagnostics()
    {
        string text = DiagnosticsService.Build(_powerMode, _modeSets, _settings);
        try
        {
            Clipboard.SetText(text);
            ShowInfo("Diagnostics copied to the clipboard. Paste them into an email or a GitHub issue.");
        }
        catch (ExternalException)
        {
            // Another app is holding the clipboard.
            ShowWarning("Couldn't copy to the clipboard. Please try again.");
        }
    }

    private void ToggleWidget() => _widget.SetWidgetVisible(!_widget.IsVisible);

    private void ToggleLock()
    {
        _settings.Settings.PositionLocked = !_settings.Settings.PositionLocked;
        _settings.Save();
    }

    private void ToggleStartWithWindows()
    {
        bool enable = !StartupService.IsEnabled();
        if (!StartupService.TryApply(enable, out string? error))
        {
            ShowWarning($"Couldn't change Start with Windows. {error}");
            return;
        }

        _settings.Settings.StartWithWindows = enable;
        _settings.Save();
    }

    private void ShowInfo(string message) =>
        _notifyIcon.ShowBalloonTip(5000, AppName, message, Forms.ToolTipIcon.Info);

    private void ShowWarning(string message) =>
        _notifyIcon.ShowBalloonTip(5000, AppName, message, Forms.ToolTipIcon.Warning);

    private static Forms.ToolStripMenuItem AddItem(Forms.ToolStrip strip, string text, Action onClick)
    {
        var item = new Forms.ToolStripMenuItem(text, null, (_, _) => onClick());
        strip.Items.Add(item);
        return item;
    }

    /// <summary>Loads an icon from the app's resources at the size the tray uses on this display.</summary>
    private static DrawingIcon LoadIcon(string fileName)
    {
        var uri = new Uri($"pack://application:,,,/Assets/{fileName}");
        using Stream stream = Application.GetResourceStream(uri)!.Stream;
        return new DrawingIcon(stream, Forms.SystemInformation.SmallIconSize);
    }
}
