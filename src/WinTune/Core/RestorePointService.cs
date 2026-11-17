using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace WinTune.Core;

/// <summary>Windows restore points: creation, last point, restore procedure.</summary>
public static class RestorePointService
{
    private const string SystemRestoreKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";
    private const string FrequencyValue = "SystemRestorePointCreationFrequency";

    /// <summary>Creates a restore point. Turns on System Protection on the Windows drive if it is off.</summary>
    public static async Task<(bool Ok, string Message)> CreateAsync(string description)
    {
        // Windows refuses a second point within 24 hours: the limit is removed only for this creation.
        int? previousFrequency = RegistryUtil.GetDword(RegistryHive.LocalMachine, SystemRestoreKey, FrequencyValue);

        try
        {
            RegistryUtil.SetDword(RegistryHive.LocalMachine, SystemRestoreKey, FrequencyValue, 0);

            string safeDescription = description.Replace("'", "''");
            string script =
                "Enable-ComputerRestore -Drive ($env:SystemDrive + '\\'); " +
                "Checkpoint-Computer -Description '" + safeDescription + "' -RestorePointType MODIFY_SETTINGS; " +
                "Write-Output 'OK'";

            ProcessResult result = await ProcessRunner.RunPowerShellAsync(script, 240000);

            return result.Success
                ? (true, "Restore point created.")
                : (false, "Creation failed. " + result.Summary);
        }
        catch (Exception ex)
        {
            return (false, "Creation failed. " + ex.Message);
        }
        finally
        {
            try
            {
                if (previousFrequency.HasValue)
                {
                    RegistryUtil.SetDword(RegistryHive.LocalMachine, SystemRestoreKey, FrequencyValue, previousFrequency.Value);
                }
                else
                {
                    RegistryUtil.DeleteValue(RegistryHive.LocalMachine, SystemRestoreKey, FrequencyValue);
                }
            }
            catch
            {
                // If restoring the limit fails, all that is left is a more permissive limit.
            }
        }
    }

    /// <summary>Description and date of the last restore point, or null if there are none.</summary>
    public static async Task<string?> GetLastAsync()
    {
        const string script =
            "$p = Get-ComputerRestorePoint | Sort-Object SequenceNumber | Select-Object -Last 1; " +
            "if ($p) { Write-Output ($p.ConvertToDateTime($p.CreationTime).ToString('dd/MM/yyyy HH:mm') + ' - ' + $p.Description) }";

        try
        {
            ProcessResult result = await ProcessRunner.RunPowerShellAsync(script, 60000);
            string text = result.Output.Trim();
            return result.Success && text.Length > 0 ? text : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Opens the Windows wizard to go back to a restore point.</summary>
    public static void OpenRestoreWizard()
    {
        Process.Start(new ProcessStartInfo(ProcessRunner.SystemTool("rstrui.exe")) { UseShellExecute = true })?.Dispose();
    }
}
