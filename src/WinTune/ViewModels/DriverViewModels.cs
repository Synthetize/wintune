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
