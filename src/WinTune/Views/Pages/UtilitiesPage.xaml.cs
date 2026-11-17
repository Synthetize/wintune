using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using WinTune.Core;
using WinTune.ViewModels;

namespace WinTune.Views.Pages;

public partial class UtilitiesPage : Page
{
    private const string ProfileFilter = "WinTune profile (*.json)|*.json|All files (*.*)|*.*";

    private readonly DispatcherTimer _countdownTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly ObservableCollection<CleanupRowViewModel> _cleanup = new();
    private bool _measuredOnce;

    public UtilitiesPage()
    {
        InitializeComponent();
        _countdownTimer.Tick += (_, _) => UpdateCountdown();

        foreach (CleanupTarget target in CleanupService.Targets)
        {
            // The recycle bin starts unticked: its content cannot be recovered.
            _cleanup.Add(new CleanupRowViewModel(target, target.Id != CleanupService.RecycleBin));
        }

        CleanupList.ItemsSource = _cleanup;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _countdownTimer.Start();
        UpdateCountdown();
        UpdateProfileStatus();

        if (!_measuredOnce)
        {
            // Read only the first time: the restore point status is updated again after creating one.
            _measuredOnce = true;
            await UpdateRestoreStatusAsync();
            await MeasureCleanupAsync();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => _countdownTimer.Stop();

    // ---------- Timed shutdown ----------

    private void OnPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.Tag is string minutes)
        {
            MinutesBox.Text = minutes;
        }
    }

    private void OnStartShutdownClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(MinutesBox.Text.Trim(), out int minutes) || minutes < 1 || minutes > 7 * 24 * 60)
        {
            ShowMessage("Invalid time", "Type a number of minutes between 1 and 10080 (7 days).", Wpf.Ui.Controls.InfoBarSeverity.Warning);
            return;
        }

        var action = (ShutdownAction)Math.Max(0, ActionBox.SelectedIndex);

        try
        {
            ShutdownService.Schedule(action, minutes);
            MessageBar.IsOpen = false;
        }
        catch (Exception ex)
        {
            ShowMessage("Scheduling failed", ex.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
        }

        UpdateCountdown();
    }

    private void OnCancelShutdownClick(object sender, RoutedEventArgs e)
    {
        ShutdownService.Cancel();
        UpdateCountdown();
        ShowMessage("Shutdown cancelled", "", Wpf.Ui.Controls.InfoBarSeverity.Success);
    }

    private void UpdateCountdown()
    {
        DateTime? target = ShutdownService.Target;
        ShutdownAction? action = ShutdownService.PendingAction;

        if (target is null || action is null)
        {
            CountdownText.Text = "No shutdown scheduled.";
            return;
        }

        TimeSpan remaining = target.Value - DateTime.Now;
        if (remaining <= TimeSpan.Zero)
        {
            ShutdownService.Clear();
            CountdownText.Text = "No shutdown scheduled.";
            return;
        }

        string label = action.Value switch
        {
            ShutdownAction.Restart => "Restart",
            ShutdownAction.Sleep => "Sleep",
            _ => "Shutdown",
        };

        string time = remaining.TotalHours >= 1
            ? $"{(int)remaining.TotalHours}:{remaining.Minutes:00}:{remaining.Seconds:00}"
            : $"{remaining.Minutes:00}:{remaining.Seconds:00}";

        CountdownText.Text = $"{label} in {time} (at {target.Value:HH:mm})";
    }

    // ---------- Restore point ----------

    private async void OnCreateRestorePointClick(object sender, RoutedEventArgs e)
    {
        CreateRestoreButton.IsEnabled = false;
        RestoreRing.Visibility = Visibility.Visible;
        RestoreStatusText.Text = "Creating the restore point...";

        try
        {
            (bool ok, string message) = await RestorePointService.CreateAsync("WinTune " + DateTime.Now.ToString("dd/MM/yyyy HH:mm"));
            ShowMessage(
                ok ? "Restore point created" : "Restore point not created",
                ok ? "" : message,
                ok ? Wpf.Ui.Controls.InfoBarSeverity.Success : Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
        catch (Exception ex)
        {
            ShowMessage("Restore point not created", ex.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
        finally
        {
            RestoreRing.Visibility = Visibility.Collapsed;
            CreateRestoreButton.IsEnabled = true;
        }

        await UpdateRestoreStatusAsync();
    }

    private void OnOpenRestoreWizardClick(object sender, RoutedEventArgs e)
    {
        try
        {
            RestorePointService.OpenRestoreWizard();
        }
        catch (Exception ex)
        {
            ShowMessage("Could not open System Restore", ex.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
    }

    private async Task UpdateRestoreStatusAsync()
    {
        string? last = await RestorePointService.GetLastAsync();
        RestoreStatusText.Text = last is null
            ? "Last point: none found."
            : "Last point: " + last;
    }

    // ---------- Disk cleanup ----------

    private async void OnMeasureClick(object sender, RoutedEventArgs e) => await MeasureCleanupAsync();

    private async Task MeasureCleanupAsync()
    {
        SetCleanupBusy(true, "Calculating space...");
        long total = 0;

        try
        {
            foreach (CleanupRowViewModel row in _cleanup)
            {
                long size = await CleanupService.MeasureAsync(row.Target.Id);
                row.SizeText = CleanupService.FormatSize(size);
                total += size;
            }

            SetCleanupBusy(false, "Total: " + CleanupService.FormatSize(total));
        }
        catch (Exception ex)
        {
            SetCleanupBusy(false, "Calculation failed: " + ex.Message);
        }
    }

    private async void OnCleanClick(object sender, RoutedEventArgs e)
    {
        List<CleanupRowViewModel> targets = _cleanup.Where(c => c.IsSelected).ToList();
        if (targets.Count == 0)
        {
            ShowMessage("Nothing to clean", "Tick at least one item.", Wpf.Ui.Controls.InfoBarSeverity.Informational);
            return;
        }

        bool confirmed = await Dialogs.ConfirmAsync(
            "Delete these files?",
            string.Join("\n", targets.Select(t => "•  " + t.Name)) + "\n\nDeleted files cannot be recovered.",
            "Clean");

        if (!confirmed)
        {
            return;
        }

        long freed = 0;
        SetCleanupBusy(true, "Cleaning...");

        try
        {
            foreach (CleanupRowViewModel row in targets)
            {
                freed += await CleanupService.CleanAsync(row.Target.Id);
            }

            TweakEngine.LogChange("cleanup.disk", string.Join(", ", targets.Select(t => t.Target.Id)), CleanupService.FormatSize(freed) + " freed");
            ShowMessage("Cleanup completed", CleanupService.FormatSize(freed) + " freed.", Wpf.Ui.Controls.InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowMessage("Cleanup failed", ex.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
        }

        await MeasureCleanupAsync();
    }

    private void SetCleanupBusy(bool busy, string status)
    {
        CleanupRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CleanButton.IsEnabled = !busy;
        MeasureButton.IsEnabled = !busy;
        CleanupStatusText.Text = status;
    }

    // ---------- Profile ----------

    private async void OnSaveProfileClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Profile? existing = TryLoadProfile();
            if (existing != null)
            {
                bool confirmed = await Dialogs.ConfirmAsync(
                    "Replace the profile?",
                    "The saved profile will be replaced with the current state of all optimizations.",
                    "Replace");

                if (!confirmed)
                {
                    return;
                }
            }

            ProfileService.Save(ProfileService.Capture(existing));
            ShowMessage("Profile saved", "", Wpf.Ui.Controls.InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowMessage("Saving failed", ex.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
        }

        UpdateProfileStatus();
    }

    private void OnExportProfileClick(object sender, RoutedEventArgs e)
    {
        try
        {
            // Without a saved profile the current state is exported.
            Profile? saved = TryLoadProfile();
            Profile profile = saved is { Tweaks.Count: > 0 } ? saved : ProfileService.Capture(saved);

            var dialog = new SaveFileDialog
            {
                Title = "Export profile",
                FileName = "profile.json",
                Filter = ProfileFilter,
            };

            if (dialog.ShowDialog() == true)
            {
                // The saved tool configurations travel inside the exported file.
                List<string> tools = ExternalToolService.AttachBackups(profile);
                ProfileService.Save(profile, dialog.FileName);
                profile.ToolBackups.Clear();

                ShowMessage(
                    "Profile exported",
                    dialog.FileName + (tools.Count > 0 ? "\nIncludes the configurations of: " + string.Join(", ", tools) + "." : ""),
                    Wpf.Ui.Controls.InfoBarSeverity.Success);
            }
        }
        catch (Exception ex)
        {
            ShowMessage("Export failed", ex.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
    }

    private async void OnImportProfileClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new OpenFileDialog
            {
                Title = "Import profile",
                Filter = ProfileFilter,
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            Profile? profile = ProfileService.Load(dialog.FileName);
            if (profile is null)
            {
                return;
            }

            List<ProfileDifference> differences = ProfileService.Compare(profile);

            if (differences.Count > 0)
            {
                string list = string.Join("\n", differences.Take(12).Select(d =>
                    "•  " + d.Tweak.Title + ": " + d.Tweak.DisplayValue(d.Actual) + " → " + d.Tweak.DisplayValue(d.Expected)));

                if (differences.Count > 12)
                {
                    list += "\n... and " + (differences.Count - 12);
                }

                bool apply = await Dialogs.ConfirmAsync(
                    "Apply the profile \"" + profile.Name + "\"?",
                    differences.Count + " settings differ from this PC:\n\n" + list,
                    "Apply");

                if (!apply)
                {
                    return;
                }

                List<string> errors = ProfileService.Apply(profile);
                if (errors.Count > 0)
                {
                    ShowMessage("Profile imported with errors", string.Join("\n", errors), Wpf.Ui.Controls.InfoBarSeverity.Error);
                }
                else
                {
                    ShowMessage("Profile imported and applied", differences.Count + " changes.", Wpf.Ui.Controls.InfoBarSeverity.Success);
                }
            }
            else
            {
                ShowMessage("Profile imported", "This PC already matches the profile.", Wpf.Ui.Controls.InfoBarSeverity.Success);
            }

            // The tool configurations go into the data folder: they are put back from the Tool page.
            List<string> importedTools = ExternalToolService.ImportBackups(profile);

            // The imported profile becomes the reference one of this PC.
            ProfileService.Save(profile);

            if (importedTools.Count > 0)
            {
                MessageBar.Message = (MessageBar.Message + "\nConfigurations imported for: " + string.Join(", ", importedTools)
                    + ". To apply them, open the Tools page and press \"Restore\".").Trim();
            }
        }
        catch (Exception ex)
        {
            ShowMessage("Import failed", ex.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
        }

        UpdateProfileStatus();
    }

    private void OnOpenDataFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(ProfileService.DataFolder);
            Process.Start(new ProcessStartInfo(ProfileService.DataFolder) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            ShowMessage("Could not open the folder", ex.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
    }

    private static Profile? TryLoadProfile()
    {
        try
        {
            return ProfileService.Load();
        }
        catch
        {
            // An unreadable profile is treated as missing.
            return null;
        }
    }

    private void UpdateProfileStatus()
    {
        Profile? profile = TryLoadProfile();
        ProfileStatusText.Text = profile is null
            ? "No saved profile."
            : "Profile \"" + profile.Name + "\": " + profile.Tweaks.Count + " settings, " + profile.Apps.Count + " apps in the list. File: " + ProfileService.ProfilePath;
    }

    private void ShowMessage(string title, string message, Wpf.Ui.Controls.InfoBarSeverity severity)
    {
        MessageBar.Title = title;
        MessageBar.Message = message;
        MessageBar.Severity = severity;
        MessageBar.IsOpen = true;
    }
}
