using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using WinTune.Core;
using WinTune.ViewModels;

namespace WinTune.Views.Pages;

public partial class AppPage : Page
{
    private readonly ObservableCollection<AppCategoryViewModel> _categories = new();
    private readonly ObservableCollection<ResultRowViewModel> _results = new();
    private readonly ObservableCollection<BloatRowViewModel> _bloat = new();
    private readonly ObservableCollection<UpgradeRowViewModel> _upgrades = new();

    // Uninstall list: all the installed apps, and the ones that match the filter (the ticks live in the first list).
    private readonly List<UninstallRowViewModel> _allUninstall = new();
    private readonly ObservableCollection<UninstallRowViewModel> _uninstallShown = new();
    private readonly ObservableCollection<IgnoredUpdateRowViewModel> _ignored = new();

    // Last "winget upgrade" result and the apps whose updates are ignored (ID and name).
    private List<WingetUpgrade> _allUpgrades = new();
    private Dictionary<string, string> _ignoredUpdates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<StartupRowViewModel> _startup = new();

    private HashSet<string>? _installedIds;
    private bool _wingetReady;
    private bool _firstLoadDone;
    private bool _loadingApps;
    private bool _working;

    // Search as you type: starts shortly after the last key pressed.
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private int _searchVersion;
    private int _searchesRunning;
    private bool _resultsFromSearch;

    public AppPage()
    {
        InitializeComponent();

        CategoriesList.ItemsSource = _categories;
        SearchResults.ItemsSource = _results;

        _searchTimer.Tick += async (_, _) =>
        {
            _searchTimer.Stop();
            await SearchAsync(fromTyping: true);
        };
        BloatList.ItemsSource = _bloat;
        UninstallList.ItemsSource = _uninstallShown;
        UpgradeList.ItemsSource = _upgrades;
        IgnoredList.ItemsSource = _ignored;
        StartupList.ItemsSource = _startup;

        _onStartupError = message => ShowMessage("Change failed", message, Wpf.Ui.Controls.InfoBarSeverity.Error);
    }

    // The event is static: the page hooks up only while visible, otherwise it would never be freed.
    private readonly Action<string> _onStartupError;

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StartupRowViewModel.ErrorOccurred -= _onStartupError;
    }

    private IEnumerable<AppRowViewModel> AllApps => _categories.SelectMany(c => c.Apps);

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        StartupRowViewModel.ErrorOccurred -= _onStartupError;
        StartupRowViewModel.ErrorOccurred += _onStartupError;

        if (_working)
        {
            // During an installation the list stays as it is, so the state of the rows is not lost.
            return;
        }

        LoadAppsFromProfile();
        LoadStartup();
        _ = LoadBackgroundStateAsync();

        if (_firstLoadDone)
        {
            return;
        }

        _firstLoadDone = true;

        await CheckWingetAsync();
        await RefreshInstalledAsync();
        await LoadBloatAsync();
        await LoadUpgradesAsync();
        await LoadUninstallIfVisibleAsync();
    }

    // ---------- Update notifications ----------

    // True until the state has been read: during that time the controls must not save anything.
    private bool _loadingBackground = true;

    private async Task LoadBackgroundStateAsync()
    {
        _loadingBackground = true;

        try
        {
            (bool check, int hours) = await Task.Run(() => (
                BackgroundUpdates.IsCheckEnabled,
                BackgroundUpdates.LoadSettings().IntervalHours));

            NotifySwitch.IsChecked = check;

            IntervalBox.SelectedItem = IntervalBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(i => i.Tag is string tag && tag == hours.ToString())
                ?? IntervalBox.Items[1];
        }
        catch (Exception)
        {
            // State not readable: the controls stay off.
        }
        finally
        {
            _loadingBackground = false;
        }
    }

    private int SelectedIntervalHours() =>
        IntervalBox.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out int hours) ? hours : 6;

    private async void OnNotifyClick(object sender, RoutedEventArgs e)
    {
        bool enable = NotifySwitch.IsChecked == true;
        int hours = SelectedIntervalHours();

        try
        {
            await Task.Run(() => BackgroundUpdates.SetCheck(enable, hours));
            BackgroundStatusText.Text = enable
                ? "WinTune checks for app updates when you sign in and then every " + hours + " hours, and tells you about new ones."
                : "Background update checks are off.";
        }
        catch (Exception ex)
        {
            NotifySwitch.IsChecked = !enable;
            ShowMessage("Change failed", ex.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
    }

    private async void OnIntervalChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingBackground || NotifySwitch.IsChecked != true)
        {
            return;
        }

        int hours = SelectedIntervalHours();

        try
        {
            await Task.Run(() => BackgroundUpdates.SetCheck(true, hours));
            BackgroundStatusText.Text = "WinTune will check for app updates every " + hours + " hours.";
        }
        catch (Exception ex)
        {
            ShowMessage("Change failed", ex.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
    }

    private async void OnTestNotificationClick(object sender, RoutedEventArgs e)
    {
        bool shown = await BackgroundUpdates.SendTestAsync();

        if (shown)
        {
            ShowMessage(
                "Notification sent",
                "If you do not see it, check that notifications are on for WinTune in Windows settings (System, Notifications) and that Do not disturb is off.",
                Wpf.Ui.Controls.InfoBarSeverity.Success);
        }
        else
        {
            ShowMessage("Notification failed", "Windows did not show the notification.", Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
    }

    // ---------- winget ----------

    private async Task CheckWingetAsync()
    {
        WingetRing.Visibility = Visibility.Visible;
        WingetStatusText.Text = "Checking winget...";

        string? version = await WingetService.GetVersionAsync();

        if (version is null || WingetService.IsTooOld(version))
        {
            WingetStatusText.Text = version is null
                ? "winget not found: installing, this can take a few minutes..."
                : "winget " + version + " is too old: updating...";

            (bool ok, string message) = await WingetService.InstallOrRepairAsync();
            version = await WingetService.GetVersionAsync();

            if (!ok || version is null)
            {
                _wingetReady = false;
                WingetRing.Visibility = Visibility.Collapsed;
                WingetStatusText.Text = "winget not available.";
                ShowMessage(
                    "winget installation failed",
                    message + "\nYou can install it from the Microsoft Store by searching for \"App Installer\".",
                    Wpf.Ui.Controls.InfoBarSeverity.Error);
                return;
            }
        }

        _wingetReady = true;
        WingetRing.Visibility = Visibility.Collapsed;
        WingetStatusText.Text = "winget " + version + " present.";
    }

    // ---------- Categories and apps ----------

    private void LoadAppsFromProfile()
    {
        _loadingApps = true;
        try
        {
            foreach (AppRowViewModel row in AllApps)
            {
                row.PropertyChanged -= OnAppRowChanged;
            }

            _categories.Clear();

            Profile profile = TryLoadProfile() ?? new Profile();
            if (AppCatalog.EnsureInitialized(profile))
            {
                // First opening: the catalog of recommended apps goes into the profile.
                ProfileService.Save(profile);
            }

            foreach (string name in profile.AppCategories)
            {
                _categories.Add(new AppCategoryViewModel(name));
            }

            foreach (ProfileApp app in profile.Apps)
            {
                string categoryName = string.IsNullOrWhiteSpace(app.Category) ? AppCatalog.OtherCategory : app.Category;
                AppCategoryViewModel category = FindCategory(categoryName) ?? AddCategory(categoryName);
                AddRow(category, app.Id, app.Name, app.Selected);
            }
        }
        catch (Exception ex)
        {
            ShowMessage("Reading the list failed", ex.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
        finally
        {
            _loadingApps = false;
        }

        ApplyInstalledState();
        UpdateCategoryChoices();
        UpdateSelectionSummary();
    }

    private AppCategoryViewModel? FindCategory(string name) =>
        _categories.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    private AppCategoryViewModel AddCategory(string name)
    {
        var category = new AppCategoryViewModel(name);
        _categories.Add(category);
        return category;
    }

    private AppRowViewModel AddRow(AppCategoryViewModel category, string id, string name, bool selected)
    {
        var row = new AppRowViewModel(id, name, selected, category.Name);
        row.PropertyChanged += OnAppRowChanged;
        category.Apps.Add(row);
        return row;
    }

    private void OnAppRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_loadingApps && e.PropertyName == nameof(AppRowViewModel.IsSelected))
        {
            SaveApps();
            UpdateSelectionSummary();
        }
    }

    private void SaveApps()
    {
        try
        {
            Profile profile = TryLoadProfile() ?? new Profile();

            profile.AppCategories.Clear();
            profile.AppCategories.AddRange(_categories.Select(c => c.Name));

            profile.Apps.Clear();
            profile.Apps.AddRange(AllApps.Select(a => new ProfileApp(a.Id, a.Name, a.IsSelected, a.Category)));

            profile.HasAppCatalog = true;
            ProfileService.Save(profile);
        }
        catch (Exception ex)
        {
            ShowMessage("Saving the list failed", ex.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
    }

    /// <summary>Updates the list of categories in the "Category" box keeping the current text.</summary>
    private void UpdateCategoryChoices()
    {
        string current = TargetCategoryBox.Text?.Trim() ?? "";
        List<string> names = _categories.Select(c => c.Name).ToList();

        TargetCategoryBox.ItemsSource = names;
        TargetCategoryBox.Text = current.Length > 0 ? current : names.FirstOrDefault() ?? "";
    }

    /// <summary>Writes next to the buttons how many apps are selected, when no operation is in progress.</summary>
    private void UpdateSelectionSummary()
    {
        if (_working)
        {
            return;
        }

        int selected = AllApps.Count(a => a.IsSelected);
        InstallStatusText.Text = selected switch
        {
            0 => "",
            1 => "1 app selected",
            _ => selected + " apps selected",
        };
    }

    /// <summary>Category chosen or typed in the "Category" box.</summary>
    private string TargetCategory()
    {
        string typed = TargetCategoryBox.Text?.Trim() ?? "";
        if (typed.Length > 0)
        {
            return typed;
        }

        return _categories.FirstOrDefault()?.Name ?? AppCatalog.OtherCategory;
    }

    /// <summary>Adds an app to a category (creating it if it does not exist). False if the app was already in the list or the ID is not valid.</summary>
    private bool AddApp(string id, string name, string categoryName)
    {
        if (!WingetService.IsValidId(id))
        {
            return false;
        }

        if (AllApps.Any(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        AppCategoryViewModel category = FindCategory(categoryName) ?? AddCategory(categoryName);

        // An app not yet installed is added already selected, ready for "Install selected".
        bool installed = _installedIds != null && _installedIds.Contains(id);
        AppRowViewModel row = AddRow(category, id, name, !installed);
        SetInstalledState(row);
        return true;
    }

    private void OnAddSelectedResultsClick(object sender, RoutedEventArgs e)
    {
        List<ResultRowViewModel> chosen = _results.Where(r => r.IsSelected).ToList();
        if (chosen.Count == 0)
        {
            ShowMessage("No app selected", "Tick the apps to add.", Wpf.Ui.Controls.InfoBarSeverity.Informational);
            return;
        }

        string categoryName = TargetCategory();
        int added = 0;

        _loadingApps = true;
        try
        {
            foreach (ResultRowViewModel row in chosen)
            {
                if (AddApp(row.Result.Id, row.Result.Name, categoryName))
                {
                    added++;
                }

                _results.Remove(row);
            }
        }
        finally
        {
            _loadingApps = false;
        }

        SaveApps();
        UpdateCategoryChoices();
        UpdateSelectionSummary();

        if (_results.Count == 0)
        {
            CloseResults();
        }

        int skipped = chosen.Count - added;
        ShowMessage(
            added == 1 ? "1 app added to \"" + categoryName + "\"" : added + " apps added to \"" + categoryName + "\"",
            skipped > 0 ? skipped + " were already in the list." : "",
            Wpf.Ui.Controls.InfoBarSeverity.Success);
    }

    private void OnSelectAllResultsClick(object sender, RoutedEventArgs e)
    {
        // If they are all already ticked, the button deselects them.
        bool target = _results.Any(r => !r.IsSelected);
        foreach (ResultRowViewModel row in _results)
        {
            row.IsSelected = target;
        }
    }

    private async void OnRemoveSelectedClick(object sender, RoutedEventArgs e)
    {
        List<AppRowViewModel> targets = AllApps.Where(a => a.IsSelected).ToList();
        if (targets.Count == 0)
        {
            ShowMessage("No app selected", "Tick the apps to remove from the list.", Wpf.Ui.Controls.InfoBarSeverity.Informational);
            return;
        }

        bool confirmed = await Dialogs.ConfirmAsync(
            "Remove " + targets.Count + " apps from the list?",
            ListNames(targets) + "\n\nThey are not uninstalled from the PC.",
            "Remove");

        if (!confirmed)
        {
            return;
        }

        foreach (AppRowViewModel row in targets)
        {
            row.PropertyChanged -= OnAppRowChanged;
            FindCategory(row.Category)?.Apps.Remove(row);
        }

        SaveApps();
        UpdateSelectionSummary();
    }

    private void OnNewCategoryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CreateCategory();
        }
    }

    private void OnAddCategoryClick(object sender, RoutedEventArgs e) => CreateCategory();

    private void CreateCategory()
    {
        string name = NewCategoryBox.Text.Trim();
        if (name.Length == 0)
        {
            ShowMessage("Name missing", "Type the name of the new category.", Wpf.Ui.Controls.InfoBarSeverity.Warning);
            return;
        }

        if (FindCategory(name) != null)
        {
            ShowMessage("Category already exists", "A category with this name already exists.", Wpf.Ui.Controls.InfoBarSeverity.Informational);
            return;
        }

        AddCategory(name);
        NewCategoryBox.Text = "";
        SaveApps();
        UpdateCategoryChoices();

        // The new category becomes the one the next added apps go into.
        TargetCategoryBox.Text = name;
        MessageBar.IsOpen = false;
    }

    private async void OnDeleteCategoryClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not AppCategoryViewModel category)
        {
            return;
        }

        if (category.Apps.Count > 0)
        {
            bool confirmed = await Dialogs.ConfirmAsync(
                "Delete the category \"" + category.Name + "\"?",
                "Its " + category.Apps.Count + " apps will be removed from the list. They are not uninstalled from the PC.",
                "Delete");

            if (!confirmed)
            {
                return;
            }
        }

        foreach (AppRowViewModel row in category.Apps)
        {
            row.PropertyChanged -= OnAppRowChanged;
        }

        _categories.Remove(category);
        SaveApps();
        UpdateCategoryChoices();
        UpdateSelectionSummary();
    }

    // ---------- Search and detection ----------

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _searchTimer.Stop();

        if (SearchBox.Text.Trim().Length >= 3)
        {
            _searchTimer.Start();
        }
        else if (_resultsFromSearch)
        {
            // Box cleared: the results of the previous search are no longer needed.
            _searchVersion++;
            CloseResults();
        }
    }

    private async void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _searchTimer.Stop();
            await SearchAsync(fromTyping: false);
        }
    }

    private async void OnSearchClick(object sender, RoutedEventArgs e)
    {
        _searchTimer.Stop();
        await SearchAsync(fromTyping: false);
    }

    /// <summary>
    /// Searches apps with winget. While typing several searches can start:
    /// only the result of the last one is shown, the others are discarded.
    /// </summary>
    private async Task SearchAsync(bool fromTyping)
    {
        string query = SearchBox.Text.Trim();
        if (query.Length < (fromTyping ? 3 : 2))
        {
            return;
        }

        if (!_wingetReady)
        {
            if (!fromTyping)
            {
                ShowMessage("winget not available", "Searching requires winget.", Wpf.Ui.Controls.InfoBarSeverity.Warning);
            }

            return;
        }

        int version = ++_searchVersion;
        _searchesRunning++;
        SearchRing.Visibility = Visibility.Visible;

        try
        {
            List<WingetSearchResult> results = await WingetService.SearchAsync(query);

            if (version != _searchVersion)
            {
                // In the meantime a more recent search has started.
                return;
            }

            _resultsFromSearch = true;
            ShowResults(
                results,
                results.Count == 0
                    ? "No app found for \"" + query + "\""
                    : "Results for \"" + query + "\": tick the ones to add");
            MessageBar.IsOpen = false;
        }
        catch (Exception ex)
        {
            if (version == _searchVersion && !fromTyping)
            {
                ShowMessage("Search failed", ex.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
            }
        }
        finally
        {
            _searchesRunning--;
            if (_searchesRunning <= 0)
            {
                _searchesRunning = 0;
                SearchRing.Visibility = Visibility.Collapsed;
            }
        }
    }

    private async void OnDetectInstalledClick(object sender, RoutedEventArgs e)
    {
        if (!RequireWinget("Detection requires winget."))
        {
            return;
        }

        _searchTimer.Stop();
        _searchVersion++;
        SetDetectBusy(true);
        try
        {
            List<WingetSearchResult> installed = await WingetService.GetInstalledAppsAsync();

            if (installed.Count == 0)
            {
                CloseResults();
                ShowMessage("Detection failed", "winget did not return the list of installed apps.", Wpf.Ui.Controls.InfoBarSeverity.Warning);
                return;
            }

            // The state of the rows is updated with what has just been read.
            _installedIds = new HashSet<string>(installed.Select(a => a.Id), StringComparer.OrdinalIgnoreCase);
            ApplyInstalledState();

            var known = new HashSet<string>(AllApps.Select(a => a.Id), StringComparer.OrdinalIgnoreCase);
            List<WingetSearchResult> missing = installed.Where(a => !known.Contains(a.Id)).ToList();

            if (missing.Count == 0)
            {
                CloseResults();
                ShowMessage("No new apps", "All the installed apps that winget recognizes are already in your list.", Wpf.Ui.Controls.InfoBarSeverity.Success);
            }
            else
            {
                _resultsFromSearch = false;
                ShowResults(missing, missing.Count + " installed apps not yet in the list: tick the ones to add");
                MessageBar.IsOpen = false;
            }
        }
        catch (Exception ex)
        {
            ShowMessage("Detection failed", ex.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
        finally
        {
            SetDetectBusy(false);
        }
    }

    private void ShowResults(List<WingetSearchResult> results, string title)
    {
        _results.Clear();
        foreach (WingetSearchResult result in results)
        {
            _results.Add(new ResultRowViewModel(result));
        }

        ResultsTitle.Text = title;
        ResultsPanel.Visibility = Visibility.Visible;
    }

    private void OnCloseResultsClick(object sender, RoutedEventArgs e) => CloseResults();

    private void CloseResults()
    {
        _results.Clear();
        _resultsFromSearch = false;
        ResultsPanel.Visibility = Visibility.Collapsed;
    }

    private void SetDetectBusy(bool busy)
    {
        DetectButton.IsEnabled = !busy;
        if (busy)
        {
            SearchRing.Visibility = Visibility.Visible;
        }
        else if (_searchesRunning <= 0)
        {
            SearchRing.Visibility = Visibility.Collapsed;
        }
    }

    private bool RequireWinget(string message)
    {
        if (_wingetReady)
        {
            return true;
        }

        ShowMessage("winget not available", message, Wpf.Ui.Controls.InfoBarSeverity.Warning);
        return false;
    }

    // ---------- State, installation, uninstallation ----------

    private async void OnRefreshAppsClick(object sender, RoutedEventArgs e) => await RefreshInstalledAsync();

    private async Task RefreshInstalledAsync()
    {
        if (!_wingetReady)
        {
            return;
        }

        SetWorkBusy(true, "Reading installed apps...");
        try
        {
            _installedIds = await WingetService.GetInstalledIdsAsync();
            ApplyInstalledState();
        }
        finally
        {
            SetWorkBusy(false, _installedIds is null ? "App state could not be read." : "");
            if (_installedIds != null)
            {
                UpdateSelectionSummary();
            }
        }
    }

    private void ApplyInstalledState()
    {
        foreach (AppRowViewModel row in AllApps)
        {
            SetInstalledState(row);
        }
    }

    private void SetInstalledState(AppRowViewModel row)
    {
        if (_installedIds is null)
        {
            row.IsInstalled = false;
            row.Status = "";
            return;
        }

        row.IsInstalled = _installedIds.Contains(row.Id);
        row.Status = row.IsInstalled ? "Installed" : "Not installed";
    }

    private async void OnInstallClick(object sender, RoutedEventArgs e)
    {
        if (!RequireWinget("Installing requires winget."))
        {
            return;
        }

        List<AppRowViewModel> targets = AllApps.Where(a => a.IsSelected && !a.IsInstalled).ToList();
        if (targets.Count == 0)
        {
            ShowMessage(
                "Nothing to install",
                "No app selected, or the selected ones are already installed.",
                Wpf.Ui.Controls.InfoBarSeverity.Informational);
            return;
        }

        bool confirmed = await Dialogs.ConfirmAsync(
            "Install " + targets.Count + " apps?",
            ListNames(targets) + "\n\nInstallation runs in the background and can take several minutes.",
            "Install");

        if (!confirmed)
        {
            return;
        }

        await RunOnAppsAsync(targets, install: true);
    }

    private static string ListNames(List<AppRowViewModel> rows)
    {
        string list = string.Join("\n", rows.Take(15).Select(r => "•  " + r.Name));
        return rows.Count > 15 ? list + "\n... and " + (rows.Count - 15) : list;
    }

    /// <summary>Installs or uninstalls the apps one after the other, updating the state of each row.</summary>
    private async Task RunOnAppsAsync(List<AppRowViewModel> targets, bool install)
    {
        string verb = install ? "Installing" : "Uninstalling";
        var failed = new List<string>();
        int done = 0;

        _working = true;
        try
        {
            foreach (AppRowViewModel row in targets)
            {
                row.Status = "Queued";
            }

            foreach (AppRowViewModel row in targets)
            {
                done++;
                SetWorkBusy(true, verb + " " + done + " of " + targets.Count + ": " + row.Name);
                row.Status = verb + "...";

                (bool ok, string message) = install
                    ? await WingetService.InstallAsync(row.Id)
                    : await WingetService.UninstallAsync(row.Id);

                TweakEngine.LogChange(
                    "app." + row.Id,
                    install ? "install" : "uninstall",
                    ok ? "succeeded" : "failed: " + message);

                if (!ok)
                {
                    failed.Add(row.Name);
                    row.Status = "Error: " + message;
                    continue;
                }

                if (install)
                {
                    _installedIds?.Add(row.Id);
                    row.IsInstalled = true;
                    row.Status = "Installed";
                }
                else
                {
                    _installedIds?.Remove(row.Id);
                    row.IsInstalled = false;
                    row.Status = "Not installed";

                    // Tick removed, so a later "Install selected" does not reinstall it by mistake.
                    row.IsSelected = false;
                }
            }
        }
        finally
        {
            _working = false;
            SetWorkBusy(false, "");
            UpdateSelectionSummary();
        }

        if (failed.Count == 0)
        {
            ShowMessage(install ? "Installation completed" : "Uninstallation completed", targets.Count + " apps.", Wpf.Ui.Controls.InfoBarSeverity.Success);
        }
        else
        {
            ShowMessage(
                install ? "Installation completed with errors" : "Uninstallation completed with errors",
                "Failed for: " + string.Join(", ", failed) + ". The reason is shown next to each app.",
                Wpf.Ui.Controls.InfoBarSeverity.Warning);
        }
    }

    private void SetWorkBusy(bool busy, string status)
    {
        InstallRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        InstallButton.IsEnabled = !busy;
        RemoveFromListButton.IsEnabled = !busy;
        RefreshAppsButton.IsEnabled = !busy;
        InstallStatusText.Text = status;
    }

    // ---------- Uninstall apps ----------

    private bool _uninstallLoaded;

    // The list of installed apps is slow to read: it is loaded the first time the section is opened.
    private async Task LoadUninstallIfVisibleAsync()
    {
        if (!_uninstallLoaded && UninstallPanel.Visibility == Visibility.Visible && _wingetReady)
        {
            _uninstallLoaded = true;
            await LoadUninstallListAsync();
        }
    }

    private async void OnSectionClick(object sender, RoutedEventArgs e)
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
        InstallPanel.Visibility = section == "Install" ? Visibility.Visible : Visibility.Collapsed;
        UninstallPanel.Visibility = section == "Uninstall" ? Visibility.Visible : Visibility.Collapsed;
        PreinstalledPanel.Visibility = section == "Preinstalled" ? Visibility.Visible : Visibility.Collapsed;
        StartupPanel.Visibility = section == "Startup" ? Visibility.Visible : Visibility.Collapsed;

        await LoadUninstallIfVisibleAsync();
    }

    private async void OnUninstallRefreshClick(object sender, RoutedEventArgs e) => await LoadUninstallListAsync();

    private void OnUninstallFilterChanged(object sender, TextChangedEventArgs e) => ApplyUninstallFilter();

    private void OnUninstallClearClick(object sender, RoutedEventArgs e)
    {
        foreach (UninstallRowViewModel row in _allUninstall)
        {
            row.IsSelected = false;
        }
    }

    private async Task LoadUninstallListAsync()
    {
        if (_working)
        {
            return;
        }

        if (!_wingetReady)
        {
            UninstallCountText.Text = "winget not available: cannot read the installed apps.";
            return;
        }

        SetUninstallBusy(true, "Reading installed apps...");
        try
        {
            List<WingetSearchResult> apps = await WingetService.GetAllInstalledAsync();

            // Keep the ticks the user already made.
            var ticked = new HashSet<string>(
                _allUninstall.Where(r => r.IsSelected).Select(r => r.Id),
                StringComparer.OrdinalIgnoreCase);

            _allUninstall.Clear();
            foreach (WingetSearchResult app in apps)
            {
                var row = new UninstallRowViewModel(app) { IsSelected = ticked.Contains(app.Id) };
                row.PropertyChanged += (_, _) => UpdateUninstallSummary();
                _allUninstall.Add(row);
            }

            ApplyUninstallFilter();

            if (apps.Count == 0)
            {
                UninstallCountText.Text = "Could not read the installed apps.";
            }
        }
        finally
        {
            SetUninstallBusy(false, "");
        }
    }

    private void ApplyUninstallFilter()
    {
        string filter = UninstallFilterBox.Text.Trim();

        _uninstallShown.Clear();
        foreach (UninstallRowViewModel row in _allUninstall)
        {
            if (filter.Length == 0
                || row.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                || row.Id.Contains(filter, StringComparison.CurrentCultureIgnoreCase))
            {
                _uninstallShown.Add(row);
            }
        }

        UpdateUninstallSummary();
    }

    private void UpdateUninstallSummary()
    {
        if (_allUninstall.Count == 0)
        {
            return;
        }

        int ticked = _allUninstall.Count(r => r.IsSelected);
        UninstallCountText.Text = _uninstallShown.Count + " of " + _allUninstall.Count + " apps"
            + (ticked > 0 ? "  ·  " + ticked + " selected" : "");
    }

    private void SetUninstallBusy(bool busy, string status)
    {
        UninstallRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UninstallButton.IsEnabled = !busy;
        UninstallClearButton.IsEnabled = !busy;
        UninstallRefreshButton.IsEnabled = !busy;
        UninstallStatusText.Text = status;
    }

    private async void OnUninstallClick(object sender, RoutedEventArgs e)
    {
        if (_working)
        {
            return;
        }

        if (!RequireWinget("Uninstalling requires winget."))
        {
            return;
        }

        List<UninstallRowViewModel> targets = _allUninstall.Where(r => r.IsSelected).ToList();
        if (targets.Count == 0)
        {
            ShowMessage("No app selected", "Tick the apps to uninstall.", Wpf.Ui.Controls.InfoBarSeverity.Informational);
            return;
        }

        string list = string.Join("\n", targets.Take(15).Select(t => "•  " + t.Name));
        if (targets.Count > 15)
        {
            list += "\n... and " + (targets.Count - 15);
        }

        bool confirmed = await Dialogs.ConfirmAsync(
            "Uninstall " + targets.Count + " apps from the PC?",
            list + "\n\nThis removes the programs from Windows. Some uninstallers may show their own window.",
            "Uninstall");

        if (!confirmed)
        {
            return;
        }

        var failed = new List<string>();
        var removed = new List<UninstallRowViewModel>();
        int done = 0;

        _working = true;
        InstallButton.IsEnabled = false;
        RemoveFromListButton.IsEnabled = false;
        try
        {
            foreach (UninstallRowViewModel row in targets)
            {
                row.Status = "Queued";
            }

            foreach (UninstallRowViewModel row in targets)
            {
                done++;
                SetUninstallBusy(true, "Uninstalling " + done + " of " + targets.Count + ": " + row.Name);
                row.Status = "Uninstalling...";

                (bool ok, string message) = await WingetService.UninstallAsync(row.Id);

                TweakEngine.LogChange("app." + row.Id, "uninstall", ok ? "succeeded" : "failed: " + message);

                if (ok)
                {
                    removed.Add(row);
                    _installedIds?.Remove(row.Id);
                    row.Status = "Uninstalled";
                }
                else
                {
                    failed.Add(row.Name);
                    row.Status = "Error: " + message;
                }
            }
        }
        finally
        {
            _working = false;
            InstallButton.IsEnabled = true;
            RemoveFromListButton.IsEnabled = true;
            SetUninstallBusy(false, "");
        }

        // Uninstalled apps leave the list; the ones that failed stay, with the reason.
        foreach (UninstallRowViewModel row in removed)
        {
            _allUninstall.Remove(row);
        }

        ApplyUninstallFilter();
        ApplyInstalledState();
        UpdateSelectionSummary();

        if (failed.Count == 0)
        {
            ShowMessage("Uninstallation completed", targets.Count + " apps.", Wpf.Ui.Controls.InfoBarSeverity.Success);
        }
        else
        {
            ShowMessage(
                "Uninstallation completed with errors",
                "Failed for: " + string.Join(", ", failed) + ". The reason is shown next to each app.",
                Wpf.Ui.Controls.InfoBarSeverity.Warning);
        }
    }

    // ---------- App updates ----------

    private async void OnCheckUpgradesClick(object sender, RoutedEventArgs e) => await LoadUpgradesAsync();

    private async Task LoadUpgradesAsync()
    {
        if (_working)
        {
            return;
        }

        if (!_wingetReady)
        {
            UpgradeStatusText.Text = "winget not available: cannot check for updates.";
            return;
        }

        SetUpgradeBusy(true);
        UpgradeStatusText.Text = "Checking for updates...";

        try
        {
            _allUpgrades = await WingetService.GetUpgradesAsync();
            LoadIgnoredUpdates();

            _upgrades.Clear();
            ShowUpgradeFailures(new List<UpgradeFailure>());
            foreach (WingetUpgrade upgrade in _allUpgrades.Where(u => !_ignoredUpdates.ContainsKey(u.Id)))
            {
                _upgrades.Add(new UpgradeRowViewModel(upgrade));
            }

            RebuildIgnoredList();
            UpdateUpgradeSummary();
        }
        catch (Exception ex)
        {
            UpgradeStatusText.Text = "Search failed: " + ex.Message;
        }
        finally
        {
            SetUpgradeBusy(false);
        }
    }

    private void UpdateUpgradeSummary()
    {
        int ignored = _allUpgrades.Count(u => _ignoredUpdates.ContainsKey(u.Id));
        string suffix = ignored == 0 ? "" : ignored == 1 ? " (1 ignored)" : " (" + ignored + " ignored)";

        UpgradeStatusText.Text = _upgrades.Count switch
        {
            0 => (ignored == 0 ? "All apps are up to date." : "No updates to install" + suffix + "."),
            1 => "1 update available" + suffix + ".",
            _ => _upgrades.Count + " updates available" + suffix + ".",
        };
    }

    // ---------- Ignored updates ----------

    private void LoadIgnoredUpdates()
    {
        Profile? profile = TryLoadProfile();
        _ignoredUpdates = profile is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(profile.IgnoredUpdates, StringComparer.OrdinalIgnoreCase);
    }

    private void SaveIgnoredUpdates()
    {
        try
        {
            Profile profile = TryLoadProfile() ?? new Profile();

            profile.IgnoredUpdates.Clear();
            foreach (KeyValuePair<string, string> entry in _ignoredUpdates)
            {
                profile.IgnoredUpdates[entry.Key] = entry.Value;
            }

            ProfileService.Save(profile);
        }
        catch (Exception ex)
        {
            ShowMessage("Saving failed", ex.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
    }

    /// <summary>Rebuilds the list of ignored apps, with the version available right now.</summary>
    private void RebuildIgnoredList()
    {
        _ignored.Clear();

        foreach (KeyValuePair<string, string> entry in _ignoredUpdates.OrderBy(e => e.Value, StringComparer.CurrentCultureIgnoreCase))
        {
            WingetUpgrade? upgrade = _allUpgrades.FirstOrDefault(u => string.Equals(u.Id, entry.Key, StringComparison.OrdinalIgnoreCase));

            _ignored.Add(new IgnoredUpdateRowViewModel(
                entry.Key,
                entry.Value,
                upgrade is null ? "No update at the moment" : "Available: " + upgrade.Current + "  →  " + upgrade.Available));
        }

        IgnoredExpander.Header = "Ignored updates (" + _ignored.Count + ")";
        IgnoredExpander.Visibility = _ignored.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnIgnoreSelectedClick(object sender, RoutedEventArgs e)
    {
        if (_working)
        {
            return;
        }

        List<UpgradeRowViewModel> targets = _upgrades.Where(u => u.IsSelected).ToList();
        if (targets.Count == 0)
        {
            ShowMessage("No app selected", "Tick the apps whose updates to ignore.", Wpf.Ui.Controls.InfoBarSeverity.Informational);
            return;
        }

        foreach (UpgradeRowViewModel row in targets)
        {
            _ignoredUpdates[row.Id] = row.Name;
            _upgrades.Remove(row);
        }

        SaveIgnoredUpdates();
        RebuildIgnoredList();
        UpdateUpgradeSummary();

        ShowMessage(
            targets.Count == 1 ? "Updates of " + targets[0].Name + " ignored" : "Updates ignored for " + targets.Count + " apps",
            "To re-enable them, open \"Ignored updates\" below the buttons.",
            Wpf.Ui.Controls.InfoBarSeverity.Success);
    }

    private void OnRestoreIgnoredClick(object sender, RoutedEventArgs e)
    {
        if (_working)
        {
            return;
        }

        List<IgnoredUpdateRowViewModel> targets = _ignored.Where(i => i.IsSelected).ToList();
        if (targets.Count == 0)
        {
            ShowMessage("No app selected", "Tick the apps to re-enable.", Wpf.Ui.Controls.InfoBarSeverity.Informational);
            return;
        }

        foreach (IgnoredUpdateRowViewModel row in targets)
        {
            _ignoredUpdates.Remove(row.Id);

            // If there is a pending update it goes straight back to the list, in alphabetical order.
            WingetUpgrade? upgrade = _allUpgrades.FirstOrDefault(u => string.Equals(u.Id, row.Id, StringComparison.OrdinalIgnoreCase));
            if (upgrade != null && !_upgrades.Any(u => string.Equals(u.Id, row.Id, StringComparison.OrdinalIgnoreCase)))
            {
                int index = 0;
                while (index < _upgrades.Count
                    && string.Compare(_upgrades[index].Name, upgrade.Name, StringComparison.CurrentCultureIgnoreCase) <= 0)
                {
                    index++;
                }

                _upgrades.Insert(index, new UpgradeRowViewModel(upgrade));
            }
        }

        SaveIgnoredUpdates();
        RebuildIgnoredList();
        UpdateUpgradeSummary();
    }

    private void OnSelectAllUpgradesClick(object sender, RoutedEventArgs e)
    {
        foreach (UpgradeRowViewModel row in _upgrades)
        {
            row.IsSelected = true;
        }
    }

    private void OnClearUpgradesClick(object sender, RoutedEventArgs e)
    {
        foreach (UpgradeRowViewModel row in _upgrades)
        {
            row.IsSelected = false;
        }
    }

    private async void OnUpgradeSelectedClick(object sender, RoutedEventArgs e) => await UpgradeSelectedAsync(interactive: false);

    private async void OnUpgradeInteractiveClick(object sender, RoutedEventArgs e) => await UpgradeSelectedAsync(interactive: true);

    /// <summary>Opens under the arrow the menu with the other ways to update.</summary>
    private void OnUpgradeMenuClick(object sender, RoutedEventArgs e)
    {
        if (UpgradeMenuButton.ContextMenu is ContextMenu menu)
        {
            menu.PlacementTarget = UpgradeMenuButton;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private void ShowUpgradeFailures(List<UpgradeFailure> failures)
    {
        // Always a new list: reassigning the same one would not update the page.
        UpgradeErrorsList.ItemsSource = failures.ToList();
        UpgradeErrorsPanel.Visibility = failures.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task UpgradeSelectedAsync(bool interactive)
    {
        if (_working)
        {
            return;
        }

        List<UpgradeRowViewModel> targets = _upgrades.Where(u => u.IsSelected).ToList();
        if (targets.Count == 0)
        {
            ShowMessage("No app selected", "Tick the apps to update, or press \"Select all\".", Wpf.Ui.Controls.InfoBarSeverity.Informational);
            return;
        }

        bool confirmed = await Dialogs.ConfirmAsync(
            targets.Count == 1 ? "Update " + targets[0].Name + "?" : "Update " + targets.Count + " apps?",
            interactive
                ? "For each app its installer window will open: follow the instructions to the end."
                : "Open apps may be closed during the update.",
            "Update");

        if (!confirmed)
        {
            return;
        }

        var failed = new List<UpgradeFailure>();
        var updated = new List<UpgradeRowViewModel>();
        int done = 0;

        _working = true;
        SetUpgradeBusy(true);
        ShowUpgradeFailures(new List<UpgradeFailure>());

        // One winget operation at a time: during updates nothing is installed or uninstalled.
        InstallButton.IsEnabled = false;
        UninstallButton.IsEnabled = false;

        try
        {
            foreach (UpgradeRowViewModel row in targets)
            {
                row.Status = "Queued";
            }

            foreach (UpgradeRowViewModel row in targets)
            {
                done++;
                UpgradeStatusText.Text = "Updating " + done + " of " + targets.Count + ": " + row.Name;
                row.Status = "Updating...";

                (bool ok, string message) = await WingetService.UpgradeAsync(row.Id, interactive);

                TweakEngine.LogChange(
                    "app." + row.Id,
                    "update " + row.Upgrade.Current + " -> " + row.Upgrade.Available,
                    ok ? "succeeded" : "failed: " + message);

                if (ok)
                {
                    _allUpgrades.RemoveAll(u => string.Equals(u.Id, row.Id, StringComparison.OrdinalIgnoreCase));
                    updated.Add(row);
                    row.Status = "Updated to " + row.Upgrade.Available;
                }
                else
                {
                    // The tile only holds the result; the full reason goes in the list below.
                    failed.Add(new UpgradeFailure(row.Name, message));
                    row.Status = "Failed (reason below)";
                    row.IsSelected = false;
                }
            }
        }
        finally
        {
            _working = false;
            SetUpgradeBusy(false);
            InstallButton.IsEnabled = true;
            UninstallButton.IsEnabled = true;
        }

        // Updated apps leave the list; failed ones stay with the reason.
        foreach (UpgradeRowViewModel row in updated)
        {
            _upgrades.Remove(row);
        }

        UpdateUpgradeSummary();
        ShowUpgradeFailures(failed);

        if (failed.Count == 0)
        {
            ShowMessage("Update completed", updated.Count + " apps updated.", Wpf.Ui.Controls.InfoBarSeverity.Success);
        }
        else
        {
            ShowMessage(
                "Update completed with errors",
                "Failed for: " + string.Join(", ", failed.Select(f => f.Name)) + ". The reason is written below the list of updates.",
                Wpf.Ui.Controls.InfoBarSeverity.Warning);
        }
    }

    private void SetUpgradeBusy(bool busy)
    {
        UpgradeRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UpgradeButton.IsEnabled = !busy;
        UpgradeMenuButton.IsEnabled = !busy;
        UpgradeSelectAllButton.IsEnabled = !busy;
        IgnoreUpgradesButton.IsEnabled = !busy;
        RestoreIgnoredButton.IsEnabled = !busy;
        UpgradeClearButton.IsEnabled = !busy;
        CheckUpgradesButton.IsEnabled = !busy;
    }

    // ---------- Preinstalled apps ----------

    private async Task LoadBloatAsync()
    {
        BloatStatusText.Text = "Reading installed apps...";
        BloatStatusText.Visibility = Visibility.Visible;
        _bloat.Clear();

        HashSet<string>? installed = await AppxService.GetInstalledAsync();
        if (installed is null)
        {
            BloatStatusText.Text = "Could not read the installed apps.";
            return;
        }

        foreach (BloatApp app in AppxService.Known.Where(a => installed.Contains(a.Package)))
        {
            _bloat.Add(new BloatRowViewModel(app));
        }

        if (_bloat.Count == 0)
        {
            BloatStatusText.Text = "None of the known preinstalled apps is present on this PC.";
        }
        else
        {
            BloatStatusText.Visibility = Visibility.Collapsed;
        }
    }

    private void OnSelectRecommendedBloatClick(object sender, RoutedEventArgs e)
    {
        foreach (BloatRowViewModel row in _bloat)
        {
            row.IsSelected = row.App.Recommended;
        }
    }

    private void OnClearBloatSelectionClick(object sender, RoutedEventArgs e)
    {
        foreach (BloatRowViewModel row in _bloat)
        {
            row.IsSelected = false;
        }
    }

    private async void OnRemoveBloatClick(object sender, RoutedEventArgs e)
    {
        List<BloatRowViewModel> targets = _bloat.Where(b => b.IsSelected).ToList();
        if (targets.Count == 0)
        {
            ShowMessage("No app selected", "Tick the apps to remove.", Wpf.Ui.Controls.InfoBarSeverity.Informational);
            return;
        }

        bool confirmed = await Dialogs.ConfirmAsync(
            "Remove " + targets.Count + " apps?",
            string.Join("\n", targets.Take(15).Select(t => "•  " + t.Name))
                + (targets.Count > 15 ? "\n... and " + (targets.Count - 15) : "")
                + "\n\nThey can be reinstalled from the Microsoft Store.",
            "Remove");

        if (!confirmed)
        {
            return;
        }

        RemoveBloatButton.IsEnabled = false;
        BloatRing.Visibility = Visibility.Visible;

        try
        {
            List<string> errors = await AppxService.RemoveAsync(targets.Select(t => t.Package));

            foreach (BloatRowViewModel row in targets)
            {
                TweakEngine.LogChange("preinstalledApp." + row.Package, "present", "removal requested");
            }

            await LoadBloatAsync();

            if (errors.Count == 0)
            {
                ShowMessage("Apps removed", targets.Count + " apps removed.", Wpf.Ui.Controls.InfoBarSeverity.Success);
            }
            else
            {
                ShowMessage("Some apps were not removed", string.Join("\n", errors), Wpf.Ui.Controls.InfoBarSeverity.Warning);
            }
        }
        catch (Exception ex)
        {
            ShowMessage("Removal failed", ex.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
        finally
        {
            BloatRing.Visibility = Visibility.Collapsed;
            RemoveBloatButton.IsEnabled = true;
        }
    }

    // ---------- Automatic start ----------

    private void LoadStartup()
    {
        _startup.Clear();

        try
        {
            foreach (StartupEntry entry in StartupService.List())
            {
                _startup.Add(new StartupRowViewModel(entry));
            }

            StartupStatusText.Text = _startup.Count == 0 ? "No startup program found." : "";
            StartupStatusText.Visibility = _startup.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            StartupStatusText.Text = "Could not read the startup programs: " + ex.Message;
            StartupStatusText.Visibility = Visibility.Visible;
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
