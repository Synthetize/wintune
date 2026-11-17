using System;
using System.IO;
using System.Text.Json;

namespace WinTune.Core;

/// <summary>Program settings, in %AppData%\WinTune\impostazioni.json.</summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// True: the X button hides the window and WinTune keeps running in the notification area.
    /// False: the X button closes the program.
    /// </summary>
    public bool HideOnClose { get; set; } = true;

    private static string Path_ => Path.Combine(ProfileService.DataFolder, "impostazioni.json");

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
