using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Win32;

namespace WinTune.Core;

internal enum RegKind
{
    DWord,
    QWord,
    String,
}

/// <summary>
/// A registry value that is part of an optimization.
/// Applied is the value with the optimization applied, Original the starting one; null means "value absent".
/// DefaultApplied says how to treat the value when it is missing: applied, not applied, or (null) according to Applied.
/// </summary>
internal sealed record RegEntry(
    RegistryHive Hive,
    string Path,
    string Name,
    RegKind Kind,
    string? Applied,
    string? Original,
    bool? DefaultApplied);

/// <summary>
/// Optimizations made of one or more registry values: they count as applied only if all the values
/// match, and they are undone by bringing every value back to the starting one.
/// </summary>
internal static class RegistryBundle
{
    /// <summary>
    /// Creates a switch. With onIsApplied the switch on matches the "applied" values;
    /// without it, it is the opposite: the switch shows a Windows feature that the optimization turns off.
    /// </summary>
    public static ToggleTweak Toggle(
        string id,
        string category,
        string title,
        string description,
        string onText,
        string offText,
        bool onIsApplied,
        RegEntry[] entries,
        string? recommended = null,
        bool needsRestart = false,
        bool needsExplorerRestart = false,
        bool windows11Only = false,
        Action? afterApply = null,
        Action? afterUndo = null,
        Func<string, string?>? confirm = null) =>
        new()
        {
            Id = id,
            Category = category,
            Title = title,
            Description = description,
            OnText = onText,
            OffText = offText,
            Recommended = recommended,
            NeedsRestart = needsRestart,
            NeedsExplorerRestart = needsExplorerRestart,
            Windows11Only = windows11Only,
            Confirm = confirm,
            Read = () => IsApplied(entries) == onIsApplied,
            Write = on =>
            {
                bool apply = on == onIsApplied;
                Set(entries, apply);

                if (apply)
                {
                    afterApply?.Invoke();
                }
                else
                {
                    afterUndo?.Invoke();
                }
            },
        };

    public static bool IsApplied(IReadOnlyList<RegEntry> entries) => entries.All(IsApplied);

    public static void Set(IReadOnlyList<RegEntry> entries, bool applied)
    {
        foreach (RegEntry entry in entries)
        {
            Write(entry, applied ? entry.Applied : entry.Original);
        }
    }

    private static bool IsApplied(RegEntry entry)
    {
        using RegistryKey baseKey = RegistryKey.OpenBaseKey(entry.Hive, RegistryView.Registry64);
        using RegistryKey? key = baseKey.OpenSubKey(entry.Path);
        object? raw = key?.GetValue(entry.Name);

        if (raw is null)
        {
            // Absent value: Windows default behavior decides, if known.
            return entry.DefaultApplied ?? entry.Applied is null;
        }

        if (entry.Applied is null)
        {
            return false;
        }

        if (entry.Kind == RegKind.String)
        {
            return string.Equals(raw.ToString(), entry.Applied, StringComparison.OrdinalIgnoreCase);
        }

        return ToNumber(raw) == long.Parse(entry.Applied, CultureInfo.InvariantCulture);
    }

    private static long? ToNumber(object raw) => raw switch
    {
        int i => (uint)i,
        long l => l,
        string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) => parsed,
        _ => null,
    };

    private static void Write(RegEntry entry, string? value)
    {
        using RegistryKey baseKey = RegistryKey.OpenBaseKey(entry.Hive, RegistryView.Registry64);

        if (value is null)
        {
            using RegistryKey? existing = baseKey.OpenSubKey(entry.Path, true);
            existing?.DeleteValue(entry.Name, false);
            return;
        }

        using RegistryKey key = baseKey.CreateSubKey(entry.Path, true)
            ?? throw new InvalidOperationException("Could not open the registry key " + entry.Path);

        // Windows keeps some numeric values as text: in that case the existing type is respected.
        bool keepAsText = entry.Kind != RegKind.String
            && key.GetValue(entry.Name) != null
            && key.GetValueKind(entry.Name) == RegistryValueKind.String;

        if (entry.Kind == RegKind.String || keepAsText)
        {
            key.SetValue(entry.Name, value, RegistryValueKind.String);
        }
        else if (entry.Kind == RegKind.QWord)
        {
            key.SetValue(entry.Name, long.Parse(value, CultureInfo.InvariantCulture), RegistryValueKind.QWord);
        }
        else
        {
            key.SetValue(entry.Name, unchecked((int)uint.Parse(value, CultureInfo.InvariantCulture)), RegistryValueKind.DWord);
        }
    }
}
