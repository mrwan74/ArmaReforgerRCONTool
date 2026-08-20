using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace ReforgerRcon.Converters;

public class EqualityConverter : IValueConverter
{
    public static readonly EqualityConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return Equals(value?.ToString(), parameter?.ToString());
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true ? parameter : Avalonia.Data.BindingOperations.DoNothing;
    }
}
