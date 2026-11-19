using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using WinTune.Core;
using WinTune.ViewModels;

namespace WinTune.Views.Pages;

/// <summary>Row of the "Changed settings" list.</summary>
public sealed record DifferenceRow(string Title, string Detail);

public partial class OverviewPage : Page
{
    // Reminders proposed the first time; after that the list belongs to the user.
    private static readonly string[] DefaultReminders =
    {
        "Enable XMP/EXPO in the BIOS",
        "Set the fan curve",
        "Enable Resizable BAR",
        "Check PBO and Curve Optimizer",
    };

    private readonly ObservableCollection<CheckTileViewModel> _checks = new();
    private readonly ObservableCollection<ReminderRowViewModel> _reminders = new();
    private readonly ObservableCollection<DriveTileViewModel> _drives = new();

    // True while a display refresh rate change is waiting for confirmation.
    private bool _changingFrequency;

    private bool _checksLoaded;
    private bool _checksRunning;
    private bool _loadingReminders;

    public OverviewPage()
    {
        InitializeComponent();

        ChecksList.ItemsSource = _checks;
        DrivesList.ItemsSource = _drives;
        RemindersList.ItemsSource = _reminders;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshProfileState();
        LoadRemindersAndNotes();
        _ = LoadStartupStateAsync();
        LoadCloseAction();
        _ = LoadSelfUpdateAsync();

        if (!_checksLoaded)
        {
            _checksLoaded = true;
            await RunChecksAsync();
        }
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        RefreshProfileState();
        await RunChecksAsync();
    }

    // ---------- Profile ----------

    private async void OnSaveProfileClick(object sender, RoutedEventArgs e)
    {
        Profile? existing = TryLoadProfile();

        try
        {
            if (existing != null && existing.Tweaks.Count > 0)
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
        }
        catch (Exception ex)
        {
            await Dialogs.InfoAsync("Saving failed", ex.Message);
        }

        RefreshProfileState();
    }

    private async void OnReapplyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Profile? profile = ProfileService.Load();
            if (profile is null || profile.Tweaks.Count == 0)
            {
                return;
            }

            List<ProfileDifference> differences = ProfileService.Compare(profile);
            if (differences.Count == 0)
            {
                RefreshProfileState();
                return;
            }

            bool confirmed = await Dialogs.ConfirmAsync(
                "Reapply the profile?",
                differences.Count + " settings will go back to the values of the profile \"" + profile.Name + "\".",
                "Reapply");

            if (!confirmed)
            {
                return;
            }

            List<string> errors = ProfileService.Apply(profile);
            if (errors.Count > 0)
            {
                await Dialogs.InfoAsync("Some changes failed", string.Join("\n", errors));
            }
        }
        catch (Exception ex)
        {
            await Dialogs.InfoAsync("Operation failed", ex.Message);
        }

        RefreshProfileState();
    }

    private void RefreshProfileState()
    {
        HardwareText.Text = SystemInfo.Summary;

        Profile? profile;
        try
        {
            profile = ProfileService.Load();
        }
        catch (Exception ex)
        {
            ShowProfileState("The saved profile cannot be read", ex.Message, Wpf.Ui.Controls.InfoBarSeverity.Error, false, null);
            return;
        }

        if (profile is null || profile.Tweaks.Count == 0)
        {
            ShowProfileState(
                "No saved profile",
                "Set the optimizations as you like, then save the current state as a profile: from then on you will see here whether anything changes.",
                Wpf.Ui.Controls.InfoBarSeverity.Informational,
                false,
                null);
            return;
        }

        List<ProfileDifference> differences = ProfileService.Compare(profile);
        if (differences.Count == 0)
        {
            ShowProfileState(
                "Everything matches the profile \"" + profile.Name + "\"",
                profile.Tweaks.Count + " settings checked.",
                Wpf.Ui.Controls.InfoBarSeverity.Success,
                false,
                null);
            return;
        }

        List<DifferenceRow> rows = differences
            .Select(d => new DifferenceRow(
                d.Tweak.Category + " - " + d.Tweak.Title,
                "Profile: " + d.Tweak.DisplayValue(d.Expected) + "   ·   Now: " + d.Tweak.DisplayValue(d.Actual)))
            .ToList();

        ShowProfileState(
            differences.Count == 1
                ? "1 setting has changed from the profile"
                : differences.Count + " settings have changed from the profile",
            "You can restore them to the profile values or save the current state as a new profile.",
            Wpf.Ui.Controls.InfoBarSeverity.Warning,
            true,
            rows);
    }

    private void ShowProfileState(
        string title,
        string message,
        Wpf.Ui.Controls.InfoBarSeverity severity,
        bool canReapply,
        List<DifferenceRow>? rows)
    {
        ProfileBar.Title = title;
        ProfileBar.Message = message;
        ProfileBar.Severity = severity;
        ProfileBar.IsOpen = true;

        ReapplyButton.IsEnabled = canReapply;

        DifferencesList.ItemsSource = rows;
        DifferencesPanel.Visibility = rows is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- Start with Windows ----------

    private async Task LoadStartupStateAsync()
    {
        try
        {
            StartupSwitch.IsChecked = await Task.Run(() => BackgroundUpdates.IsStartupEnabled);
        }
        catch (Exception)
        {
            // State not readable: the switch stays off.
        }
    }

    private async void OnStartWithWindowsClick(object sender, RoutedEventArgs e)
    {
        bool enable = StartupSwitch.IsChecked == true;

        try
        {
            await Task.Run(() => BackgroundUpdates.SetStartup(enable));
        }
        catch (Exception ex)
        {
            StartupSwitch.IsChecked = !enable;
            await Dialogs.InfoAsync("Change failed", ex.Message);
        }
    }

    // ---------- WinTune updates (GitHub releases) ----------

    private ReleaseInfo? _release;
    private bool _selfUpdateBusy;

    private async Task LoadSelfUpdateAsync()
    {
        SelfUpdateSwitch.IsChecked = AppSettings.Current.CheckSelfUpdates;
        SelfUpdateTitle.Text = "Check for WinTune updates (running " + SelfUpdateService.CurrentVersion + ")";

        // Automatic check: only if turned on and not done in the last day.
        if (AppSettings.Current.CheckSelfUpdates
            && DateTime.Now - AppSettings.Current.LastSelfUpdateCheck > TimeSpan.FromDays(1)
            && _release is null)
        {
            await CheckSelfUpdateAsync(manual: false);
        }
    }

    private async void OnSelfUpdateSwitchClick(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.CheckSelfUpdates = SelfUpdateSwitch.IsChecked == true;
        AppSettings.Current.Save();

        if (AppSettings.Current.CheckSelfUpdates)
        {
            await CheckSelfUpdateAsync(manual: false);
        }
    }

    private async void OnCheckSelfUpdateClick(object sender, RoutedEventArgs e) => await CheckSelfUpdateAsync(manual: true);

    private async Task CheckSelfUpdateAsync(bool manual)
    {
        if (_selfUpdateBusy)
        {
            return;
        }

        _selfUpdateBusy = true;
        CheckSelfUpdateButton.IsEnabled = false;
        SelfUpdateStatusText.Text = "Checking GitHub...";

        try
        {
            ReleaseInfo? release = await SelfUpdateService.GetLatestAsync();
            AppSettings.Current.LastSelfUpdateCheck = DateTime.Now;
            AppSettings.Current.Save();

            if (release is not null && SelfUpdateService.IsNewer(release))
            {
                _release = release;
                SelfUpdateStatusText.Text = "New release available: " + release.Tag + " (you have " + SelfUpdateService.CurrentVersion + ").";
                InstallSelfUpdateButton.Visibility = Visibility.Visible;
                ReleaseNotesButton.Visibility = release.PageUrl.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            else
            {
                _release = null;
                InstallSelfUpdateButton.Visibility = Visibility.Collapsed;
                ReleaseNotesButton.Visibility = Visibility.Collapsed;
                SelfUpdateStatusText.Text = release is null
                    ? "No release found on GitHub."
                    : "WinTune is up to date (" + SelfUpdateService.CurrentVersion + ").";
            }
        }
        catch (Exception ex)
        {
            SelfUpdateStatusText.Text = manual || AppSettings.Current.CheckSelfUpdates
                ? "Could not check for updates: " + ex.Message
                : "";
        }
        finally
        {
            CheckSelfUpdateButton.IsEnabled = true;
            _selfUpdateBusy = false;
        }
    }

    private void OnReleaseNotesClick(object sender, RoutedEventArgs e)
    {
        if (_release is not null && _release.PageUrl.Length > 0)
        {
            try
            {
                UrlUtil.Open(_release.PageUrl);
            }
            catch
            {
                // The link could not be opened: nothing else to do.
            }
        }
    }

    private async void OnInstallSelfUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_release is null || _selfUpdateBusy)
        {
            return;
        }

        bool confirmed = await Dialogs.ConfirmAsync(
            "Install WinTune " + _release.Tag + "?",
            "WinTune downloads the new version, checks it and restarts. Anything you are doing in WinTune is closed.",
            "Install");

        if (!confirmed)
        {
            return;
        }

        _selfUpdateBusy = true;
        InstallSelfUpdateButton.IsEnabled = false;
        CheckSelfUpdateButton.IsEnabled = false;
        SelfUpdateStatusText.Text = "Downloading " + _release.Tag + "...";

        string? error = await SelfUpdateService.InstallAsync(_release);

        // On success the program restarts and this code does not matter any more.
        if (error is not null)
        {
            SelfUpdateStatusText.Text = error;
            InstallSelfUpdateButton.IsEnabled = true;
            CheckSelfUpdateButton.IsEnabled = true;
            _selfUpdateBusy = false;
        }
    }

    private void LoadCloseAction()
    {
        CloseActionSwitch.IsChecked = AppSettings.Current.HideOnClose;
    }

    private async void OnCloseActionClick(object sender, RoutedEventArgs e)
    {
        bool hide = CloseActionSwitch.IsChecked == true;

        try
        {
            AppSettings.Current.HideOnClose = hide;
            AppSettings.Current.Save();

            if (hide)
            {
                TrayService.Enable();
            }
            else
            {
                TrayService.Disable();
            }
        }
        catch (Exception ex)
        {
            CloseActionSwitch.IsChecked = !hide;
            await Dialogs.InfoAsync("Change failed", ex.Message);
        }
    }

    // ---------- Checks ----------

    private async Task RunChecksAsync()
    {
        if (_checksRunning)
        {
            return;
        }

        _checksRunning = true;
        ChecksRing.Visibility = Visibility.Visible;
        RefreshChecksButton.IsEnabled = false;

        try
        {
            LoadDrives();

            // The displays are read right away; the other checks take a few seconds.
            List<CheckTileViewModel> displayTiles = BuildDisplayTiles();

            _checks.Clear();
            foreach (CheckTileViewModel tile in displayTiles)
            {
                _checks.Add(tile);
            }

            List<CheckResult> results = await SystemChecks.RunAsync();

            _checks.Clear();
            for (int i = 0; i < results.Count; i++)
            {
                _checks.Add(new CheckTileViewModel(results[i]));

                // The displays come right after the RAM, which is the first check.
                if (i == 0)
                {
                    foreach (CheckTileViewModel tile in displayTiles)
                    {
                        _checks.Add(tile);
                    }
                }
            }

            _checks.Add(await BuildSensorsTileAsync());
            _checks.Add(await BuildTempTileAsync());

            // The app updates check needs winget and a few seconds: it appears last.
            var updatesTile = new CheckTileViewModel(
                new CheckResult("App updates", CheckLevel.Info, "Checking...", "Looking for app updates with winget."),
                "", null, typeof(AppPage));
            _checks.Add(updatesTile);
            int updatesIndex = _checks.Count - 1;

            CheckTileViewModel finalTile = await BuildUpdatesTileAsync();
            if (updatesIndex < _checks.Count && ReferenceEquals(_checks[updatesIndex], updatesTile))
            {
                _checks[updatesIndex] = finalTile;
            }
        }
        catch (Exception ex)
        {
            _checks.Add(new CheckTileViewModel(new CheckResult("Checks", CheckLevel.Warning, "Failed", ex.Message)));
        }
        finally
        {
            ChecksRing.Visibility = Visibility.Collapsed;
            RefreshChecksButton.IsEnabled = true;
            _checksRunning = false;
        }
    }

    private void LoadDrives()
    {
        _drives.Clear();

        try
        {
            foreach (DriveInfo d in DriveInfo.GetDrives())
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady || d.TotalSize <= 0)
                {
                    continue;
                }

                string label = string.Empty;
                try
                {
                    label = d.VolumeLabel;
                }
                catch (Exception)
                {
                    // Label not readable: the generic name is used.
                }

                _drives.Add(new DriveTileViewModel("(" + d.Name.TrimEnd('\\') + ")", label, d.AvailableFreeSpace, d.TotalSize));
            }
        }
        catch (Exception)
        {
            // No readable drive: the section stays empty.
        }
    }

    private void OnDriveTileClick(object sender, MouseButtonEventArgs e)
    {
        if (Application.Current.MainWindow is MainWindow main)
        {
            main.NavigateTo(typeof(ToolPage));
        }
    }

    private const string TempCleanTag = "cleanTemp";

    /// <summary>Tile of the temporary files: warns when they take more than the limit of the settings.</summary>
    private static async Task<CheckTileViewModel> BuildTempTileAsync()
    {
        const string title = "Temporary files";

        try
        {
            long total = await CleanupService.MeasureAsync(CleanupService.UserTemp)
                + await CleanupService.MeasureAsync(CleanupService.WindowsTemp);

            // The limit is a percentage of the system drive, so it fits small and large disks alike.
            double percent = Math.Clamp(AppSettings.Current.TempWarningPercent, 0.5, 100);
            string drive = System.IO.Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "C:\\";
            long driveSize = new DriveInfo(drive).TotalSize;
            long limit = (long)(driveSize * percent / 100.0);
            string size = CleanupService.FormatSize(total);
            string limitText = percent.ToString("0.#") + "% of " + drive.TrimEnd('\\') + " (" + CleanupService.FormatSize(limit) + ")";

            if (total >= limit)
            {
                return new CheckTileViewModel(
                    new CheckResult(title, CheckLevel.Warning, size, "Over " + limitText + " in the user and Windows Temp folders. Files in use are skipped."),
                    "Clean now",
                    TempCleanTag,
                    typeof(UtilitiesPage));
            }

            return new CheckTileViewModel(
                new CheckResult(title, CheckLevel.Ok, size, "Under the limit of " + limitText + " (user and Windows Temp folders). Click to open the cleanup."),
                "",
                null,
                typeof(UtilitiesPage));
        }
        catch (Exception ex)
        {
            return new CheckTileViewModel(new CheckResult(title, CheckLevel.Info, "Not available", ex.Message));
        }
    }

    private async Task CleanTempAsync(CheckTileViewModel tile)
    {
        try
        {
            long freed = await CleanupService.CleanAsync(CleanupService.UserTemp);
            freed += await CleanupService.CleanAsync(CleanupService.WindowsTemp);
            TweakEngine.LogChange("cleanup.disk", "temporary files", CleanupService.FormatSize(freed) + " freed");
        }
        catch
        {
            // The tile is measured again below and shows what is left.
        }

        int index = _checks.IndexOf(tile);
        CheckTileViewModel fresh = await BuildTempTileAsync();
        if (index >= 0 && index < _checks.Count)
        {
            _checks[index] = fresh;
        }
    }

    private static async Task<CheckTileViewModel> BuildSensorsTileAsync()
    {
        const string title = "Temperatures";

        try
        {
            List<HardwareReading> data = await Task.Run(SensorService.Read);
            float? cpu = SensorService.FindCpuTemperature(data);
            float? gpu = SensorService.FindGpuTemperature(data);

            if (cpu is null && gpu is null)
            {
                return new CheckTileViewModel(
                    new CheckResult(title, CheckLevel.Info, "Not available", "CPU sensors may need the PawnIO driver. Click to open Sensors."),
                    "", null, typeof(SensorsPage));
            }

            var parts = new List<string>();
            if (cpu is float c)
            {
                parts.Add("CPU " + Math.Round(c).ToString("0") + " °C");
            }

            if (gpu is float g)
            {
                parts.Add("GPU " + Math.Round(g).ToString("0") + " °C");
            }

            bool hot = (cpu ?? 0) >= 85 || (gpu ?? 0) >= 85;

            return new CheckTileViewModel(
                new CheckResult(
                    title,
                    hot ? CheckLevel.Warning : CheckLevel.Ok,
                    string.Join("  ·  ", parts),
                    hot ? "Running hot. Click to open Sensors." : "Click to open Sensors."),
                "", null, typeof(SensorsPage));
        }
        catch (Exception ex)
        {
            return new CheckTileViewModel(new CheckResult(title, CheckLevel.Info, "Could not read", ex.Message));
        }
    }

    private static async Task<CheckTileViewModel> BuildUpdatesTileAsync()
    {
        const string title = "App updates";

        try
        {
            string? version = await WingetService.GetVersionAsync();
            if (version is null)
            {
                return new CheckTileViewModel(
                    new CheckResult(title, CheckLevel.Info, "winget not available", "Click to open the Apps page."),
                    "", null, typeof(AppPage));
            }

            List<WingetUpgrade> all = await WingetService.GetUpgradesAsync();
            Dictionary<string, string> ignored = TryLoadProfile()?.IgnoredUpdates
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            List<WingetUpgrade> pending = all.Where(u => !ignored.ContainsKey(u.Id)).ToList();

            if (pending.Count == 0)
            {
                return new CheckTileViewModel(
                    new CheckResult(title, CheckLevel.Ok, "All apps are up to date", "Click to open the Apps page."),
                    "", null, typeof(AppPage));
            }

            string names = string.Join(", ", pending.Take(3).Select(u => u.Name))
                + (pending.Count > 3 ? " and " + (pending.Count - 3) + " more" : "");
            return new CheckTileViewModel(
                new CheckResult(
                    title,
                    CheckLevel.Warning,
                    pending.Count == 1 ? "1 update available" : pending.Count + " updates available",
                    names + "  ·  Click to open the Apps page."),
                "", null, typeof(AppPage));
        }
        catch (Exception ex)
        {
            return new CheckTileViewModel(
                new CheckResult(title, CheckLevel.Info, "Could not check", ex.Message),
                "", null, typeof(AppPage));
        }
    }

    private void OnCheckTileClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element
            && element.DataContext is CheckTileViewModel tile
            && tile.NavigateTo is Type page
            && Application.Current.MainWindow is MainWindow main)
        {
            main.NavigateTo(page);
        }
    }

    private static List<CheckTileViewModel> BuildDisplayTiles()
    {
        var tiles = new List<CheckTileViewModel>();

        try
        {
            List<DisplayInfo> displays = DisplayService.List();

            foreach (DisplayInfo display in displays)
            {
                string title = displays.Count == 1 ? "Display refresh rate" : "Display refresh rate " + display.Number;
                string resolution = display.Width + " x " + display.Height;

                if (display.IsAtMax)
                {
                    tiles.Add(new CheckTileViewModel(new CheckResult(
                        title,
                        CheckLevel.Ok,
                        display.CurrentHz + " Hz",
                        resolution + "  ·  This is the maximum refresh rate at this resolution.")));
                }
                else
                {
                    tiles.Add(new CheckTileViewModel(
                        new CheckResult(
                            title,
                            CheckLevel.Warning,
                            display.CurrentHz + " Hz out of " + display.MaxHz + " Hz",
                            resolution + "  ·  The display supports a higher refresh rate."),
                        "Set " + display.MaxHz + " Hz",
                        display));
                }
            }
        }
        catch
        {
            // Without the displays reading the tile simply does not appear.
        }

        return tiles;
    }

    private async void OnCheckActionClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement source
            && source.DataContext is CheckTileViewModel tempTile
            && tempTile.Tag as string == TempCleanTag)
        {
            await CleanTempAsync(tempTile);
            return;
        }

        if (sender is not FrameworkElement element
            || element.DataContext is not CheckTileViewModel tile
            || tile.Tag is not DisplayInfo display)
        {
            return;
        }

        if (_changingFrequency)
        {
            return;
        }

        _changingFrequency = true;
        try
        {
            try
            {
                DisplayService.SetFrequency(display.DeviceName, display.MaxHz);
            }
            catch (Exception ex)
            {
                ProfileBar.Title = "Refresh rate not set";
                ProfileBar.Message = ex.Message;
                ProfileBar.Severity = Wpf.Ui.Controls.InfoBarSeverity.Error;
                ProfileBar.IsOpen = true;
                return;
            }

            // As Windows does: if nobody confirms (for example because the screen stayed black)
            // after a few seconds the previous refresh rate comes back.
            bool keep = await ConfirmFrequencyAsync(display);

            if (!keep)
            {
                try
                {
                    DisplayService.SetFrequency(display.DeviceName, display.CurrentHz);
                }
                catch
                {
                    // If the restore fails the new rate stays: the tile will show the real state.
                }
            }
        }
        finally
        {
            _changingFrequency = false;
        }

        await RunChecksAsync();
    }

    /// <summary>
    /// Confirmation window in the center of the screen, with a countdown.
    /// Returns true only if the user presses "Keep"; when the time runs out it closes by itself.
    /// </summary>
    private async Task<bool> ConfirmFrequencyAsync(DisplayInfo display)
    {
        int seconds = 20;

        var text = new TextBlock
        {
            MaxWidth = 380,
            TextWrapping = TextWrapping.Wrap,
        };

        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Keep " + display.MaxHz + " Hz?",
            Content = text,
            PrimaryButtonText = "Keep " + display.MaxHz + " Hz",
            CloseButtonText = "Go back to " + display.CurrentHz + " Hz",
        };

        if (Window.GetWindow(this) is Window owner)
        {
            box.Owner = owner;
            box.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        void UpdateText() => text.Text =
            "The display refresh rate is now " + display.MaxHz + " Hz. If the picture looks fine, press \"Keep\".\n\n"
            + "Without confirmation, in " + seconds + " seconds it goes back to " + display.CurrentHz + " Hz.";

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            seconds--;
            if (seconds <= 0)
            {
                timer.Stop();
                box.Close();
            }
            else
            {
                UpdateText();
            }
        };

        UpdateText();
        timer.Start();

        try
        {
            Wpf.Ui.Controls.MessageBoxResult result = await box.ShowDialogAsync();
            return result == Wpf.Ui.Controls.MessageBoxResult.Primary;
        }
        finally
        {
            timer.Stop();
        }
    }

    // ---------- Reminders and OC notes ----------

    private void LoadRemindersAndNotes()
    {
        _loadingReminders = true;
        try
        {
            foreach (ReminderRowViewModel row in _reminders)
            {
                row.PropertyChanged -= OnReminderChanged;
            }

            _reminders.Clear();

            Profile? profile = TryLoadProfile();

            if (profile is null || !profile.HasReminders)
            {
                foreach (string text in DefaultReminders)
                {
                    AddReminderRow(text, false);
                }
            }
            else
            {
                foreach (ProfileReminder reminder in profile.Reminders)
                {
                    AddReminderRow(reminder.Text, reminder.Done);
                }
            }

            // Unsaved notes must not be overwritten when coming back to the page.
            if (!OcNotesBox.IsKeyboardFocusWithin)
            {
                OcNotesBox.Text = profile?.OcNotes ?? "";
                NotesStatusText.Text = "";
            }
        }
        finally
        {
            _loadingReminders = false;
        }
    }

    private void AddReminderRow(string text, bool done)
    {
        var row = new ReminderRowViewModel(text, done);
        row.PropertyChanged += OnReminderChanged;
        _reminders.Add(row);
    }

    private void OnReminderChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_loadingReminders)
        {
            SaveReminders();
        }
    }

    private void SaveReminders()
    {
        try
        {
            Profile profile = TryLoadProfile() ?? new Profile();
            profile.Reminders.Clear();
            profile.Reminders.AddRange(_reminders.Select(r => new ProfileReminder(r.Text, r.IsDone)));
            profile.HasReminders = true;
            ProfileService.Save(profile);
        }
        catch (Exception ex)
        {
            NotesStatusText.Text = "Saving failed: " + ex.Message;
        }
    }

    private void OnNewReminderKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            AddReminder();
        }
    }

    private void OnAddReminderClick(object sender, RoutedEventArgs e) => AddReminder();

    private void AddReminder()
    {
        string text = NewReminderBox.Text.Trim();
        if (text.Length == 0)
        {
            return;
        }

        AddReminderRow(text, false);
        NewReminderBox.Text = "";
        SaveReminders();
    }

    private void OnRemoveDoneRemindersClick(object sender, RoutedEventArgs e)
    {
        List<ReminderRowViewModel> done = _reminders.Where(r => r.IsDone).ToList();
        if (done.Count == 0)
        {
            return;
        }

        foreach (ReminderRowViewModel row in done)
        {
            row.PropertyChanged -= OnReminderChanged;
            _reminders.Remove(row);
        }

        SaveReminders();
    }

    private void OnSaveNotesClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Profile profile = TryLoadProfile() ?? new Profile();
            profile.OcNotes = OcNotesBox.Text.Trim();
            ProfileService.Save(profile);
            NotesStatusText.Text = "Saved at " + DateTime.Now.ToString("HH:mm");
        }
        catch (Exception ex)
        {
            NotesStatusText.Text = "Saving failed: " + ex.Message;
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
}
