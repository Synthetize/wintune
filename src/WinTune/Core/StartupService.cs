using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace WinTune.Core;

/// <summary>A program that starts with Windows.</summary>
public sealed class StartupEntry
{
    public required string Name { get; init; }

    public required string Command { get; init; }

    /// <summary>Where it starts from: "User", "All users", "Startup folder".</summary>
    public required string Source { get; init; }

    /// <summary>Where Windows remembers whether the entry is enabled (the same place Task Manager uses).</summary>
    public required RegistryHive ApprovedHive { get; init; }

    public required string ApprovedPath { get; init; }

    public required string ValueName { get; init; }
}

/// <summary>
/// Lists the programs that start automatically and turns them on or off as Task Manager does:
/// the entry stays in place and only the state in "StartupApproved" changes.
/// It does not include Store apps, which are managed from Windows Settings.
/// </summary>
public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunKey32 = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedBase = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    public static List<StartupEntry> List()
    {
        var entries = new List<StartupEntry>();

        AddRegistryEntries(entries, RegistryHive.CurrentUser, RunKey, "User", "Run");
        AddRegistryEntries(entries, RegistryHive.LocalMachine, RunKey, "All users", "Run");
        AddRegistryEntries(entries, RegistryHive.LocalMachine, RunKey32, "All users (32-bit)", "Run32");

        AddFolderEntries(
            entries,
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            RegistryHive.CurrentUser,
            "Startup folder (user)");

        AddFolderEntries(
            entries,
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
            RegistryHive.LocalMachine,
            "Startup folder (all users)");

        return entries.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static bool IsEnabled(StartupEntry entry)
    {
        byte[]? state = RegistryUtil.GetBinary(entry.ApprovedHive, entry.ApprovedPath, entry.ValueName);

        // No value means enabled. First byte even (02, 06) = enabled, odd (03, 07) = disabled.
        return state is null || state.Length == 0 || (state[0] & 1) == 0;
    }

    public static void SetEnabled(StartupEntry entry, bool enabled)
    {
        var state = new byte[12];

        if (enabled)
        {
            state[0] = 0x02;
        }
        else
        {
            state[0] = 0x03;
            BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(state, 4);
        }

        RegistryUtil.SetBinary(entry.ApprovedHive, entry.ApprovedPath, entry.ValueName, state);
    }

    private static void AddRegistryEntries(List<StartupEntry> entries, RegistryHive hive, string runKey, string source, string approvedName)
    {
        Dictionary<string, string> values;
        try
        {
            values = RegistryUtil.GetStringValues(hive, runKey);
        }
        catch
        {
            return;
        }

        foreach (KeyValuePair<string, string> value in values)
        {
            entries.Add(new StartupEntry
            {
                Name = value.Key,
                Command = value.Value,
                Source = source,
                ApprovedHive = hive,
                ApprovedPath = ApprovedBase + approvedName,
                ValueName = value.Key,
            });
        }
    }

    private static void AddFolderEntries(List<StartupEntry> entries, string folder, RegistryHive hive, string source)
    {
        try
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                return;
            }

            foreach (string file in Directory.GetFiles(folder))
            {
                string fileName = Path.GetFileName(file);
                if (string.Equals(fileName, "desktop.ini", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                entries.Add(new StartupEntry
                {
                    Name = Path.GetFileNameWithoutExtension(file),
                    Command = file,
                    Source = source,
                    ApprovedHive = hive,
                    ApprovedPath = ApprovedBase + "StartupFolder",
                    ValueName = fileName,
                });
            }
        }
        catch
        {
            // An unreadable folder is skipped.
        }
    }
}
