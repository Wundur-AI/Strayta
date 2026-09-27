namespace Strayta.Core;

public enum BlendMode
{
    Normal,
    Dissolve,

    Darken,
    Multiply,
    ColorBurn,
    LinearBurn,
    DarkerColor,

    Lighten,
    Screen,
    ColorDodge,
    LinearDodge,
    LighterColor,

    Overlay,
    SoftLight,
    HardLight,
    VividLight,
    LinearLight,
    PinLight,
    HardMix,

    Difference,
    Exclusion,
    Subtract,
    Divide,

    Hue,
    Saturation,
    Color,
    Luminosity,

    /// <summary>Groups only: children blend directly into what is below the group.</summary>
    PassThrough,
}
