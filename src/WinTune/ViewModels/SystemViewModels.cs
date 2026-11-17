using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using WinTune.Core;

namespace WinTune.ViewModels;

/// <summary>Tile of a drive in the "Storage" section of the Overview.</summary>
public sealed class DriveTileViewModel
{
    // As in File Explorer: the bar turns red when little space is left.
    private const double RedBelowFreePercent = 10;
    private const double AmberBelowFreePercent = 20;

    public DriveTileViewModel(string name, string label, long freeBytes, long totalBytes)
    {
        Name = name;
        Label = label.Length > 0 ? label : "Local disk";
        UsedPercent = totalBytes > 0 ? 100.0 * (totalBytes - freeBytes) / totalBytes : 0;

        double freePercent = 100 - UsedPercent;
        string brushKey = freePercent < RedBelowFreePercent
            ? "SystemFillColorCriticalBrush"
            : freePercent < AmberBelowFreePercent ? "SystemFillColorCautionBrush" : "AccentFillColorDefaultBrush";
        BarBrush = Application.Current?.TryFindResource(brushKey) as Brush ?? Brushes.SteelBlue;

        FreeText = CleanupService.FormatSize(freeBytes) + " free of " + CleanupService.FormatSize(totalBytes);
        LowText = freePercent < RedBelowFreePercent ? "Almost full" : "";
    }

    public string Name { get; }

    public string Label { get; }

    public double UsedPercent { get; }

    public Brush BarBrush { get; }

    public string FreeText { get; }

    public string LowText { get; }

    public Visibility LowVisibility => LowText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>Tile of an Overview check.</summary>
public sealed class CheckTileViewModel
{
    public CheckTileViewModel(CheckResult result, string actionText = "", object? tag = null, Type? navigateTo = null)
    {
        NavigateTo = navigateTo;
        Title = result.Title;
        Value = result.Value;
        Detail = result.Detail;
        ActionText = actionText;
        Tag = tag;

        string brushKey;
        switch (result.Level)
        {
            case CheckLevel.Ok:
                StatusText = "OK";
                brushKey = "SystemFillColorSuccessBrush";
                break;
            case CheckLevel.Warning:
                StatusText = "Warning";
                brushKey = "SystemFillColorCautionBrush";
                break;
            default:
                StatusText = "Info";
                brushKey = "TextFillColorSecondaryBrush";
                break;
        }

        StatusBrush = Application.Current?.TryFindResource(brushKey) as Brush ?? Brushes.Gray;
    }

    public string Title { get; }

    public string StatusText { get; }

    public Brush StatusBrush { get; }

    public string Value { get; }

    public string Detail { get; }

    /// <summary>Text of the action button (e.g. "Set 144 Hz"); empty if the check has no actions.</summary>
    public string ActionText { get; }

    public Visibility ActionVisibility => ActionText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Page to open when the tile is clicked (for example the app updates); null if the tile is not clickable.</summary>
    public Type? NavigateTo { get; }

    public Cursor TileCursor => NavigateTo is null ? Cursors.Arrow : Cursors.Hand;

    /// <summary>Data tied to the action, for example the display whose refresh rate is changed.</summary>
    public object? Tag { get; }
}

/// <summary>Row of the manual reminders.</summary>
public sealed class ReminderRowViewModel : ObservableObject
{
    private bool _isDone;

    public ReminderRowViewModel(string text, bool isDone)
    {
        Text = text;
        _isDone = isDone;
    }

    public string Text { get; }

    public bool IsDone
    {
        get => _isDone;
        set
        {
            if (value != _isDone)
            {
                _isDone = value;
                Raise();
            }
        }
    }
}

/// <summary>Row of the event list in Diagnostics.</summary>
public sealed class EventRowViewModel
{
    public EventRowViewModel(SystemEvent systemEvent)
    {
        Event = systemEvent;
    }

    public SystemEvent Event { get; }

    public string TimeText => Event.Time.ToString("dd/MM/yyyy HH:mm");

    public string Category => Event.Category;

    public string SourceText => Event.Count > 1 ? Event.Source + "  ×" + Event.Count : Event.Source;

    public string Explanation => Event.Explanation;

    /// <summary>Full text shown on mouse hover.</summary>
    public string Details
    {
        get
        {
            string message = Event.Message.Length > 700 ? Event.Message.Substring(0, 700) + "..." : Event.Message;
            return message.Length > 0 ? Event.Explanation + "\n\n" + message : Event.Explanation;
        }
    }
}

/// <summary>Highlighted number in the Diagnostics summary.</summary>
public sealed record SummaryTile(string Value, string Label);

/// <summary>Row of the disk cleanup.</summary>
public sealed class CleanupRowViewModel : ObservableObject
{
    private bool _isSelected;
    private string _sizeText = "";

    public CleanupRowViewModel(CleanupTarget target, bool isSelected)
    {
        Target = target;
        _isSelected = isSelected;
    }

    public CleanupTarget Target { get; }

    public string Name => Target.Name;

    public string Description => Target.Description;

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

    public string SizeText
    {
        get => _sizeText;
        set
        {
            if (value != _sizeText)
            {
                _sizeText = value;
                Raise();
            }
        }
    }
}
