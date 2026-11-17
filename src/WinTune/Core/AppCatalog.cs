using System;
using System.Collections.Generic;
using System.Linq;

namespace WinTune.Core;

/// <summary>
/// Starting catalog of recommended apps, divided by category. It is copied into the profile the first time;
/// from then on the user edits it freely from the interface (categories and apps).
/// </summary>
public static class AppCatalog
{
    public const string OtherCategory = "Other";
    public const string DetectedCategory = "Detected on this PC";

    /// <summary>Converts category names saved in Italian by earlier versions.</summary>
    public static string MigrateCategory(string name) => name switch
    {
        "Altro" => OtherCategory,
        "Rilevate su questo PC" => DetectedCategory,
        "Comunicazione" => "Communication",
        "Utilità" => "Utilities",
        "Hardware e monitoraggio" => "Hardware & monitoring",
        "Sviluppo" => "Development",
        _ => name,
    };

    private static readonly (string Category, string Name, string Id)[] Defaults =
    {
        ("Browser", "Firefox", "Mozilla.Firefox"),
        ("Browser", "Chrome", "Google.Chrome"),
        ("Browser", "Brave", "Brave.Brave"),

        ("Gaming", "Steam", "Valve.Steam"),
        ("Gaming", "Epic Games Launcher", "EpicGames.EpicGamesLauncher"),
        ("Gaming", "GOG Galaxy", "GOG.Galaxy"),
        ("Gaming", "Ubisoft Connect", "Ubisoft.Connect"),
        ("Gaming", "EA app", "ElectronicArts.EADesktop"),

        ("Communication", "Discord", "Discord.Discord"),
        ("Communication", "Telegram", "Telegram.TelegramDesktop"),
        ("Communication", "Zoom", "Zoom.Zoom"),
        ("Communication", "Thunderbird", "Mozilla.Thunderbird"),

        ("Multimedia", "VLC", "VideoLAN.VLC"),
        ("Multimedia", "Spotify", "Spotify.Spotify"),
        ("Multimedia", "OBS Studio", "OBSProject.OBSStudio"),

        ("Utilities", "7-Zip", "7zip.7zip"),
        ("Utilities", "Notepad++", "Notepad++.Notepad++"),
        ("Utilities", "PowerToys", "Microsoft.PowerToys"),
        ("Utilities", "Everything", "voidtools.Everything"),
        ("Utilities", "ShareX", "ShareX.ShareX"),
        ("Utilities", "qBittorrent", "qBittorrent.qBittorrent"),

        ("Hardware & monitoring", "HWiNFO", "REALiX.HWiNFO"),
        ("Hardware & monitoring", "CPU-Z", "CPUID.CPU-Z"),
        ("Hardware & monitoring", "HWMonitor", "CPUID.HWMonitor"),
        ("Hardware & monitoring", "MSI Afterburner", "Guru3D.Afterburner"),
        ("Hardware & monitoring", "CrystalDiskInfo", "CrystalDewWorld.CrystalDiskInfo"),
        ("Hardware & monitoring", "FanControl", "Rem0o.FanControl"),

        ("Development", "Visual Studio Code", "Microsoft.VisualStudioCode"),
        ("Development", "Git", "Git.Git"),
    };

    /// <summary>
    /// If the profile does not have the catalog yet, creates it with the recommended apps (not selected).
    /// Apps already in the list stay, with their selection. Returns true if it changed the profile.
    /// </summary>
    public static bool EnsureInitialized(Profile profile)
    {
        if (profile.HasAppCatalog)
        {
            return false;
        }

        List<ProfileApp> existing = profile.Apps.ToList();
        profile.Apps.Clear();
        profile.AppCategories.Clear();

        foreach ((string category, string name, string id) in Defaults)
        {
            if (!profile.AppCategories.Contains(category))
            {
                profile.AppCategories.Add(category);
            }

            ProfileApp? already = existing.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
            profile.Apps.Add(new ProfileApp(id, name, already?.Selected ?? false, category));
        }

        foreach (ProfileApp app in existing)
        {
            bool inDefaults = Defaults.Any(d => string.Equals(d.Id, app.Id, StringComparison.OrdinalIgnoreCase));
            if (inDefaults)
            {
                continue;
            }

            string category = string.IsNullOrWhiteSpace(app.Category) ? OtherCategory : app.Category;
            if (!profile.AppCategories.Contains(category))
            {
                profile.AppCategories.Add(category);
            }

            profile.Apps.Add(app with { Category = category });
        }

        profile.HasAppCatalog = true;
        return true;
    }
}
