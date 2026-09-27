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

/// <summary>A layer with no pixels that modifies the colors of what lies beneath it.</summary>
public sealed class AdjustmentLayer : LayerNode
{
    /// <summary>The adjustment, or null when the source format's adjustment type is not supported yet.</summary>
    public Adjustment? Adjustment { get; set; }

    /// <summary>A short name for the adjustment type as stored in the source file (e.g. "Levels").</summary>
    public string Kind { get; set; } = "";

    public LayerMask? Mask { get; set; }
}
