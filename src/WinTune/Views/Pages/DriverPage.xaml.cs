using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using WinTune.Core;
using WinTune.ViewModels;

namespace WinTune.Views.Pages;

public partial class DriverPage : Page
{
    private readonly ObservableCollection<DriverRowViewModel> _rows = new();
    private readonly ObservableCollection<DriverOfferRowViewModel> _offers = new();
    private bool _hardwareLoaded;
    private bool _busy;

    public DriverPage()
    {
        InitializeComponent();
        HardwareList.ItemsSource = _rows;
        OfferList.ItemsSource = _offers;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // If the expiry has passed and the scheduled task did not run, the drivers are turned back on here.
            DriverUpdateService.EnforceExpiry();
        }
        catch
        {
            // The state shown below will tell the real situation anyway.
        }

        RefreshAutoState();

        if (!_hardwareLoaded)
        {
            _hardwareLoaded = true;
            LoadHardware();
        }
    }

    // ---------- Automatic drivers ----------

    private void RefreshAutoState()
    {
        bool blocked;
        DateTime? until;

        try
        {
            blocked = DriverUpdateService.IsBlocked;
            until = DriverUpdateService.BlockedUntil;
        }
        catch (Exception ex)
        {
            AutoStateText.Text = "State could not be read: " + ex.Message;
            return;
        }

        if (!blocked)
        {
            AutoStateText.Text = "Enabled: Windows Update installs and updates drivers.";
        }
        else if (until.HasValue)
        {
            int days = Math.Max(0, (int)Math.Ceiling((until.Value - DateTime.Now).TotalDays));
            AutoStateText.Text = "Disabled until " + until.Value.ToString("dd/MM/yyyy")
                + (days == 1 ? " (1 day left)." : " (" + days + " days left).");
        }
        else
        {
            AutoStateText.Text = "Disabled with no expiry.";
        }

        BlockButton.Content = blocked ? "Change duration" : "Turn off automatic drivers";
        UnblockButton.IsEnabled = blocked;
    }

    private void OnDurationChanged(object sender, SelectionChangedEventArgs e)
    {
        // The event also arrives while the page is being created, when the date picker does not exist yet.
        if (UntilPicker is null)
        {
            return;
        }

        UntilPicker.Visibility = DurationHelper.IsCustomDate(DurationBox) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnBlockClick(object sender, RoutedEventArgs e)
    {
        if (!DurationHelper.TryResolve(DurationBox, UntilPicker, out DateTime? until, out string error))
        {
            ShowMessage("Invalid duration", error, Wpf.Ui.Controls.InfoBarSeverity.Warning);
            return;
        }

        try
        {
            DriverUpdateService.Block(until);
            ShowMessage(
                "Automatic drivers turned off",
                until.HasValue ? "They turn back on by themselves on " + until.Value.ToString("dd/MM/yyyy") + "." : "",
                Wpf.Ui.Controls.InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowMessage("Change failed", ex.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
        }

        RefreshAutoState();
    }

    private void OnUnblockClick(object sender, RoutedEventArgs e)
    {
        try
        {
            DriverUpdateService.Unblock();
            ShowMessage("Automatic drivers turned back on", "", Wpf.Ui.Controls.InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowMessage("Change failed", ex.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
        }

        RefreshAutoState();
    }

    // ---------- Sections ----------

    private void OnSectionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked)
        {
            return;
        }

        // Only one section at a time.
        foreach (ToggleButton button in SectionPanel.Children.OfType<ToggleButton>())
        {
            button.IsChecked = ReferenceEquals(button, clicked);
        }

        string section = (string)clicked.Tag;
        UpdatesPanel.Visibility = section == "Updates" ? Visibility.Visible : Visibility.Collapsed;
        HardwarePanel.Visibility = section == "Hardware" ? Visibility.Visible : Visibility.Collapsed;
        AutoPanel.Visibility = section == "Auto" ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- Driver updates (Windows Update) ----------

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        ScanRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ScanButton.IsEnabled = !busy;
        InstallDriversButton.IsEnabled = !busy && _offers.Count > 0;
        ScanStatusText.Text = status;
    }

    private async void OnScanClick(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true, "Checking Windows Update. This can take a minute or two...");
        _offers.Clear();

        (List<DriverOffer> offers, string? error) = await DriverScanService.ScanAsync();

        foreach (DriverOffer offer in offers)
        {
            _offers.Add(new DriverOfferRowViewModel(offer));
        }

        if (error is not null)
        {
            SetBusy(false, "Could not check: " + error);
        }
        else if (offers.Count == 0)
        {
            SetBusy(false, "No newer drivers found: the installed ones are up to date for Windows Update.");
        }
        else
        {
            SetBusy(false, offers.Count + (offers.Count == 1 ? " driver available." : " drivers available."));
        }
    }

    private async void OnInstallDriversClick(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        List<DriverOfferRowViewModel> targets = _offers.Where(o => o.IsSelected).ToList();
        if (targets.Count == 0)
        {
            ShowMessage("No driver selected", "Tick the drivers to install.", Wpf.Ui.Controls.InfoBarSeverity.Informational);
            return;
        }

        string list = string.Join("\n", targets.Take(15).Select(t => "•  " + t.Title));
        if (targets.Count > 15)
        {
            list += "\n... and " + (targets.Count - 15);
        }

        bool confirmed = await Dialogs.ConfirmAsync(
            "Install " + targets.Count + " drivers?",
            list + "\n\nThe screen may flicker and some drivers ask for a restart. A restore point is recommended.",
            "Install");

        if (!confirmed)
        {
            return;
        }

        foreach (DriverOfferRowViewModel row in targets)
        {
            row.Status = "Queued";
        }

        SetBusy(true, "Downloading and installing " + targets.Count + " drivers. Do not turn off the PC...");

        (List<DriverInstallResult> results, string? error) = await DriverScanService.InstallAsync(targets.Select(t => t.Id));

        bool restart = false;
        int ok = 0;
        foreach (DriverOfferRowViewModel row in targets)
        {
            DriverInstallResult? result = results.FirstOrDefault(r => string.Equals(r.Id, row.Id, StringComparison.OrdinalIgnoreCase));
            if (result is null)
            {
                row.Status = error ?? "Not installed";
                TweakEngine.LogChange("driver." + row.Id, "install", "failed: " + (error ?? "not found"));
                continue;
            }

            row.Status = result.Message + (result.RebootRequired ? " (restart needed)" : "");
            restart |= result.RebootRequired;
            if (result.Ok)
            {
                ok++;
                row.IsSelected = false;
            }

            TweakEngine.LogChange("driver." + row.Id, "install", result.Ok ? "succeeded" : "failed: " + result.Message);
        }

        SetBusy(false, ok + " of " + targets.Count + " installed." + (restart ? " Restart the PC to finish." : ""));

        if (error is not null)
        {
            ShowMessage("Installation failed", error, Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
        else if (ok == targets.Count)
        {
            ShowMessage(
                "Drivers installed",
                restart ? "Restart the PC to finish the installation." : "Nothing else to do.",
                Wpf.Ui.Controls.InfoBarSeverity.Success);
        }
        else
        {
            ShowMessage("Some drivers were not installed", "The reason is next to each driver.", Wpf.Ui.Controls.InfoBarSeverity.Warning);
        }
    }

    // ---------- Hardware and links ----------

    private void OnDetectClick(object sender, RoutedEventArgs e)
    {
        LoadHardware();
        MessageBar.IsOpen = false;
    }

    private void LoadHardware()
    {
        _rows.Clear();

        try
        {
            Profile? profile = TryLoadProfile();

            foreach (HardwareComponent component in HardwareService.Detect())
            {
                string? custom = null;
                profile?.DriverLinks.TryGetValue(component.Key, out custom);
                _rows.Add(new DriverRowViewModel(component, custom));
            }
        }
        catch (Exception ex)
        {
            ShowMessage("Detection failed", ex.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
    }

    private void OnOpenLinkClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is DriverRowViewModel row)
        {
            try
            {
                UrlUtil.Open(row.Link);
            }
            catch (Exception ex)
            {
                ShowMessage("Could not open the link", ex.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
            }
        }
    }

    private async void OnEditLinkClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not DriverRowViewModel row)
        {
            return;
        }

        string? text = await Dialogs.PromptAsync(
            "Driver link",
            row.Type + ": " + row.Model + "\n\nPaste the address of the driver page. Leave it empty to go back to the default link.",
            row.Link);

        if (text is null)
        {
            return;
        }

        if (text.Length > 0 && !UrlUtil.IsWebLink(text))
        {
            ShowMessage("Invalid address", "The link must start with http:// or https://.", Wpf.Ui.Controls.InfoBarSeverity.Warning);
            return;
        }

        // Putting the default link back is the same as having no custom link.
        bool isDefault = text.Length == 0 || string.Equals(text, row.Component.DefaultLink, StringComparison.OrdinalIgnoreCase);

        try
        {
            Profile profile = TryLoadProfile() ?? new Profile();
            if (isDefault)
            {
                profile.DriverLinks.Remove(row.Component.Key);
            }
            else
            {
                profile.DriverLinks[row.Component.Key] = text;
            }

            ProfileService.Save(profile);
            row.SetCustomLink(isDefault ? null : text);
            MessageBar.IsOpen = false;
        }
        catch (Exception ex)
        {
            ShowMessage("Saving failed", ex.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
    }

    // ---------- Common ----------

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

    private void ShowMessage(string title, string message, Wpf.Ui.Controls.InfoBarSeverity severity)
    {
        MessageBar.Title = title;
        MessageBar.Message = message;
        MessageBar.Severity = severity;
        MessageBar.IsOpen = true;
    }
}
