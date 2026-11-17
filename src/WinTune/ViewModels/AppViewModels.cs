using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using WinTune.Core;

namespace WinTune.ViewModels;

/// <summary>Base with property change notification.</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>Row of the list of apps to install.</summary>
public sealed class AppRowViewModel : ObservableObject
{
    private bool _isSelected;
    private bool _isInstalled;
    private string _status = "";

    public AppRowViewModel(string id, string name, bool isSelected, string category)
    {
        Id = id;
        Name = name;
        Category = category;
        _isSelected = isSelected;
    }

    public string Id { get; }

    public string Name { get; }

    public string Category { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (value != _isSelected)
            {
                _isSelected = value;
                Raise();
            }
        }
    }

    public bool IsInstalled
    {
        get => _isInstalled;
        set
        {
            if (value != _isInstalled)
            {
                _isInstalled = value;
                Raise();
            }
        }
    }

    public string Status
    {
        get => _status;
        set
        {
            if (value != _status)
            {
                _status = value;
                Raise();
                Raise(nameof(ShortStatus));
            }
        }
    }

    /// <summary>State in one word for the compact rows; the full one stays in the tooltip.</summary>
    public string ShortStatus
    {
        get
        {
            if (_status.StartsWith("Error", StringComparison.Ordinal))
            {
                return "Error";
            }

            if (_status.EndsWith("...", StringComparison.Ordinal))
            {
                return "Working";
            }

            return _status == "Not installed" ? "" : _status;
        }
    }
}

/// <summary>Row of the results list (search or detected apps), with its checkbox.</summary>
public sealed class ResultRowViewModel : ObservableObject
{
    private bool _isSelected;

    public ResultRowViewModel(WingetSearchResult result)
    {
        Result = result;
    }

    public WingetSearchResult Result { get; }

    public string Name => Result.Name;

    public string Detail => Result.Detail;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (value != _isSelected)
            {
                _isSelected = value;
                Raise();
            }
        }
    }
}

/// <summary>A category of the app list, with its apps.</summary>
public sealed class AppCategoryViewModel : ObservableObject
{
    public AppCategoryViewModel(string name)
    {
        Name = name;
        Apps.CollectionChanged += OnAppsChanged;
    }

    public string Name { get; }

    public ObservableCollection<AppRowViewModel> Apps { get; } = new();

    public string Header => Name + " (" + Apps.Count + ")";

    /// <summary>Header checkbox: true if all selected, false if none, null if only some.</summary>
    public bool? AllSelected
    {
        get
        {
            int selected = Apps.Count(a => a.IsSelected);
            if (selected == 0)
            {
                return false;
            }

            return selected == Apps.Count ? true : null;
        }

        set
        {
            bool target = value == true;
            foreach (AppRowViewModel row in Apps.ToList())
            {
                row.IsSelected = target;
            }

            Raise();
        }
    }

    private void OnAppsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
        {
            foreach (AppRowViewModel row in e.OldItems)
            {
                row.PropertyChanged -= OnRowChanged;
            }
        }

        if (e.NewItems != null)
        {
            foreach (AppRowViewModel row in e.NewItems)
            {
                row.PropertyChanged += OnRowChanged;
            }
        }

        Raise(nameof(Header));
        Raise(nameof(AllSelected));
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppRowViewModel.IsSelected))
        {
            Raise(nameof(AllSelected));
        }
    }
}

/// <summary>Row of the list of preinstalled apps.</summary>
public sealed class BloatRowViewModel : ObservableObject
{
    private bool _isSelected;

    public BloatRowViewModel(BloatApp app)
    {
        App = app;
    }

    public BloatApp App { get; }

    public string Name => App.Name;

    public string Package => App.Package;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (value != _isSelected)
            {
                _isSelected = value;
                Raise();
            }
        }
    }
}

/// <summary>Tile of an app whose updates are ignored.</summary>
public sealed class IgnoredUpdateRowViewModel : ObservableObject
{
    private bool _isSelected;

    public IgnoredUpdateRowViewModel(string id, string name, string detail)
    {
        Id = id;
        Name = name;
        Detail = detail;
    }

    public string Id { get; }

    public string Name { get; }

    /// <summary>Version available right now, if any.</summary>
    public string Detail { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (value != _isSelected)
            {
                _isSelected = value;
                Raise();
            }
        }
    }
}

/// <summary>A failed update, with the full reason.</summary>
public sealed record UpgradeFailure(string Name, string Message);

/// <summary>Tile of an app with an update available.</summary>
public sealed class UpgradeRowViewModel : ObservableObject
{
    private bool _isSelected;
    private string _status = "";

    public UpgradeRowViewModel(WingetUpgrade upgrade)
    {
        Upgrade = upgrade;
    }

    public WingetUpgrade Upgrade { get; }

    public string Name => Upgrade.Name;

    public string Id => Upgrade.Id;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (value != _isSelected)
            {
                _isSelected = value;
                Raise();
            }
        }
    }

    /// <summary>Result of the update; empty until it is started.</summary>
    public string Status
    {
        get => _status;
        set
        {
            if (value != _status)
            {
                _status = value;
                Raise();
                Raise(nameof(Detail));
            }
        }
    }

    /// <summary>Second line of the tile: the versions, or the result of the update.</summary>
    public string Detail => _status.Length > 0 ? _status : Upgrade.Current + "  →  " + Upgrade.Available;
}

/// <summary>Row of the list of programs that start automatically.</summary>
public sealed class StartupRowViewModel : ObservableObject
{
    private readonly StartupEntry _entry;
    private bool _isEnabled;

    public StartupRowViewModel(StartupEntry entry)
    {
        _entry = entry;
        _isEnabled = StartupService.IsEnabled(entry);
        Icon = IconUtil.ForCommand(entry.Command);
    }

    /// <summary>Icon of the program, or null if it cannot be obtained.</summary>
    public ImageSource? Icon { get; }

    /// <summary>Reports an error during a change to the page.</summary>
    public static event Action<string>? ErrorOccurred;

    public string Name => _entry.Name;

    public string Command => _entry.Command;

    public string Source => _entry.Source;

    public string StateText => _isEnabled ? "Enabled" : "Disabled";

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (value == _isEnabled)
            {
                return;
            }

            try
            {
                StartupService.SetEnabled(_entry, value);
                TweakEngine.LogChange("startup." + _entry.Name, _isEnabled ? "true" : "false", value ? "true" : "false");
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(Name + ": " + ex.Message);
            }

            try
            {
                _isEnabled = StartupService.IsEnabled(_entry);
            }
            catch
            {
                // If reading again fails the previous state stays.
            }

            Raise();
            Raise(nameof(StateText));
        }
    }
}
