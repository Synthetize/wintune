using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace WinTune.Core;

/// <summary>
/// Encryption of the system drive (BitLocker, or "Device encryption" on Windows Home).
/// The state is read with PowerShell, which takes a couple of seconds: so it is kept in memory
/// and read again in the background when it is old.
/// </summary>
internal static class BitLockerService
{
    private sealed record State(bool Supported, bool Encrypted, DateTime ReadAt);

    private static readonly object Gate = new();
    private static State? _state;
    private static Task? _refresh;

    /// <summary>False if the encryption state cannot be read on this PC.</summary>
    public static bool IsSupported => Current().Supported;

    /// <summary>True if the drive is encrypted or becoming encrypted; false if it is in clear or being decrypted.</summary>
    public static bool IsEncrypted => Current().Encrypted;

    private static State Current()
    {
        State? state;
        lock (Gate)
        {
            state = _state;
        }

        if (state is null)
        {
            state = Query();
            lock (Gate)
            {
                _state = state;
            }

            return state;
        }

        if (DateTime.UtcNow - state.ReadAt > TimeSpan.FromSeconds(20))
        {
            RefreshInBackground();
        }

        return state;
    }

    private static void RefreshInBackground()
    {
        lock (Gate)
        {
            if (_refresh is { IsCompleted: false })
            {
                return;
            }

            _refresh = Task.Run(() =>
            {
                State fresh = Query();
                lock (Gate)
                {
                    _state = fresh;
                }
            });
        }
    }

    private static State Query()
    {
        try
        {
            ProcessResult result = ProcessRunner
                .RunPowerShellAsync("$v = Get-BitLockerVolume -MountPoint $env:SystemDrive; Write-Output ('STATO=' + $v.VolumeStatus)", 30000)
                .GetAwaiter()
                .GetResult();

            string status = "";
            foreach (string line in result.Output.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("STATO=", StringComparison.Ordinal))
                {
                    status = trimmed.Substring("STATO=".Length);
                }
            }

            if (!result.Success || status.Length == 0)
            {
                return new State(false, false, DateTime.UtcNow);
            }

            // FullyDecrypted, DecryptionInProgress and DecryptionSuspended count as "not encrypted".
            bool decrypted = status.StartsWith("FullyDecrypted", StringComparison.OrdinalIgnoreCase)
                || status.StartsWith("Decryption", StringComparison.OrdinalIgnoreCase);

            return new State(true, !decrypted, DateTime.UtcNow);
        }
        catch
        {
            return new State(false, false, DateTime.UtcNow);
        }
    }

    /// <summary>Starts decrypting the system drive. Windows carries it on in the background.</summary>
    public static void Disable()
    {
        ProcessResult result = ProcessRunner
            .RunPowerShellAsync("Disable-BitLocker -MountPoint $env:SystemDrive -ErrorAction Stop | Out-Null", 60000)
            .GetAwaiter()
            .GetResult();

        if (!result.Success)
        {
            throw new InvalidOperationException(result.Summary);
        }

        State fresh = Query();
        lock (Gate)
        {
            _state = fresh;
        }
    }

    /// <summary>
    /// Opens the Windows page from which encryption can be turned back on. It goes through there, and not through a command,
    /// because it is Windows that has the recovery key saved.
    /// </summary>
    public static void OpenSettings()
    {
        string edition = RegistryUtil.GetString(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "EditionID") ?? "";

        ProcessStartInfo start = edition.StartsWith("Core", StringComparison.OrdinalIgnoreCase)
            ? new ProcessStartInfo("ms-settings:deviceencryption")
            : new ProcessStartInfo(ProcessRunner.SystemTool("control.exe"), "/name Microsoft.BitLockerDriveEncryption");

        start.UseShellExecute = true;
        Process.Start(start)?.Dispose();
    }
}
