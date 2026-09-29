using System.Globalization;

namespace Strayta.Editor.Editing;

/// <summary>Photoshop's ruler units (right-click a ruler, or Preferences › Units &amp; Rulers).</summary>
public enum RulerUnit
{
    Pixels,
    Inches,
    Centimeters,
    Millimeters,
    Points,
    Picas,
    Percent,
}

/// <summary>
/// Converts document pixels to and from ruler units. Physical units go through the document's resolution (pixels per
/// inch, PSD resource 1005); Percent is relative to the document's width on the horizontal axis and height on the
/// vertical one, as Photoshop measures it.
/// </summary>
public static class RulerUnits
{
    public static readonly IReadOnlyList<RulerUnit> All = Enum.GetValues<RulerUnit>();

    public static string Name(RulerUnit unit) => unit switch
    {
        RulerUnit.Pixels => "Pixels",
        RulerUnit.Inches => "Inches",
        RulerUnit.Centimeters => "Centimeters",
        RulerUnit.Millimeters => "Millimeters",
        RulerUnit.Points => "Points",
        RulerUnit.Picas => "Picas",
        _ => "Percent",
    };

    public static string Suffix(RulerUnit unit) => unit switch
    {
        RulerUnit.Pixels => "px",
        RulerUnit.Inches => "in",
        RulerUnit.Centimeters => "cm",
        RulerUnit.Millimeters => "mm",
        RulerUnit.Points => "pt",
        RulerUnit.Picas => "pica",
        _ => "%",
    };

    /// <summary>Document pixels in one unit. <paramref name="extent"/> is the document's size along the axis (for Percent).</summary>
    public static double PixelsPerUnit(RulerUnit unit, double resolution, double extent)
    {
        double ppi = resolution > 0 && double.IsFinite(resolution) ? resolution : 72;
        return unit switch
        {
            RulerUnit.Pixels => 1,
            RulerUnit.Inches => ppi,
            RulerUnit.Centimeters => ppi / 2.54,
            RulerUnit.Millimeters => ppi / 25.4,
            RulerUnit.Points => ppi / 72,
            RulerUnit.Picas => ppi / 6,
            _ => Math.Max(1, extent) / 100,
        };
    }

    public static double ToUnits(double pixels, RulerUnit unit, double resolution, double extent) =>
        pixels / PixelsPerUnit(unit, resolution, extent);

    public static double ToPixels(double value, RulerUnit unit, double resolution, double extent) =>
        value * PixelsPerUnit(unit, resolution, extent);

    /// <summary>A value for display: whole pixels with one decimal, other units with Photoshop's two or three.</summary>
    public static string Format(double value, RulerUnit unit) => unit switch
    {
        RulerUnit.Pixels => value.ToString("0.#", CultureInfo.CurrentCulture),
        RulerUnit.Inches => value.ToString("0.000", CultureInfo.CurrentCulture),
        RulerUnit.Percent => value.ToString("0.0", CultureInfo.CurrentCulture),
        _ => value.ToString("0.00", CultureInfo.CurrentCulture),
    };

    /// <summary>
    /// Reads a typed length such as "120", "2.5 cm", "1in", "50%" or "12 pt": a number in <paramref name="defaultUnit"/>
    /// unless it names its own unit. Returns the length in document pixels, or null when it cannot be read.
    /// </summary>
    public static double? ParsePixels(string? text, RulerUnit defaultUnit, double resolution, double extent)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim().ToLowerInvariant();
        var unit = defaultUnit;
        (string Suffix, RulerUnit Unit)[] suffixes =
        [
            ("pixels", RulerUnit.Pixels), ("px", RulerUnit.Pixels), ("inches", RulerUnit.Inches), ("inch", RulerUnit.Inches),
            ("in", RulerUnit.Inches), ("\"", RulerUnit.Inches), ("cm", RulerUnit.Centimeters), ("mm", RulerUnit.Millimeters),
            ("points", RulerUnit.Points), ("pt", RulerUnit.Points), ("picas", RulerUnit.Picas), ("pica", RulerUnit.Picas),
            ("%", RulerUnit.Percent),
        ];
        foreach (var (suffix, u) in suffixes)
            if (s.EndsWith(suffix, StringComparison.Ordinal))
            {
                s = s[..^suffix.Length].Trim();
                unit = u;
                break;
            }
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out double value)
            && !double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return null;
        return double.IsFinite(value) ? ToPixels(value, unit, resolution, extent) : null;
    }
}
