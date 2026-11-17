using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace WinTune.Core;

public sealed record ProcessResult(int ExitCode, string Output, string Error)
{
    public bool Success => ExitCode == 0;

    /// <summary>First useful line of the output, for error messages.</summary>
    public string Summary
    {
        get
        {
            string text = string.IsNullOrWhiteSpace(Error) ? Output : Error;
            foreach (string line in text.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.Length > 0)
                {
                    return trimmed;
                }
            }

            return "exit code " + ExitCode;
        }
    }
}

/// <summary>Runs system commands without showing windows.</summary>
internal static class ProcessRunner
{
    public static string SystemTool(string exeName) =>
        Path.Combine(Environment.SystemDirectory, exeName);

    public static string PowerShellPath =>
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    public static ProcessResult Run(string fileName, string arguments, int timeoutMs = 60000, Encoding? outputEncoding = null)
    {
        var startInfo = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        if (outputEncoding != null)
        {
            startInfo.StandardOutputEncoding = outputEncoding;
            startInfo.StandardErrorEncoding = outputEncoding;
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(timeoutMs))
        {
            try
            {
                process.Kill(true);
            }
            catch
            {
                // The process may have already ended.
            }

            throw new TimeoutException("The command did not respond in time: " + Path.GetFileName(fileName));
        }

        process.WaitForExit();

        return new ProcessResult(
            process.ExitCode,
            outputTask.GetAwaiter().GetResult(),
            errorTask.GetAwaiter().GetResult());
    }

    public static Task<ProcessResult> RunAsync(string fileName, string arguments, int timeoutMs = 60000, Encoding? outputEncoding = null) =>
        Task.Run(() => Run(fileName, arguments, timeoutMs, outputEncoding));

    public static void RunOrThrow(string fileName, string arguments, int timeoutMs = 60000)
    {
        ProcessResult result = Run(fileName, arguments, timeoutMs);
        if (!result.Success)
        {
            throw new InvalidOperationException(
                Path.GetFileName(fileName) + " " + arguments + ": " + result.Summary);
        }
    }

    /// <summary>
    /// Opens a PowerShell window that runs the script and stays visible, for long operations
    /// whose progress is worth seeing. WinTune is administrator, so the window is too.
    /// </summary>
    public static void StartVisiblePowerShell(string script)
    {
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        Process.Start(new ProcessStartInfo(
            PowerShellPath,
            "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + encoded)
        {
            UseShellExecute = true,
        })?.Dispose();
    }

    /// <summary>Runs a script with Windows PowerShell 5.1. On error the output starts with "ERROR:".</summary>
    public static Task<ProcessResult> RunPowerShellAsync(string script, int timeoutMs = 120000)
    {
        string wrapped =
            "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; " +
            "$ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue'; " +
            "try { " + script + " } catch { Write-Output ('ERROR: ' + $_.Exception.Message); exit 1 }";

        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(wrapped));
        string arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded;

        return Task.Run(() => Run(PowerShellPath, arguments, timeoutMs, Encoding.UTF8));
    }
}
