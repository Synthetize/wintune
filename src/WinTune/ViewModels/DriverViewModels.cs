using System.Windows;
using WinTune.Core;

namespace WinTune.ViewModels;

/// <summary>Row of the Drivers page: a component with the link to its drivers page.</summary>
public sealed class DriverRowViewModel : ObservableObject
{
    private string _link;
    private bool _isCustom;

    public DriverRowViewModel(HardwareComponent component, string? customLink)
    {
        Component = component;
        _isCustom = !string.IsNullOrWhiteSpace(customLink);
        _link = _isCustom ? customLink! : component.DefaultLink;
    }

    public HardwareComponent Component { get; }

    public string Type => Component.Type;

    public string Model => Component.Model;

    public string Detail => Component.Detail;

    public Visibility DetailVisibility => string.IsNullOrWhiteSpace(Component.Detail) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Current link: the one chosen by the user, otherwise the default one.</summary>
    public string Link => _link;

    public string LinkNote => _isCustom ? "Custom link" : "Default link";

    /// <summary>Sets a custom link; with null it goes back to the default one.</summary>
    public void SetCustomLink(string? link)
    {
        _isCustom = !string.IsNullOrWhiteSpace(link);
        _link = _isCustom ? link! : Component.DefaultLink;
        Raise(nameof(Link));
        Raise(nameof(LinkNote));
    }
}

/// <summary>Row of the list of drivers offered by Windows Update.</summary>
public sealed class DriverOfferRowViewModel : ObservableObject
{
    private bool _isSelected = true;
    private string _status = "";

    public DriverOfferRowViewModel(DriverOffer offer)
    {
        Offer = offer;
    }

    public DriverOffer Offer { get; }

    public string Id => Offer.Id;

    public string Title => Offer.Title;

    public string Detail
    {
        get
        {
            var parts = new System.Collections.Generic.List<string>();
            if (Offer.DriverClass.Length > 0)
            {
                parts.Add(Offer.DriverClass);
            }

            if (Offer.Date.Length > 0)
            {
                parts.Add(Offer.Date);
            }

            if (Offer.Size > 0)
            {
                parts.Add((Offer.Size / 1048576.0).ToString("0.#") + " MB");
            }

            if (Offer.RebootRequired)
            {
                parts.Add("restart needed");
            }

            return string.Join("  ·  ", parts);
        }
    }

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

    public string Status
    {
        get => _status;
        set
        {
            if (value != _status)
            {
                _status = value;
                Raise();
            }
        }
    }
}
