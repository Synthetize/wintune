using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace WinTune.Core;

/// <summary>An item of the disk cleanup.</summary>
public sealed record CleanupTarget(string Id, string Name, string Description);

/// <summary>Disk cleanup: temporary files, Windows Update cache, recycle bin.</summary>
public static class CleanupService
{
    public const string UserTemp = "userTemp";
    public const string WindowsTemp = "tempWindows";
    public const string UpdateCache = "cacheUpdate";
    public const string RecycleBin = "recycleBin";

    public static IReadOnlyList<CleanupTarget> Targets { get; } = new[]
    {
        new CleanupTarget(UserTemp, "User temporary files", "The Temp folder of your profile."),
        new CleanupTarget(WindowsTemp, "Windows temporary files", "The Windows Temp folder."),
        new CleanupTarget(UpdateCache, "Windows Update cache", "Installation files of updates already downloaded."),
        new CleanupTarget(RecycleBin, "Recycle Bin", "All files in the Recycle Bin, on all drives."),
    };

    /// <summary>Space taken by the item, in bytes.</summary>
    public static Task<long> MeasureAsync(string id) => Task.Run(() => Measure(id));

    /// <summary>Cleans the item. Returns the space freed, in bytes. Files in use are skipped.</summary>
    public static Task<long> CleanAsync(string id) => Task.Run(() => Clean(id));

    public static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
        {
            return (bytes / 1024.0 / 1024 / 1024).ToString("0.0") + " GB";
        }

        if (bytes >= 1024 * 1024)
        {
            return (bytes / 1024.0 / 1024).ToString("0") + " MB";
        }

        return (bytes / 1024.0).ToString("0") + " KB";
    }

    private static string? FolderFor(string id)
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        return id switch
        {
            UserTemp => Path.GetTempPath(),
            WindowsTemp => Path.Combine(windows, "Temp"),
            UpdateCache => Path.Combine(windows, "SoftwareDistribution", "Download"),
            _ => null,
        };
    }

    private static long Measure(string id)
    {
        if (id == RecycleBin)
        {
            return MeasureRecycleBin();
        }

        string? folder = FolderFor(id);
        if (folder is null || !Directory.Exists(folder))
        {
            return 0;
        }

        long total = 0;
        foreach (FileInfo file in EnumerateFiles(folder))
        {
            try
            {
                total += file.Length;
            }
            catch
            {
                // File gone or unreadable: it does not count.
            }
        }

        return total;
    }

    private static long Clean(string id)
    {
        if (id == RecycleBin)
        {
            long before = MeasureRecycleBin();
            NativeMethods.SHEmptyRecycleBin(
                IntPtr.Zero,
                null,
                NativeMethods.SHERB_NOCONFIRMATION | NativeMethods.SHERB_NOPROGRESSUI | NativeMethods.SHERB_NOSOUND);
            return Math.Max(0, before - MeasureRecycleBin());
        }

        string? folder = FolderFor(id);
        if (folder is null || !Directory.Exists(folder))
        {
            return 0;
        }

        long freed = 0;

        foreach (FileInfo file in EnumerateFiles(folder).ToList())
        {
            try
            {
                long size = file.Length;
                if (file.IsReadOnly)
                {
                    file.IsReadOnly = false;
                }

                file.Delete();
                freed += size;
            }
            catch
            {
                // File in use or protected: skip it.
            }
        }

        // Folders left empty, innermost first. The main folder stays.
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };

            foreach (string directory in Directory.EnumerateDirectories(folder, "*", options)
                .OrderByDescending(d => d.Length)
                .ToList())
            {
                try
                {
                    Directory.Delete(directory, false);
                }
                catch
                {
                    // Folder not empty or in use: it stays.
                }
            }
        }
        catch
        {
            // The file cleanup happened anyway.
        }

        return freed;
    }

    private static IEnumerable<FileInfo> EnumerateFiles(string folder)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        // The ".net" folder holds the files WinTune itself extracts at startup: it must not be touched while in use.
        return new DirectoryInfo(folder)
            .EnumerateFiles("*", options)
            .Where(f => !f.FullName.Contains(@"\.net\", StringComparison.OrdinalIgnoreCase));
    }

    private static long MeasureRecycleBin()
    {
        try
        {
            var info = default(NativeMethods.SHQUERYRBINFO);
            info.cbSize = Marshal.SizeOf<NativeMethods.SHQUERYRBINFO>();
            return NativeMethods.SHQueryRecycleBin(null, ref info) == 0 ? info.i64Size : 0;
        }
        catch
        {
            return 0;
        }
    }
}
