using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Strayta.Editor.ViewModels;

/// <summary>Turns a <see cref="Color"/> into a brush for swatches.</summary>
public sealed class ColorBrushes : IValueConverter
{
    public static readonly ColorBrushes Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Color c ? new SolidColorBrush(c) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
