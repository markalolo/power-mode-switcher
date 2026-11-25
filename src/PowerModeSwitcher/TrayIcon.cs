using System.IO;
using System.Windows;
using PowerModeSwitcher.Services;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Forms = System.Windows.Forms;
using DrawingIcon = System.Drawing.Icon;

namespace PowerModeSwitcher;

/// <summary>
/// The notification area (tray) icon: shows the current mode as ice cube, sailboat or rocket,
/// and owns the app's right-click menu (opened from the tray icon or from the widget).
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const string AppName = "Power Mode Switcher";

    private readonly PowerModeService _powerMode;
    private readonly SettingsService _settings;
    private readonly WidgetWindow _widget;

    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Dictionary<PowerMode, DrawingIcon> _icons;

    // One renderer per Windows color mode; picked each time the menu opens.
    private readonly ThemedMenuRenderer _lightRenderer = new(MenuPalette.Light);
    private readonly ThemedMenuRenderer _darkRenderer = new(MenuPalette.Dark);

    private readonly Forms.ToolStripMenuItem _efficiencyItem;
    private readonly Forms.ToolStripMenuItem _balancedItem;
    private readonly Forms.ToolStripMenuItem _performanceItem;
    private readonly Forms.ToolStripMenuItem _showHideItem;
    private readonly Forms.ToolStripMenuItem _lockItem;
    private readonly Forms.ToolStripMenuItem _modeNamesItem;
    private readonly Forms.ToolStripMenuItem _halfSizeItem;
    private readonly Forms.ToolStripMenuItem _startupItem;

    public TrayIcon(PowerModeService powerMode, SettingsService settings, WidgetWindow widget)
    {
        _powerMode = powerMode;
        _settings = settings;
        _widget = widget;

        _icons = new Dictionary<PowerMode, DrawingIcon>
        {
            [PowerMode.BestPowerEfficiency] = LoadIcon("knob-battery-ice.ico"),
            [PowerMode.Balanced] = LoadIcon("knob-balanced-sailboat.ico"),
            [PowerMode.BestPerformance] = LoadIcon("knob-performance-rocket.ico"),
        };

        _menu = new Forms.ContextMenuStrip { Padding = new Forms.Padding(0, 4, 0, 4) };
        _menu.HandleCreated += (_, _) => RoundCorners(_menu.Handle);

        // Header with the version, so it's easy to tell which release is running.
        _menu.Items.Add(new Forms.ToolStripMenuItem($"{AppName} {AppVersion()}") { Enabled = false });
        _menu.Items.Add(new Forms.ToolStripSeparator());

        _efficiencyItem = AddItem(PowerModeService.DisplayName(PowerMode.BestPowerEfficiency), () => SetMode(PowerMode.BestPowerEfficiency));
        _balancedItem = AddItem(PowerModeService.DisplayName(PowerMode.Balanced), () => SetMode(PowerMode.Balanced));
        _performanceItem = AddItem(PowerModeService.DisplayName(PowerMode.BestPerformance), () => SetMode(PowerMode.BestPerformance));
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _showHideItem = AddItem("Hide widget", ToggleWidget);
        _lockItem = AddItem("Lock widget position", ToggleLock);
        _modeNamesItem = AddItem("Show mode names", () => _widget.SetModeNamesVisible(!_settings.Settings.ShowModeNames));
        _halfSizeItem = AddItem("Half-size widget", () => _widget.SetHalfSize(!_settings.Settings.HalfSize));
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _startupItem = AddItem("Start with Windows", ToggleStartWithWindows);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        AddItem("Exit", () => Application.Current.Shutdown());
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

        _powerMode.Changed += PowerMode_Changed;
        UpdateIconAndTooltip();
        _notifyIcon.Visible = true;
    }

    public void Dispose()
    {
        _powerMode.Changed -= PowerMode_Changed;

        // Hide first so no "ghost" icon is left in the tray.
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        foreach (DrawingIcon icon in _icons.Values) icon.Dispose();
    }

    /// <summary>Opens the same menu at the mouse pointer (used when the widget is right-clicked).</summary>
    public void ShowMenuAtCursor() => _menu.Show(Forms.Cursor.Position);

    private void PowerMode_Changed(object? sender, EventArgs e) => UpdateIconAndTooltip();

    /// <summary>Light or dark menu, matching the Windows app color mode right now.</summary>
    private void ApplyTheme()
    {
        ThemedMenuRenderer renderer = MenuPalette.WindowsUsesDarkMode() ? _darkRenderer : _lightRenderer;
        if (_menu.Renderer == renderer) return;

        _menu.Renderer = renderer;
        _menu.BackColor = renderer.Palette.Background;
        _menu.ForeColor = renderer.Palette.Text;
    }

    /// <summary>Windows 11 style rounded corners on the menu (ignored on older Windows).</summary>
    private static unsafe void RoundCorners(nint hwnd)
    {
        DWM_WINDOW_CORNER_PREFERENCE preference = DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_ROUND;
        PInvoke.DwmSetWindowAttribute(new HWND(hwnd), DWMWINDOWATTRIBUTE.DWMWA_WINDOW_CORNER_PREFERENCE,
            &preference, (uint)sizeof(DWM_WINDOW_CORNER_PREFERENCE));
    }

    private static string AppVersion() =>
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "";

    private void UpdateIconAndTooltip()
    {
        PowerModeReading reading = _powerMode.Current;
        PowerMode mode = reading.Error is null ? reading.Mode : PowerMode.Unknown;

        // There is no icon for an unknown mode; the sailboat is the neutral choice and the tooltip says "Unknown".
        _notifyIcon.Icon = _icons.TryGetValue(mode, out DrawingIcon? icon) ? icon : _icons[PowerMode.Balanced];
        _notifyIcon.Text = DescribeForTooltip(reading, mode);
    }

    private static string DescribeForTooltip(PowerModeReading reading, PowerMode mode)
    {
        var details = new List<string>();
        if (reading.Source == PowerSource.PluggedIn) details.Add("plugged in");
        if (reading.Source == PowerSource.OnBattery) details.Add("on battery");
        if (reading.EnergySaverOn) details.Add("Energy Saver on");

        string text = $"Power mode: {PowerModeService.DisplayName(mode)}";
        return details.Count == 0 ? text : $"{text} ({string.Join(", ", details)})";
    }

    private void RefreshMenu()
    {
        PowerModeReading reading = _powerMode.Current;
        PowerMode mode = reading.Error is null ? reading.Mode : PowerMode.Unknown;

        _efficiencyItem.Checked = mode == PowerMode.BestPowerEfficiency;
        _balancedItem.Checked = mode == PowerMode.Balanced;
        _performanceItem.Checked = mode == PowerMode.BestPerformance;
        _showHideItem.Text = _widget.IsVisible ? "Hide widget" : "Show widget";
        _lockItem.Checked = _settings.Settings.PositionLocked;
        _modeNamesItem.Checked = _settings.Settings.ShowModeNames;
        _halfSizeItem.Checked = _settings.Settings.HalfSize;
        _startupItem.Checked = StartupService.IsEnabled();
    }

    private void SetMode(PowerMode mode)
    {
        if (!_powerMode.TrySetMode(mode, out string? error))
        {
            ShowWarning($"Couldn't switch to {PowerModeService.DisplayName(mode)}. {error}");
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

    private void ShowWarning(string message) =>
        _notifyIcon.ShowBalloonTip(5000, AppName, message, Forms.ToolTipIcon.Warning);

    private Forms.ToolStripMenuItem AddItem(string text, Action onClick)
    {
        var item = new Forms.ToolStripMenuItem(text, null, (_, _) => onClick());
        _menu.Items.Add(item);
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
