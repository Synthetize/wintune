using System;
using System.Globalization;
using Microsoft.Win32;

namespace WinTune.Core;

public enum UpdateMode
{
    Attivi,
    SoloNotifica,
    InPausa,
    Disattivati,
}

/// <summary>Windows Update modes: on, notify only, paused until a date, off.</summary>
public static class WindowsUpdateService
{
    private const string AuPolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU";
    private const string UxSettingsKey = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";
    private const string UntilValue = "AggiornamentiDisattivatiFinoAl";
    private const string TaskName = "RiattivaAggiornamenti";
    private const string ServiceName = "wuauserv";

    private static readonly string[] PauseValues =
    {
        "PauseUpdatesExpiryTime",
        "PauseUpdatesStartTime",
        "PauseFeatureUpdatesStartTime",
        "PauseFeatureUpdatesEndTime",
        "PauseQualityUpdatesStartTime",
        "PauseQualityUpdatesEndTime",
    };

    public static UpdateMode CurrentMode
    {
        get
        {
            if (RegistryUtil.GetDword(RegistryHive.LocalMachine, AuPolicyKey, "NoAutoUpdate") == 1)
            {
                return UpdateMode.Disattivati;
            }

            if (PausedUntil.HasValue)
            {
                return UpdateMode.InPausa;
            }

            if (RegistryUtil.GetDword(RegistryHive.LocalMachine, AuPolicyKey, "AUOptions") == 2)
            {
                return UpdateMode.SoloNotifica;
            }

            return UpdateMode.Attivi;
        }
    }

    /// <summary>End of the pause, if one is in progress.</summary>
    public static DateTime? PausedUntil
    {
        get
        {
            string? text = RegistryUtil.GetString(RegistryHive.LocalMachine, UxSettingsKey, "PauseUpdatesExpiryTime");
            if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime utc))
            {
                DateTime local = utc.ToLocalTime();
                return local > DateTime.Now ? local : null;
            }

            return null;
        }
    }

    /// <summary>Date on which the disabled updates turn back on by themselves. Null if no expiry.</summary>
    public static DateTime? DisabledUntil =>
        CurrentMode == UpdateMode.Disattivati ? DriverUpdateService.ReadDate(UntilValue) : null;

    /// <summary>Short text of the current state, for the interface and the change log.</summary>
    public static string Describe()
    {
        switch (CurrentMode)
        {
            case UpdateMode.Disattivati:
                DateTime? until = DriverUpdateService.ReadDate(UntilValue);
                return until.HasValue ? "Disabled until " + until.Value.ToString("dd/MM/yyyy") : "Disabled with no expiry";
            case UpdateMode.InPausa:
                return "Paused until " + (PausedUntil ?? DateTime.Now).ToString("dd/MM/yyyy");
            case UpdateMode.SoloNotifica:
                return "Notify only";
            default:
                return "Enabled";
        }
    }

    /// <summary>
    /// Applies a mode. "until" is required for the pause and optional for "Disabled"
    /// (without a date they stay disabled until the mode is changed).
    /// </summary>
    public static void Apply(UpdateMode mode, DateTime? until)
    {
        if (mode == UpdateMode.InPausa && (!until.HasValue || until.Value <= DateTime.Now))
        {
            throw new ArgumentException("A future date is required for the pause.");
        }

        if (mode == UpdateMode.Disattivati && until.HasValue && until.Value.Date <= DateTime.Today)
        {
            throw new ArgumentException("The expiry date must be after today.");
        }

        string before = Describe();

        // Always start from a clean situation.
        ClearAll();

        switch (mode)
        {
            case UpdateMode.SoloNotifica:
                RegistryUtil.SetDword(RegistryHive.LocalMachine, AuPolicyKey, "NoAutoUpdate", 0);
                RegistryUtil.SetDword(RegistryHive.LocalMachine, AuPolicyKey, "AUOptions", 2);
                break;

            case UpdateMode.InPausa:
                string start = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
                string end = until!.Value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

                RegistryUtil.SetString(RegistryHive.LocalMachine, UxSettingsKey, "PauseUpdatesStartTime", start);
                RegistryUtil.SetString(RegistryHive.LocalMachine, UxSettingsKey, "PauseUpdatesExpiryTime", end);
                RegistryUtil.SetString(RegistryHive.LocalMachine, UxSettingsKey, "PauseFeatureUpdatesStartTime", start);
                RegistryUtil.SetString(RegistryHive.LocalMachine, UxSettingsKey, "PauseFeatureUpdatesEndTime", end);
                RegistryUtil.SetString(RegistryHive.LocalMachine, UxSettingsKey, "PauseQualityUpdatesStartTime", start);
                RegistryUtil.SetString(RegistryHive.LocalMachine, UxSettingsKey, "PauseQualityUpdatesEndTime", end);
                break;

            case UpdateMode.Disattivati:
                RegistryUtil.SetDword(RegistryHive.LocalMachine, AuPolicyKey, "NoAutoUpdate", 1);
                TrySetService(false);

                if (until.HasValue)
                {
                    DateTime expiry = until.Value.Date.AddHours(9);
                    DriverUpdateService.WriteDate(UntilValue, expiry);

                    string reg = ProcessRunner.SystemTool("reg.exe");
                    string sc = ProcessRunner.SystemTool("sc.exe");
                    ScheduledTaskUtil.CreateOnce(
                        TaskName,
                        expiry,
                        "WinTune: re-enables Windows Update at the chosen expiry.",
                        new[]
                        {
                            (reg, "delete \"HKLM\\" + AuPolicyKey + "\" /v NoAutoUpdate /f"),
                            (sc, "config " + ServiceName + " start= demand"),
                            (reg, "delete \"HKLM\\SOFTWARE\\WinTune\" /v " + UntilValue + " /f"),
                        });
                }

                break;
        }

        TweakEngine.LogChange("aggiornamenti.modalita", before, Describe());
    }

    /// <summary>
    /// Safety net: if the expiry of "Disabled" has passed but the scheduled task
    /// did not do its job, turns the updates back on. To be called when the page opens.
    /// </summary>
    public static void EnforceExpiry()
    {
        if (CurrentMode != UpdateMode.Disattivati)
        {
            return;
        }

        DateTime? until = DriverUpdateService.ReadDate(UntilValue);
        if (until.HasValue && until.Value <= DateTime.Now)
        {
            Apply(UpdateMode.Attivi, null);
        }
    }

    /// <summary>Removes policies, pause, expiries and brings the service back to its normal start.</summary>
    private static void ClearAll()
    {
        RegistryUtil.DeleteValue(RegistryHive.LocalMachine, AuPolicyKey, "NoAutoUpdate");
        RegistryUtil.DeleteValue(RegistryHive.LocalMachine, AuPolicyKey, "AUOptions");

        foreach (string name in PauseValues)
        {
            RegistryUtil.DeleteValue(RegistryHive.LocalMachine, UxSettingsKey, name);
        }

        DriverUpdateService.ClearDate(UntilValue);
        ScheduledTaskUtil.Delete(TaskName);
        TrySetService(true);
    }

    private static void TrySetService(bool enabled)
    {
        try
        {
            string sc = ProcessRunner.SystemTool("sc.exe");
            if (enabled)
            {
                if (!ServiceUtil.IsEnabled(ServiceName))
                {
                    ProcessRunner.Run(sc, "config " + ServiceName + " start= demand", 30000);
                }
            }
            else
            {
                ProcessRunner.Run(sc, "config " + ServiceName + " start= disabled", 30000);
                ProcessRunner.Run(sc, "stop " + ServiceName, 30000);
            }
        }
        catch
        {
            // Windows may prevent touching the service: the NoAutoUpdate policy stays active anyway.
        }
    }
}
