using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using WinTune.Core;
using WinTune.ViewModels;

namespace WinTune.Views.Pages;

public partial class DriverPage : Page
{
    private readonly ObservableCollection<DriverRowViewModel> _rows = new();
    private bool _hardwareLoaded;

    public DriverPage()
    {
        InitializeComponent();
        HardwareList.ItemsSource = _rows;
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
