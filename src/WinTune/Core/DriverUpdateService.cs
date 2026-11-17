using System;
using System.Globalization;
using Microsoft.Win32;

namespace WinTune.Core;

/// <summary>
/// Automatic Windows Update drivers: on or off, also timed.
/// When off, Windows Update neither installs nor overwrites drivers; manual installation is still possible.
/// </summary>
public static class DriverUpdateService
{
    private const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
    private const string PolicyValue = "ExcludeWUDriversInQualityUpdate";
    private const string SearchingKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\DriverSearching";
    private const string SearchingValue = "SearchOrderConfig";
    private const string StateKey = @"SOFTWARE\WinTune";
    private const string UntilValue = "DriverDisattivatiFinoAl";
    private const string TaskName = "RiattivaDriverAutomatici";

    /// <summary>True if Windows Update does not install drivers.</summary>
    public static bool IsBlocked =>
        RegistryUtil.GetDword(RegistryHive.LocalMachine, PolicyKey, PolicyValue) == 1;

    /// <summary>Date on which automatic drivers turn back on by themselves. Null if on or with no expiry.</summary>
    public static DateTime? BlockedUntil
    {
        get
        {
            if (!IsBlocked)
            {
                return null;
            }

            return ReadDate(UntilValue);
        }
    }

    /// <summary>Turns automatic drivers off. With a date, turns them back on by itself that day.</summary>
    public static void Block(DateTime? until)
    {
        if (until.HasValue && until.Value.Date <= DateTime.Today)
        {
            throw new ArgumentException("The expiry date must be after today.");
        }

        string before = Describe();

        RegistryUtil.SetDword(RegistryHive.LocalMachine, PolicyKey, PolicyValue, 1);
        TrySetSearchOrder(0);

        ScheduledTaskUtil.Delete(TaskName);

        if (until.HasValue)
        {
            DateTime expiry = until.Value.Date.AddHours(9);
            WriteDate(UntilValue, expiry);

            string reg = ProcessRunner.SystemTool("reg.exe");
            ScheduledTaskUtil.CreateOnce(
                TaskName,
                expiry,
                "WinTune: re-enables automatic driver updates from Windows Update at the chosen expiry.",
                new[]
                {
                    (reg, "delete \"HKLM\\" + PolicyKey + "\" /v " + PolicyValue + " /f"),
                    (reg, "add \"HKLM\\" + SearchingKey + "\" /v " + SearchingValue + " /t REG_DWORD /d 1 /f"),
                    (reg, "delete \"HKLM\\" + StateKey + "\" /v " + UntilValue + " /f"),
                });
        }
        else
        {
            RegistryUtil.DeleteValue(RegistryHive.LocalMachine, StateKey, UntilValue);
        }

        TweakEngine.LogChange("driver.automatici", before, Describe());
    }

    /// <summary>Turns automatic drivers back on immediately.</summary>
    public static void Unblock()
    {
        string before = Describe();

        RegistryUtil.DeleteValue(RegistryHive.LocalMachine, PolicyKey, PolicyValue);
        TrySetSearchOrder(1);
        RegistryUtil.DeleteValue(RegistryHive.LocalMachine, StateKey, UntilValue);
        ScheduledTaskUtil.Delete(TaskName);

        TweakEngine.LogChange("driver.automatici", before, Describe());
    }

    /// <summary>
    /// Safety net: if the expiry has passed but the scheduled task did not do its job,
    /// turns the drivers back on. To be called when the page opens.
    /// </summary>
    public static void EnforceExpiry()
    {
        DateTime? until = BlockedUntil;
        if (until.HasValue && until.Value <= DateTime.Now)
        {
            Unblock();
        }
    }

    internal static DateTime? ReadDate(string valueName)
    {
        string? text = RegistryUtil.GetString(RegistryHive.LocalMachine, StateKey, valueName);
        if (DateTime.TryParseExact(text, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date))
        {
            return date;
        }

        return null;
    }

    internal static void WriteDate(string valueName, DateTime date)
    {
        RegistryUtil.SetString(
            RegistryHive.LocalMachine,
            StateKey,
            valueName,
            date.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture));
    }

    internal static void ClearDate(string valueName) =>
        RegistryUtil.DeleteValue(RegistryHive.LocalMachine, StateKey, valueName);

    private static string Describe()
    {
        if (!IsBlocked)
        {
            return "attivi";
        }

        DateTime? until = ReadDate(UntilValue);
        return until.HasValue ? "disabled until " + until.Value.ToString("dd/MM/yyyy") : "disattivati";
    }

    private static void TrySetSearchOrder(int value)
    {
        try
        {
            RegistryUtil.SetDword(RegistryHive.LocalMachine, SearchingKey, SearchingValue, value);
        }
        catch
        {
            // Secondary setting: the real block is the Windows Update policy.
        }
    }
}
