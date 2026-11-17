using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WinTune.Core;
using WinTune.ViewModels;

namespace WinTune.Views.Pages;

public partial class SensorsPage : Page
{
    private const string DriverId = "namazso.PawnIO";

    private readonly ObservableCollection<SensorGroupViewModel> _groups = new();
    private readonly ObservableCollection<ComparisonRowViewModel> _comparison = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };

    private List<HardwareReading> _data = new();
    private string _signature = "";
    private bool _reading;
    private bool _busy;
    private readonly bool _ready;

    public SensorsPage()
    {
        InitializeComponent();

        GroupsList.ItemsSource = _groups;
        CompareList.ItemsSource = _comparison;
        _timer.Tick += async (_, _) => await ReadAsync();

        RefreshSnapshots();
        _ready = true;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _timer.Start();
        await ReadAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready)
        {
            Apply();
        }
    }

    // ---------- Reading ----------

    private async Task ReadAsync()
    {
        // With the window minimized the sensors are not read: nobody is looking at them.
        if (_reading || _busy || Window.GetWindow(this)?.WindowState == WindowState.Minimized)
        {
            return;
        }

        _reading = true;

        try
        {
            _data = await Task.Run(SensorService.Read);
            Apply();
            UpdateDriverPanel();
        }
        catch (Exception ex)
        {
            ShowMessage("Reading the sensors failed", ex.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
        finally
        {
            LoadRing.Visibility = Visibility.Collapsed;
            _reading = false;
        }
    }

    private SensorKind? SelectedKind()
    {
        if (FilterBox.SelectedItem is ComboBoxItem { Tag: string tag }
            && Enum.TryParse(tag, out SensorKind kind))
        {
            // "Memory" in the filter also includes the values in MB (graphics card memory).
            return kind;
        }

        return null;
    }

    private static bool Matches(SensorKind? filter, SensorKind kind) =>
        filter is null
        || kind == filter
        || (filter == SensorKind.Data && kind == SensorKind.SmallData);

    private void Apply()
    {
        SensorKind? filter = SelectedKind();

        var shown = _data
            .Select(h => (Hardware: h, Sensors: h.Sensors.Where(s => Matches(filter, s.Kind)).ToList()))
            .Where(x => x.Sensors.Count > 0)
            .ToList();

        string signature = string.Join("|", shown.SelectMany(x => x.Sensors.Select(s => s.Key)));

        if (signature != _signature || _groups.Count != shown.Count)
        {
            _groups.Clear();
            foreach (var (hardware, sensors) in shown)
            {
                _groups.Add(new SensorGroupViewModel(
                    hardware.Name,
                    hardware.TypeLabel,
                    sensors.Select(s => new SensorRowViewModel(s)).ToList()));
            }

            _signature = signature;
            return;
        }

        // Same sensors as before: only the values are updated, so the list does not flicker.
        for (int i = 0; i < shown.Count; i++)
        {
            List<SensorReading> sensors = shown[i].Sensors;
            List<SensorRowViewModel> rows = _groups[i].Rows;

            for (int j = 0; j < sensors.Count && j < rows.Count; j++)
            {
                rows[j].Update(sensors[j]);
            }
        }
    }

    // ---------- Driver ----------

    private void UpdateDriverPanel()
    {
        bool missing = _data.Count == 0 || SensorService.FindCpuTemperature(_data) is null;

        DriverPanel.Visibility = missing ? Visibility.Visible : Visibility.Collapsed;
        if (!missing)
        {
            return;
        }

        string? error = SensorService.OpenError;
        DriverBar.Message = _data.Count == 0
            ? "No sensor could be read." + (error is null ? "" : " " + error)
            : "The CPU temperature is not available. CPU, RAM and motherboard sensors need the PawnIO driver, which is a signed driver also used by LibreHardwareMonitor. The other sensors work without it.";
    }

    private async void OnInstallDriverClick(object sender, RoutedEventArgs e)
    {
        bool confirmed = await Dialogs.ConfirmAsync(
            "Install the PawnIO driver",
            "PawnIO is a signed kernel driver that lets programs read CPU, RAM and motherboard sensors.\n\n"
            + "WinTune installs it with winget (" + DriverId + "). Some anti-cheat systems may not like drivers of this kind: you can uninstall it from Windows settings if you do not need the sensors.",
            "Install");

        if (!confirmed)
        {
            return;
        }

        _busy = true;
        InstallDriverButton.IsEnabled = false;
        LoadRing.Visibility = Visibility.Visible;

        try
        {
            (bool ok, string message) = await WingetService.InstallAsync(DriverId);

            if (ok)
            {
                await Task.Run(SensorService.Reset);
                ShowMessage("PawnIO installed", "Sensors reloaded.", Wpf.Ui.Controls.InfoBarSeverity.Success);
            }
            else
            {
                ShowMessage("Installing PawnIO failed", message, Wpf.Ui.Controls.InfoBarSeverity.Error);
            }
        }
        finally
        {
            _busy = false;
            InstallDriverButton.IsEnabled = true;
        }

        await ReadAsync();
    }

    private async void OnReloadClick(object sender, RoutedEventArgs e)
    {
        LoadRing.Visibility = Visibility.Visible;
        await Task.Run(SensorService.Reset);
        await ReadAsync();
    }

    // ---------- Snapshots ----------

    private void RefreshSnapshots(string? selectPath = null)
    {
        List<SensorSnapshot> snapshots = SnapshotService.List();
        SnapshotBox.ItemsSource = snapshots;

        SnapshotBox.SelectedItem = snapshots.FirstOrDefault(s => s.FilePath == selectPath)
            ?? snapshots.FirstOrDefault();
    }

    private async void OnSaveSnapshotClick(object sender, RoutedEventArgs e)
    {
        if (_data.Count == 0)
        {
            ShowMessage("Nothing to save", "No sensor values have been read yet.", Wpf.Ui.Controls.InfoBarSeverity.Informational);
            return;
        }

        string? name = await Dialogs.PromptAsync(
            "Save snapshot",
            "Name of the snapshot (for example: before the changes).",
            "Before changes");

        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            SensorSnapshot snapshot = SnapshotService.Save(name.Trim(), _data);
            RefreshSnapshots(snapshot.FilePath);
            ShowMessage("Snapshot saved", snapshot.Label, Wpf.Ui.Controls.InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowMessage("Saving the snapshot failed", ex.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
    }

    private void OnCompareClick(object sender, RoutedEventArgs e)
    {
        if (SnapshotBox.SelectedItem is not SensorSnapshot snapshot)
        {
            ShowMessage("No snapshot selected", "Save a snapshot first, then pick it from the list.", Wpf.Ui.Controls.InfoBarSeverity.Informational);
            return;
        }

        _comparison.Clear();
        foreach (SnapshotComparison row in SnapshotService.Compare(snapshot, _data))
        {
            _comparison.Add(new ComparisonRowViewModel(row));
        }

        CompareTitle.Text = "Compared with: " + snapshot.Label;
        CompareHeader.Visibility = _comparison.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (_comparison.Count == 0)
        {
            ShowMessage("Nothing to compare", "The snapshot and the current readings have no sensor in common.", Wpf.Ui.Controls.InfoBarSeverity.Informational);
        }
    }

    private async void OnDeleteSnapshotClick(object sender, RoutedEventArgs e)
    {
        if (SnapshotBox.SelectedItem is not SensorSnapshot snapshot)
        {
            return;
        }

        if (!await Dialogs.ConfirmAsync("Delete snapshot", "Delete \"" + snapshot.Name + "\"?", "Delete"))
        {
            return;
        }

        try
        {
            SnapshotService.Delete(snapshot);
            _comparison.Clear();
            CompareHeader.Visibility = Visibility.Collapsed;
            RefreshSnapshots();
        }
        catch (Exception ex)
        {
            ShowMessage("Deleting the snapshot failed", ex.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
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
