using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using WinTune.Core;

namespace WinTune.ViewModels;

/// <summary>A category with its optimizations, for the Optimizations page.</summary>
public sealed class TweakGroup
{
    public TweakGroup(string name, List<TweakViewModel> items)
    {
        Name = name;
        Items = items;
    }

    public string Name { get; }

    public List<TweakViewModel> Items { get; }
}

/// <summary>Common base of the rows of the Optimizations page.</summary>
public abstract class TweakViewModel : INotifyPropertyChanged
{
    protected TweakViewModel(Tweak tweak)
    {
        Tweak = tweak;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Reports an error during a change to the page.</summary>
    public static event Action<string>? ErrorOccurred;

    /// <summary>
    /// Signals that an entry has been changed: some entries touch the same settings as others,
    /// so the page reads the state of all of them again.
    /// </summary>
    public static event Action? Changed;

    public Tweak Tweak { get; }

    public string Title => Tweak.Title;

    public string Description
    {
        get
        {
            if (Tweak.Recommended is null)
            {
                return Tweak.Description;
            }

            return Tweak.Description + " Recommended: " + Tweak.DisplayValue(Tweak.Recommended).ToLowerInvariant() + ".";
        }
    }

    public string Badge =>
        Tweak.NeedsRestart ? "Restart required"
        : Tweak.NeedsExplorerRestart ? "File Explorer restart required"
        : "";

    public Visibility BadgeVisibility => Badge.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Tooltip text that appears when hovering over the tile.</summary>
    public string Help => Badge.Length > 0 ? Description + " " + Badge + "." : Description;

    /// <summary>Reads the state from the system again.</summary>
    public abstract void Refresh();

    public static TweakViewModel Create(Tweak tweak) => tweak switch
    {
        ToggleTweak toggle => new ToggleTweakViewModel(toggle),
        ChoiceTweak choice => new ChoiceTweakViewModel(choice),
        _ => throw new NotSupportedException("Unsupported tweak type: " + tweak.GetType().Name),
    };

    protected void Raise([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected void ReportError(Exception exception) =>
        ErrorOccurred?.Invoke(Title + ": " + exception.Message);

    protected static void ReportChanged() => Changed?.Invoke();
}

/// <summary>Row with a switch.</summary>
public sealed class ToggleTweakViewModel : TweakViewModel
{
    private readonly ToggleTweak _tweak;
    private bool _isOn;
    private bool _known;

    public ToggleTweakViewModel(ToggleTweak tweak)
        : base(tweak)
    {
        _tweak = tweak;
    }

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (value == _isOn && _known)
            {
                return;
            }

            // Delicate entries ask for confirmation; if the user backs out the switch goes back as it was.
            string? question = _tweak.Confirm?.Invoke(value ? "true" : "false");
            if (question != null
                && MessageBox.Show(question, Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                Refresh();
                return;
            }

            try
            {
                TweakEngine.Apply(_tweak, value ? "true" : "false");
            }
            catch (Exception ex)
            {
                ReportError(ex);
            }

            Refresh();
            ReportChanged();
        }
    }

    public string StateText => !_known ? "Unknown" : _isOn ? _tweak.OnText : _tweak.OffText;

    /// <summary>Small line under the title: current state and any restart required.</summary>
    public string StateLine => Badge.Length > 0 ? StateText + "  ·  " + Badge.ToLowerInvariant() : StateText;

    public override void Refresh()
    {
        try
        {
            _isOn = _tweak.Read();
            _known = true;
        }
        catch
        {
            _known = false;
        }

        Raise(nameof(IsOn));
        Raise(nameof(StateText));
        Raise(nameof(StateLine));
    }
}

/// <summary>Row with a drop-down list.</summary>
public sealed class ChoiceTweakViewModel : TweakViewModel
{
    private readonly ChoiceTweak _tweak;
    private ChoiceOption? _selected;
    private bool _loading;

    public ChoiceTweakViewModel(ChoiceTweak tweak)
        : base(tweak)
    {
        _tweak = tweak;
    }

    public ObservableCollection<ChoiceOption> Options { get; } = new();

    public ChoiceOption? Selected
    {
        get => _selected;
        set
        {
            if (_loading || value is null || value == _selected)
            {
                return;
            }

            try
            {
                TweakEngine.Apply(_tweak, value.Key);
            }
            catch (Exception ex)
            {
                ReportError(ex);
            }

            Refresh();
            ReportChanged();
        }
    }

    public override void Refresh()
    {
        _loading = true;
        try
        {
            IReadOnlyList<ChoiceOption> options = _tweak.GetOptions();
            if (!options.SequenceEqual(Options))
            {
                Options.Clear();
                foreach (ChoiceOption option in options)
                {
                    Options.Add(option);
                }
            }

            string? current = _tweak.Read();
            _selected = Options.FirstOrDefault(o => string.Equals(o.Key, current, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            _selected = null;
        }
        finally
        {
            _loading = false;
        }

        Raise(nameof(Selected));
    }
}
