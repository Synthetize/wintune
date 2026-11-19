using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Threading;

namespace WinTune.Core;

public enum ShutdownAction
{
    Shutdown = 0,
    Restart = 1,
    Sleep = 2,
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

    public const int MaxMinutes = 7 * 24 * 60;

    private static readonly Regex AtTime = new(@"^(?:at|@|alle|ore)?\s*(\d{1,2})[:.](\d{2})$", RegexOptions.Compiled);
    private static readonly Regex HoursMinutesColon = new(@"^(\d{1,3}):(\d{2})$", RegexOptions.Compiled);
    private static readonly Regex WithHours = new(@"^(\d+(?:[.,]\d+)?)\s*h(?:ours?|rs?)?\s*(\d+)?\s*(?:m(?:in(?:ute)?s?)?)?$", RegexOptions.Compiled);
    private static readonly Regex OnlyMinutes = new(@"^(\d+)\s*(?:m|min|mins|minutes?)?$", RegexOptions.Compiled);

    /// <summary>
    /// Reads the time typed by the user and returns the minutes to wait. Accepted: "90", "45m", "2h", "1.5h",
    /// "2h30", "2h 30m", "1:30" (hours:minutes) and a time of the day ("at 03:30", "@3:30": the next 03:30).
    /// </summary>
    public static bool TryParseDelay(string text, out int minutes, out string error)
    {
        minutes = 0;
        error = "";
        string t = text.Trim().ToLowerInvariant();

        if (t.StartsWith("at ", StringComparison.Ordinal) || t.StartsWith("@", StringComparison.Ordinal)
            || t.StartsWith("alle ", StringComparison.Ordinal) || t.StartsWith("ore ", StringComparison.Ordinal))
        {
            Match at = AtTime.Match(t);
            if (at.Success
                && int.TryParse(at.Groups[1].Value, out int hour) && hour is >= 0 and <= 23
                && int.TryParse(at.Groups[2].Value, out int minute) && minute is >= 0 and <= 59)
            {
                DateTime now = DateTime.Now;
                DateTime target = now.Date.AddHours(hour).AddMinutes(minute);
                if (target <= now)
                {
                    target = target.AddDays(1);
                }

                minutes = Math.Max(1, (int)Math.Ceiling((target - now).TotalMinutes));
                return Check(ref minutes, out error);
            }

            error = "Time of day not valid. Example: at 03:30";
            return false;
        }

        Match colon = HoursMinutesColon.Match(t);
        if (colon.Success && int.TryParse(colon.Groups[2].Value, out int cm) && cm < 60)
        {
            minutes = int.Parse(colon.Groups[1].Value, CultureInfo.InvariantCulture) * 60 + cm;
            return Check(ref minutes, out error);
        }

        Match hours = WithHours.Match(t);
        if (hours.Success
            && double.TryParse(hours.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double h))
        {
            double extra = hours.Groups[2].Success ? double.Parse(hours.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
            minutes = (int)Math.Round(h * 60 + extra);
            return Check(ref minutes, out error);
        }

        Match plain = OnlyMinutes.Match(t);
        if (plain.Success && int.TryParse(plain.Groups[1].Value, out int m))
        {
            minutes = m;
            return Check(ref minutes, out error);
        }

        error = "Type a time like 90, 45m, 2h, 2h30, 1:30 or at 03:30.";
        return false;
    }

    private static bool Check(ref int minutes, out string error)
    {
        error = "";
        if (minutes is < 1 or > MaxMinutes)
        {
            error = "The time must be between 1 minute and 7 days.";
            return false;
        }

        return true;
    }

    /// <summary>"2 h 30 min" for a number of minutes.</summary>
    public static string FormatMinutes(int minutes)
    {
        int h = minutes / 60;
        int m = minutes % 60;
        if (h == 0)
        {
            return m + " min";
        }

        return m == 0 ? h + " h" : h + " h " + m + " min";
    }

    private static string ShutdownExe => ProcessRunner.SystemTool("shutdown.exe");

    public static void Schedule(ShutdownAction action, int minutes)
    {
        if (minutes < 1 || minutes > 7 * 24 * 60)
        {
            throw new ArgumentOutOfRangeException(nameof(minutes), "The time must be between 1 minute and 7 days.");
        }

        Cancel();

        int seconds = minutes * 60;

        if (action == ShutdownAction.Sleep)
        {
            _suspendTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            _suspendTimer.Tick += OnSuspendTimerTick;
            _suspendTimer.Start();
        }
        else
        {
            string mode = action == ShutdownAction.Restart ? "/r" : "/s";
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
