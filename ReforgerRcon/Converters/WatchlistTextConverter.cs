using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace ReforgerRcon.Converters;

public class WatchlistTextConverter : IValueConverter
{
    public static readonly WatchlistTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true ? "Remove from Watchlist" : "Add to Watchlist";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}