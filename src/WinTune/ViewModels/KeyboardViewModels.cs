using WinTune.Core;

namespace WinTune.ViewModels;

/// <summary>Row of the table of keyboard shortcuts.</summary>
public sealed class KeyMappingRowViewModel : ObservableObject
{
    private string _key;
    private string _normal;
    private string _shift;

    public KeyMappingRowViewModel(KeyMapping mapping)
    {
        _key = mapping.Key;
        _normal = mapping.Normal;
        _shift = mapping.Shift;
    }

    public string Key
    {
        get => _key;
        set
        {
            if (value != _key)
            {
                _key = value;
                Raise();
            }
        }
    }

    public string Normal
    {
        get => _normal;
        set
        {
            if (value != _normal)
            {
                _normal = value;
                Raise();
            }
        }
    }

    public string Shift
    {
        get => _shift;
        set
        {
            if (value != _shift)
            {
                _shift = value;
                Raise();
            }
        }
    }

    public KeyMapping ToMapping() => new() { Key = Key.Trim(), Normal = Normal, Shift = Shift };
}
