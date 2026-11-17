using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace WinTune.Core;

/// <summary>
/// Turns Windows services on and off. Before turning one off it saves its original start type
/// in servizi-originali.json, so turning it back on restores it as it was.
/// </summary>
internal static class ServiceUtil
{
    private const string ServicesKey = @"SYSTEM\CurrentControlSet\Services\";

    private static string OriginalsPath => Path.Combine(ProfileService.DataFolder, "servizi-originali.json");

    private static string Sc => ProcessRunner.SystemTool("sc.exe");

    public static bool Exists(string name) =>
        RegistryUtil.GetDword(RegistryHive.LocalMachine, ServicesKey + name, "Start").HasValue;

    public static bool IsEnabled(string name) =>
        (RegistryUtil.GetDword(RegistryHive.LocalMachine, ServicesKey + name, "Start") ?? 4) != 4;

    /// <summary>Current start type in sc.exe format, or null if not recognized.</summary>
    public static string? CurrentStartType(string name)
    {
        int? start = RegistryUtil.GetDword(RegistryHive.LocalMachine, ServicesKey + name, "Start");
        int delayed = RegistryUtil.GetDword(RegistryHive.LocalMachine, ServicesKey + name, "DelayedAutostart") ?? 0;

        return start switch
        {
            2 => delayed == 1 ? "delayed-auto" : "auto",
            3 => "demand",
            4 => "disabled",
            _ => null,
        };
    }

    public static void SetEnabled(string name, bool enabled, string defaultStart)
    {
        if (enabled)
        {
            Dictionary<string, string> originals = LoadOriginals();
            string start = originals.TryGetValue(name, out string? saved) && saved != "disabled" ? saved : defaultStart;
            ProcessRunner.RunOrThrow(Sc, "config \"" + name + "\" start= " + start);
            return;
        }

        SaveOriginal(name);
        ProcessRunner.RunOrThrow(Sc, "config \"" + name + "\" start= disabled");

        try
        {
            // The service is also stopped immediately; if it does not stop it is still disabled from the next start.
            ProcessRunner.Run(Sc, "stop \"" + name + "\"", 30000);
        }
        catch
        {
            // No problem: it applies from the next restart.
        }
    }

    private static Dictionary<string, string> LoadOriginals()
    {
        try
        {
            if (File.Exists(OriginalsPath))
            {
                Dictionary<string, string>? loaded =
                    JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(OriginalsPath));
                if (loaded != null)
                {
                    return new Dictionary<string, string>(loaded, StringComparer.OrdinalIgnoreCase);
                }
            }
        }
        catch
        {
            // Unreadable file: start again from an empty list.
        }

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private static void SaveOriginal(string name)
    {
        try
        {
            Dictionary<string, string> originals = LoadOriginals();
            if (originals.ContainsKey(name))
            {
                return;
            }

            string? current = CurrentStartType(name);
            if (current is null || current == "disabled")
            {
                return;
            }

            originals[name] = current;
            Directory.CreateDirectory(ProfileService.DataFolder);
            File.WriteAllText(OriginalsPath, JsonSerializer.Serialize(originals, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Without the saved value the catalog default start type will be used.
        }
    }
}
