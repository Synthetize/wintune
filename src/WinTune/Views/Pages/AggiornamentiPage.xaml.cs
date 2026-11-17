using System;
using System.Windows;
using System.Windows.Controls;
using WinTune.Core;

namespace WinTune.Views.Pages;

public partial class AggiornamentiPage : Page
{
    public AggiornamentiPage()
    {
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // If the expiry has passed and the scheduled task did not run, the updates are turned back on here.
            WindowsUpdateService.EnforceExpiry();
        }
        catch
        {
            // The state shown below will tell the real situation anyway.
        }

        RefreshState(selectCurrentMode: true);
    }

    private UpdateMode SelectedMode
    {
        get
        {
            if (ModeNotifica.IsChecked == true)
            {
                return UpdateMode.SoloNotifica;
            }

            if (ModePausa.IsChecked == true)
            {
                return UpdateMode.InPausa;
            }

            return ModeDisattivati.IsChecked == true ? UpdateMode.Disattivati : UpdateMode.Attivi;
        }
    }

    private void RefreshState(bool selectCurrentMode)
    {
        try
        {
            UpdateMode mode = WindowsUpdateService.CurrentMode;
            StateText.Text = "Current state: " + WindowsUpdateService.Describe() + ".";

            if (selectCurrentMode)
            {
                ModeAttivi.IsChecked = mode == UpdateMode.Attivi;
                ModeNotifica.IsChecked = mode == UpdateMode.SoloNotifica;
                ModePausa.IsChecked = mode == UpdateMode.InPausa;
                ModeDisattivati.IsChecked = mode == UpdateMode.Disattivati;
            }
        }
        catch (Exception ex)
        {
            StateText.Text = "State could not be read: " + ex.Message;
        }

        try
        {
            DateTime? until = DriverUpdateService.BlockedUntil;
            DriverStateText.Text = !DriverUpdateService.IsBlocked
                ? "Enabled: Windows Update also installs and updates drivers."
                : until.HasValue
                    ? "Disabled until " + until.Value.ToString("dd/MM/yyyy") + "."
                    : "Disabled with no expiry.";
        }
        catch
        {
            DriverStateText.Text = "";
        }

        UpdateDurationControls();
    }

    private void OnModeChecked(object sender, RoutedEventArgs e) => UpdateDurationControls();

    private void OnDurationChanged(object sender, SelectionChangedEventArgs e) => UpdateDurationControls();

    /// <summary>The duration is only used for "Paused" and "Disabled".</summary>
    private void UpdateDurationControls()
    {
        // Events also arrive while the page is being created, when not all the controls exist.
        if (DurationBox is null || UntilPicker is null || DurationLabel is null || ModeDisattivati is null)
        {
            return;
        }

        UpdateMode mode = SelectedMode;
        bool needsDuration = mode == UpdateMode.InPausa || mode == UpdateMode.Disattivati;

        DurationBox.IsEnabled = needsDuration;
        DurationLabel.Opacity = needsDuration ? 1.0 : 0.5;
        UntilPicker.Visibility = needsDuration && DurationHelper.IsCustomDate(DurationBox)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void OnApplyClick(object sender, RoutedEventArgs e)
    {
        UpdateMode mode = SelectedMode;
        DateTime? until = null;

        if (mode == UpdateMode.InPausa || mode == UpdateMode.Disattivati)
        {
            if (!DurationHelper.TryResolve(DurationBox, UntilPicker, out until, out string error))
            {
                ShowMessage("Invalid duration", error, Wpf.Ui.Controls.InfoBarSeverity.Warning);
                return;
            }

            if (mode == UpdateMode.InPausa && !until.HasValue)
            {
                ShowMessage("Duration missing", "For the pause, pick a duration or a date.", Wpf.Ui.Controls.InfoBarSeverity.Warning);
                return;
            }
        }

        if (mode == UpdateMode.Disattivati)
        {
            bool confirmed = await Dialogs.ConfirmAsync(
                "Turn off updates?",
                (until.HasValue
                    ? "Windows will not receive updates until " + until.Value.ToString("dd/MM/yyyy") + "."
                    : "Windows will not receive updates until you change mode.")
                + "\n\nWithout updates you also miss security fixes. Windows may also turn the service back on by itself.",
                "Turn off");

            if (!confirmed)
            {
                return;
            }
        }

        try
        {
            WindowsUpdateService.Apply(mode, until);
            RefreshState(selectCurrentMode: true);
            ShowMessage("Mode applied", WindowsUpdateService.Describe() + ".", Wpf.Ui.Controls.InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            RefreshState(selectCurrentMode: false);
            ShowMessage("Change failed", ex.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
    }

    private void OnOpenDriverPageClick(object sender, RoutedEventArgs e)
    {
        if (Application.Current.MainWindow is MainWindow main)
        {
            main.NavigateTo(typeof(DriverPage));
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
