namespace Strayta.Core;

/// <summary>The color model of a document. Values match the Photoshop file format.</summary>
public enum ColorMode
{
    Bitmap = 0,
    Grayscale = 1,
    Indexed = 2,
    Rgb = 3,
    Cmyk = 4,
    Multichannel = 7,
    Duotone = 8,
    Lab = 9,
}

public static class ColorModeExtensions
{
    /// <summary>Number of color (non-alpha) channels a raster in this mode carries.</summary>
    public static int ColorChannelCount(this ColorMode mode) => mode switch
    {
        ColorMode.Bitmap or ColorMode.Grayscale or ColorMode.Indexed or ColorMode.Duotone => 1,
        ColorMode.Rgb or ColorMode.Lab => 3,
        ColorMode.Cmyk => 4,
        ColorMode.Multichannel => 0,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };
}
