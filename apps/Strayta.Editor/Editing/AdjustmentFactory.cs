using Strayta.Core;
using Strayta.Core.Painting;

namespace Strayta.Editor.Editing;

/// <summary>The adjustment layers the editor can create, with Photoshop's starting settings.</summary>
public static class AdjustmentFactory
{
    /// <summary>Kinds in Photoshop's menu order; also the layers' <see cref="AdjustmentLayer.Kind"/> and base name.</summary>
    public static IReadOnlyList<string> Kinds { get; } =
    [
        "Brightness/Contrast", "Levels", "Curves", "Exposure",
        "Vibrance", "Hue/Saturation", "Color Balance", "Black & White", "Photo Filter", "Channel Mixer", "Color Lookup",
        "Invert", "Posterize", "Threshold", "Gradient Map", "Selective Color",
    ];

    /// <summary>True for the first kind of each of Photoshop's menu groups after the first (a separator goes before it).</summary>
    public static bool StartsMenuGroup(string kind) => kind is "Vibrance" or "Invert";

    /// <summary>The menu item's text: an ellipsis for kinds with settings.</summary>
    public static string MenuTitle(string kind) => kind == "Invert" ? kind : kind + "…";

    /// <summary>Settings that leave the image unchanged (except those that act immediately, as in Photoshop).</summary>
    public static Adjustment Default(string kind) => kind switch
    {
        "Levels" => new LevelsAdjustment(LevelsChannel.Identity, [LevelsChannel.Identity, LevelsChannel.Identity, LevelsChannel.Identity]),
        "Curves" => new CurvesAdjustment(IdentityCurve, [null, null, null]),
        "Hue/Saturation" => new HueSaturationAdjustment(0, 0, 0, false, 0, 25, 0, false),
        "Brightness/Contrast" => new BrightnessContrastAdjustment(0, 0),
        "Invert" => new InvertAdjustment(),
        "Threshold" => new ThresholdAdjustment(128),
        "Posterize" => new PosterizeAdjustment(4),
        "Exposure" => ExposureAdjustment.Default,
        "Vibrance" => new VibranceAdjustment(0, 0),
        "Color Balance" => new ColorBalanceAdjustment(default, default, default, true),
        "Black & White" => BlackWhiteAdjustment.Default,
        "Photo Filter" => new PhotoFilterAdjustment(PhotoFilters.Default.Color, 25, true),
        "Channel Mixer" => ChannelMixerAdjustment.Default,
        "Color Lookup" => ColorLookupAdjustment.None,
        "Gradient Map" => new GradientMapAdjustment(ForegroundToBackground, false, false, GradientMethod.Classic),
        "Selective Color" => SelectiveColorAdjustment.Default,
        _ => throw new ArgumentException($"Unknown adjustment \"{kind}\".", nameof(kind)),
    };

    /// <summary>The kind whose defaults <paramref name="adjustment"/> resets to (for the Properties panel's reset button).</summary>
    public static string? KindOf(Adjustment adjustment) => adjustment switch
    {
        LevelsAdjustment => "Levels",
        CurvesAdjustment => "Curves",
        HueSaturationAdjustment => "Hue/Saturation",
        BrightnessContrastAdjustment => "Brightness/Contrast",
        InvertAdjustment => "Invert",
        ThresholdAdjustment => "Threshold",
        PosterizeAdjustment => "Posterize",
        ExposureAdjustment => "Exposure",
        VibranceAdjustment => "Vibrance",
        ColorBalanceAdjustment => "Color Balance",
        BlackWhiteAdjustment => "Black & White",
        PhotoFilterAdjustment => "Photo Filter",
        ChannelMixerAdjustment => "Channel Mixer",
        ColorLookupAdjustment => "Color Lookup",
        GradientMapAdjustment => "Gradient Map",
        SelectiveColorAdjustment => "Selective Color",
        _ => null,
    };

    public static IReadOnlyList<CurvePoint> IdentityCurve { get; } = [new(0, 0), new(255, 255)];

    /// <summary>Photoshop's starting gradient map: the foreground color to the background color.</summary>
    public static Gradient ForegroundToBackground { get; } = new(
        [
            new GradientColorStop(0f, 0.5f, RgbColor.Black) { Kind = GradientStopKind.Foreground },
            new GradientColorStop(1f, 0.5f, new RgbColor(1, 1, 1)) { Kind = GradientStopKind.Background },
        ],
        [new GradientOpacityStop(0f, 0.5f, 1f), new GradientOpacityStop(1f, 0.5f, 1f)]) { Name = "Foreground to Background" };

    /// <summary>
    /// A new adjustment layer with a white (reveal-all) mask, as Photoshop creates them, named like "Levels 1".
    /// A Gradient Map starts from today's foreground and background colors.
    /// </summary>
    public static AdjustmentLayer Create(Document doc, string kind, RgbColor? foreground = null, RgbColor? background = null)
    {
        var adjustment = Default(kind);
        if (adjustment is GradientMapAdjustment g && (foreground, background) is ({ } fg, { } bg))
            adjustment = g with { Gradient = g.Gradient.Resolve(fg, bg) };
        return new AdjustmentLayer
        {
            Name = LayerFactory.NextName(doc, kind),
            Kind = kind,
            Adjustment = adjustment,
            Mask = LayerMasks.Solid(reveal: true),
        };
    }
}

/// <summary>Photoshop's Photo Filter filters, in its menu order, with their colors.</summary>
public static class PhotoFilters
{
    public sealed record Filter(string Name, RgbColor Color);

    private static Filter F(string name, int rgb) => new(name, new RgbColor((rgb >> 16 & 255) / 255f, (rgb >> 8 & 255) / 255f, (rgb & 255) / 255f));

    public static IReadOnlyList<Filter> All { get; } =
    [
        F("Warming Filter (85)", 0xEC8A00), F("Warming Filter (LBA)", 0xFA9600), F("Warming Filter (81)", 0xEBB113),
        F("Cooling Filter (80)", 0x006DFF), F("Cooling Filter (LBB)", 0x005DFF), F("Cooling Filter (82)", 0x00B5FF),
        F("Red", 0xEA1A1A), F("Orange", 0xF38417), F("Yellow", 0xF9E31C), F("Green", 0x19C919), F("Cyan", 0x1DCBEA),
        F("Blue", 0x1D35EA), F("Violet", 0x9B1DEA), F("Magenta", 0xE318E3), F("Sepia", 0xAC7A33), F("Deep Red", 0xFF0000),
        F("Deep Blue", 0x0022CD), F("Deep Emerald", 0x008C00), F("Deep Yellow", 0xFFD500), F("Underwater", 0x00C1B1),
    ];

    public static Filter Default => All[0];

    /// <summary>The filter with this color (to 8 bits), if any.</summary>
    public static Filter? Matching(RgbColor c) => All.FirstOrDefault(f => Same(f.Color, c));

    private static bool Same(RgbColor a, RgbColor b) =>
        MathF.Abs(a.R - b.R) < 0.6f / 255 && MathF.Abs(a.G - b.G) < 0.6f / 255 && MathF.Abs(a.B - b.B) < 0.6f / 255;
}
