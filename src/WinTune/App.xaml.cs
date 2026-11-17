using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using WinTune.Core;

namespace WinTune;

public partial class App : Application
{
    private const string SingleInstanceName = @"Local\WinTune.SingleInstance";
    private const string ShowEventName = @"Local\WinTune.Show";
    private const string ShowAppsEventName = @"Local\WinTune.ShowApps";

    private Mutex? _singleInstance;

    /// <summary>True when the program is really exiting (icon menu or Windows sign-out).</summary>
    public static bool IsExiting { get; set; }

    // No window: "WinTune.exe --check-updates" (started by the scheduled task) looks for
    // app updates, shows a notification if there are new ones, and exits.
    protected override async void OnStartup(StartupEventArgs e)
    {
        if (HasArgument(e, BackgroundUpdates.CheckArgument))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            await BackgroundUpdates.RunBackgroundCheckAsync();

            Shutdown();
            return;
        }

        bool openApps = e.Args.Any(a => a.StartsWith(BackgroundUpdates.ProtocolScheme + ":", StringComparison.OrdinalIgnoreCase));

        // One copy at a time: if WinTune is already open (even in the notification area), bring it to the front.
        _singleInstance = new Mutex(true, SingleInstanceName, out bool isFirst);
        if (!isFirst)
        {
            try
            {
                using var existing = EventWaitHandle.OpenExisting(openApps ? ShowAppsEventName : ShowEventName);
                existing.Set();
            }
            catch (Exception)
            {
                // The first copy is not responding: nothing else to do.
            }

            Shutdown();
            return;
        }

        base.OnStartup(e);

        // The app does not exit by itself when the window closes: with the notification-area icon
        // WinTune keeps running without a window (and with almost no memory). When needed, OnMainWindowClosed exits.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        SessionEnding += (_, _) => IsExiting = true;
        StartShowListener(ShowEventName, openApps: false);
        StartShowListener(ShowAppsEventName, openApps: true);

        bool hideOnClose = AppSettings.Current.HideOnClose;
        if (hideOnClose)
        {
            TrayService.Enable();
        }

        // Start with Windows: with the notification-area icon the window is not created at all.
        bool minimized = HasArgument(e, BackgroundUpdates.MinimizedArgument);
        if (!(hideOnClose && minimized))
        {
            ShowMainWindow(openApps, startMinimized: minimized);
        }
    }

    private static MainWindow? _window;

    /// <summary>
    /// Shows the window: creates it if it does not exist (with the notification-area icon the window is really closed,
    /// to free memory) or brings it to the front.
    /// </summary>
    public static void ShowMainWindow(bool openApps = false, bool startMinimized = false)
    {
        Application app = Current;

        app.Dispatcher.Invoke(() =>
        {
            MemoryTrimmer.Cancel();

            if (_window is null)
            {
                var window = new MainWindow(openApps, startMinimized);
                window.Closed += (_, _) => OnMainWindowClosed(window);
                _window = window;
                app.MainWindow = window;
                window.Show();
                return;
            }

            if (_window.WindowState == WindowState.Minimized)
            {
                _window.WindowState = WindowState.Normal;
            }

            if (openApps)
            {
                MainWindow target = _window;
                target.Dispatcher.BeginInvoke(
                    DispatcherPriority.ApplicationIdle,
                    new Action(() => target.NavigateTo(typeof(Views.Pages.AppPage))));
            }

            _window.Activate();
        });
    }

    // The window has closed: either exit, or stay in the notification area and free the memory.
    private static void OnMainWindowClosed(MainWindow window)
    {
        if (ReferenceEquals(_window, window))
        {
            _window = null;
        }

        if (IsExiting || !AppSettings.Current.HideOnClose)
        {
            TrayService.Disable();
            Current.Shutdown();
            return;
        }

        TrayService.Enable();
        TrayService.ShowHideHint();

        // The sensors are closed (and the driver with them): they are not needed without a window.
        _ = Task.Run(SensorService.Reset);
        MemoryTrimmer.TrimSoon();
    }

    private static bool HasArgument(StartupEventArgs e, string argument) =>
        e.Args.Any(a => string.Equals(a, argument, StringComparison.OrdinalIgnoreCase));

    // A second start of WinTune wakes this copy through a named system event.
    private static void StartShowListener(string eventName, bool openApps)
    {
        var handle = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);

        var thread = new Thread(() =>
        {
            while (handle.WaitOne())
            {
                TrayService.ShowWindow(openApps);
            }
        })
        {
            IsBackground = true,
            Name = "WinTune show listener",
        };

        thread.Start();
    }

    // An unexpected error must not close the program: it shows it and writes it to errors.log.
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(ProfileService.DataFolder);
            File.AppendAllText(
                Path.Combine(ProfileService.DataFolder, "errors.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n{e.Exception}\n\n");
        }
        catch
        {
            // If the log cannot be written the message is still shown.
        }

        MessageBox.Show(
            "An error occurred:\n\n" + e.Exception.Message,
            "WinTune",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = true;
    }
}
