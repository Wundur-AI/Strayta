using System.Globalization;
using Avalonia.Data.Converters;
using Strayta.Core;

namespace Strayta.Editor.Controls;

/// <summary>Blend mode names as Photoshop shows them.</summary>
public sealed class BlendModeNames : IValueConverter
{
    public static readonly BlendModeNames Instance = new();

    public static string Of(BlendMode mode) => mode switch
    {
        BlendMode.PassThrough => "Pass Through",
        BlendMode.ColorBurn => "Color Burn",
        BlendMode.LinearBurn => "Linear Burn",
        BlendMode.DarkerColor => "Darker Color",
        BlendMode.ColorDodge => "Color Dodge",
        BlendMode.LinearDodge => "Linear Dodge (Add)",
        BlendMode.LighterColor => "Lighter Color",
        BlendMode.SoftLight => "Soft Light",
        BlendMode.HardLight => "Hard Light",
        BlendMode.VividLight => "Vivid Light",
        BlendMode.LinearLight => "Linear Light",
        BlendMode.PinLight => "Pin Light",
        BlendMode.HardMix => "Hard Mix",
        _ => mode.ToString(),
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is BlendMode m ? Of(m) : value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
