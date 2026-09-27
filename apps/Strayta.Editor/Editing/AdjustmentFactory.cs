using Strayta.Core;

namespace Strayta.Editor.Editing;

/// <summary>The adjustment layers the editor can create, with Photoshop's starting settings.</summary>
public static class AdjustmentFactory
{
    /// <summary>Kinds in Photoshop's menu order; also the layers' <see cref="AdjustmentLayer.Kind"/> and base name.</summary>
    public static IReadOnlyList<string> Kinds { get; } =
        ["Brightness/Contrast", "Levels", "Curves", "Hue/Saturation", "Invert", "Posterize", "Threshold"];

    /// <summary>Settings that leave the image unchanged (except Invert, Threshold and Posterize, which act immediately).</summary>
    public static Adjustment Default(string kind) => kind switch
    {
        "Levels" => new LevelsAdjustment(LevelsChannel.Identity, [LevelsChannel.Identity, LevelsChannel.Identity, LevelsChannel.Identity]),
        "Curves" => new CurvesAdjustment(IdentityCurve, [null, null, null]),
        "Hue/Saturation" => new HueSaturationAdjustment(0, 0, 0, false, 0, 25, 0, false),
        "Brightness/Contrast" => new BrightnessContrastAdjustment(0, 0),
        "Invert" => new InvertAdjustment(),
        "Threshold" => new ThresholdAdjustment(128),
        "Posterize" => new PosterizeAdjustment(4),
        _ => throw new ArgumentException($"Unknown adjustment \"{kind}\".", nameof(kind)),
    };

    public static IReadOnlyList<CurvePoint> IdentityCurve { get; } = [new(0, 0), new(255, 255)];

    /// <summary>
    /// A new adjustment layer with a white (reveal-all) mask, as Photoshop creates them, named like "Levels 1".
    /// </summary>
    public static AdjustmentLayer Create(Document doc, string kind) => new()
    {
        Name = LayerFactory.NextName(doc, kind),
        Kind = kind,
        Adjustment = Default(kind),
        Mask = LayerMasks.Solid(reveal: true),
    };
}
