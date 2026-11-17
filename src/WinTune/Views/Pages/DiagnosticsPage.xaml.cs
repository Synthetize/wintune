using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using WinTune.Core;
using WinTune.ViewModels;

namespace WinTune.Views.Pages;

public partial class DiagnosticsPage : Page
{
    private const int MaxRows = 400;

    private List<SystemEvent> _events = new();
    private string? _category;
    private bool _loaded;
    private bool _loading;

    public DiagnosticsPage()
    {
        InitializeComponent();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshMinidumps();

        if (!_loaded)
        {
            _loaded = true;
            await LoadEventsAsync();
        }
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        RefreshMinidumps();
        await LoadEventsAsync();
    }

    private async void OnPeriodChanged(object sender, SelectionChangedEventArgs e)
    {
        // The event also arrives while the page is being created: nothing is loaded at that moment.
        if (!_loaded)
        {
            return;
        }

        await LoadEventsAsync();
    }

    // ---------- Events ----------

    private int SelectedDays =>
        int.TryParse((PeriodBox.SelectedItem as ComboBoxItem)?.Tag as string, out int days) ? days : 30;

    private async Task LoadEventsAsync()
    {
        if (_loading)
        {
            return;
        }

        _loading = true;
        LoadRing.Visibility = Visibility.Visible;
        RefreshButton.IsEnabled = false;
        EventsStatusText.Text = "Reading events...";
        EventsStatusText.Visibility = Visibility.Visible;

        try
        {
            _events = await EventLogService.ReadAsync(SelectedDays);
            BuildSummary();
            BuildFilters();
            ShowEvents();
            MessageBar.IsOpen = false;
        }
        catch (Exception ex)
        {
            ShowMessage("Reading events failed", ex.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
        finally
        {
            LoadRing.Visibility = Visibility.Collapsed;
            RefreshButton.IsEnabled = true;
            _loading = false;
        }
    }

    private int CountOf(Func<SystemEvent, bool> filter) => _events.Where(filter).Sum(e => e.Count);

    /// <summary>Highlighted numbers: the problems that matter most.</summary>
    private void BuildSummary()
    {
        int unexpected = CountOf(e => e.Source == "Kernel-Power 41" || e.Source == "EventLog 6008");
        int blueScreens = CountOf(e => e.Source == "WER-SystemErrorReporting 1001");
        int hardware = CountOf(e => e.Category == EventLogService.Hardware);
        int video = CountOf(e => e.Category == EventLogService.GraphicsCard);
        int disks = CountOf(e => e.Category == EventLogService.Disks);
        int apps = CountOf(e => e.Category == EventLogService.Applications);

        SummaryList.ItemsSource = new List<SummaryTile>
        {
            new(unexpected.ToString(), "Unexpected shutdowns"),
            new(blueScreens.ToString(), "Blue screens"),
            new(hardware.ToString(), "Hardware errors"),
            new(video.ToString(), "Graphics card errors"),
            new(disks.ToString(), "Disk errors"),
            new(apps.ToString(), "Program crashes"),
        };
    }

    /// <summary>One button for each category that has events, plus "All".</summary>
    private void BuildFilters()
    {
        FilterPanel.Children.Clear();

        List<string> present = EventLogService.Categories.Where(c => _events.Any(e => e.Category == c)).ToList();
        if (_category != null && !present.Contains(_category))
        {
            _category = null;
        }

        AddFilter("All (" + _events.Count + ")", null);
        foreach (string category in present)
        {
            AddFilter(category + " (" + _events.Count(e => e.Category == category) + ")", category);
        }
    }

    private void AddFilter(string label, string? category)
    {
        var button = new ToggleButton
        {
            Content = label,
            Tag = category,
            IsChecked = category == _category,
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

        _category = clicked.Tag as string;
        ShowEvents();
    }

    private void ShowEvents()
    {
        List<SystemEvent> filtered = _category is null
            ? _events
            : _events.Where(e => e.Category == _category).ToList();

        EventsList.ItemsSource = filtered.Take(MaxRows).Select(e => new EventRowViewModel(e)).ToList();

        if (filtered.Count == 0)
        {
            EventsStatusText.Text = _events.Count == 0
                ? "No crash or instability events in the chosen period."
                : "No events in this category.";
        }
        else if (filtered.Count > MaxRows)
        {
            EventsStatusText.Text = "Showing the " + MaxRows + " most recent events out of " + filtered.Count + ". Hover over a row for the full message.";
        }
        else
        {
            EventsStatusText.Text = filtered.Count + " events, newest first. Hover over a row for the full message.";
        }
    }

    // ---------- Blue screens and Windows tools ----------

    private void RefreshMinidumps()
    {
        (int count, DateTime? latest, string _) = EventLogService.GetMinidumps();

        if (count == 0)
        {
            MinidumpText.Text = "No blue screen dump files found.";
            MinidumpButton.IsEnabled = false;
        }
        else
        {
            MinidumpText.Text = count + " blue screen dump files"
                + (latest.HasValue ? ", the latest from " + latest.Value.ToString("dd/MM/yyyy HH:mm") + "." : ".");
            MinidumpButton.IsEnabled = true;
        }
    }

    private void OnOpenMinidumpClick(object sender, RoutedEventArgs e)
    {
        (int _, DateTime? _, string folder) = EventLogService.GetMinidumps();
        Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "\"" + folder + "\"", false);
    }

    private void OnOpenReliabilityClick(object sender, RoutedEventArgs e) =>
        Start(ProcessRunner.SystemTool("perfmon.exe"), "/rel", true);

    private void OnOpenEventViewerClick(object sender, RoutedEventArgs e) =>
        Start(ProcessRunner.SystemTool("eventvwr.msc"), "", true);

    // ---------- System health ----------

    private void OnRunSfcClick(object sender, RoutedEventArgs e) =>
        Start(ProcessRunner.SystemTool("cmd.exe"), "/k sfc /scannow", true);

    private void OnRunDismClick(object sender, RoutedEventArgs e) =>
        Start(ProcessRunner.SystemTool("cmd.exe"), "/k DISM /Online /Cleanup-Image /RestoreHealth", true);

    // ---------- Common ----------

    private void Start(string fileName, string arguments, bool useShell)
    {
        try
        {
            Process.Start(new ProcessStartInfo(fileName, arguments) { UseShellExecute = useShell })?.Dispose();
        }
        catch (Exception ex)
        {
            ShowMessage("Could not start the tool", ex.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
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
