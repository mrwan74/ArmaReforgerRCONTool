using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace ReforgerRcon.Converters;

public class BooleanToOpacityConverter : IValueConverter
{
    public static readonly BooleanToOpacityConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true ? 1.0 : 0.3;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}
