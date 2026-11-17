using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using WinTune.Core;
using WinTune.ViewModels;

namespace WinTune.Views.Pages;

public partial class OttimizzazioniPage : Page
{
    private readonly List<TweakViewModel> _items = new();
    private readonly List<TweakGroup> _groups = new();

    // Special filter: shows only the one-time actions.
    private const string ActionsFilter = "__azioni";

    public OttimizzazioniPage()
    {
        InitializeComponent();

        List<TweakGroup> groups = _groups;

        // Categories appear in the order set by the catalog; inside each category the order of the entries applies.
        foreach (IGrouping<string, Tweak> category in TweakCatalog.All
            .Where(t => t.IsAvailable && !t.Hidden)
            .GroupBy(t => t.Category)
            .OrderBy(g => CategoryIndex(g.Key)))
        {
            // First the switches, side by side; then the entries with a drop-down list, one per row.
            List<TweakViewModel> rows = category
                .Select(TweakViewModel.Create)
                .OrderBy(row => row is ChoiceTweakViewModel ? 1 : 0)
                .ToList();
            _items.AddRange(rows);
            groups.Add(new TweakGroup(category.Key, rows));
        }

        RefreshAll();
        GroupsList.ItemsSource = groups;

        AddFilter("All", null, true);
        foreach (TweakGroup group in groups)
        {
            AddFilter(group.Name, group.Name, false);
        }

        AddFilter("Actions", ActionsFilter, false);

        _onError = message => ShowMessage("Change failed", message, Wpf.Ui.Controls.InfoBarSeverity.Error);
    }

    // The events are static: the page hooks up only while visible, otherwise it would never be freed.
    private readonly Action<string> _onError;

    private void Subscribe()
    {
        TweakViewModel.ErrorOccurred -= _onError;
        TweakViewModel.ErrorOccurred += _onError;

        // Some entries touch the same settings as others: after every change everything is read again.
        TweakViewModel.Changed -= RefreshAll;
        TweakViewModel.Changed += RefreshAll;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        TweakViewModel.ErrorOccurred -= _onError;
        TweakViewModel.Changed -= RefreshAll;
    }

    private static int CategoryIndex(string category)
    {
        for (int i = 0; i < TweakCatalog.CategoryOrder.Count; i++)
        {
            if (TweakCatalog.CategoryOrder[i] == category)
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Subscribe();
        RefreshAll();
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        RefreshAll();
        MessageBar.IsOpen = false;
    }

    private async void OnRestartExplorerClick(object sender, RoutedEventArgs e)
    {
        bool confirmed = await Dialogs.ConfirmAsync(
            "Restart File Explorer?",
            "The taskbar and the folder windows close and reopen after a few seconds.",
            "Restart");

        if (!confirmed)
        {
            return;
        }

        try
        {
            await Task.Run(ExplorerUtil.Restart);
            ShowMessage("File Explorer restarted", "", Wpf.Ui.Controls.InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowMessage("Restart failed", ex.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
    }

    private async void OnApplyRecommendedClick(object sender, RoutedEventArgs e)
    {
        // Only the entries with a recommended value that differs from the current one.
        var pending = new List<(Tweak Tweak, string Value)>();
        foreach (TweakViewModel item in _items)
        {
            string? recommended = item.Tweak.Recommended;
            if (recommended is null)
            {
                continue;
            }

            string? current = TweakEngine.SafeRead(item.Tweak);
            if (!string.Equals(current, recommended, StringComparison.OrdinalIgnoreCase))
            {
                pending.Add((item.Tweak, recommended));
            }
        }

        if (pending.Count == 0)
        {
            ShowMessage("No change needed", "All recommended settings are already applied.", Wpf.Ui.Controls.InfoBarSeverity.Success);
            return;
        }

        string list = string.Join("\n", pending.Take(14).Select(p => "•  " + p.Tweak.Title + ": " + p.Tweak.DisplayValue(p.Value)));
        if (pending.Count > 14)
        {
            list += "\n... and " + (pending.Count - 14);
        }

        // Privacy and services are the most invasive changes: a restore point first is advisable.
        bool invasive = pending.Any(p => p.Tweak.Category == TweakCatalog.Privacy
            || p.Tweak.Category == TweakCatalog.ServiziPrudenti
            || p.Tweak.Category == TweakCatalog.ServiziAggressivi);

        string advice = invasive
            ? "\n\nThis includes privacy and service changes: it is recommended to create a restore point first from the Utilities section."
            : "";

        bool confirmed = await Dialogs.ConfirmAsync(
            "Apply the recommended settings?",
            "This will change " + pending.Count + " settings:\n\n" + list + advice,
            "Apply");

        if (!confirmed)
        {
            return;
        }

        var errors = new List<string>();
        foreach ((Tweak tweak, string value) in pending)
        {
            try
            {
                TweakEngine.Apply(tweak, value);
            }
            catch (Exception ex)
            {
                errors.Add(tweak.Title + ": " + ex.Message);
            }
        }

        RefreshAll();

        if (errors.Count == 0)
        {
            ShowMessage("Recommended settings applied", pending.Count + " changes.", Wpf.Ui.Controls.InfoBarSeverity.Success);
        }
        else
        {
            ShowMessage("Some changes failed", string.Join("\n", errors), Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
    }

    private void AddFilter(string label, string? category, bool isChecked)
    {
        var button = new ToggleButton
        {
            Content = label,
            Tag = category,
            IsChecked = isChecked,
            Margin = new Thickness(0, 0, 8, 8),
        };
        button.Click += OnFilterClick;
        FilterPanel.Children.Add(button);
    }

    private void OnFilterClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked)
        {
            return;
        }

        // Only one filter active at a time.
        foreach (ToggleButton button in FilterPanel.Children.OfType<ToggleButton>())
        {
            button.IsChecked = ReferenceEquals(button, clicked);
        }

        string? category = clicked.Tag as string;
        GroupsList.ItemsSource = category is null
            ? _groups
            : _groups.Where(g => g.Name == category).ToList();

        // Actions are seen with "All" and with their own filter.
        ActionsPanel.Visibility = category is null || category == ActionsFilter ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- One-time actions ----------

    private async void OnCreateRestorePointClick(object sender, RoutedEventArgs e)
    {
        SetActionsBusy(true, "Creating the restore point...");
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
            SetActionsBusy(false, "");
        }
    }

    private async void OnDeleteTempClick(object sender, RoutedEventArgs e)
    {
        bool confirmed = await Dialogs.ConfirmAsync(
            "Delete temporary files?",
            "The user temporary folder and the Windows one are emptied. Files in use are skipped.",
            "Delete");

        if (!confirmed)
        {
            return;
        }

        SetActionsBusy(true, "Deleting temporary files...");
        try
        {
            long freed = await CleanupService.CleanAsync(CleanupService.UserTemp);
            freed += await CleanupService.CleanAsync(CleanupService.WindowsTemp);

            TweakEngine.LogChange("pulizia.disco", "temporary files", CleanupService.FormatSize(freed) + " freed");
            ShowMessage("Temporary files deleted", CleanupService.FormatSize(freed) + " liberati.", Wpf.Ui.Controls.InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowMessage("Deletion failed", ex.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
        finally
        {
            SetActionsBusy(false, "");
        }
    }

    private async void OnWindowsCleanupClick(object sender, RoutedEventArgs e)
    {
        bool confirmed = await Dialogs.ConfirmAsync(
            "Start Windows Disk Cleanup?",
            "A window opens that runs Disk Cleanup and then the Windows component cleanup. It can take quite a few minutes.\n\nAfterwards, the Windows updates already installed can no longer be uninstalled.",
            "Start");

        if (!confirmed)
        {
            return;
        }

        try
        {
            string drive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";

            Process.Start(new ProcessStartInfo(
                ProcessRunner.SystemTool("cmd.exe"),
                "/k cleanmgr.exe /d " + drive + " /VERYLOWDISK & Dism.exe /Online /Cleanup-Image /StartComponentCleanup /ResetBase")
            {
                UseShellExecute = true,
            })?.Dispose();

            TweakEngine.LogChange("pulizia.windows", null, "started");
        }
        catch (Exception ex)
        {
            ShowMessage("Start failed", ex.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
    }

    private async void OnRemoveWidgetsClick(object sender, RoutedEventArgs e)
    {
        bool confirmed = await Dialogs.ConfirmAsync(
            "Uninstall the widgets?",
            "The Windows 11 widgets components are removed and the taskbar restarts. To get them back they must be reinstalled from the Microsoft Store.\n\nIf you only want to turn them off, use the \"Widget\" switch.",
            "Uninstall");

        if (!confirmed)
        {
            return;
        }

        try
        {
            ProcessRunner.StartVisiblePowerShell(
                "$ErrorActionPreference = 'Continue'\n" +
                "Write-Host 'Removing the widgets...'\n" +
                "Get-Process *Widget* -ErrorAction SilentlyContinue | Stop-Process -Force\n" +
                "Get-AppxPackage Microsoft.WidgetsPlatformRuntime -AllUsers | Remove-AppxPackage -AllUsers\n" +
                "Get-AppxPackage MicrosoftWindows.Client.WebExperience -AllUsers | Remove-AppxPackage -AllUsers\n" +
                "Stop-Process -Name explorer -Force\n" +
                "Write-Host 'Done.' -ForegroundColor Green\n" +
                "Read-Host 'Press Enter to close'");

            TweakEngine.LogChange("widget", null, "uninstalled");
        }
        catch (Exception ex)
        {
            ShowMessage("Start failed", ex.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
    }

    private async void OnRemoveOneDriveClick(object sender, RoutedEventArgs e)
    {
        // If Desktop, Documents or Pictures are inside OneDrive, removing it would make them unreachable.
        const string shellFolders = @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders";
        var redirected = new List<string>();
        foreach (string name in new[] { "Desktop", "Personal", "My Pictures" })
        {
            string? path = RegistryUtil.GetString(Microsoft.Win32.RegistryHive.CurrentUser, shellFolders, name);
            if (path is not null && path.Contains("OneDrive", StringComparison.OrdinalIgnoreCase))
            {
                redirected.Add(name == "Personal" ? "Documents" : name == "My Pictures" ? "Pictures" : name);
            }
        }

        if (redirected.Count > 0)
        {
            await Dialogs.InfoAsync(
                "OneDrive is still in use",
                "Your " + string.Join(", ", redirected) + " folder(s) are stored in OneDrive. "
                + "Move them back to a local folder first (right-click the folder, Properties, Location), then try again.");
            return;
        }

        bool confirmed = await Dialogs.ConfirmAsync(
            "Uninstall OneDrive?",
            "OneDrive is closed and uninstalled. The files in your OneDrive folder are not deleted, but they stop syncing and anything only stored online stays online.\n\nYou can reinstall it later from the Microsoft Store or with winget.",
            "Uninstall");

        if (!confirmed)
        {
            return;
        }

        try
        {
            ProcessRunner.StartVisiblePowerShell(
                "$ErrorActionPreference = 'Continue'\n" +
                "Write-Host 'Removing OneDrive...'\n" +
                "Stop-Process -Name OneDrive -Force -ErrorAction SilentlyContinue\n" +
                "Start-Sleep -Seconds 2\n" +
                "$setups = @(\"$env:SystemRoot\\SysWOW64\\OneDriveSetup.exe\", \"$env:SystemRoot\\System32\\OneDriveSetup.exe\", \"$env:LOCALAPPDATA\\Microsoft\\OneDrive\\OneDriveSetup.exe\")\n" +
                "foreach ($s in $setups) { if (Test-Path $s) { Write-Host ('Running ' + $s); Start-Process $s -ArgumentList '/uninstall' -Wait } }\n" +
                "winget uninstall -e --id Microsoft.OneDrive --silent --accept-source-agreements 2>$null\n" +
                "Remove-Item \"$env:LOCALAPPDATA\\Microsoft\\OneDrive\" -Recurse -Force -ErrorAction SilentlyContinue\n" +
                "Remove-Item \"$env:ProgramData\\Microsoft OneDrive\" -Recurse -Force -ErrorAction SilentlyContinue\n" +
                "Write-Host 'The files in your OneDrive folder were not touched.'\n" +
                "Write-Host 'Done.' -ForegroundColor Green\n" +
                "Read-Host 'Press Enter to close'");

            TweakEngine.LogChange("onedrive", null, "uninstalled");
        }
        catch (Exception ex)
        {
            ShowMessage("Start failed", ex.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
    }

    private void SetActionsBusy(bool busy, string status)
    {
        ActionsRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ActionsStatusText.Text = status;
        RestorePointButton.IsEnabled = !busy;
        DeleteTempButton.IsEnabled = !busy;
    }

    private void RefreshAll()
    {
        foreach (TweakViewModel item in _items)
        {
            item.Refresh();
        }
    }

    private void ShowMessage(string title, string message, Wpf.Ui.Controls.InfoBarSeverity severity)
    {
        MessageBar.Title = title;
        MessageBar.Message = message;
        MessageBar.Severity = severity;
        MessageBar.IsOpen = true;
    }
}
