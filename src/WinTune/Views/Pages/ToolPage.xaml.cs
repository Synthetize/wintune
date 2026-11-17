using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;
using WinTune.Core;
using WinTune.ViewModels;

namespace WinTune.Views.Pages;

public partial class ToolPage : Page
{
    private bool _busy;

    public ToolPage()
    {
        InitializeComponent();

        BesideBox.IsChecked = ExternalToolService.InstallBesideWinTune;
        UpdateFolderText();
    }

    // ---------- Where to install ----------

    private void OnBesideClick(object sender, RoutedEventArgs e)
    {
        try
        {
            ExternalToolService.InstallBesideWinTune = BesideBox.IsChecked == true;
        }
        catch (Exception ex)
        {
            ShowMessage("Setting not saved", ex.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
        }

        UpdateFolderText();
    }

    private void UpdateFolderText()
    {
        FolderText.Text = BesideBox.IsChecked == true
            ? "Folder: " + ExternalToolService.ToolsFolder + ". Applies to future installations: programs already installed stay where they are."
            : "Future installations will use the folder chosen by the program installer.";
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    // ---------- State ----------

    private async Task RefreshAsync()
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true, "Reading state...");
        try
        {
            await LoadStatusAsync();
        }
        catch (Exception ex)
        {
            ShowMessage("Reading state failed", ex.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false, "");
        }
    }

    private async Task LoadStatusAsync()
    {
        List<ToolStatus> list = await ExternalToolService.GetStatusAsync();

        // First the installable programs, then the tools that start as scripts.
        var items = new List<object>();
        items.AddRange(list.Select(s => new ToolViewModel(s)));
        items.AddRange(ExternalToolService.Scripts.Select(s => new ScriptToolViewModel(s)));
        ToolsList.ItemsSource = items;
    }

    /// <summary>Runs an operation showing the wait, then reads the state of the tools again.</summary>
    private async Task RunAsync(string busyText, Func<Task> action)
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true, busyText);
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            ShowMessage("Operation failed", ex.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
        }

        try
        {
            await LoadStatusAsync();
        }
        catch
        {
            // The state stays as before: it can be read again with "Refresh state".
        }
        finally
        {
            SetBusy(false, "");
        }
    }

    private void SetBusy(bool busy, string text)
    {
        _busy = busy;
        BusyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BusyText.Text = text;
        RefreshButton.IsEnabled = !busy;
        ToolsList.IsEnabled = !busy;
    }

    private static ToolViewModel? ToolOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as ToolViewModel;

    // ---------- Installation ----------

    private async void OnInstallClick(object sender, RoutedEventArgs e)
    {
        if (ToolOf(sender) is not { } tool)
        {
            return;
        }

        await RunAsync("Installing " + tool.Name + "...", async () =>
        {
            string? version = await WingetService.GetVersionAsync();
            if (version is null || WingetService.IsTooOld(version))
            {
                (bool ready, string reason) = await WingetService.InstallOrRepairAsync();
                if (!ready)
                {
                    ShowMessage("winget not available", reason, Wpf.Ui.Controls.InfoBarSeverity.Error);
                    return;
                }
            }

            ExternalTool definition = tool.Status.Tool;
            string? location = ExternalToolService.InstallLocationFor(definition);
            if (location != null)
            {
                Directory.CreateDirectory(ExternalToolService.ToolsFolder);
            }

            (bool ok, string message) = await WingetService.InstallAsync(definition.WingetId, location);

            TweakEngine.LogChange("tool." + definition.Id, "install", ok ? "succeeded" : "failed: " + message);

            if (!ok)
            {
                ShowMessage("Installing " + tool.Name + " failed", message, Wpf.Ui.Controls.InfoBarSeverity.Error);
                return;
            }

            // Not all installers accept the requested folder: check where it really ended up.
            string? exe = await Task.Run(() => ExternalToolService.FindExe(definition));

            if (exe is null)
            {
                ShowMessage(
                    tool.Name + " installed, but I cannot find it",
                    "Use \"Already installed: choose where\" to point to the program.",
                    Wpf.Ui.Controls.InfoBarSeverity.Warning);
            }
            else if (location != null && !ExternalToolService.IsInside(exe, location))
            {
                ShowMessage(
                    tool.Name + " installed in a different folder",
                    "The installer did not use the WinTune folder: the program is in " + Path.GetDirectoryName(exe) + ".",
                    Wpf.Ui.Controls.InfoBarSeverity.Warning);
            }
            else
            {
                ShowMessage(tool.Name + " installed", Path.GetDirectoryName(exe) ?? "", Wpf.Ui.Controls.InfoBarSeverity.Success);
            }
        });
    }

    private async void OnLocateClick(object sender, RoutedEventArgs e)
    {
        if (ToolOf(sender) is not { } tool)
        {
            return;
        }

        string exeName = tool.Status.Tool.ExeName;
        var dialog = new OpenFileDialog
        {
            Title = "Where is " + exeName + "?",
            Filter = exeName + "|" + exeName,
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (!string.Equals(Path.GetFileName(dialog.FileName), exeName, StringComparison.OrdinalIgnoreCase))
        {
            ShowMessage("Invalid file", "The file is required: " + exeName + ".", Wpf.Ui.Controls.InfoBarSeverity.Error);
            return;
        }

        await RunAsync("Reading state...", () =>
        {
            ExternalToolService.SetManualPath(tool.Status.Tool, dialog.FileName);
            return Task.CompletedTask;
        });
    }

    // ---------- Start and close ----------

    private async void OnOpenClick(object sender, RoutedEventArgs e)
    {
        if (ToolOf(sender) is not { } tool)
        {
            return;
        }

        await RunAsync("Starting " + tool.Name + "...", async () =>
        {
            ExternalToolService.Open(tool.Status);

            // A moment of waiting, so the state read again shows the program running.
            await Task.Delay(1500);
        });
    }

    private async void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if (ToolOf(sender) is not { } tool)
        {
            return;
        }

        await RunAsync("Closing " + tool.Name + "...", () => ExternalToolService.CloseAsync(tool.Status));
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        if (ToolOf(sender)?.Status.InstallFolder is not { Length: > 0 } folder)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            ShowMessage("Could not open the folder", ex.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
    }

    private void OnRunScriptClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ScriptToolViewModel script)
        {
            return;
        }

        try
        {
            ExternalToolService.RunScript(script.Tool);
            ShowMessage(
                script.Name + " starting",
                "A PowerShell window opened: the program appears after a few seconds.",
                Wpf.Ui.Controls.InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            ShowMessage("Start failed", ex.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
    }

    // ---------- Start with Windows ----------

    private async void OnAutostartClick(object sender, RoutedEventArgs e)
    {
        if (ToolOf(sender) is not { } tool)
        {
            return;
        }

        bool enabled = (sender as ToggleButton)?.IsChecked == true;

        await RunAsync(enabled ? "Turning on start with Windows..." : "Turning off start with Windows...", async () =>
        {
            await ExternalToolService.SetAutostartAsync(tool.Status, enabled);

            TweakEngine.LogChange(
                "tool." + tool.Status.Tool.Id + ".avvio",
                enabled ? "disabled" : "enabled",
                enabled ? "enabled" : "disabled");
        });
    }

    // ---------- Configuration ----------

    private async void OnBackupClick(object sender, RoutedEventArgs e)
    {
        if (ToolOf(sender) is not { } tool)
        {
            return;
        }

        if (tool.Status.Backup != null)
        {
            bool replace = await Dialogs.ConfirmAsync(
                "Replace the saved configuration?",
                "The one saved on " + tool.Status.Backup.Date.ToString("dd/MM/yyyy") + " at " + tool.Status.Backup.Date.ToString("HH:mm")
                    + " will be replaced with the current configuration of " + tool.Name + ".",
                "Replace");

            if (!replace)
            {
                return;
            }
        }

        await RunAsync("Saving the configuration...", async () =>
        {
            ToolBackupInfo info = await ExternalToolService.BackupAsync(tool.Status);
            ShowMessage(
                "Configuration of " + tool.Name + " saved",
                info.FileCount + " files. It is also included when you export the profile.",
                Wpf.Ui.Controls.InfoBarSeverity.Success);
        });
    }

    private async void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        if (ToolOf(sender) is not { } tool || tool.Status.Backup is null)
        {
            return;
        }

        bool confirmed = await Dialogs.ConfirmAsync(
            "Restore the configuration of " + tool.Name + "?",
            "The current files will be replaced with those saved on " + tool.Status.Backup.Date.ToString("dd/MM/yyyy")
                + " at " + tool.Status.Backup.Date.ToString("HH:mm") + "."
                + (tool.Status.IsRunning ? "\n\n" + tool.Name + " will be closed and reopened." : ""),
            "Restore");

        if (!confirmed)
        {
            return;
        }

        await RunAsync("Restoring the configuration...", async () =>
        {
            int count = await ExternalToolService.RestoreAsync(tool.Status);
            ShowMessage(
                "Configuration of " + tool.Name + " restored",
                count + " files put back in place.",
                Wpf.Ui.Controls.InfoBarSeverity.Success);
        });
    }

    // ---------- Profiles that can be activated on the fly ----------

    private async void OnProfileClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ToolProfileViewModel profile)
        {
            return;
        }

        ToolViewModel tool = profile.Owner;

        await RunAsync("Activating profile \"" + profile.Name + "\"...", async () =>
        {
            ExternalToolService.ActivateProfile(tool.Status, profile.FilePath);
            TweakEngine.LogChange("tool." + tool.Status.Tool.Id + ".profilo", null, profile.Name);

            ShowMessage(
                "Profile \"" + profile.Name + "\" sent to " + tool.Name,
                "",
                Wpf.Ui.Controls.InfoBarSeverity.Success);

            await Task.Delay(1500);
        });
    }

    // ---------- Common ----------

    private void ShowMessage(string title, string message, Wpf.Ui.Controls.InfoBarSeverity severity)
    {
        MessageBar.Title = title;
        MessageBar.Message = message;
        MessageBar.Severity = severity;
        MessageBar.IsOpen = true;
    }
}
