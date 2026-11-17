using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using WinTune.Core;

namespace WinTune.ViewModels;

/// <summary>A profile that can be activated on the fly (for example a fan configuration).</summary>
public sealed class ToolProfileViewModel
{
    public ToolProfileViewModel(ToolViewModel owner, string filePath)
    {
        Owner = owner;
        FilePath = filePath;
        Name = Path.GetFileNameWithoutExtension(filePath);
    }

    public ToolViewModel Owner { get; }

    public string FilePath { get; }

    public string Name { get; }
}

/// <summary>
/// Tile of a tool that starts as a script, without installation. It has the same properties
/// of <see cref="ToolViewModel"/> used by the tile, so the page shows them the same way.
/// </summary>
public sealed class ScriptToolViewModel
{
    public ScriptToolViewModel(ScriptTool tool)
    {
        Tool = tool;
        StatusBrush = Application.Current?.TryFindResource("TextFillColorSecondaryBrush") as Brush ?? Brushes.Gray;
    }

    public ScriptTool Tool { get; }

    public string Name => Tool.Name;

    public string Description => Tool.Description;

    public Brush StatusBrush { get; }

    public string StatusText => "Nothing to install";

    public string FolderText => Tool.Source;

    public string ScriptNote =>
        "Opens a PowerShell window as administrator and runs the official script downloaded from "
        + Tool.Source + ". An Internet connection is required; the program appears after a few seconds.";

    public Visibility InstalledVisibility => Visibility.Collapsed;

    public Visibility MissingVisibility => Visibility.Collapsed;

    public Visibility ScriptVisibility => Visibility.Visible;

    public Visibility ExtrasVisibility => Visibility.Collapsed;
}

/// <summary>Tile of an external tool on the Tool page.</summary>
public sealed class ToolViewModel
{
    public ToolViewModel(ToolStatus status)
    {
        Status = status;
        Profiles = status.Profiles.Select(p => new ToolProfileViewModel(this, p)).ToList();

        string brushKey = !status.IsInstalled
            ? "TextFillColorSecondaryBrush"
            : status.IsRunning ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush";

        StatusBrush = Application.Current?.TryFindResource(brushKey) as Brush ?? Brushes.Gray;
    }

    public ToolStatus Status { get; }

    public string Name => Status.Tool.Name;

    public string Description => Status.Tool.Description;

    public Brush StatusBrush { get; }

    public string StatusText
    {
        get
        {
            if (!Status.IsInstalled)
            {
                return "Not installed";
            }

            string version = Status.Version.Length > 0 ? "Version " + Status.Version : "Installed";
            return version + "  ·  " + (Status.IsRunning ? "running" : "not running");
        }
    }

    /// <summary>Program folder, shown on mouse hover.</summary>
    public string FolderText => Status.InstallFolder ?? "";

    public Visibility InstalledVisibility => Status.IsInstalled ? Visibility.Visible : Visibility.Collapsed;

    public Visibility MissingVisibility => Status.IsInstalled ? Visibility.Collapsed : Visibility.Visible;

    public Visibility ScriptVisibility => Visibility.Collapsed;

    public Visibility CloseVisibility => Status.IsInstalled && Status.IsRunning ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ExtrasVisibility => Status.Tool.HasExtras ? Visibility.Visible : Visibility.Collapsed;

    public bool IsAutostart => Status.Autostart;

    public string AutostartText
    {
        get
        {
            if (Status.Tasks.Count == 0)
            {
                return "Not set.";
            }

            ToolTask task = Status.Tasks.FirstOrDefault(t => t.Enabled) ?? Status.Tasks[0];
            return (task.Enabled ? "Enabled" : "Disabled") + ": scheduled task \"" + task.Name + "\".";
        }
    }

    public string BackupText => Status.Backup is null
        ? "No configuration saved."
        : "Saved on " + Status.Backup.Date.ToString("dd/MM/yyyy") + " at " + Status.Backup.Date.ToString("HH:mm")
            + " (" + Status.Backup.FileCount + " files).";

    public bool CanRestore => Status.Backup != null;

    public List<ToolProfileViewModel> Profiles { get; }

    public string ProfileLabel => Status.Tool.ProfileLabel + " (click to activate)";

    public Visibility ProfilesVisibility => Profiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
}
