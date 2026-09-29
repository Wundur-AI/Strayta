namespace Strayta.Core;

/// <summary>A non-destructive color adjustment applied to everything beneath an <see cref="AdjustmentLayer"/>.</summary>
public abstract record Adjustment;

/// <summary>One channel's Levels settings, in 0..255 units.</summary>
public sealed record LevelsChannel(int InputBlack, int InputWhite, int OutputBlack, int OutputWhite, float Gamma)
{
    public static LevelsChannel Identity { get; } = new(0, 255, 0, 255, 1f);
    public bool IsIdentity => this == Identity;
}

/// <summary>Levels: a master record applied to all color channels, plus optional per-channel records.</summary>
public sealed record LevelsAdjustment(LevelsChannel Master, IReadOnlyList<LevelsChannel> Channels) : Adjustment;

/// <summary>A point on a curve, in 0..255 units.</summary>
public readonly record struct CurvePoint(int Input, int Output);

/// <summary>Curves: an optional master curve plus optional per-channel curves (null = unchanged).</summary>
public sealed record CurvesAdjustment(IReadOnlyList<CurvePoint>? Master, IReadOnlyList<IReadOnlyList<CurvePoint>?> Channels) : Adjustment;

/// <summary>Hue/Saturation. Hue in degrees (-180..180), saturation and lightness in -100..100.</summary>
public sealed record HueSaturationAdjustment(
    int Hue, int Saturation, int Lightness,
    bool Colorize, int ColorizeHue, int ColorizeSaturation, int ColorizeLightness,
    bool HasColorRangeEdits) : Adjustment;

/// <summary>Brightness/Contrast, each -100..100 (brightness up to 150 in modern Photoshop).</summary>
public sealed record BrightnessContrastAdjustment(int Brightness, int Contrast) : Adjustment;

public sealed record InvertAdjustment : Adjustment;

/// <summary>Pixels at or above <paramref name="Level"/> (1..255) become white, the rest black.</summary>
public sealed record ThresholdAdjustment(int Level) : Adjustment;

/// <summary>Reduces each channel to <paramref name="Levels"/> (2..255) tones.</summary>
public sealed record PosterizeAdjustment(int Levels) : Adjustment;

/// <summary>
/// Exposure, computed in linear light: <paramref name="Exposure"/> in stops (-20..20) scales, <paramref name="Offset"/>
/// (-0.5..0.5) is added, then <paramref name="Gamma"/> (0.01..9.99) is a power 1/gamma.
/// </summary>
public sealed record ExposureAdjustment(float Exposure, float Offset, float Gamma) : Adjustment
{
    public static ExposureAdjustment Default { get; } = new(0f, 0f, 1f);
}

/// <summary>Vibrance and Saturation, each -100..100.</summary>
public sealed record VibranceAdjustment(int Vibrance, int Saturation) : Adjustment;

/// <summary>One tonal range's Color Balance sliders, each -100..100 (negative toward cyan, magenta, yellow).</summary>
public readonly record struct ColorBalanceTone(int CyanRed, int MagentaGreen, int YellowBlue)
{
    public bool IsZero => CyanRed == 0 && MagentaGreen == 0 && YellowBlue == 0;
}

/// <summary>Color Balance for shadows, midtones and highlights, optionally keeping each pixel's luminosity.</summary>
public sealed record ColorBalanceAdjustment(ColorBalanceTone Shadows, ColorBalanceTone Midtones, ColorBalanceTone Highlights, bool PreserveLuminosity)
    : Adjustment;

/// <summary>
/// Black &amp; White: the six hue weights in percent (-200..300; Photoshop's defaults are 40, 60, 40, 60, 20, 80), and an
/// optional tint laid over the gray result.
/// </summary>
public sealed record BlackWhiteAdjustment(int Reds, int Yellows, int Greens, int Cyans, int Blues, int Magentas, bool Tint, RgbColor TintColor)
    : Adjustment
{
    /// <summary>Photoshop's default tint color.</summary>
    public static RgbColor DefaultTint { get; } = new(225 / 255f, 211 / 255f, 179 / 255f);

    public static BlackWhiteAdjustment Default { get; } = new(40, 60, 40, 60, 20, 80, false, DefaultTint);
}

/// <summary>Photo Filter: a color at a density (1..100 percent), optionally keeping luminosity.</summary>
public sealed record PhotoFilterAdjustment(RgbColor Color, int Density, bool PreserveLuminosity) : Adjustment;

/// <summary>One output channel of the Channel Mixer: percentages (-200..200) of the source channels and a constant.</summary>
public readonly record struct MixerChannel(int Red, int Green, int Blue, int Constant);

/// <summary>Channel Mixer. In <see cref="Monochrome"/> mode every channel gets <see cref="Gray"/>.</summary>
public sealed record ChannelMixerAdjustment(bool Monochrome, MixerChannel Red, MixerChannel Green, MixerChannel Blue, MixerChannel Gray) : Adjustment
{
    public static ChannelMixerAdjustment Default { get; } =
        new(false, new(100, 0, 0, 0), new(0, 100, 0, 0), new(0, 0, 100, 0), new(40, 40, 20, 0));
}

/// <summary>The color ranges Selective Color adjusts, in Photoshop's order.</summary>
public enum SelectiveColorRange
{
    Reds,
    Yellows,
    Greens,
    Cyans,
    Blues,
    Magentas,
    Whites,
    Neutrals,
    Blacks,
}

/// <summary>Cyan, magenta, yellow and black changes for one color range, each -100..100.</summary>
public readonly record struct SelectiveColorValues(int Cyan, int Magenta, int Yellow, int Black)
{
    public bool IsZero => Cyan == 0 && Magenta == 0 && Yellow == 0 && Black == 0;
}

/// <summary>Selective Color: nine ranges (see <see cref="SelectiveColorRange"/>), relative or absolute.</summary>
public sealed record SelectiveColorAdjustment(bool Absolute, IReadOnlyList<SelectiveColorValues> Ranges) : Adjustment
{
    public static SelectiveColorAdjustment Default { get; } = new(false, new SelectiveColorValues[9]);

    public SelectiveColorValues this[SelectiveColorRange range] => (int)range < Ranges.Count ? Ranges[(int)range] : default;

    public SelectiveColorAdjustment With(SelectiveColorRange range, SelectiveColorValues values)
    {
        var list = Enumerable.Range(0, 9).Select(i => i < Ranges.Count ? Ranges[i] : default).ToArray();
        list[(int)range] = values;
        return this with { Ranges = list };
    }

    /// <summary>Ranges compare by value, so an unchanged layer is recognised after a round trip.</summary>
    public bool Equals(SelectiveColorAdjustment? other) =>
        other is not null && Absolute == other.Absolute
        && Enumerable.Range(0, 9).All(i => this[(SelectiveColorRange)i] == other[(SelectiveColorRange)i]);

    public override int GetHashCode() => HashCode.Combine(Absolute, Ranges.Count);
}

/// <summary>Gradient Map: the image's luminosity picks a color along <see cref="Gradient"/>.</summary>
public sealed record GradientMapAdjustment(Gradient Gradient, bool Reverse, bool Dither, Painting.GradientMethod Method) : Adjustment;

/// <summary>What a Color Lookup layer holds: a 3D LUT file, or an ICC abstract or device link profile.</summary>
public enum ColorLookupKind
{
    Lut3D,
    AbstractProfile,
    DeviceLinkProfile,
}

/// <summary>
/// Color Lookup: the lookup file itself (a .cube or .3dl for <see cref="ColorLookupKind.Lut3D"/>), embedded in the
/// layer as Photoshop does. <see cref="Data"/> is the file's bytes (empty when none is loaded); <see cref="Format"/>
/// its type ("CUBE", "3DL", ...).
/// </summary>
public sealed record ColorLookupAdjustment(ColorLookupKind Kind, string Name, string Format, byte[] Data, bool Dither) : Adjustment
{
    public static ColorLookupAdjustment None { get; } = new(ColorLookupKind.Lut3D, "", "CUBE", [], false);

    public bool Equals(ColorLookupAdjustment? other) =>
        other is not null && Kind == other.Kind && Name == other.Name && Format == other.Format && Dither == other.Dither
        && Data.AsSpan().SequenceEqual(other.Data);

    public override int GetHashCode() => HashCode.Combine(Kind, Name, Data.Length);
}

/// <summary>A layer with no pixels that modifies the colors of what lies beneath it.</summary>
public sealed class AdjustmentLayer : LayerNode
{
    /// <summary>The adjustment, or null when the source format's adjustment type is not supported yet.</summary>
    public Adjustment? Adjustment { get; set; }

    /// <summary>A short name for the adjustment type as stored in the source file (e.g. "Levels").</summary>
    public string Kind { get; set; } = "";

    public LayerMask? Mask { get; set; }
}
