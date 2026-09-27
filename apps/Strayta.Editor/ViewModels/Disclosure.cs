using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace Strayta.Editor.ViewModels;

/// <summary>Chevron for a group row: pointing down when expanded, right when collapsed.</summary>
public sealed class Disclosure : IValueConverter
{
    public static readonly Disclosure Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Application.Current is { } app && app.TryFindResource(value is true ? "IconChevronDown" : "IconChevronRight", out var g) ? g : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
