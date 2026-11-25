using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using PowerModeSwitcher.Services;

namespace PowerModeSwitcher;

/// <summary>
/// The floating widget: a vertical track with a round knob that slides between
/// Performance (top), Balanced (middle) and Battery (bottom).
/// </summary>
public partial class WidgetWindow : Window
{
    // Knob geometry, in track coordinates. Knob centers sit at 50, 150 and 250.
    private const double KnobSize = 80;
    private const double TopStop = 50;
    private const double MiddleStop = 150;
    private const double BottomStop = 250;
    private const double DragThreshold = 4;

    // Panel width with mode names, and without (border 1 + padding 19 + track 88 + padding 19 + border 1).
    private const double PanelWidthWithNames = 230;
    private const double PanelWidthTrackOnly = 128;

    // 200 ms ease-out with no bounce: the same curve as the design canvas.
    private static readonly TimeSpan KnobSlideDuration = TimeSpan.FromMilliseconds(200);
    private static readonly KeySpline KnobSlideEase = Frozen(new KeySpline(0.2, 0.8, 0.2, 1));

    private static readonly Brush ActiveLabelBrush = Brushes.White;
    private static readonly Brush InactiveLabelBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x9E, 0xFF, 0xFF, 0xFF))); // 62%

    private static readonly TimeSpan ErrorTipDuration = TimeSpan.FromSeconds(4);
    private static readonly Brush ErrorTipBackground = Frozen(new SolidColorBrush(Color.FromRgb(0x0F, 0x2F, 0x4C)));
    private static readonly Brush ErrorTipBorder = Frozen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)));

    private readonly PowerModeService _powerMode;
    private readonly SettingsService _settings;
    private IntPtr _hwnd;

    // Moving the whole widget by dragging its background (screen pixels).
    private bool _movingWindow;
    private bool _windowMoved;
    private (int X, int Y) _moveStartCursor;
    private (int X, int Y) _moveStartWindow;

    private double _knobTargetTop = double.NaN;
    private bool _trackPressed;
    private bool _dragging;
    private double _pressY;
    private double _grabOffset;
    private Border? _pressedLabel;

    private ToolTip? _errorTip;
    private int _errorTipToken;

    public WidgetWindow(PowerModeService powerMode, SettingsService settings)
    {
        InitializeComponent();

        _powerMode = powerMode;
        _settings = settings;

        _powerMode.Changed += PowerMode_Changed;
        SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
        Closed += (_, _) =>
        {
            _powerMode.Changed -= PowerMode_Changed;
            SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;

            // "Hide widget" only hides. If the window is actually closed (Exit, or Windows asking
            // the app to close), exit the whole app so the tray icon is removed too.
            Application.Current.Shutdown();
        };

        ShowReading(_powerMode.Current, animate: false);
        ApplyLayout(keepNearestCorner: false);
    }

    /// <summary>Shows or hides the mode names next to the track, and remembers the choice.</summary>
    public void SetModeNamesVisible(bool visible)
    {
        _settings.Settings.ShowModeNames = visible;
        _settings.Save();
        ApplyLayout(keepNearestCorner: true);
    }

    /// <summary>Switches between full and half size, and remembers the choice.</summary>
    public void SetHalfSize(bool halfSize)
    {
        _settings.Settings.HalfSize = halfSize;
        _settings.Save();
        ApplyLayout(keepNearestCorner: true);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;

        // Desktop level: bottom of the z-order, no focus stealing, not in taskbar or Alt+Tab.
        DesktopZOrder.Attach(this);

        // Saved spot if it's still on a screen, otherwise bottom-right of the main screen.
        ScreenPlacement.PlaceOnScreen(_hwnd, _settings.Settings.WindowLeft, _settings.Settings.WindowTop);
    }

    /// <summary>Shows or hides the widget and remembers the choice for next time.</summary>
    public void SetWidgetVisible(bool visible)
    {
        _settings.Settings.WidgetVisible = visible;
        _settings.Save();

        if (!visible)
        {
            Hide();
            return;
        }

        Show();
        if (_hwnd != IntPtr.Zero) ScreenPlacement.EnsureOnScreen(_hwnd);
    }

    private void DisplaySettingsChanged(object? sender, EventArgs e)
    {
        // A monitor was unplugged or the layout changed. Bring the widget back if it's off-screen,
        // but don't save: if the monitor comes back, the next launch uses the saved spot again.
        if (_hwnd != IntPtr.Zero) ScreenPlacement.EnsureOnScreen(_hwnd);
    }

    private void PowerMode_Changed(object? sender, EventArgs e) => ShowReading(_powerMode.Current, animate: true);

    // ---- Layout: mode names on/off, full or half size ----

    private void ApplyLayout(bool keepNearestCorner)
    {
        bool showNames = _settings.Settings.ShowModeNames;
        LabelsPanel.Visibility = showNames ? Visibility.Visible : Visibility.Collapsed;
        LabelGapColumn.Width = new GridLength(showNames ? 16 : 0);
        Panel.Width = showNames ? PanelWidthWithNames : PanelWidthTrackOnly;
        Panel.Padding = showNames ? new Thickness(19, 19, 5, 19) : new Thickness(19);
        StatusPill.MaxWidth = Panel.Width;

        double scale = _settings.Settings.HalfSize ? 0.5 : 1;
        RootScale.ScaleX = scale;
        RootScale.ScaleY = scale;

        FitWindowToContent(keepNearestCorner);
    }

    /// <summary>
    /// Sizes the window to fit the widget. When keepNearestCorner is true (the user changed the
    /// size), the corner nearest the screen edge stays where it was and the new spot is saved.
    /// </summary>
    private void FitWindowToContent(bool keepNearestCorner = false)
    {
        // InvalidateMeasure first: a change deeper in the widget (like the panel width) does not
        // mark Root itself as needing a new measurement until the next layout pass.
        Root.InvalidateMeasure();
        Root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double width = Math.Ceiling(Root.DesiredSize.Width);
        double height = Math.Ceiling(Root.DesiredSize.Height);
        if (width == Width && height == Height) return;

        (int X, int Y)? newPosition = keepNearestCorner && _hwnd != IntPtr.Zero
            ? ScreenPlacement.PositionKeepingNearestCorner(_hwnd, width, height)
            : null;

        Width = width;
        Height = height;

        if (newPosition is (int x, int y))
        {
            ScreenPlacement.MoveWindow(_hwnd, x, y);
            SavePosition();
        }
    }

    // ---- Showing state ----

    private void ShowReading(PowerModeReading reading, bool animate)
    {
        string status = DescribeStatus(reading);
        if (StatusText.Text != status)
        {
            StatusText.Text = status;
            FitWindowToContent(); // a long status can wrap onto a second line
        }

        // While the knob is being dragged, leave it under the mouse.
        if (_dragging) return;

        ShowMode(reading.Error is null ? reading.Mode : PowerMode.Unknown, animate);
    }

    private void ShowMode(PowerMode mode, bool animate)
    {
        HighlightLabel(mode);

        if (!HasStop(mode))
        {
            // Neutral state: no knob, no highlighted label (the status line names the mode).
            // Clicking a stop still works.
            Knob.Visibility = Visibility.Hidden;
            return;
        }

        bool wasHidden = Knob.Visibility != Visibility.Visible;
        Knob.Visibility = Visibility.Visible;
        Knob.Source = KnobImageFor(mode);
        MoveKnob(StopFor(mode) - KnobSize / 2, animate && !wasHidden);
    }

    private void HighlightLabel(PowerMode mode)
    {
        StyleLabel(PerformanceText, mode == PowerMode.BestPerformance);
        StyleLabel(BalancedText, mode == PowerMode.Balanced);
        StyleLabel(BatteryText, mode == PowerMode.BestPowerEfficiency);
    }

    private static void StyleLabel(TextBlock label, bool active)
    {
        label.FontSize = active ? 15 : 13;
        label.FontWeight = active ? FontWeights.SemiBold : FontWeights.Medium;
        label.Foreground = active ? ActiveLabelBrush : InactiveLabelBrush;
    }

    private void MoveKnob(double top, bool animate)
    {
        if (animate && top == _knobTargetTop) return; // already there or on its way
        _knobTargetTop = top;

        double from = Canvas.GetTop(Knob);
        SetKnobTop(top);
        if (!animate || from == top) return;

        var slide = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        slide.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        slide.KeyFrames.Add(new SplineDoubleKeyFrame(top, KeyTime.FromTimeSpan(KnobSlideDuration), KnobSlideEase));
        Knob.BeginAnimation(Canvas.TopProperty, slide);
    }

    /// <summary>Puts the knob at a position right away, cancelling any slide in progress.</summary>
    private void SetKnobTop(double top)
    {
        Knob.BeginAnimation(Canvas.TopProperty, null);
        Canvas.SetTop(Knob, top);
    }

    private static string DescribeStatus(PowerModeReading reading)
    {
        if (reading.Error is not null) return "Can't read power mode";

        string text = reading.Source switch
        {
            PowerSource.OnBattery => "On battery",
            PowerSource.PluggedIn => "Plugged in",
            _ => "Power source unknown",
        };
        if (reading.EnergySaverOn) text += " · Energy Saver on";
        if (!HasStop(reading.Mode))
        {
            string name = reading.Mode == PowerMode.Unknown ? "Unknown power mode" : PowerModeService.DisplayName(reading.Mode);
            text = name + " · " + text;
        }
        return text;
    }

    /// <summary>
    /// True for modes with a stop on the three-stop widget. Unknown and Better performance (3af9b8d9)
    /// have none, so the widget shows them without guessing a position.
    /// </summary>
    private static bool HasStop(PowerMode mode) =>
        mode is PowerMode.BestPowerEfficiency or PowerMode.Balanced or PowerMode.BestPerformance;

    // ---- Changing the mode ----

    private void SelectMode(PowerMode mode)
    {
        ShowMode(mode, animate: true); // respond right away

        PowerModeReading current = _powerMode.Current;
        bool alreadySet = current.Error is null && current.Mode == mode;
        if (!alreadySet && !_powerMode.TrySetMode(mode, out string? error))
        {
            ShowError($"Couldn't switch to {PowerModeService.DisplayName(mode)}.\n{error}");
        }

        // Always end up showing what Windows actually reports (snaps back if the change failed).
        ShowReading(_powerMode.Current, animate: true);
    }

    private async void ShowError(string message)
    {
        // Navy with white text, like the widget, so it reads well in light and dark mode.
        _errorTip ??= new ToolTip
        {
            PlacementTarget = Panel,
            Placement = PlacementMode.Bottom,
            Background = ErrorTipBackground,
            Foreground = Brushes.White,
            BorderBrush = ErrorTipBorder,
            Padding = new Thickness(10, 6, 10, 6),
        };
        _errorTip.Content = message;
        _errorTip.IsOpen = true;

        int token = ++_errorTipToken;
        await Task.Delay(ErrorTipDuration);
        if (token == _errorTipToken) _errorTip.IsOpen = false;
    }

    // ---- Mouse: track (click a stop or drag the knob) ----

    private void Track_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true; // don't start moving the window

        _pressY = e.GetPosition(Track).Y;
        double knobCenter = Canvas.GetTop(Knob) + KnobSize / 2;
        bool onKnob = Knob.Visibility == Visibility.Visible && Math.Abs(_pressY - knobCenter) <= KnobSize / 2;
        _grabOffset = onKnob ? _pressY - knobCenter : 0;

        _trackPressed = true;
        _dragging = false;
        Track.CaptureMouse();
    }

    private void Track_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_trackPressed) return;

        double y = e.GetPosition(Track).Y;
        if (!_dragging)
        {
            if (Math.Abs(y - _pressY) < DragThreshold) return;
            _dragging = true;
            Knob.Visibility = Visibility.Visible;
        }

        double center = Math.Clamp(y - _grabOffset, TopStop, BottomStop);
        SetKnobTop(center - KnobSize / 2);
        _knobTargetTop = double.NaN;

        // Preview the picture and label of the nearest stop while dragging.
        PowerMode nearest = NearestMode(center);
        Knob.Source = KnobImageFor(nearest);
        HighlightLabel(nearest);
    }

    private void Track_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_trackPressed) return;
        e.Handled = true;

        bool wasDragging = _dragging;
        _trackPressed = false;
        _dragging = false;
        Track.ReleaseMouseCapture();

        double y = wasDragging ? Canvas.GetTop(Knob) + KnobSize / 2 : e.GetPosition(Track).Y;
        SelectMode(NearestMode(y));
    }

    private void Track_LostMouseCapture(object sender, MouseEventArgs e)
    {
        // Capture lost mid-drag (for example, a system dialog popped up): put the knob back.
        if (!_trackPressed) return;
        _trackPressed = false;
        _dragging = false;
        ShowReading(_powerMode.Current, animate: true);
    }

    // ---- Mouse: labels ----

    private void Label_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true; // don't start moving the window
        _pressedLabel = (Border)sender;
    }

    private void Label_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var label = (Border)sender;
        if (label == _pressedLabel)
        {
            e.Handled = true;
            SelectMode(Enum.Parse<PowerMode>((string)label.Tag));
        }
        _pressedLabel = null;
    }

    // ---- Mouse: background (move the widget) ----
    // Moved by hand with SetWindowPos instead of DragMove, so the widget is never activated
    // or raised while dragging.

    private void Background_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_settings.Settings.PositionLocked || _hwnd == IntPtr.Zero) return;

        _moveStartCursor = ScreenPlacement.GetCursorPosition();
        _moveStartWindow = ScreenPlacement.GetWindowPosition(_hwnd);
        _movingWindow = true;
        _windowMoved = false;
        ((UIElement)sender).CaptureMouse();
        e.Handled = true;
    }

    private void Background_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_movingWindow) return;

        (int x, int y) = ScreenPlacement.GetCursorPosition();
        int dx = x - _moveStartCursor.X;
        int dy = y - _moveStartCursor.Y;
        if (!_windowMoved && Math.Abs(dx) < 2 && Math.Abs(dy) < 2) return;

        _windowMoved = true;
        ScreenPlacement.MoveWindow(_hwnd, _moveStartWindow.X + dx, _moveStartWindow.Y + dy);
    }

    private void Background_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_movingWindow) return;
        e.Handled = true;
        ((UIElement)sender).ReleaseMouseCapture(); // finishes in Background_LostMouseCapture
    }

    private void Background_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!_movingWindow || e.OriginalSource != sender) return;
        _movingWindow = false;

        if (_windowMoved) SavePosition();
    }

    private void SavePosition()
    {
        (int left, int top) = ScreenPlacement.GetWindowPosition(_hwnd);
        _settings.Settings.WindowLeft = left;
        _settings.Settings.WindowTop = top;
        _settings.Save();
    }

    // ---- Right-click menu ----

    /// <summary>Raised when the widget is right-clicked. The app opens the tray menu at the mouse.</summary>
    public event EventHandler? MenuRequested;

    private void Background_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        // Let the menu take focus so it closes when you click elsewhere. The widget stays at the bottom.
        DesktopZOrder.AllowMenuFocus(this);
        MenuRequested?.Invoke(this, EventArgs.Empty);
    }

    // ---- Helpers ----

    private static double StopFor(PowerMode mode) => mode switch
    {
        PowerMode.BestPerformance => TopStop,
        PowerMode.BestPowerEfficiency => BottomStop,
        _ => MiddleStop,
    };

    private static PowerMode NearestMode(double y) =>
        y < (TopStop + MiddleStop) / 2 ? PowerMode.BestPerformance
        : y < (MiddleStop + BottomStop) / 2 ? PowerMode.Balanced
        : PowerMode.BestPowerEfficiency;

    private ImageSource KnobImageFor(PowerMode mode) => (ImageSource)FindResource(mode switch
    {
        PowerMode.BestPerformance => "KnobPerformance",
        PowerMode.BestPowerEfficiency => "KnobBattery",
        _ => "KnobBalanced",
    });

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
