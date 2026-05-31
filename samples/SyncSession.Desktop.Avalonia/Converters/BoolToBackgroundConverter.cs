using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace SyncSession.Samples.Desktop.Converters;

/// <summary>
/// Returns a brush based on a boolean value. ConverterParameter format: "TrueBrush|FalseBrush".
/// Used for DataGrid row dirty-state tinting.
/// </summary>
public class BoolToBackgroundConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var parts = (parameter as string)?.Split('|');
        if (parts is not { Length: 2 }) return Brushes.Transparent;

        var hex = (value is true) ? parts[0] : parts[1];
        try { return Brush.Parse(hex); }
        catch { return Brushes.Transparent; }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
