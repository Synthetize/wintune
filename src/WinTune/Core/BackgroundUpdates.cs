using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace WinTune.Core;

/// <summary>Notification settings and memory, in %AppData%\WinTune\notifications.json.</summary>
public sealed class UpdateNotifySettings
{
    /// <summary>How many hours between update checks.</summary>
    public int IntervalHours { get; set; } = 6;

    /// <summary>Updates already notified ("id|version"), so the same alert is not repeated.</summary>
    public List<string> Notified { get; set; } = new();
}

/// <summary>
/// Start with Windows and background check for app updates.
/// The check does not leave a program running all the time: a scheduled task starts
/// "WinTune.exe --check-updates" at sign-in and then every N hours; the program asks winget,
/// shows a Windows notification if there is something new, and exits.
/// </summary>
public static class BackgroundUpdates
{
    public const string CheckArgument = "--check-updates";
    public const string MinimizedArgument = "--minimized";
    public const string ProtocolScheme = "wintune";

    private const string CheckTaskName = "App update check";
    private const string StartupTaskName = "Start WinTune";
    private const string AppUserModelId = "WinTune";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string SettingsPath => Path.Combine(ProfileService.DataFolder, "notifications.json");

    private static string ExePath => Environment.ProcessPath
        ?? throw new InvalidOperationException("The path of WinTune.exe is not available.");

    // ---------- State and settings ----------

    public static bool IsStartupEnabled => ScheduledTaskUtil.Exists(StartupTaskName);

    public static bool IsCheckEnabled => ScheduledTaskUtil.Exists(CheckTaskName);

    public static UpdateNotifySettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                return JsonSerializer.Deserialize<UpdateNotifySettings>(File.ReadAllText(SettingsPath))
                    ?? new UpdateNotifySettings();
            }
        }
        catch (Exception)
        {
            // Damaged file: start again from the default settings.
        }

        return new UpdateNotifySettings();
    }

    private static void SaveSettings(UpdateNotifySettings settings)
    {
        Directory.CreateDirectory(ProfileService.DataFolder);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
    }

    // ---------- Scheduled tasks ----------

    /// <summary>Starts (or stops starting) WinTune when signing in to Windows, with the window minimized.</summary>
    public static void SetStartup(bool enabled)
    {
        if (!enabled)
        {
            ScheduledTaskUtil.Delete(StartupTaskName);
            return;
        }

        ScheduledTaskUtil.CreateAtLogon(
            StartupTaskName,
            "Starts WinTune when you sign in to Windows.",
            ExePath,
            MinimizedArgument,
            Path.GetDirectoryName(ExePath) ?? string.Empty);
    }

    /// <summary>Turns the periodic check on or off; when on, recreates the task with the given interval.</summary>
    public static void SetCheck(bool enabled, int intervalHours)
    {
        if (!enabled)
        {
            ScheduledTaskUtil.Delete(CheckTaskName);
            return;
        }

        intervalHours = Math.Clamp(intervalHours, 1, 168);

        UpdateNotifySettings settings = LoadSettings();
        settings.IntervalHours = intervalHours;
        SaveSettings(settings);

        RegisterNotificationSource();

        ScheduledTaskUtil.CreateAtLogon(
            CheckTaskName,
            "Checks for app updates with winget and shows a notification when there are new ones.",
            ExePath,
            CheckArgument,
            Path.GetDirectoryName(ExePath) ?? string.Empty,
            repeatEveryHours: intervalHours,
            needsNetwork: true);
    }

    // ---------- Background check ----------

    /// <summary>
    /// Run by "WinTune.exe --check-updates": looks for updates and alerts only for the
    /// new ones (not ignored and not already notified).
    /// </summary>
    public static async Task RunBackgroundCheckAsync()
    {
        try
        {
            if (await WingetService.GetVersionAsync() is null)
            {
                return;
            }

            List<WingetUpgrade> upgrades = await WingetService.GetUpgradesAsync();

            Profile? profile = null;
            try
            {
                profile = ProfileService.Load();
            }
            catch (Exception)
            {
                // Unreadable profile: no update is excluded.
            }

            HashSet<string> ignored = profile is null
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(profile.IgnoredUpdates.Keys, StringComparer.OrdinalIgnoreCase);

            List<WingetUpgrade> relevant = upgrades.Where(u => !ignored.Contains(u.Id)).ToList();

            UpdateNotifySettings settings = LoadSettings();
            string KeyOf(WingetUpgrade u) => u.Id + "|" + u.Available;

            var known = new HashSet<string>(settings.Notified, StringComparer.OrdinalIgnoreCase);
            List<WingetUpgrade> fresh = relevant.Where(u => !known.Contains(KeyOf(u))).ToList();

            // Only updates that are still available are remembered: installed ones leave the memory.
            var stillAvailable = new HashSet<string>(relevant.Select(KeyOf), StringComparer.OrdinalIgnoreCase);
            settings.Notified = settings.Notified.Where(k => stillAvailable.Contains(k)).ToList();

            if (fresh.Count > 0)
            {
                RegisterNotificationSource();

                bool shown = await ShowToastAsync(
                    fresh.Count == 1 ? "1 app update available" : fresh.Count + " app updates available",
                    Summarize(fresh));

                // If the notification did not go out, try again at the next check.
                if (shown)
                {
                    settings.Notified.AddRange(fresh.Select(KeyOf));
                }
            }

            SaveSettings(settings);
        }
        catch (Exception ex)
        {
            WriteLog("Background update check failed: " + ex.Message);
        }
    }

    private static string Summarize(List<WingetUpgrade> upgrades)
    {
        const int maxNames = 4;

        string names = string.Join(", ", upgrades.Take(maxNames).Select(u => u.Name));
        int more = upgrades.Count - maxNames;
        return more > 0 ? names + " and " + more + " more. Click to open WinTune." : names + ". Click to open WinTune.";
    }

    /// <summary>Shows a test notification, to check that Windows displays them.</summary>
    public static async Task<bool> SendTestAsync()
    {
        RegisterNotificationSource();
        return await ShowToastAsync("WinTune notifications work", "You will see a message like this when new app updates are found.");
    }

    // ---------- Windows notifications ----------

    /// <summary>
    /// Registers for the user the name "WinTune" as the source of notifications and the link
    /// "wintune://", which opens the program when the notification is clicked.
    /// </summary>
    private static void RegisterNotificationSource()
    {
        try
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + AppUserModelId))
            {
                key.SetValue("DisplayName", "WinTune");
            }

            using RegistryKey protocol = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ProtocolScheme);
            protocol.SetValue(string.Empty, "URL:WinTune");
            protocol.SetValue("URL Protocol", string.Empty);

            using RegistryKey command = protocol.CreateSubKey(@"shell\open\command");
            command.SetValue(string.Empty, "\"" + ExePath + "\" \"%1\"");
        }
        catch (Exception ex)
        {
            WriteLog("Registering notifications failed: " + ex.Message);
        }
    }

    private static async Task<bool> ShowToastAsync(string title, string message)
    {
        string xml =
            "<toast activationType=\"protocol\" launch=\"" + ProtocolScheme + "://updates\">" +
            "<visual><binding template=\"ToastGeneric\">" +
            "<text>" + SecurityElement.Escape(title) + "</text>" +
            "<text>" + SecurityElement.Escape(message) + "</text>" +
            "</binding></visual></toast>";

        // In the script the text is between single quotes: real ones must be doubled.
        string script =
            "[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null; " +
            "[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] | Out-Null; " +
            "$doc = New-Object Windows.Data.Xml.Dom.XmlDocument; " +
            "$doc.LoadXml('" + xml.Replace("'", "''") + "'); " +
            "$toast = New-Object Windows.UI.Notifications.ToastNotification $doc; " +
            "[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('" + AppUserModelId + "').Show($toast)";

        try
        {
            ProcessResult result = await ProcessRunner.RunPowerShellAsync(script, 30000);
            return result.Success && !result.Output.StartsWith("ERROR:", StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            WriteLog("Showing the notification failed: " + ex.Message);
            return false;
        }
    }

    private static void WriteLog(string message)
    {
        try
        {
            Directory.CreateDirectory(ProfileService.DataFolder);
            File.AppendAllText(
                Path.Combine(ProfileService.DataFolder, "errors.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n{message}\n\n");
        }
        catch (Exception)
        {
            // If the log cannot be written there is nothing else to do.
        }
    }
}
