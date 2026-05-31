using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace SyncSession.Samples.Desktop.Converters;

/// <summary>
/// Converts a bool to one of two brushes supplied as "TrueBrush|FalseBrush" in ConverterParameter.
/// </summary>
public class BoolToBrushConverter : IValueConverter
{
    public static readonly BoolToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var parts = (parameter as string)?.Split('|');
        if (parts?.Length != 2) return null;
        var hex = value is true ? parts[0] : parts[1];
        return Brush.Parse(hex);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
