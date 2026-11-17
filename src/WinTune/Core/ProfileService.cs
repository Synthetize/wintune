using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WinTune.Core;

/// <summary>An app of the automatic install list.</summary>
public sealed record ProfileApp(string Id, string Name, bool Selected, string Category = "");

/// <summary>A manual reminder on the Overview (e.g. "Enable EXPO in the BIOS").</summary>
public sealed record ProfileReminder(string Text, bool Done);

/// <summary>The profile: name, wanted value of each optimization and list of apps to install.</summary>
public sealed class Profile
{
    public string Name { get; set; } = "Main PC";

    public Dictionary<string, string> Tweaks { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<ProfileApp> Apps { get; } = new();

    /// <summary>App categories, in the order they are shown (empty ones too).</summary>
    public List<string> AppCategories { get; } = new();

    /// <summary>True if the profile already contains the app catalog divided by category.</summary>
    public bool HasAppCatalog { get; set; }

    /// <summary>Links to the driver pages chosen by the user, per component (key "Type|Model").</summary>
    public Dictionary<string, string> DriverLinks { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Manual reminders: things to check that Windows cannot verify by itself.</summary>
    public List<ProfileReminder> Reminders { get; } = new();

    /// <summary>True if the profile already contains the list of reminders (even empty).</summary>
    public bool HasReminders { get; set; }

    /// <summary>Free notes on the overclock and undervolt values set in the BIOS.</summary>
    public string OcNotes { get; set; } = "";

    /// <summary>Apps whose updates are ignored: winget ID and name to show.</summary>
    public Dictionary<string, string> IgnoredUpdates { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Saved configurations of the external tools (archive in Base64, by tool id).
    /// They are present only in exported profiles: on the PC they live in separate files.
    /// </summary>
    public Dictionary<string, string> ToolBackups { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>A difference between the profile and the current state of the PC.</summary>
public sealed record ProfileDifference(Tweak Tweak, string Expected, string Actual);

/// <summary>Saves, loads and compares the profile (profilo.json).</summary>
public static class ProfileService
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Program data folder: %AppData%\WinTune.</summary>
    public static string DataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinTune");

    public static string ProfilePath => Path.Combine(DataFolder, "profilo.json");

    /// <summary>Loads a profile. Null if the file does not exist; exception if it is not valid.</summary>
    public static Profile? Load(string? path = null)
    {
        path ??= ProfilePath;
        if (!File.Exists(path))
        {
            return null;
        }

        if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root)
        {
            throw new InvalidDataException("The file is not a valid WinTune profile.");
        }

        var profile = new Profile();

        string? name = ReadString(root["nome"]);
        if (!string.IsNullOrWhiteSpace(name))
        {
            profile.Name = name;
        }

        if (root["ottimizzazioni"] is JsonObject tweaks)
        {
            foreach (KeyValuePair<string, JsonNode?> entry in tweaks)
            {
                if (entry.Value is null)
                {
                    continue;
                }

                profile.Tweaks[entry.Key] = entry.Value.GetValueKind() switch
                {
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.String => entry.Value.GetValue<string>(),
                    _ => entry.Value.ToJsonString(),
                };
            }
        }

        if (root["app"] is JsonArray apps)
        {
            foreach (JsonNode? node in apps)
            {
                if (node is not JsonObject app)
                {
                    continue;
                }

                string? id = ReadString(app["id"]);
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                string appName = ReadString(app["nome"]) ?? id;
                string category = AppCatalog.MigrateCategory(ReadString(app["categoria"]) ?? "");
                bool selected = app["selezionata"] is not JsonNode flag || flag.GetValueKind() != JsonValueKind.False;
                profile.Apps.Add(new ProfileApp(id, appName, selected, category));
            }
        }

        profile.OcNotes = ReadString(root["noteOC"]) ?? "";

        if (root["promemoria"] is JsonArray reminders)
        {
            profile.HasReminders = true;
            foreach (JsonNode? node in reminders)
            {
                if (node is not JsonObject reminder)
                {
                    continue;
                }

                string? text = ReadString(reminder["testo"]);
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                bool done = reminder["fatto"] is JsonNode flag && flag.GetValueKind() == JsonValueKind.True;
                profile.Reminders.Add(new ProfileReminder(text, done));
            }
        }

        if (root["linkDriver"] is JsonObject links)
        {
            foreach (KeyValuePair<string, JsonNode?> entry in links)
            {
                string? link = ReadString(entry.Value);
                if (!string.IsNullOrWhiteSpace(link))
                {
                    profile.DriverLinks[entry.Key] = link;
                }
            }
        }

        if (root["aggiornamentiIgnorati"] is JsonArray ignoredUpdates)
        {
            foreach (JsonNode? node in ignoredUpdates)
            {
                if (node is not JsonObject ignored)
                {
                    continue;
                }

                string? ignoredId = ReadString(ignored["id"]);
                if (!string.IsNullOrWhiteSpace(ignoredId))
                {
                    profile.IgnoredUpdates[ignoredId] = ReadString(ignored["nome"]) ?? ignoredId;
                }
            }
        }

        if (root["configurazioniTool"] is JsonObject toolBackups)
        {
            foreach (KeyValuePair<string, JsonNode?> entry in toolBackups)
            {
                string? data = ReadString(entry.Value);
                if (!string.IsNullOrWhiteSpace(data))
                {
                    profile.ToolBackups[entry.Key] = data;
                }
            }
        }

        if (root["categorieApp"] is JsonArray categories)
        {
            profile.HasAppCatalog = true;
            foreach (JsonNode? node in categories)
            {
                string? rawCategoryName = ReadString(node);
                string? categoryName = rawCategoryName is null ? null : AppCatalog.MigrateCategory(rawCategoryName);
                if (!string.IsNullOrWhiteSpace(categoryName) && !profile.AppCategories.Contains(categoryName))
                {
                    profile.AppCategories.Add(categoryName);
                }
            }
        }

        return profile;
    }

    public static void Save(Profile profile, string? path = null)
    {
        path ??= ProfilePath;

        var tweaks = new JsonObject();
        foreach (KeyValuePair<string, string> entry in profile.Tweaks.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
        {
            JsonNode? node;
            if (string.Equals(entry.Value, "true", StringComparison.OrdinalIgnoreCase))
            {
                node = JsonValue.Create(true);
            }
            else if (string.Equals(entry.Value, "false", StringComparison.OrdinalIgnoreCase))
            {
                node = JsonValue.Create(false);
            }
            else
            {
                node = JsonValue.Create(entry.Value);
            }

            tweaks[entry.Key] = node;
        }

        var apps = new JsonArray();
        foreach (ProfileApp app in profile.Apps)
        {
            apps.Add(new JsonObject
            {
                ["id"] = app.Id,
                ["nome"] = app.Name,
                ["categoria"] = app.Category,
                ["selezionata"] = app.Selected,
            });
        }

        var root = new JsonObject
        {
            ["nome"] = profile.Name,
            ["versione"] = 1,
            ["ottimizzazioni"] = tweaks,
            ["app"] = apps,
        };

        if (profile.OcNotes.Length > 0)
        {
            root["noteOC"] = profile.OcNotes;
        }

        if (profile.HasReminders)
        {
            var reminders = new JsonArray();
            foreach (ProfileReminder reminder in profile.Reminders)
            {
                reminders.Add(new JsonObject
                {
                    ["testo"] = reminder.Text,
                    ["fatto"] = reminder.Done,
                });
            }

            root["promemoria"] = reminders;
        }

        if (profile.DriverLinks.Count > 0)
        {
            var links = new JsonObject();
            foreach (KeyValuePair<string, string> entry in profile.DriverLinks)
            {
                links[entry.Key] = entry.Value;
            }

            root["linkDriver"] = links;
        }

        if (profile.HasAppCatalog)
        {
            var categories = new JsonArray();
            foreach (string category in profile.AppCategories)
            {
                categories.Add(JsonValue.Create(category));
            }

            root["categorieApp"] = categories;
        }

        if (profile.IgnoredUpdates.Count > 0)
        {
            var ignoredUpdates = new JsonArray();
            foreach (KeyValuePair<string, string> entry in profile.IgnoredUpdates.OrderBy(e => e.Value, StringComparer.CurrentCultureIgnoreCase))
            {
                ignoredUpdates.Add(new JsonObject
                {
                    ["id"] = entry.Key,
                    ["nome"] = entry.Value,
                });
            }

            root["aggiornamentiIgnorati"] = ignoredUpdates;
        }

        if (profile.ToolBackups.Count > 0)
        {
            var toolBackups = new JsonObject();
            foreach (KeyValuePair<string, string> entry in profile.ToolBackups)
            {
                toolBackups[entry.Key] = entry.Value;
            }

            root["configurazioniTool"] = toolBackups;
        }

        string? folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        File.WriteAllText(path, root.ToJsonString(WriteOptions));
    }

    /// <summary>
    /// Creates a profile with the current state of all available optimizations.
    /// Name and app list are taken from the given profile, if any.
    /// </summary>
    public static Profile Capture(Profile? basedOn = null)
    {
        var profile = new Profile();

        if (basedOn != null)
        {
            profile.Name = basedOn.Name;
            profile.Apps.AddRange(basedOn.Apps);
            profile.AppCategories.AddRange(basedOn.AppCategories);
            profile.HasAppCatalog = basedOn.HasAppCatalog;
            foreach (KeyValuePair<string, string> entry in basedOn.DriverLinks)
            {
                profile.DriverLinks[entry.Key] = entry.Value;
            }

            foreach (KeyValuePair<string, string> entry in basedOn.IgnoredUpdates)
            {
                profile.IgnoredUpdates[entry.Key] = entry.Value;
            }

            profile.Reminders.AddRange(basedOn.Reminders);
            profile.HasReminders = basedOn.HasReminders;
            profile.OcNotes = basedOn.OcNotes;
        }

        foreach (Tweak tweak in TweakCatalog.All.Where(t => t.IsAvailable))
        {
            string? value = TweakEngine.SafeRead(tweak);
            if (value != null)
            {
                profile.Tweaks[tweak.Id] = value;
            }
        }

        return profile;
    }

    /// <summary>Lists the optimizations whose current state differs from the profile one.</summary>
    public static List<ProfileDifference> Compare(Profile profile)
    {
        var differences = new List<ProfileDifference>();

        foreach (KeyValuePair<string, string> entry in profile.Tweaks)
        {
            Tweak? tweak = TweakCatalog.Find(entry.Key);
            if (tweak is null || !tweak.IsAvailable)
            {
                continue;
            }

            string? actual = TweakEngine.SafeRead(tweak);
            if (actual is null)
            {
                continue;
            }

            if (!string.Equals(actual, entry.Value, StringComparison.OrdinalIgnoreCase))
            {
                differences.Add(new ProfileDifference(tweak, entry.Value, actual));
            }
        }

        return differences;
    }

    /// <summary>Brings the PC back to the profile values. Returns the errors, one per optimization that failed.</summary>
    public static List<string> Apply(Profile profile)
    {
        var errors = new List<string>();

        foreach (ProfileDifference difference in Compare(profile))
        {
            try
            {
                TweakEngine.Apply(difference.Tweak, difference.Expected);
            }
            catch (Exception ex)
            {
                errors.Add(difference.Tweak.Title + ": " + ex.Message);
            }
        }

        return errors;
    }

    private static string? ReadString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) ? text : null;
}
