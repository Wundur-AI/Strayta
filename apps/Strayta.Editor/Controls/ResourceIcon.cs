using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Strayta.Editor.Controls;

/// <summary>Looks up an icon geometry (Icons.axaml) by its resource key, for view models that name their icon.</summary>
public sealed class ResourceIcon : IValueConverter
{
    public static ResourceIcon Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string key && Avalonia.Application.Current is { } app
        && Avalonia.Controls.ResourceNodeExtensions.TryFindResource(app, key, out var r) && r is StreamGeometry g
            ? g
            : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
