using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;

namespace WinTune.Core;

/// <summary>Basic information about Windows and the hardware, read without drivers.</summary>
public static class SystemInfo
{
    private const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    private const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;

    public static string WindowsName
    {
        get
        {
            string name = IsWindows11 ? "Windows 11" : "Windows 10";
            string? edition = RegistryUtil.GetString(RegistryHive.LocalMachine, CurrentVersionKey, "EditionID");
            string? release = RegistryUtil.GetString(RegistryHive.LocalMachine, CurrentVersionKey, "DisplayVersion");

            edition = edition switch
            {
                "Core" or "CoreSingleLanguage" or "CoreN" => "Home",
                "Professional" or "ProfessionalN" => "Pro",
                _ => edition,
            };

            return string.Join(" ", new[] { name, edition, release }.Where(p => !string.IsNullOrWhiteSpace(p)));
        }
    }

    public static string CpuName
    {
        get
        {
            string? name = RegistryUtil.GetString(
                RegistryHive.LocalMachine,
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0",
                "ProcessorNameString");
            return string.IsNullOrWhiteSpace(name) ? "Unknown CPU" : name.Trim();
        }
    }

    public static string GpuName
    {
        get
        {
            var names = new List<string>();

            for (int i = 0; i < 8; i++)
            {
                string? description = RegistryUtil.GetString(
                    RegistryHive.LocalMachine,
                    DisplayClassKey + @"\" + i.ToString("D4"),
                    "DriverDesc");

                if (string.IsNullOrWhiteSpace(description))
                {
                    continue;
                }

                bool generic = description.Contains("Basic", StringComparison.OrdinalIgnoreCase)
                    || description.Contains("Remote", StringComparison.OrdinalIgnoreCase);

                if (!generic && !names.Contains(description))
                {
                    names.Add(description);
                }
            }

            return names.Count == 0 ? "Unknown GPU" : string.Join(" + ", names);
        }
    }

    public static string RamText
    {
        get
        {
            try
            {
                if (NativeMethods.GetPhysicallyInstalledSystemMemory(out long kilobytes) && kilobytes > 0)
                {
                    return Math.Round(kilobytes / 1024.0 / 1024.0) + " GB of RAM";
                }
            }
            catch
            {
                // Move on to the estimate below.
            }

            long bytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            return Math.Round(bytes / 1024.0 / 1024.0 / 1024.0) + " GB of RAM";
        }
    }

    public static string Summary => string.Join("  ·  ", CpuName, GpuName, RamText, WindowsName);
}
