using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace WinTune.Core;

/// <summary>Registry reading and writing, always in the 64-bit view.</summary>
internal static class RegistryUtil
{
    private static RegistryKey OpenBase(RegistryHive hive) =>
        RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);

    public static int? GetDword(RegistryHive hive, string path, string name)
    {
        using RegistryKey baseKey = OpenBase(hive);
        using RegistryKey? key = baseKey.OpenSubKey(path);
        object? value = key?.GetValue(name);

        switch (value)
        {
            case int i:
                return i;
            case long l:
                return (int)l;
            case string s when int.TryParse(s, out int parsed):
                return parsed;
            default:
                return null;
        }
    }

    public static string? GetString(RegistryHive hive, string path, string name)
    {
        using RegistryKey baseKey = OpenBase(hive);
        using RegistryKey? key = baseKey.OpenSubKey(path);
        return key?.GetValue(name)?.ToString();
    }

    public static void SetDword(RegistryHive hive, string path, string name, int value)
    {
        using RegistryKey baseKey = OpenBase(hive);
        using RegistryKey key = baseKey.CreateSubKey(path, true)
            ?? throw new InvalidOperationException("Could not open the registry key " + path);
        key.SetValue(name, value, RegistryValueKind.DWord);
    }

    public static void SetString(RegistryHive hive, string path, string name, string value)
    {
        using RegistryKey baseKey = OpenBase(hive);
        using RegistryKey key = baseKey.CreateSubKey(path, true)
            ?? throw new InvalidOperationException("Could not open the registry key " + path);
        key.SetValue(name, value, RegistryValueKind.String);
    }

    public static byte[]? GetBinary(RegistryHive hive, string path, string name)
    {
        using RegistryKey baseKey = OpenBase(hive);
        using RegistryKey? key = baseKey.OpenSubKey(path);
        return key?.GetValue(name) as byte[];
    }

    public static void SetBinary(RegistryHive hive, string path, string name, byte[] value)
    {
        using RegistryKey baseKey = OpenBase(hive);
        using RegistryKey key = baseKey.CreateSubKey(path, true)
            ?? throw new InvalidOperationException("Could not open the registry key " + path);
        key.SetValue(name, value, RegistryValueKind.Binary);
    }

    /// <summary>All the values of a key, as text. Empty list if the key does not exist.</summary>
    public static Dictionary<string, string> GetStringValues(RegistryHive hive, string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using RegistryKey baseKey = OpenBase(hive);
        using RegistryKey? key = baseKey.OpenSubKey(path);
        if (key is null)
        {
            return values;
        }

        foreach (string name in key.GetValueNames())
        {
            if (name.Length > 0)
            {
                values[name] = key.GetValue(name)?.ToString() ?? "";
            }
        }

        return values;
    }

    /// <summary>Names of the subkeys. Empty list if the key does not exist.</summary>
    public static List<string> GetSubKeyNames(RegistryHive hive, string path)
    {
        using RegistryKey baseKey = OpenBase(hive);
        using RegistryKey? key = baseKey.OpenSubKey(path);
        return key is null ? new List<string>() : new List<string>(key.GetSubKeyNames());
    }

    public static bool KeyExists(RegistryHive hive, string path)
    {
        using RegistryKey baseKey = OpenBase(hive);
        using RegistryKey? key = baseKey.OpenSubKey(path);
        return key != null;
    }

    public static void DeleteKeyTree(RegistryHive hive, string path)
    {
        using RegistryKey baseKey = OpenBase(hive);
        baseKey.DeleteSubKeyTree(path, false);
    }

    public static void DeleteValue(RegistryHive hive, string path, string name)
    {
        using RegistryKey baseKey = OpenBase(hive);
        using RegistryKey? key = baseKey.OpenSubKey(path, true);
        key?.DeleteValue(name, false);
    }
}
