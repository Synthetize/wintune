using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace WinTune.Core;

public sealed record WingetSearchResult(string Name, string Id, string Version)
{
    public string Detail => string.IsNullOrWhiteSpace(Version) ? Id : Id + "   ·   " + Version;
}

/// <summary>Search, state and installation of apps with winget.</summary>
/// <summary>An installed app for which winget has a newer version.</summary>
public sealed record WingetUpgrade(string Name, string Id, string Current, string Available);

public static class WingetService
{
    // winget codes that mean "already installed" or "no update".
    private const int UpdateNotApplicable = unchecked((int)0x8A15002B);
    private const int PackageAlreadyInstalled = unchecked((int)0x8A150061);
    private const int NoApplicationsFound = unchecked((int)0x8A150014);

    private static readonly Regex IdPattern = new(@"^[A-Za-z0-9][A-Za-z0-9.+_\-]{1,120}$", RegexOptions.Compiled);
    private static readonly Regex VersionPattern = new(@"(\d+)\.(\d+)", RegexOptions.Compiled);
    private static readonly Regex HexPattern = new(@"0x([0-9A-Fa-f]{8})", RegexOptions.Compiled);

    // Frequent winget errors, explained in plain words.
    private static readonly Dictionary<string, string> KnownErrors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["80072EE7"] = "Download server unreachable (address not found). The problem is with the site hosting the file or with your connection: try again later.",
        ["80072EFD"] = "Could not connect to the download server. Try again later.",
        ["80072EE2"] = "The download server did not respond in time. Try again later.",
        ["80190194"] = "Installation file not found on the server (error 404). Try again later.",
        ["8A150008"] = "Download failed. Try again later.",
        ["8A150011"] = "The downloaded file does not match the expected one. This usually fixes itself within a few hours.",
        ["8A150010"] = "No installer suitable for this PC.",
        ["8A150014"] = "App not found.",
        ["8A150016"] = "More than one app matches this ID.",
        ["8A150101"] = "The app is open. Close it and try again.",
        ["8A150102"] = "Another installation is already in progress. Try again when it has finished.",
        ["8A150103"] = "Some of the app files are in use. Close the app and try again.",
        ["8A150105"] = "Not enough disk space.",
        ["8A150107"] = "The installer cannot connect to the Internet.",
        ["8A150109"] = "A PC restart is needed to complete the installation.",
        ["8A15010A"] = "Restart the PC and try again: the installer asks for it before continuing.",
        ["8A15010C"] = "The installer stopped by itself. This usually happens when the app is open: close it and try again, or open the arrow next to the button and choose \"showing the installer\" to see what it asks.",
    };

    private static string LogPath => Path.Combine(ProfileService.DataFolder, "winget.log");

    public static bool IsValidId(string id) => IdPattern.IsMatch(id);

    /// <summary>
    /// Wider than <see cref="IsValidId"/>: also accepts the IDs winget gives to programs it does not know
    /// (for example "ARP\Machine\X64\{...}"), as long as they cannot break the command line.
    /// </summary>
    public static bool IsUninstallableId(string id) =>
        id.Length is >= 2 and <= 250
        && !id.Contains('"')
        && !id.Contains('\u2026')
        && !id.EndsWith('\\')
        && !id.Any(char.IsControl)
        && id.Trim() == id;

    private static string Exe
    {
        get
        {
            string alias = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft",
                "WindowsApps",
                "winget.exe");

            return File.Exists(alias) ? alias : "winget.exe";
        }
    }

    private static Task<ProcessResult> RunAsync(string arguments, int timeoutMs) =>
        ProcessRunner.RunAsync(Exe, arguments, timeoutMs, Encoding.UTF8);

    /// <summary>winget version (e.g. "v1.10.340"), or null if winget is missing or does not start.</summary>
    public static async Task<string?> GetVersionAsync()
    {
        try
        {
            ProcessResult result = await RunAsync("--version", 30000);
            string text = result.Output.Trim();
            return result.Success && text.Length > 0 ? text : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Versions before 1.6 do not have the options used here.</summary>
    public static bool IsTooOld(string version)
    {
        Match match = VersionPattern.Match(version);
        if (!match.Success)
        {
            return false;
        }

        int major = int.Parse(match.Groups[1].Value);
        int minor = int.Parse(match.Groups[2].Value);
        return major < 1 || (major == 1 && minor < 6);
    }

    /// <summary>Installs or repairs winget. First tries to register the package already in Windows, then downloads it.</summary>
    public static async Task<(bool Ok, string Message)> InstallOrRepairAsync()
    {
        try
        {
            await ProcessRunner.RunPowerShellAsync(
                "Add-AppxPackage -RegisterByFamilyName -MainPackage Microsoft.DesktopAppInstaller_8wekyb3d8bbwe",
                120000);

            string? version = await GetVersionAsync();
            if (version != null && !IsTooOld(version))
            {
                return (true, "winget " + version);
            }

            // Method indicated by Microsoft: official PowerShell module that downloads winget and its dependencies.
            const string script =
                "[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; " +
                "Install-PackageProvider -Name NuGet -Force | Out-Null; " +
                "Install-Module -Name Microsoft.WinGet.Client -Force -Repository PSGallery -Scope CurrentUser | Out-Null; " +
                "Repair-WinGetPackageManager -Force -Latest | Out-Null; " +
                "Write-Output 'OK'";

            ProcessResult result = await ProcessRunner.RunPowerShellAsync(script, 600000);

            version = await GetVersionAsync();
            if (version != null)
            {
                return (true, "winget " + version);
            }

            return (false, result.Summary);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>IDs of the installed packages winget recognizes. Null if reading fails.</summary>
    public static async Task<HashSet<string>?> GetInstalledIdsAsync()
    {
        string file = Path.Combine(Path.GetTempPath(), "wintune-winget-export.json");

        try
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }

            await RunAsync("export -o \"" + file + "\" --accept-source-agreements --disable-interactivity", 240000);

            if (!File.Exists(file))
            {
                return null;
            }

            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (JsonNode.Parse(File.ReadAllText(file)) is JsonObject root && root["Sources"] is JsonArray sources)
            {
                foreach (JsonNode? source in sources)
                {
                    if (source is JsonObject sourceObject && sourceObject["Packages"] is JsonArray packages)
                    {
                        foreach (JsonNode? package in packages)
                        {
                            if (package is JsonObject packageObject
                                && packageObject["PackageIdentifier"] is JsonValue idValue
                                && idValue.TryGetValue(out string? id)
                                && !string.IsNullOrWhiteSpace(id))
                            {
                                ids.Add(id);
                            }
                        }
                    }
                }
            }

            return ids;
        }
        catch
        {
            return null;
        }
        finally
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
            catch
            {
                // A leftover temporary file is not a problem.
            }
        }
    }

    public static async Task<List<WingetSearchResult>> SearchAsync(string query)
    {
        string clean = query.Replace("\"", "").Trim();
        if (clean.Length == 0)
        {
            return new List<WingetSearchResult>();
        }

        ProcessResult result = await RunAsync(
            "search \"" + clean + "\" --source winget --count 20 --accept-source-agreements --disable-interactivity",
            120000);

        return ParseSearchTable(result.Output);
    }

    /// <summary>
    /// Installs an app silently. It can take several minutes.
    /// The install folder is optional and only applies if the app installer accepts it.
    /// </summary>
    public static async Task<(bool Ok, string Message)> InstallAsync(string id, string? location = null)
    {
        if (!IsValidId(id))
        {
            return (false, "Invalid ID.");
        }

        // A trailing backslash before the quotes would be read as an escape character.
        string locationArgument = string.IsNullOrWhiteSpace(location) || location.Contains('"')
            ? ""
            : "--location \"" + location.TrimEnd('\\') + "\" ";

        try
        {
            ProcessResult result = await RunAsync(
                "install --id \"" + id + "\" --exact --source winget --silent " + locationArgument +
                "--accept-package-agreements --accept-source-agreements --disable-interactivity",
                45 * 60 * 1000);

            bool ok = result.ExitCode == 0
                || result.ExitCode == UpdateNotApplicable
                || result.ExitCode == PackageAlreadyInstalled;

            return (ok, ok ? "Installed" : DescribeFailure("install " + id, result));
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Installed apps that have an update available. The ones winget
    /// updates only on explicit request (pinned apps or apps that update themselves) are left out.
    /// </summary>
    public static async Task<List<WingetUpgrade>> GetUpgradesAsync()
    {
        ProcessResult result = await RunAsync(
            "upgrade --source winget --accept-source-agreements --disable-interactivity",
            240000);

        List<WingetUpgrade> upgrades = ParseUpgradeTable(result.Output);

        bool nothingToDo = result.ExitCode == 0
            || result.ExitCode == UpdateNotApplicable
            || result.ExitCode == NoApplicationsFound;

        if (upgrades.Count == 0 && !nothingToDo)
        {
            throw new InvalidOperationException(DescribeFailure("upgrade", result));
        }

        return upgrades
            .GroupBy(u => u.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(u => u.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Updates an app. It can take several minutes. Normally the installer works without showing itself;
    /// in interactive mode it opens its window, useful when it hangs or stops by itself.
    /// </summary>
    public static async Task<(bool Ok, string Message)> UpgradeAsync(string id, bool interactive = false, bool asAdministrator = false)
    {
        if (!IsValidId(id))
        {
            return (false, "Invalid ID.");
        }

        // Many installers misbehave when elevated: by default the update runs as the normal user.
        if (!asAdministrator)
        {
            return await UpgradeAsUserAsync(id, interactive);
        }

        try
        {
            ProcessResult result = await RunAsync(
                "upgrade --id \"" + id + "\" --exact --source winget " + (interactive ? "--interactive " : "--silent ") +
                "--accept-package-agreements --accept-source-agreements --disable-interactivity",
                45 * 60 * 1000);

            bool ok = result.ExitCode == 0 || result.ExitCode == UpdateNotApplicable;
            return (ok, ok ? "Updated" : DescribeFailure("upgrade " + id, result));
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>Uninstalls an app silently.</summary>
    public static async Task<(bool Ok, string Message)> UninstallAsync(string id)
    {
        if (!IsUninstallableId(id))
        {
            return (false, "Invalid ID.");
        }

        try
        {
            ProcessResult result = await RunAsync(
                "uninstall --id \"" + id + "\" --exact --silent --accept-source-agreements --disable-interactivity",
                30 * 60 * 1000);

            if (result.ExitCode != 0)
            {
                // Apps installed "for the current user" refuse to be removed from an elevated process, and the
                // message depends on the Windows language: so any failure is retried as the normal user.
                return await UninstallAsUserAsync(id);
            }

            bool ok = result.ExitCode == 0;
            return (ok, ok ? "Uninstalled" : DescribeFailure("uninstall " + id, result));
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Runs winget without administrator rights, as the user who is logged in, through a temporary
    /// scheduled task (the only reliable way to drop elevation). <paramref name="wingetArguments"/> must be
    /// PowerShell-safe (IDs in single quotes). Returns the exit code of winget and its last output line.
    /// </summary>
    private static async Task<(int? ExitCode, string LastLine)> RunWingetAsUserAsync(string wingetArguments, int timeoutMs)
    {
        string work = Path.Combine(Path.GetTempPath(), "WinTune-winget-" + Guid.NewGuid().ToString("N"));
        string taskName = "WinTune-winget-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(work);
        string outFile = Path.Combine(work, "out.txt");
        string codeFile = Path.Combine(work, "code.txt");

        string inner =
            "$ErrorActionPreference='Continue'; "
            + "& winget " + wingetArguments + " *> '" + outFile + "'; "
            + "$LASTEXITCODE | Out-File -Encoding ascii '" + codeFile + "'";
        string encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(inner));

        string script =
            "$u = (Get-CimInstance Win32_ComputerSystem).UserName; "
            + "if (-not $u) { Write-Output 'NOUSER'; exit 1 }; "
            + "$a = New-ScheduledTaskAction -Execute 'conhost.exe' -Argument '--headless powershell.exe -NoProfile -EncodedCommand " + encoded + "'; "
            + "$p = New-ScheduledTaskPrincipal -UserId $u -LogonType Interactive -RunLevel Limited; "
            + "$s = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Hours 2); "
            + "Register-ScheduledTask -TaskName '" + taskName + "' -Action $a -Principal $p -Settings $s -Force | Out-Null; "
            + "Start-ScheduledTask -TaskName '" + taskName + "'; "
            + "Start-Sleep -Seconds 3; "
            + "while ((Get-ScheduledTask -TaskName '" + taskName + "').State -eq 'Running') { Start-Sleep -Seconds 2 }; "
            + "Unregister-ScheduledTask -TaskName '" + taskName + "' -Confirm:$false";

        try
        {
            ProcessResult run = await ProcessRunner.RunPowerShellAsync(script, timeoutMs);
            if (run.Output.Contains("NOUSER", StringComparison.Ordinal))
            {
                return (null, "No user is logged in to run winget.");
            }

            string output = File.Exists(outFile) ? File.ReadAllText(outFile).Trim() : "";
            string codeText = File.Exists(codeFile) ? File.ReadAllText(codeFile).Trim() : "";
            string last = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? run.Error;

            return (int.TryParse(codeText, out int code) ? code : null, last);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
        finally
        {
            try
            {
                Directory.Delete(work, true);
            }
            catch
            {
                // Temporary files: nothing to do if they cannot be removed.
            }
        }
    }

    private static async Task<(bool Ok, string Message)> UninstallAsUserAsync(string id)
    {
        string safeId = id.Replace("'", "''");
        (int? code, string last) = await RunWingetAsUserAsync(
            "uninstall --id '" + safeId + "' --exact --silent --accept-source-agreements --disable-interactivity",
            30 * 60 * 1000);

        return code == 0
            ? (true, "Uninstalled")
            : (false, "Uninstall failed (as the current user): " + last);
    }

    /// <summary>"winget upgrade" without administrator rights.</summary>
    private static async Task<(bool Ok, string Message)> UpgradeAsUserAsync(string id, bool interactive)
    {
        string safeId = id.Replace("'", "''");
        (int? code, string last) = await RunWingetAsUserAsync(
            "upgrade --id '" + safeId + "' --exact --source winget " + (interactive ? "--interactive " : "--silent ")
                + "--accept-package-agreements --accept-source-agreements --disable-interactivity",
            45 * 60 * 1000);

        if (code is 0 || code == UpdateNotApplicable)
        {
            return (true, "Updated");
        }

        return (false, "Run as administrator. winget said: " + last);
    }

    /// <summary>
    /// Apps installed on this PC that winget recognizes, with name and ID.
    /// If the "winget list" table is not readable it uses only the IDs, deriving the name from the ID.
    /// </summary>
    public static async Task<List<WingetSearchResult>> GetInstalledAppsAsync()
    {
        var apps = new List<WingetSearchResult>();

        try
        {
            ProcessResult result = await RunAsync(
                "list --source winget --accept-source-agreements --disable-interactivity",
                240000);
            apps = ParseSearchTable(result.Output);
        }
        catch
        {
            // Move on to the fallback method below.
        }

        if (apps.Count == 0)
        {
            HashSet<string>? ids = await GetInstalledIdsAsync();
            if (ids != null)
            {
                apps = ids.Select(id => new WingetSearchResult(NameFromId(id), id, "")).ToList();
            }
        }

        return apps
            .GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Every program that winget lists as installed (also the ones it does not recognize), sorted by name.
    /// Used by the uninstall list.
    /// </summary>
    public static async Task<List<WingetSearchResult>> GetAllInstalledAsync()
    {
        try
        {
            ProcessResult result = await RunAsync("list --accept-source-agreements --disable-interactivity", 240000);
            return ParseSearchTable(result.Output, anyId: true)
                .GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch
        {
            return new List<WingetSearchResult>();
        }
    }

    /// <summary>"Mozilla.Firefox" becomes "Firefox", "Microsoft.VisualStudioCode" stays readable.</summary>
    private static string NameFromId(string id)
    {
        int dot = id.IndexOf('.');
        string name = dot >= 0 && dot < id.Length - 1 ? id.Substring(dot + 1) : id;
        return name.Replace('.', ' ');
    }

    /// <summary>Understandable message for a failed command; the full output goes to winget.log.</summary>
    private static string DescribeFailure(string command, ProcessResult result)
    {
        string text = result.Output + "\n" + result.Error;
        string exitHex = result.ExitCode.ToString("X8");

        try
        {
            Directory.CreateDirectory(ProfileService.DataFolder);
            File.AppendAllText(
                LogPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  winget {command}  (code 0x{exitHex}){Environment.NewLine}" +
                string.Join(Environment.NewLine, CleanLines(text)) + Environment.NewLine + Environment.NewLine);
        }
        catch
        {
            // The log is only a help: if it cannot be written, carry on.
        }

        var codes = new List<string> { exitHex };
        foreach (Match match in HexPattern.Matches(text))
        {
            codes.Add(match.Groups[1].Value);
        }

        foreach (string code in codes)
        {
            if (KnownErrors.TryGetValue(code, out string? known))
            {
                return known;
            }
        }

        return LastLine(text) + " (0x" + exitHex + ")";
    }

    // ---------- Reading the output ----------

    /// <summary>Clean lines: without partial carriage returns, progress bars and empty lines.</summary>
    private static List<string> CleanLines(string output)
    {
        var lines = new List<string>();

        foreach (string raw in output.Split('\n'))
        {
            // winget rewrites the same line with \r for the wait indicator: only the last piece counts.
            string line = raw.Split('\r').LastOrDefault(part => part.Trim().Length > 0) ?? "";
            line = line.TrimEnd();

            if (line.Trim().Length == 0)
            {
                continue;
            }

            if (line.Contains('█') || line.Contains('▒') || line.Trim().All(c => c == '-' || c == '\\' || c == '|' || c == '/' || c == ' '))
            {
                // Lines made only of dashes are needed by ParseSearchTable: they are kept if they are long.
                if (!(line.Trim().Length > 10 && line.Trim().All(c => c == '-')))
                {
                    continue;
                }
            }

            lines.Add(line);
        }

        return lines;
    }

    private static string LastLine(string output)
    {
        List<string> lines = CleanLines(output);
        // The line with the installer log path explains nothing: the one before counts.
        string? last = lines.LastOrDefault(l => !l.Trim().All(c => c == '-') && !l.Contains("DiagOutputDir", StringComparison.OrdinalIgnoreCase));
        return last?.Trim() ?? "unknown error";
    }

    /// <summary>Reads the "winget search" table. The columns are derived from the header line, whatever the language.</summary>
    private static List<WingetSearchResult> ParseSearchTable(string output, bool anyId = false)
    {
        var results = new List<WingetSearchResult>();
        List<string> lines = CleanLines(output);

        int separator = lines.FindIndex(l => l.Trim().Length > 10 && l.Trim().All(c => c == '-'));
        if (separator < 1)
        {
            return results;
        }

        string header = lines[separator - 1];
        var starts = new List<int>();
        for (int i = 0; i < header.Length; i++)
        {
            if (header[i] != ' ' && (i == 0 || header[i - 1] == ' '))
            {
                starts.Add(i);
            }
        }

        if (starts.Count < 2)
        {
            return results;
        }

        for (int row = separator + 1; row < lines.Count; row++)
        {
            string line = lines[row];
            if (line.Length <= starts[1])
            {
                continue;
            }

            string name = Slice(line, starts[0], starts[1]);
            string id = Slice(line, starts[1], starts.Count > 2 ? starts[2] : line.Length);
            string version = starts.Count > 2 ? Slice(line, starts[2], starts.Count > 3 ? starts[3] : line.Length) : "";

            if (name.Length > 0 && (anyId ? IsUninstallableId(id) : IsValidId(id)))
            {
                results.Add(new WingetSearchResult(name, id, version));
            }
        }

        return results;
    }

    /// <summary>
    /// Reads the first "winget upgrade" table: name, ID, installed version, available version.
    /// The second table, the one of apps to update only on explicit request, is ignored.
    /// </summary>
    private static List<WingetUpgrade> ParseUpgradeTable(string output)
    {
        var results = new List<WingetUpgrade>();
        List<string> lines = CleanLines(output);

        static bool IsSeparator(string line) => line.Trim().Length > 10 && line.Trim().All(c => c == '-');

        int separator = lines.FindIndex(IsSeparator);
        if (separator < 1)
        {
            return results;
        }

        string header = lines[separator - 1];
        var starts = new List<int>();
        for (int i = 0; i < header.Length; i++)
        {
            if (header[i] != ' ' && (i == 0 || header[i - 1] == ' '))
            {
                starts.Add(i);
            }
        }

        if (starts.Count < 4)
        {
            return results;
        }

        // A possible second table starts with the header line that precedes the next separator.
        int next = lines.FindIndex(separator + 1, IsSeparator);
        int end = next > 0 ? next - 1 : lines.Count;

        for (int row = separator + 1; row < end; row++)
        {
            string line = lines[row];
            if (line.Length <= starts[3])
            {
                continue;
            }

            string name = Slice(line, starts[0], starts[1]);
            string id = Slice(line, starts[1], starts[2]);
            string current = Slice(line, starts[2], starts[3]);
            string available = Slice(line, starts[3], starts.Count > 4 ? starts[4] : line.Length);

            if (name.Length > 0 && IsValidId(id) && available.Length > 0)
            {
                results.Add(new WingetUpgrade(name, id, current, available));
            }
        }

        return results;
    }

    private static string Slice(string line, int start, int end)
    {
        if (start >= line.Length)
        {
            return "";
        }

        end = Math.Min(end, line.Length);
        return line.Substring(start, end - start).Trim();
    }
}
