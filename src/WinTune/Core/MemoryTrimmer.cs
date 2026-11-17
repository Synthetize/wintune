using System;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace WinTune.Core;

/// <summary>
/// Gives back to Windows the memory WinTune is not using (when the window is closed to the notification
/// area or minimized). What is still needed is reloaded by itself when required.
/// </summary>
internal static class MemoryTrimmer
{
    private static int _version;

    [DllImport("kernel32.dll", EntryPoint = "K32EmptyWorkingSet", SetLastError = true)]
    private static extern bool EmptyWorkingSet(IntPtr process);

    /// <summary>Frees the memory after a while, so the window closing has time to finish.</summary>
    public static void TrimSoon(int delayMs = 2000)
    {
        int version = Interlocked.Increment(ref _version);

        _ = Task.Run(async () =>
        {
            await Task.Delay(delayMs);

            // If the window has been reopened in the meantime (Cancel) nothing is freed.
            if (version == Volatile.Read(ref _version))
            {
                Trim();
            }
        });
    }

    /// <summary>Cancels the scheduled freeing.</summary>
    public static void Cancel() => Interlocked.Increment(ref _version);

    public static void Trim()
    {
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

            using Process process = Process.GetCurrentProcess();
            EmptyWorkingSet(process.Handle);
        }
        catch (Exception)
        {
            // The cleanup is only an optimization: if it fails nothing changes.
        }
    }
}
