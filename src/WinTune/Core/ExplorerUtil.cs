using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace WinTune.Core;

/// <summary>Warns about or restarts File Explorer after a change.</summary>
internal static class ExplorerUtil
{
    public static void NotifySettingsChanged()
    {
        NativeMethods.SHChangeNotify(NativeMethods.SHCNE_ASSOCCHANGED, NativeMethods.SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
    }

    public static void Restart()
    {
        int session = Process.GetCurrentProcess().SessionId;

        foreach (Process process in Process.GetProcessesByName("explorer"))
        {
            try
            {
                if (process.SessionId == session)
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
            }
            catch
            {
                // A process already closed or not accessible is ignored.
            }
            finally
            {
                process.Dispose();
            }
        }

        // Windows usually restarts the shell by itself; otherwise it is started here.
        Thread.Sleep(2000);

        Process[] running = Process.GetProcessesByName("explorer");
        bool found = running.Length > 0;
        foreach (Process process in running)
        {
            process.Dispose();
        }

        if (!found)
        {
            string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            Process.Start(new ProcessStartInfo(explorer) { UseShellExecute = true })?.Dispose();
        }
    }
}
