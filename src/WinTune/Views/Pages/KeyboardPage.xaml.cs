using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using WinTune.Core;
using WinTune.ViewModels;

namespace WinTune.Views.Pages;

public partial class KeyboardPage : Page
{
    private readonly ObservableCollection<KeyMappingRowViewModel> _rows = new();
    private bool _loading;

    public KeyboardPage()
    {
        InitializeComponent();
        MappingList.ItemsSource = _rows;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loading = true;
        AppSettings settings = AppSettings.Current;

        EnabledSwitch.IsChecked = settings.KeyboardEnabled;
        TriggerBox.SelectedIndex = settings.KeyboardTrigger == KeyboardService.RightCtrl ? 1 : 0;

        if (_rows.Count == 0)
        {
            LoadRows(settings.KeyboardMappings);
        }

        _loading = false;
    }

    private void LoadRows(System.Collections.Generic.IEnumerable<KeyMapping> mappings)
    {
        _rows.Clear();
        foreach (KeyMapping mapping in mappings)
        {
            _rows.Add(new KeyMappingRowViewModel(mapping));
        }
    }

    private void OnEnabledClick(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.KeyboardEnabled = EnabledSwitch.IsChecked == true;
        SaveAndApply(AppSettings.Current.KeyboardEnabled
            ? "Shortcuts turned on. Hold the trigger key and press a key of the table."
            : "Shortcuts turned off.");
    }

    private void OnTriggerChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || TriggerBox.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        AppSettings.Current.KeyboardTrigger = (string)item.Tag;
        SaveAndApply("Trigger key changed.");
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var mappings = _rows.Select(r => r.ToMapping()).Where(m => m.Key.Length > 0 || m.Normal.Length > 0).ToList();

        foreach (KeyMapping mapping in mappings)
        {
            if (mapping.Key.Length != 1 || KeyboardService.ResolveVk(mapping.Key) == 0)
            {
                ShowMessage("Invalid key", "\"" + mapping.Key + "\" is not a single key of the keyboard.", Wpf.Ui.Controls.InfoBarSeverity.Warning);
                return;
            }

            if (mapping.Normal.Length == 0)
            {
                ShowMessage("Missing letter", "The key \"" + mapping.Key + "\" has no letter to type.", Wpf.Ui.Controls.InfoBarSeverity.Warning);
                return;
            }
        }

        string? duplicate = mappings.GroupBy(m => m.Key, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1)?.Key;
        if (duplicate is not null)
        {
            ShowMessage("Repeated key", "The key \"" + duplicate + "\" appears more than once.", Wpf.Ui.Controls.InfoBarSeverity.Warning);
            return;
        }

        AppSettings.Current.KeyboardMappings = mappings;
        SaveAndApply("Shortcuts saved.");
    }

    private void OnAddRowClick(object sender, RoutedEventArgs e) => _rows.Add(new KeyMappingRowViewModel(new KeyMapping()));

    private void OnRemoveRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is KeyMappingRowViewModel row)
        {
            _rows.Remove(row);
        }
    }

    private void OnDefaultsClick(object sender, RoutedEventArgs e)
    {
        LoadRows(KeyMapping.Defaults());
        MessageBar.IsOpen = false;
        ShowMessage("Default table loaded", "Press \"Save shortcuts\" to use it.", Wpf.Ui.Controls.InfoBarSeverity.Informational);
    }

    private void SaveAndApply(string successMessage)
    {
        try
        {
            AppSettings.Current.Save();
            KeyboardService.Apply();
            ShowMessage(successMessage, "", Wpf.Ui.Controls.InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowMessage("Could not apply", ex.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
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
