using System;
using System.Globalization;
using System.Windows.Data;

namespace WinTune.ViewModels;

/// <summary>
/// Width of a tile that takes the whole row on the Optimizations page: as many columns
/// of normal tiles as fit, so the right edge stays aligned with the others.
/// </summary>
public sealed class TileRowWidthConverter : IValueConverter
{
    // A normal tile is 272 wide and has an 8 margin on the right.
    private const double TileWidth = 272;
    private const double Gap = 8;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double available || double.IsNaN(available) || available <= 0)
        {
            return TileWidth;
        }

        int columns = Math.Max(1, (int)((available + Gap) / (TileWidth + Gap)));
        return columns * (TileWidth + Gap) - Gap;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
