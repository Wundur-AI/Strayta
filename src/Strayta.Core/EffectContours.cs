namespace Strayta.Core;

/// <summary>
/// A point of a contour, in Photoshop's 0..255 units: <see cref="X"/> is the input (how far into the effect's falloff),
/// <see cref="Y"/> the output. A corner point breaks the curve into separate smooth pieces, as Photoshop's contour
/// editor does with its Corner checkbox.
/// </summary>
public readonly record struct ContourPoint(float X, float Y, bool Corner = false);

/// <summary>
/// A layer-style contour: the curve that reshapes an effect's falloff (shadows, glows, satin) or a bevel's profile
/// and gloss. Compares by name and points, so a contour read back from a file equals the one that was written.
/// </summary>
public sealed record Contour(IReadOnlyList<ContourPoint> Points)
{
    /// <summary>The name Photoshop shows (and stores) for it; edited curves become "Custom".</summary>
    public string Name { get; init; } = "Custom";

    public bool Equals(Contour? other) => other is not null && Name == other.Name && Points.SequenceEqual(other.Points);

    public override int GetHashCode() => HashCode.Combine(Name, Points.Count);

    /// <summary>The straight line: the effect's own falloff, unchanged.</summary>
    public static Contour Linear { get; } = new([new(0, 0), new(255, 255)]) { Name = "Linear" };

    /// <summary>True for a straight line from (0,0) to (255,255), whatever its name: rendering can skip it.</summary>
    public bool IsIdentity =>
        Points.Count >= 2 && Points.All(p => MathF.Abs(p.X - p.Y) < 0.5f) && Points[0].X <= 0.5f && Points[^1].X >= 254.5f;

    /// <summary>
    /// Photoshop's default contour set, by name. The shapes are drawn from what the presets look like, point for point
    /// simple; files keep whatever curve they stored, so these only matter for new effects and the picker.
    /// </summary>
    public static IReadOnlyList<Contour> Presets { get; } =
    [
        Linear,
        new([new(0, 0), new(128, 255, true), new(255, 0)]) { Name = "Cone" },
        new([new(0, 255), new(128, 0, true), new(255, 255)]) { Name = "Cone - Inverted" },
        new([new(0, 0), new(128, 32), new(255, 255)]) { Name = "Cove - Deep" },
        new([new(0, 0), new(128, 80), new(255, 255)]) { Name = "Cove - Shallow" },
        new([new(0, 0), new(64, 22), new(128, 128), new(192, 233), new(255, 255)]) { Name = "Gaussian" },
        new([new(0, 0), new(26, 111), new(77, 182), new(128, 221), new(191, 247), new(255, 255)]) { Name = "Half Round" },
        new([new(0, 0), new(64, 60), new(128, 250), new(192, 60), new(255, 0)]) { Name = "Ring" },
        new([new(0, 0), new(51, 230), new(102, 30), new(153, 230), new(204, 30), new(255, 0)]) { Name = "Ring - Double" },
        new([new(0, 255), new(64, 196), new(96, 210), new(160, 60), new(200, 72), new(255, 0)]) { Name = "Rolling Slope - Descending" },
        new([new(0, 0), new(52, 20), new(76, 90), new(128, 110), new(152, 180), new(204, 200), new(230, 245), new(255, 255)]) { Name = "Rounded Steps" },
        new([new(0, 0), new(85, 255, true), new(86, 0, true), new(170, 255, true), new(171, 0, true), new(255, 255)]) { Name = "Sawtooth 1" },
    ];
}
