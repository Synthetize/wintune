using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace WinTune.Core;

/// <summary>Program settings, in %AppData%\WinTune\settings.json.</summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// True: the X button hides the window and WinTune keeps running in the notification area.
    /// False: the X button closes the program.
    /// </summary>
    public bool HideOnClose { get; set; } = true;

    /// <summary>The Overview warns when the temporary files take at least this percentage of the system drive.</summary>
    public double TempWarningPercent { get; set; } = 5;

    /// <summary>True: WinTune looks for a new release on GitHub (at most once a day).</summary>
    public bool CheckSelfUpdates { get; set; }

    /// <summary>When WinTune last looked for a new release.</summary>
    public DateTime LastSelfUpdateCheck { get; set; } = DateTime.MinValue;

    /// <summary>True: the accented-letters shortcuts are active (see <see cref="KeyboardService"/>).</summary>
    public bool KeyboardEnabled { get; set; }

    /// <summary>Key held to type the special characters: "RightAlt" or "RightCtrl".</summary>
    public string KeyboardTrigger { get; set; } = KeyboardService.RightAlt;

    /// <summary>Table of the shortcuts.</summary>
    public List<KeyMapping> KeyboardMappings { get; set; } = KeyMapping.Defaults();

    private static string Path_ => Path.Combine(ProfileService.DataFolder, "settings.json");

    /// <summary>Settings in use, read once at startup.</summary>
    public static AppSettings Current { get; } = Load();

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(Path_))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path_)) ?? new AppSettings();
            }
        }
        catch (Exception)
        {
            // Damaged file: default values are used.
        }

        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(ProfileService.DataFolder);
        File.WriteAllText(Path_, JsonSerializer.Serialize(this, JsonOptions));
    }
}
