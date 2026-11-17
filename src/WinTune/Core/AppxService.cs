using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace WinTune.Core;

/// <summary>A preinstalled Windows app that can be removed.</summary>
public sealed record BloatApp(string Package, string Name, bool Recommended);

/// <summary>List and removal of preinstalled apps (Appx packages).</summary>
public static class AppxService
{
    private static readonly Regex PackagePattern = new(@"^[A-Za-z0-9.\-_]+$", RegexOptions.Compiled);

    /// <summary>
    /// Known preinstalled apps. "Recommended" marks the ones almost nobody uses.
    /// Xbox Game Bar is deliberately not in the list: removing it causes a popup when games start.
    /// </summary>
    public static IReadOnlyList<BloatApp> Known { get; } = new List<BloatApp>
    {
        new("Microsoft.BingNews", "News", true),
        new("Microsoft.BingSearch", "Bing Search", true),
        new("Microsoft.Getstarted", "Tips", true),
        new("Microsoft.MicrosoftOfficeHub", "Microsoft 365 (Office shortcut)", true),
        new("Microsoft.MicrosoftSolitaireCollection", "Solitaire Collection", true),
        new("Microsoft.People", "People", true),
        new("Microsoft.PowerAutomateDesktop", "Power Automate", true),
        new("Microsoft.Todos", "Microsoft To Do", true),
        new("Microsoft.WindowsFeedbackHub", "Feedback Hub", true),
        new("Microsoft.WindowsMaps", "Maps", true),
        new("Microsoft.ZuneVideo", "Movies & TV", true),
        new("Clipchamp.Clipchamp", "Clipchamp", true),
        new("Microsoft.549981C3F5F10", "Cortana", true),
        new("MicrosoftTeams", "Teams (personal)", true),
        new("Microsoft.Windows.DevHome", "Dev Home", true),
        new("Microsoft.MixedReality.Portal", "Mixed Reality Portal", true),
        new("Microsoft.Microsoft3DViewer", "3D Viewer", true),
        new("Microsoft.SkypeApp", "Skype", true),
        new("7EE7776C.LinkedInforWindows", "LinkedIn", true),
        new("Microsoft.Copilot", "Copilot", false),
        new("MSTeams", "Teams", false),
        new("Microsoft.OutlookForWindows", "Outlook (new)", false),
        new("Microsoft.BingWeather", "Weather", false),
        new("Microsoft.GetHelp", "Get Help", false),
        new("Microsoft.MicrosoftStickyNotes", "Sticky Notes", false),
        new("Microsoft.YourPhone", "Phone Link", false),
        new("MicrosoftCorporationII.QuickAssist", "Quick Assist", false),
        new("Microsoft.WindowsSoundRecorder", "Sound Recorder", false),
        new("Microsoft.WindowsAlarms", "Clock", false),
        new("Microsoft.ZuneMusic", "Media Player", false),
        new("Microsoft.GamingApp", "Xbox app", false),
        new("SpotifyAB.SpotifyMusic", "Spotify (preinstalled)", false),
    };

    /// <summary>Names of the Appx packages installed for the user. Null if reading fails.</summary>
    public static async Task<HashSet<string>?> GetInstalledAsync()
    {
        try
        {
            ProcessResult result = await ProcessRunner.RunPowerShellAsync(
                "Get-AppxPackage | ForEach-Object { Write-Output $_.Name }",
                120000);

            if (!result.Success)
            {
                return null;
            }

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in result.Output.Split('\n'))
            {
                string name = line.Trim();
                if (name.Length > 0)
                {
                    names.Add(name);
                }
            }

            return names;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Removes the packages for the user and from provisioning (so they do not come back with new profiles).
    /// Returns the errors, one per package that was not removed.
    /// </summary>
    public static async Task<List<string>> RemoveAsync(IEnumerable<string> packages)
    {
        var errors = new List<string>();
        List<string> valid = packages.Where(p => PackagePattern.IsMatch(p)).Distinct().ToList();
        if (valid.Count == 0)
        {
            return errors;
        }

        string list = string.Join(",", valid.Select(p => "'" + p + "'"));
        string script =
            "$names = @(" + list + "); " +
            "$provisioned = @(Get-AppxProvisionedPackage -Online); " +
            "foreach ($n in $names) { " +
            "try { " +
            "Get-AppxPackage -Name $n | Remove-AppxPackage -ErrorAction Stop; " +
            "$provisioned | Where-Object { $_.DisplayName -eq $n } | ForEach-Object { " +
            "Remove-AppxProvisionedPackage -Online -PackageName $_.PackageName -ErrorAction Stop | Out-Null }; " +
            "Write-Output ('OK|' + $n) " +
            "} catch { Write-Output ('KO|' + $n + '|' + $_.Exception.Message) } }";

        try
        {
            ProcessResult result = await ProcessRunner.RunPowerShellAsync(script, 600000);

            foreach (string line in result.Output.Split('\n'))
            {
                string text = line.Trim();
                if (text.StartsWith("KO|", StringComparison.Ordinal))
                {
                    string[] parts = text.Split('|', 3);
                    errors.Add(parts.Length == 3 ? parts[1] + ": " + parts[2] : text);
                }
                else if (text.StartsWith("ERROR:", StringComparison.Ordinal))
                {
                    errors.Add(text);
                }
            }
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
        }

        return errors;
    }
}
