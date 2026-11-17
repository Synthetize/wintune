using System;
using System.Windows.Threading;

namespace WinTune.Core;

public enum ShutdownAction
{
    Spegni = 0,
    Riavvia = 1,
    Sospendi = 2,
}

/// <summary>
/// Timed shutdown. Shutdown and restart use shutdown.exe, so they stay
/// scheduled even if WinTune is closed. Sleep uses an internal timer and
/// requires WinTune to stay open.
/// </summary>
public static class ShutdownService
{
    private static DispatcherTimer? _suspendTimer;

    public static DateTime? Target { get; private set; }

    public static ShutdownAction? PendingAction { get; private set; }

    private static string ShutdownExe => ProcessRunner.SystemTool("shutdown.exe");

    public static void Schedule(ShutdownAction action, int minutes)
    {
        if (minutes < 1 || minutes > 7 * 24 * 60)
        {
            throw new ArgumentOutOfRangeException(nameof(minutes), "The time must be between 1 minute and 7 days.");
        }

        Cancel();

        int seconds = minutes * 60;

        if (action == ShutdownAction.Sospendi)
        {
            _suspendTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            _suspendTimer.Tick += OnSuspendTimerTick;
            _suspendTimer.Start();
        }
        else
        {
            string mode = action == ShutdownAction.Riavvia ? "/r" : "/s";
            ProcessRunner.RunOrThrow(ShutdownExe, mode + " /t " + seconds);
        }

        Target = DateTime.Now.AddSeconds(seconds);
        PendingAction = action;
    }

    /// <summary>Cancels the scheduled shutdown, even if it was started outside WinTune.</summary>
    public static void Cancel()
    {
        if (_suspendTimer != null)
        {
            _suspendTimer.Stop();
            _suspendTimer.Tick -= OnSuspendTimerTick;
            _suspendTimer = null;
        }

        try
        {
            // It returns an error if there is nothing to cancel: not a problem.
            ProcessRunner.Run(ShutdownExe, "/a");
        }
        catch
        {
            // No shutdown to cancel.
        }

        Target = null;
        PendingAction = null;
    }

    /// <summary>To be called when the countdown reaches zero.</summary>
    public static void Clear()
    {
        Target = null;
        PendingAction = null;
    }

    private static void OnSuspendTimerTick(object? sender, EventArgs e)
    {
        if (_suspendTimer != null)
        {
            _suspendTimer.Stop();
            _suspendTimer.Tick -= OnSuspendTimerTick;
            _suspendTimer = null;
        }

        Clear();
        NativeMethods.SetSuspendState(false, false, false);
    }
}
