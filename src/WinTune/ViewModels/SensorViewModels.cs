using System.Collections.Generic;
using System.Windows.Media;
using System.Windows;
using WinTune.Core;

namespace WinTune.ViewModels;

/// <summary>Row of a sensor on the Sensors page. Values are updated in place.</summary>
public sealed class SensorRowViewModel : ObservableObject
{
    private string _valueText = "";
    private string _minText = "";
    private string _maxText = "";
    private Brush _valueBrush = Brushes.Gray;

    public SensorRowViewModel(SensorReading reading)
    {
        Name = reading.Name;
        KindLabel = SensorReading.KindLabel(reading.Kind);
        Update(reading);
    }

    public string Name { get; }

    public string KindLabel { get; }

    public string ValueText
    {
        get => _valueText;
        private set
        {
            if (value != _valueText)
            {
                _valueText = value;
                Raise();
            }
        }
    }

    public string MinText
    {
        get => _minText;
        private set
        {
            if (value != _minText)
            {
                _minText = value;
                Raise();
            }
        }
    }

    public string MaxText
    {
        get => _maxText;
        private set
        {
            if (value != _maxText)
            {
                _maxText = value;
                Raise();
            }
        }
    }

    public Brush ValueBrush
    {
        get => _valueBrush;
        private set
        {
            if (!ReferenceEquals(value, _valueBrush))
            {
                _valueBrush = value;
                Raise();
            }
        }
    }

    public void Update(SensorReading reading)
    {
        ValueText = SensorReading.Format(reading.Kind, reading.Value);
        MinText = reading.Min is float min ? SensorReading.Format(reading.Kind, min) : "-";
        MaxText = reading.Max is float max ? SensorReading.Format(reading.Kind, max) : "-";

        string brushKey = "TextFillColorPrimaryBrush";
        if (reading.Kind == SensorKind.Temperature)
        {
            if (reading.Value >= 90)
            {
                brushKey = "SystemFillColorCriticalBrush";
            }
            else if (reading.Value >= 80)
            {
                brushKey = "SystemFillColorCautionBrush";
            }
        }

        ValueBrush = Application.Current?.TryFindResource(brushKey) as Brush ?? Brushes.Gray;
    }
}

/// <summary>A component with its rows, on the Sensors page.</summary>
public sealed class SensorGroupViewModel
{
    public SensorGroupViewModel(string title, string typeLabel, List<SensorRowViewModel> rows)
    {
        Title = title;
        TypeLabel = typeLabel;
        Rows = rows;
    }

    public string Title { get; }

    public string TypeLabel { get; }

    public List<SensorRowViewModel> Rows { get; }
}

/// <summary>Row of the comparison with a snapshot.</summary>
public sealed class ComparisonRowViewModel
{
    public ComparisonRowViewModel(SnapshotComparison row)
    {
        Hardware = row.Hardware;
        Name = row.Name + "  (" + SensorReading.KindLabel(row.Kind).ToLowerInvariant() + ")";
        ThenText = SensorReading.Format(row.Kind, row.Then);
        NowText = SensorReading.Format(row.Kind, row.Now);

        float delta = row.Delta;
        string sign = delta > 0 ? "+" : "";
        DeltaText = sign + SensorReading.Format(row.Kind, delta);

        // Hotter or more power use is worse, less is better; for the other values the color stays neutral.
        string brushKey = "TextFillColorSecondaryBrush";
        bool lowerIsBetter = row.Kind is SensorKind.Temperature or SensorKind.Power;
        if (lowerIsBetter && System.Math.Abs(delta) >= 1)
        {
            brushKey = delta > 0 ? "SystemFillColorCautionBrush" : "SystemFillColorSuccessBrush";
        }

        DeltaBrush = Application.Current?.TryFindResource(brushKey) as Brush ?? Brushes.Gray;
    }

    public string Hardware { get; }

    public string Name { get; }

    public string ThenText { get; }

    public string NowText { get; }

    public string DeltaText { get; }

    public Brush DeltaBrush { get; }
}
