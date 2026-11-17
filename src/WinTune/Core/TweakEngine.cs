using System;
using System.IO;

namespace WinTune.Core;

/// <summary>Applies the optimizations and writes every change to the change log.</summary>
public static class TweakEngine
{
    public static string ChangeLogPath => Path.Combine(ProfileService.DataFolder, "changes.log");

    /// <summary>Reads the current value. Null if reading fails.</summary>
    public static string? SafeRead(Tweak tweak)
    {
        try
        {
            return tweak.ReadValue();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Applies a value. Throws an exception if the change fails.</summary>
    public static void Apply(Tweak tweak, string value)
    {
        string? before = SafeRead(tweak);
        tweak.WriteValue(value);
        LogChange(tweak.Id, before, value);
    }

    /// <summary>Writes a line to the change log (also for changes outside the catalog).</summary>
    public static void LogChange(string id, string? before, string after)
    {
        try
        {
            Directory.CreateDirectory(ProfileService.DataFolder);
            File.AppendAllText(
                ChangeLogPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\t{id}\t{before ?? "?"} -> {after}{Environment.NewLine}");
        }
        catch
        {
            // The change log must never block a successful change.
        }
    }
}
