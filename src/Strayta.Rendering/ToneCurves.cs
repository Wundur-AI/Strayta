using Strayta.Core;

namespace Strayta.Rendering;

/// <summary>
/// The tone functions the renderer applies for Levels and Curves, for editors that draw them (curve graphs,
/// level previews) and must show exactly what will be rendered.
/// </summary>
public static class ToneCurves
{
    /// <summary>The curve through <paramref name="points"/> (0..255 units, at least two) as a function on 0..1.</summary>
    public static Func<float, float> Curve(IReadOnlyList<CurvePoint> points) => LutTransform.Spline(points);

    /// <summary>One Levels record applied to a 0..1 value.</summary>
    public static float Levels(LevelsChannel levels, float value) => LutTransform.Levels(levels, value);
}
