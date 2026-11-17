using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace WinTune.Core;

/// <summary>Configuration file to save: folder (relative to the program one), filter, subfolders yes or no.</summary>
public sealed record ToolBackupItem(string Folder, string Pattern, bool Recursive);

/// <summary>Windows scheduled task that starts a tool.</summary>
public sealed record ToolTask(string Path, string Name, bool Enabled);

/// <summary>Saved configuration of a tool.</summary>
public sealed record ToolBackupInfo(DateTime Date, int FileCount);

/// <summary>An external program that WinTune installs, starts and whose configuration it saves.</summary>
public sealed class ExternalTool
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }

    public required string WingetId { get; init; }

    /// <summary>Name of the tool folder inside the WinTune "Tool" folder. No spaces.</summary>
    public required string FolderName { get; init; }

    /// <summary>Name of the main executable, for example "FanControl.exe".</summary>
    public required string ExeName { get; init; }

    /// <summary>Processes to close (without ".exe"), the main one first.</summary>
    public required string[] ProcessNames { get; init; }

    /// <summary>Beginning of the name under which the program appears among the installed ones.</summary>
    public string[] UninstallNames { get; init; } = Array.Empty<string>();

    /// <summary>Registry values that hold the path of the program or of its folder.</summary>
    public (RegistryHive Hive, string Path, string Value)[] RegistryHints { get; init; } = Array.Empty<(RegistryHive, string, string)>();

    /// <summary>Usual folder names inside "Program Files".</summary>
    public string[] DefaultFolders { get; init; } = Array.Empty<string>();

    /// <summary>Arguments to open the program window.</summary>
    public string OpenArguments { get; init; } = "";

    /// <summary>Arguments for automatic start (usually: minimized).</summary>
    public string AutostartArguments { get; init; } = "";

    /// <summary>Arguments with which the program closes the instance that is already open; empty if it has none.</summary>
    public string ExitArguments { get; init; } = "";

    /// <summary>Configuration files to save, given the program folder.</summary>
    public required Func<string, List<ToolBackupItem>> BackupItems { get; init; }

    /// <summary>Folder of the profiles that can be activated on the fly; null if the program has none.</summary>
    public Func<string, string>? ProfileFolder { get; init; }

    /// <summary>Title of the profile list, for example "Fan profiles".</summary>
    public string ProfileLabel { get; init; } = "";

    /// <summary>Arguments to activate a profile; {0} is the file path.</summary>
    public string ProfileArguments { get; init; } = "";

    /// <summary>False for programs that are only opened (start with Windows and configuration saving make no sense).</summary>
    public bool HasExtras { get; init; } = true;
}

/// <summary>
/// A tool that is not installed: it is a PowerShell script to run as administrator
/// (for example the Chris Titus Tech Windows Utility, downloaded at every start from the author's site).
/// </summary>
public sealed record ScriptTool(string Id, string Name, string Description, string Command, string Source);

/// <summary>State of a tool on this PC.</summary>
public sealed class ToolStatus
{
    public required ExternalTool Tool { get; init; }

    /// <summary>Path of the executable; null if the program was not found.</summary>
    public string? ExePath { get; init; }

    public string Version { get; init; } = "";

    public bool IsRunning { get; init; }

    public ToolBackupInfo? Backup { get; init; }

    /// <summary>Paths of the profiles that can be activated on the fly.</summary>
    public List<string> Profiles { get; init; } = new();

    /// <summary>Scheduled tasks that start the program.</summary>
    public List<ToolTask> Tasks { get; set; } = new();

    public bool IsInstalled => ExePath != null;

    public string? InstallFolder => ExePath is null ? null : Path.GetDirectoryName(ExePath);

    public bool Autostart => Tasks.Any(t => t.Enabled);
}

/// <summary>
/// External tools managed by WinTune: where they are installed, whether they are running, automatic start,
/// saving and restoring the configuration, profile switching.
/// </summary>
public static class ExternalToolService
{
    private const string ManifestName = "_wintune.txt";

    public static IReadOnlyList<ExternalTool> Tools { get; } = new[]
    {
        new ExternalTool
        {
            Id = "fancontrol",
            Name = "FanControl",
            Description = "Fan curves for CPU, graphics card and case.",
            WingetId = "Rem0o.FanControl",
            FolderName = "FanControl",
            ExeName = "FanControl.exe",
            ProcessNames = new[] { "FanControl" },
            UninstallNames = new[] { "FanControl", "Fan Control" },
            DefaultFolders = new[] { "FanControl" },
            OpenArguments = "-w",
            AutostartArguments = "-m",
            ExitArguments = "-e",
            BackupItems = folder => new List<ToolBackupItem>
            {
                new(RelativeTo(folder, FanControlConfigFolder(folder)), "*.json", false),
            },
            ProfileFolder = FanControlConfigFolder,
            ProfileLabel = "Fan profiles",
            ProfileArguments = "-c \"{0}\"",
        },
        new ExternalTool
        {
            Id = "rtss",
            Name = "RivaTuner Statistics Server",
            Description = "FPS limit and in-game overlay.",
            WingetId = "Guru3D.RTSS",
            FolderName = "RTSS",
            ExeName = "RTSS.exe",
            ProcessNames = new[] { "RTSS", "RTSSHooksLoader64", "EncoderServer", "EncoderServer64" },
            UninstallNames = new[] { "RivaTuner Statistics Server" },
            RegistryHints = new[]
            {
                (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Unwinder\RTSS", "InstallPath"),
                (RegistryHive.LocalMachine, @"SOFTWARE\Unwinder\RTSS", "InstallPath"),
            },
            DefaultFolders = new[] { "RivaTuner Statistics Server" },
            BackupItems = _ => new List<ToolBackupItem>
            {
                new("Profiles", "*", true),
                new(@"Plugins\Client", "*.cfg", false),
                new(@"Plugins\Client\Overlays", "*", true),
            },
        },
        new ExternalTool
        {
            Id = "spacesniffer",
            Name = "SpaceSniffer",
            Description = "Shows what takes up space on your drives, as a map of blocks.",
            WingetId = "UderzoSoftware.SpaceSniffer",
            FolderName = "SpaceSniffer",
            ExeName = "SpaceSniffer.exe",
            ProcessNames = new[] { "SpaceSniffer" },
            UninstallNames = new[] { "SpaceSniffer" },
            DefaultFolders = new[] { "SpaceSniffer" },
            HasExtras = false,
            BackupItems = _ => new List<ToolBackupItem>(),
        },
    };

    /// <summary>Tools to run as a script, without installation.</summary>
    public static IReadOnlyList<ScriptTool> Scripts { get; } = new[]
    {
        new ScriptTool(
            "winutil",
            "Chris Titus Tech Windows Utility",
            "Program installation, tweaks and repairs for Windows.",
            "[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; irm 'https://christitus.com/win' | iex",
            "christitus.com/win"),
    };

    private const string BesideKey = "_installBesideWinTune";

    private static string BackupFolder => Path.Combine(ProfileService.DataFolder, "tool");

    private static string PathsFile => Path.Combine(ProfileService.DataFolder, "tool-paths.json");

    /// <summary>"Tool" folder next to WinTune.exe, where tools are installed if the user wants.</summary>
    public static string ToolsFolder =>
        Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory, "Tool");

    /// <summary>Folder of a tool inside the WinTune one.</summary>
    public static string FolderBesideWinTune(ExternalTool tool) => Path.Combine(ToolsFolder, tool.FolderName);

    /// <summary>
    /// True if new tools should be installed in the WinTune folder instead of "Program Files".
    /// It is the initial choice; it is remembered between starts.
    /// </summary>
    public static bool InstallBesideWinTune
    {
        get => !LoadPaths().TryGetValue(BesideKey, out string? value)
            || !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
        set
        {
            Dictionary<string, string> settings = LoadPaths();
            settings[BesideKey] = value ? "true" : "false";
            SavePaths(settings);
        }
    }

    /// <summary>Folder in which to install the tool; null to leave the choice to the installer.</summary>
    public static string? InstallLocationFor(ExternalTool tool) =>
        InstallBesideWinTune ? FolderBesideWinTune(tool) : null;

    /// <summary>True if the file is inside the given folder.</summary>
    public static bool IsInside(string file, string folder) =>
        Path.GetFullPath(file).StartsWith(Path.GetFullPath(folder).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    private static string BackupPath(ExternalTool tool) => Path.Combine(BackupFolder, tool.Id + ".zip");

    // ---------- State ----------

    /// <summary>Reads the state of all tools. Takes a few seconds because of the scheduled tasks.</summary>
    public static async Task<List<ToolStatus>> GetStatusAsync()
    {
        Task<Dictionary<string, List<ToolTask>>> tasksJob = ReadAutostartTasksAsync();
        List<ToolStatus> list = await Task.Run(() => Tools.Select(ReadLocalState).ToList());

        Dictionary<string, List<ToolTask>> tasks;
        try
        {
            tasks = await tasksJob;
        }
        catch
        {
            // Without the task list, automatic start simply appears off.
            tasks = new Dictionary<string, List<ToolTask>>();
        }

        foreach (ToolStatus status in list)
        {
            if (tasks.TryGetValue(status.Tool.Id, out List<ToolTask>? found))
            {
                status.Tasks = found;
            }
        }

        return list;
    }

    private static ToolStatus ReadLocalState(ExternalTool tool)
    {
        string? exe = FindExe(tool);
        string version = "";
        var profiles = new List<string>();

        if (exe != null)
        {
            try
            {
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(exe);
                version = (info.ProductVersion ?? info.FileVersion ?? "").Split('+')[0].Trim();
            }
            catch
            {
                // The version is just extra information.
            }

            profiles = ListProfiles(tool, Path.GetDirectoryName(exe) ?? "");
        }

        return new ToolStatus
        {
            Tool = tool,
            ExePath = exe,
            Version = version,
            IsRunning = IsRunning(tool),
            Backup = GetBackupInfo(tool),
            Profiles = profiles,
        };
    }

    public static bool IsRunning(ExternalTool tool)
    {
        Process[] processes = Process.GetProcessesByName(tool.ProcessNames[0]);
        foreach (Process process in processes)
        {
            process.Dispose();
        }

        return processes.Length > 0;
    }

    // ---------- Where it is installed ----------

    /// <summary>Looks for the tool executable: path given by hand, WinTune folder, running process, registry, usual folders.</summary>
    public static string? FindExe(ExternalTool tool)
    {
        try
        {
            foreach (string candidate in Candidates(tool))
            {
                if (candidate.Length > 0 && candidate.IndexOfAny(Path.GetInvalidPathChars()) < 0 && File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }
        catch
        {
            // If the search is interrupted the program counts as not found: the user can point to it by hand.
        }

        return null;
    }

    private static IEnumerable<string> Candidates(ExternalTool tool)
    {
        // 1. Path given by the user.
        if (LoadPaths().TryGetValue(tool.Id, out string? manual))
        {
            yield return manual;
        }

        // 2. "Tool" folder next to WinTune.
        yield return Path.Combine(FolderBesideWinTune(tool), tool.ExeName);

        // 3. Program already running.
        string? running = RunningExePath(tool);
        if (running != null)
        {
            yield return running;
        }

        // 4. Registry values written by the program.
        foreach ((RegistryHive hive, string path, string value) in tool.RegistryHints)
        {
            string? found = RegistryUtil.GetString(hive, path, value);
            if (!string.IsNullOrWhiteSpace(found))
            {
                yield return ToExePath(tool, found);
            }
        }

        // 5. List of installed programs.
        foreach (string folder in UninstallFolders(tool))
        {
            yield return Path.Combine(folder, tool.ExeName);
        }

        // 6. Usual folders.
        foreach (string name in tool.DefaultFolders)
        {
            foreach (Environment.SpecialFolder root in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
            {
                yield return Path.Combine(Environment.GetFolderPath(root), name, tool.ExeName);
            }
        }

        // 7. "Portable" packages installed by winget.
        foreach (string found in WingetPortableExes(tool))
        {
            yield return found;
        }
    }

    private static string? RunningExePath(ExternalTool tool)
    {
        foreach (Process process in Process.GetProcessesByName(tool.ProcessNames[0]))
        {
            try
            {
                string? path = process.MainModule?.FileName;
                if (!string.IsNullOrEmpty(path))
                {
                    return path;
                }
            }
            catch
            {
                // For some processes Windows does not allow reading the path.
            }
            finally
            {
                process.Dispose();
            }
        }

        return null;
    }

    /// <summary>The value can be the path of the executable or that of the folder.</summary>
    private static string ToExePath(ExternalTool tool, string value)
    {
        string cleaned = value.Trim().Trim('"');
        return cleaned.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(Path.GetDirectoryName(cleaned) ?? "", tool.ExeName)
            : Path.Combine(cleaned, tool.ExeName);
    }

    private static IEnumerable<string> UninstallFolders(ExternalTool tool)
    {
        (RegistryHive Hive, string Path)[] roots =
        {
            (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        };

        foreach ((RegistryHive hive, string root) in roots)
        {
            foreach (string key in RegistryUtil.GetSubKeyNames(hive, root))
            {
                string path = root + "\\" + key;
                string? displayName = RegistryUtil.GetString(hive, path, "DisplayName");

                if (displayName is null
                    || !tool.UninstallNames.Any(n => displayName.StartsWith(n, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                string? location = RegistryUtil.GetString(hive, path, "InstallLocation");
                if (!string.IsNullOrWhiteSpace(location))
                {
                    yield return location.Trim().Trim('"');
                }

                // Without the install folder, try the icon one.
                string? icon = RegistryUtil.GetString(hive, path, "DisplayIcon");
                if (!string.IsNullOrWhiteSpace(icon))
                {
                    string file = icon.Split(',')[0].Trim().Trim('"');
                    string? folder = SafeDirectoryName(file);
                    if (!string.IsNullOrEmpty(folder))
                    {
                        yield return folder;
                    }
                }
            }
        }
    }

    private static IEnumerable<string> WingetPortableExes(ExternalTool tool)
    {
        var found = new List<string>();

        try
        {
            string packages = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft",
                "WinGet",
                "Packages");

            if (Directory.Exists(packages))
            {
                foreach (string folder in Directory.GetDirectories(packages, tool.WingetId + "*"))
                {
                    found.AddRange(Directory.GetFiles(folder, tool.ExeName, SearchOption.AllDirectories));
                }
            }
        }
        catch
        {
            // Folder not readable: move on.
        }

        return found;
    }

    private static string? SafeDirectoryName(string file)
    {
        try
        {
            return Path.GetDirectoryName(file);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Remembers the executable pointed to by hand by the user.</summary>
    public static void SetManualPath(ExternalTool tool, string exePath)
    {
        Dictionary<string, string> paths = LoadPaths();
        paths[tool.Id] = exePath;
        SavePaths(paths);
    }

    private static void SavePaths(Dictionary<string, string> paths)
    {
        var root = new JsonObject();
        foreach (KeyValuePair<string, string> entry in paths)
        {
            root[entry.Key] = entry.Value;
        }

        Directory.CreateDirectory(ProfileService.DataFolder);
        File.WriteAllText(PathsFile, root.ToJsonString());
    }

    private static Dictionary<string, string> LoadPaths()
    {
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (File.Exists(PathsFile) && JsonNode.Parse(File.ReadAllText(PathsFile)) is JsonObject root)
            {
                foreach (KeyValuePair<string, JsonNode?> entry in root)
                {
                    if (entry.Value is JsonValue value && value.TryGetValue(out string? path) && !string.IsNullOrWhiteSpace(path))
                    {
                        paths[entry.Key] = path;
                    }
                }
            }
        }
        catch
        {
            // A damaged file is the same as no path given.
        }

        return paths;
    }

    // ---------- Start and close ----------

    public static void Open(ToolStatus status) => Start(Require(status), status.Tool.OpenArguments);

    private static void Start(string exePath, string arguments)
    {
        Process.Start(new ProcessStartInfo(exePath, arguments)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? "",
        })?.Dispose();
    }

    /// <summary>Closes the tool: first the way the program provides, then by ending the remaining processes.</summary>
    public static Task CloseAsync(ToolStatus status) => Task.Run(() =>
    {
        ExternalTool tool = status.Tool;

        if (tool.ExitArguments.Length > 0 && status.ExePath != null && IsRunning(tool))
        {
            try
            {
                Start(status.ExePath, tool.ExitArguments);
                WaitUntilClosed(tool, 8000);
            }
            catch
            {
                // If the orderly close fails, move on to the forced one.
            }
        }

        foreach (string name in tool.ProcessNames)
        {
            foreach (Process process in Process.GetProcessesByName(name))
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                        process.WaitForExit(5000);
                    }
                }
                catch
                {
                    // The process may have closed in the meantime.
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
    });

    private static void WaitUntilClosed(ExternalTool tool, int timeoutMs)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeoutMs && IsRunning(tool))
        {
            System.Threading.Thread.Sleep(250);
        }
    }

    /// <summary>
    /// Runs the script in a PowerShell window. WinTune is already administrator, so the window is too.
    /// If the script does not start (for example without network) the window stays open with the reason.
    /// </summary>
    public static void RunScript(ScriptTool tool)
    {
        string command =
            "try { " + tool.Command + " } catch { " +
            "Write-Host ('Launch failed: ' + $_.Exception.Message) -ForegroundColor Red; " +
            "Read-Host 'Press Enter to close' }";

        string encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(command));

        Process.Start(new ProcessStartInfo(
            ProcessRunner.PowerShellPath,
            "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + encoded)
        {
            UseShellExecute = true,
        })?.Dispose();

        TweakEngine.LogChange("tool." + tool.Id, null, "started");
    }

    // ---------- Automatic start ----------

    /// <summary>For each tool, looks for the scheduled tasks that start its executable.</summary>
    private static async Task<Dictionary<string, List<ToolTask>>> ReadAutostartTasksAsync()
    {
        string map = string.Join("; ", Tools.Select(t => "'" + t.Id + "' = '" + t.ExeName + "'"));

        string script =
            "$map = @{ " + map + " }; " +
            "foreach ($t in Get-ScheduledTask) { foreach ($a in $t.Actions) { $x = [string]$a.Execute; if ($x) { " +
            "foreach ($k in $map.Keys) { if ($x -like ('*' + $map[$k] + '*')) { " +
            "Write-Output ($k + '|' + $t.TaskPath + '|' + $t.TaskName + '|' + $t.State) } } } } }";

        ProcessResult result = await ProcessRunner.RunPowerShellAsync(script, 60000);
        var tasks = new Dictionary<string, List<ToolTask>>(StringComparer.OrdinalIgnoreCase);

        if (!result.Success)
        {
            return tasks;
        }

        foreach (string line in result.Output.Split('\n'))
        {
            string[] parts = line.Trim().Split('|');
            if (parts.Length != 4)
            {
                continue;
            }

            if (!tasks.TryGetValue(parts[0], out List<ToolTask>? list))
            {
                list = new List<ToolTask>();
                tasks[parts[0]] = list;
            }

            list.Add(new ToolTask(parts[1], parts[2], !string.Equals(parts[3], "Disabled", StringComparison.OrdinalIgnoreCase)));
        }

        return tasks;
    }

    /// <summary>
    /// Turns start with Windows on or off. If a scheduled task already exists (for example the one
    /// created by the program itself) that one is used; otherwise WinTune creates its own.
    /// </summary>
    public static async Task SetAutostartAsync(ToolStatus status, bool enabled)
    {
        string exe = Require(status);
        ExternalTool tool = status.Tool;

        if (status.Tasks.Count == 0)
        {
            if (enabled)
            {
                await Task.Run(() => ScheduledTaskUtil.CreateAtLogon(
                    "Start " + tool.Name,
                    "Start " + tool.Name + " at sign-in. Created by WinTune.",
                    exe,
                    tool.AutostartArguments,
                    Path.GetDirectoryName(exe) ?? ""));
            }

            return;
        }

        // To turn on one task is enough; to turn off all of them must be disabled.
        IEnumerable<ToolTask> targets = enabled ? status.Tasks.Take(1) : status.Tasks;
        string verb = enabled ? "Enable-ScheduledTask" : "Disable-ScheduledTask";

        string script = string.Join("; ", targets.Select(t =>
            verb + " -TaskPath '" + t.Path.Replace("'", "''") + "' -TaskName '" + t.Name.Replace("'", "''") + "' | Out-Null"));

        ProcessResult result = await ProcessRunner.RunPowerShellAsync(script, 60000);
        if (!result.Success)
        {
            throw new InvalidOperationException(result.Summary);
        }
    }

    // ---------- Profiles that can be activated on the fly ----------

    private static List<string> ListProfiles(ExternalTool tool, string installFolder)
    {
        if (tool.ProfileFolder is null)
        {
            return new List<string>();
        }

        try
        {
            string folder = tool.ProfileFolder(installFolder);
            return Directory.Exists(folder)
                ? Directory.GetFiles(folder, "*.json").Where(IsConfigFile).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
                : new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>Activates a profile. If the program is closed it starts it with that profile.</summary>
    public static void ActivateProfile(ToolStatus status, string profilePath) =>
        Start(Require(status), string.Format(CultureInfo.InvariantCulture, status.Tool.ProfileArguments, profilePath));

    /// <summary>Recent FanControl versions keep configurations in "Configurations", older ones next to the executable.</summary>
    private static string FanControlConfigFolder(string installFolder)
    {
        string modern = Path.Combine(installFolder, "Configurations");
        return Directory.Exists(modern) ? modern : installFolder;
    }

    /// <summary>Excludes the .json files that are part of the program and are not configurations.</summary>
    private static bool IsConfigFile(string file) =>
        !file.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase)
        && !file.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase);

    private static string RelativeTo(string root, string folder) =>
        string.Equals(Path.GetFullPath(root).TrimEnd('\\'), Path.GetFullPath(folder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
            ? ""
            : Path.GetRelativePath(root, folder);

    // ---------- Saving and restoring the configuration ----------

    public static ToolBackupInfo? GetBackupInfo(ExternalTool tool)
    {
        string path = BackupPath(tool);

        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var zip = new ZipArchive(File.OpenRead(path), ZipArchiveMode.Read);

            DateTime date = File.GetLastWriteTime(path);
            ZipArchiveEntry? manifest = zip.GetEntry(ManifestName);
            if (manifest != null)
            {
                using var reader = new StreamReader(manifest.Open());
                if (DateTime.TryParse(reader.ReadToEnd().Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime saved))
                {
                    date = saved;
                }
            }

            int count = zip.Entries.Count(e => e.Name.Length > 0 && e.FullName != ManifestName);
            return new ToolBackupInfo(date, count);
        }
        catch
        {
            // A damaged archive is the same as no backup.
            return null;
        }
    }

    /// <summary>Saves the tool configuration files in the WinTune data folder.</summary>
    public static Task<ToolBackupInfo> BackupAsync(ToolStatus status) => Task.Run(() =>
    {
        ExternalTool tool = status.Tool;
        string root = Path.GetDirectoryName(Require(status)) ?? "";

        List<(string Full, string Relative)> files = CollectFiles(tool, root);
        if (files.Count == 0)
        {
            throw new InvalidOperationException("No configuration file found in " + root + ".");
        }

        Directory.CreateDirectory(BackupFolder);
        string target = BackupPath(tool);
        string temp = target + ".tmp";
        DateTime now = DateTime.Now;

        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach ((string full, string relative) in files)
            {
                ZipArchiveEntry entry = zip.CreateEntry(relative.Replace('\\', '/'), CompressionLevel.Optimal);
                using Stream output = entry.Open();

                // The program may have the file open: it is read without requiring exclusive use.
                using var input = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                input.CopyTo(output);
            }

            ZipArchiveEntry manifest = zip.CreateEntry(ManifestName);
            using var writer = new StreamWriter(manifest.Open());
            writer.Write(now.ToString("o", CultureInfo.InvariantCulture));
        }

        File.Move(temp, target, true);
        TweakEngine.LogChange("tool." + tool.Id, "configuration", "saved (" + files.Count + " files)");

        return new ToolBackupInfo(now, files.Count);
    });

    private static List<(string Full, string Relative)> CollectFiles(ExternalTool tool, string root)
    {
        var files = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ToolBackupItem item in tool.BackupItems(root))
        {
            string folder = item.Folder.Length == 0 ? root : Path.Combine(root, item.Folder);
            if (!Directory.Exists(folder))
            {
                continue;
            }

            SearchOption option = item.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            foreach (string file in Directory.GetFiles(folder, item.Pattern, option))
            {
                if (IsConfigFile(file) && seen.Add(file))
                {
                    files.Add((file, Path.GetRelativePath(root, file)));
                }
            }
        }

        return files;
    }

    /// <summary>
    /// Puts the saved files back in place. If the tool is open it is closed first and reopened afterwards,
    /// otherwise on closing it would rewrite its configuration over the restored one.
    /// </summary>
    public static async Task<int> RestoreAsync(ToolStatus status)
    {
        ExternalTool tool = status.Tool;
        string exe = Require(status);
        string root = Path.GetDirectoryName(exe) ?? "";

        bool wasRunning = IsRunning(tool);
        if (wasRunning)
        {
            await CloseAsync(status);
        }

        int count = await Task.Run(() => Extract(BackupPath(tool), root));
        TweakEngine.LogChange("tool." + tool.Id, "configuration", "restored (" + count + " files)");

        if (wasRunning)
        {
            Start(exe, tool.AutostartArguments);
        }

        return count;
    }

    private static int Extract(string zipPath, string root)
    {
        string prefix = Path.GetFullPath(root).TrimEnd('\\') + "\\";
        int count = 0;

        using var zip = new ZipArchive(File.OpenRead(zipPath), ZipArchiveMode.Read);
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            if (entry.Name.Length == 0 || entry.FullName == ManifestName)
            {
                continue;
            }

            // An archive must not be able to write outside the program folder.
            string target = Path.GetFullPath(Path.Combine(prefix, entry.FullName.Replace('/', '\\')));
            if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target) ?? prefix);

            using Stream input = entry.Open();
            using var output = new FileStream(target, FileMode.Create, FileAccess.Write);
            input.CopyTo(output);
            count++;
        }

        return count;
    }

    // ---------- Export and import with the profile ----------

    /// <summary>Adds the saved configurations to the profile being exported. Returns the names of the tools included.</summary>
    public static List<string> AttachBackups(Profile profile)
    {
        var names = new List<string>();
        profile.ToolBackups.Clear();

        foreach (ExternalTool tool in Tools)
        {
            string path = BackupPath(tool);
            if (File.Exists(path))
            {
                profile.ToolBackups[tool.Id] = Convert.ToBase64String(File.ReadAllBytes(path));
                names.Add(tool.Name);
            }
        }

        return names;
    }

    /// <summary>
    /// Copies into the data folder the configurations contained in an imported profile, then removes them
    /// from the profile: this PC's profile stays light. Returns the names of the tools imported.
    /// </summary>
    public static List<string> ImportBackups(Profile profile)
    {
        var names = new List<string>();

        foreach (ExternalTool tool in Tools)
        {
            if (!profile.ToolBackups.TryGetValue(tool.Id, out string? encoded))
            {
                continue;
            }

            try
            {
                byte[] bytes = Convert.FromBase64String(encoded);

                // Only a readable archive is accepted.
                using (var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read))
                {
                    if (zip.Entries.Count == 0)
                    {
                        continue;
                    }
                }

                Directory.CreateDirectory(BackupFolder);
                File.WriteAllBytes(BackupPath(tool), bytes);
                names.Add(tool.Name);
            }
            catch
            {
                // A damaged configuration does not block importing the rest.
            }
        }

        profile.ToolBackups.Clear();
        return names;
    }

    private static string Require(ToolStatus status) =>
        status.ExePath ?? throw new InvalidOperationException(status.Tool.Name + " is not installed.");
}
