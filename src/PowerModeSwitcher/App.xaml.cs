using System.IO;
using System.Windows;
using PowerModeSwitcher.Services;

namespace PowerModeSwitcher;

public partial class App : Application
{
    // Names shared by every copy of the app in this Windows sign-in ("Local\" = this session only).
    private const string InstanceMutexName = @"Local\PowerModeSwitcher-3F6C0A8E-5B1D-4E2A-9C47-1D8B2E6F9A03";
    private const string ShowWidgetEventName = @"Local\PowerModeSwitcher-3F6C0A8E-5B1D-4E2A-9C47-1D8B2E6F9A03-ShowWidget";
    private const string ExitEventName = @"Local\PowerModeSwitcher-3F6C0A8E-5B1D-4E2A-9C47-1D8B2E6F9A03-Exit";

    // "PowerModeSwitcher.exe --exit" asks a running copy to close cleanly (used before rebuilding).
    private const string ExitArgument = "--exit";

    // "PowerModeSwitcher.exe --diagnostics [file]" writes the Copy diagnostics report to a file.
    private const string DiagnosticsArgument = "--diagnostics";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showWidgetSignal;
    private RegisteredWaitHandle? _showWidgetWait;
    private EventWaitHandle? _exitSignal;
    private RegisteredWaitHandle? _exitWait;

    private PowerModeService? _powerMode;
    private ModeSetService? _modeSets;
    private SettingsService? _settings;
    private WidgetWindow? _widget;
    private TrayIcon? _tray;
    private ModeSetQuestionWindow? _question;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // "PowerModeSwitcher.exe --diagnostics [file]": write the Copy diagnostics report to a file and quit.
        // Read only, so it runs alongside a running copy and changes no settings.
        int diagnosticsIndex = Array.FindIndex(e.Args, arg => string.Equals(arg, DiagnosticsArgument, StringComparison.OrdinalIgnoreCase));
        if (diagnosticsIndex >= 0)
        {
            string path = diagnosticsIndex + 1 < e.Args.Length
                ? e.Args[diagnosticsIndex + 1]
                : Path.Combine(Path.GetTempPath(), "PowerModeSwitcher-diagnostics.txt");
            WriteDiagnosticsAndExit(path);
            return;
        }

        bool exitRequested = e.Args.Any(arg => string.Equals(arg, ExitArgument, StringComparison.OrdinalIgnoreCase));

        if (!TryBecomeOnlyInstance(exitRequested) || exitRequested)
        {
            // Another copy is already running and has been asked to show its widget (or to exit),
            // or we were only asked to close a running copy and there wasn't one.
            Shutdown();
            return;
        }

        // The app lives in the tray, so it keeps running when the widget is hidden.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        System.Windows.Forms.Application.EnableVisualStyles();

        _settings = new SettingsService();
        _settings.Load();

#if !DEBUG
        // Keep the Run-key entry pointing at this .exe (fixes the path if the .exe was moved).
        // Debug (development) builds skip this, so testing a new build never takes over
        // Start with Windows from the installed copy. The tray checkbox still works in both.
        StartupService.TryApply(_settings.Settings.StartWithWindows, out _);
#endif

        _powerMode = new PowerModeService();
        _powerMode.Start();
        _modeSets = new ModeSetService(_settings, _powerMode);

        _widget = new WidgetWindow(_powerMode, _modeSets, _settings);
        MainWindow = _widget;
        if (_settings.Settings.WidgetVisible) _widget.Show();

        _tray = new TrayIcon(_powerMode, _modeSets, _settings, _widget);

        // Right-clicking the widget opens the same menu as the tray icon.
        _widget.MenuRequested += (_, _) => _tray.ShowMenuAtCursor();

        // Detect the mode set now that the widget and tray are listening, then ask once if it can't tell.
        _modeSets.Changed += (_, _) => UpdateModeSetQuestion();
        _widget.IsVisibleChanged += (_, _) => UpdateModeSetQuestion();
        _modeSets.Start();
        UpdateModeSetQuestion();
    }

    /// <summary>
    /// Writes the Copy diagnostics report to a file, then quits. Waits a moment first so Windows can
    /// deliver the power plan and effective mode notifications the report includes.
    /// </summary>
    private void WriteDiagnosticsAndExit(string path)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var settings = new SettingsService();
        settings.Load();
        var power = new PowerModeService();
        power.Start();
        var modeSets = new ModeSetService(settings, power); // not started: never saves anything

        var wait = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        wait.Tick += (_, _) =>
        {
            wait.Stop();
            power.Refresh();
            try
            {
                File.WriteAllText(path, DiagnosticsService.Build(power, modeSets, settings));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Nowhere to report it: there is no window in this mode.
            }
            power.Dispose();
            Shutdown();
        };
        wait.Start();
    }

    /// <summary>
    /// Shows the one-time "which power modes does Windows show" question while the mode set is
    /// undecided and the widget is on screen, and closes it if detection decides first.
    /// </summary>
    private void UpdateModeSetQuestion()
    {
        if (_modeSets is null || _widget is null) return;

        if (_question is not null)
        {
            if (!_modeSets.IsUndecided) _question.Close();
            return;
        }

        if (!_modeSets.ShouldAskQuestion || !_widget.IsVisible) return;

        _modeSets.MarkQuestionShown(); // once, even if it is closed without an answer
        _question = new ModeSetQuestionWindow(_modeSets);
        _question.Closed += (_, _) => _question = null;
        _question.Show();
        _question.Activate();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showWidgetWait?.Unregister(null);
        _showWidgetSignal?.Dispose();
        _exitWait?.Unregister(null);
        _exitSignal?.Dispose();

        // Remove the tray icon, then unregister power notifications and stop the safety poll.
        _tray?.Dispose();
        _powerMode?.Dispose();

        if (_instanceMutex is not null)
        {
            try { _instanceMutex.ReleaseMutex(); }
            catch (ApplicationException) { /* not owned; nothing to release */ }
            _instanceMutex.Dispose();
        }

        base.OnExit(e);
    }

    /// <summary>
    /// Returns true if this is the only running copy. If another copy is already running,
    /// tells it to show its widget (or to exit, if exitRequested) and returns false.
    /// </summary>
    private bool TryBecomeOnlyInstance(bool exitRequested)
    {
        var mutex = new Mutex(initiallyOwned: true, InstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            try
            {
                using var signal = EventWaitHandle.OpenExisting(exitRequested ? ExitEventName : ShowWidgetEventName);
                signal.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // The other copy is still starting up.
            }
            return false;
        }

        _instanceMutex = mutex;
        if (exitRequested) return true; // nothing running to close; the caller just quits

        // Wait (without using any CPU) for a later launch to ask us to show the widget or exit.
        _showWidgetSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWidgetEventName);
        _showWidgetWait = ThreadPool.RegisterWaitForSingleObject(
            _showWidgetSignal,
            (_, _) => Dispatcher.InvokeAsync(() => _widget?.SetWidgetVisible(true)),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);

        _exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        _exitWait = ThreadPool.RegisterWaitForSingleObject(
            _exitSignal,
            (_, _) => Dispatcher.InvokeAsync(() => Shutdown()),
            null,
            Timeout.Infinite,
            executeOnlyOnce: true);
        return true;
    }
}
