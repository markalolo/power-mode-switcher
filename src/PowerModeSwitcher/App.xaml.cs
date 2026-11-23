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

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showWidgetSignal;
    private RegisteredWaitHandle? _showWidgetWait;
    private EventWaitHandle? _exitSignal;
    private RegisteredWaitHandle? _exitWait;

    private PowerModeService? _powerMode;
    private SettingsService? _settings;
    private WidgetWindow? _widget;
    private TrayIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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

        _widget = new WidgetWindow(_powerMode, _settings);
        MainWindow = _widget;
        if (_settings.Settings.WidgetVisible) _widget.Show();

        _tray = new TrayIcon(_powerMode, _settings, _widget);

        // Right-clicking the widget opens the same menu as the tray icon.
        _widget.MenuRequested += (_, _) => _tray.ShowMenuAtCursor();
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
