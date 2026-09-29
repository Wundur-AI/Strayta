namespace Strayta.Core.Text;

/// <summary>Kerning between characters (Photoshop's Character panel kerning menu).</summary>
public enum TextKerning
{
    /// <summary>The font's own kerning pairs ("Metrics").</summary>
    Metrics,

    /// <summary>
    /// Photoshop's "Optical" kerning, computed from glyph outlines. Rendered and stored as metrics kerning: the file
    /// encoding Photoshop uses for it has not been observed yet.
    /// </summary>
    Optical,

    /// <summary>No automatic kerning; only <see cref="TextStyle.ManualKerning"/> applies ("0" in the menu).</summary>
    None,
}

/// <summary>Letter case transformation (Photoshop's All Caps / Small Caps buttons).</summary>
public enum TextCaps
{
    Normal = 0,
    SmallCaps = 1,
    AllCaps = 2,
}

/// <summary>Vertical position (Photoshop's Superscript / Subscript buttons).</summary>
public enum TextBaselinePosition
{
    Normal = 0,
    Superscript = 1,
    Subscript = 2,
}

/// <summary>Paragraph alignment, numbered as Photoshop stores it ("Justification").</summary>
public enum TextJustification
{
    Left = 0,
    Right = 1,
    Center = 2,
    JustifyLastLeft = 3,
    JustifyLastRight = 4,
    JustifyLastCenter = 5,
    JustifyAll = 6,
}

/// <summary>Anti-aliasing method (the options bar's "aa" menu).</summary>
public enum TextAntiAlias
{
    None,
    Sharp,
    Crisp,
    Strong,
    Smooth,

    /// <summary>"Mac LCD" / "Windows LCD" (platform rendering with subpixels).</summary>
    PlatformLcd,

    /// <summary>"Mac" / "Windows" (platform grayscale rendering).</summary>
    PlatformGray,
}

public enum TextOrientation
{
    Horizontal,
    Vertical,
}

/// <summary>Point text grows from its anchor; paragraph (box) text wraps inside a box.</summary>
public enum TextKind
{
    Point,
    Paragraph,
}

/// <summary>A color with components 0..1 in the document's RGB (or gray) space.</summary>
public readonly record struct TextColor(double R, double G, double B, double A = 1)
{
    public static TextColor Black => new(0, 0, 0);
    public static TextColor White => new(1, 1, 1);

    public static TextColor FromRgb8(byte r, byte g, byte b) => new(r / 255.0, g / 255.0, b / 255.0);
}

/// <summary>
/// Character formatting for a run of text, with Photoshop's defaults. Sizes and distances are in text-space units,
/// which are document pixels before the layer's <see cref="TextLayerData.Transform"/> (Photoshop shows sizes in points:
/// points = units × transform scale × 72 / document resolution).
/// </summary>
public sealed record TextStyle
{
    /// <summary>PostScript name of the font (e.g. "ArialMT", "Helvetica-Bold"), as Photoshop stores it.</summary>
    public string FontPostScriptName { get; init; } = "MyriadPro-Regular";

    public double FontSize { get; init; } = 12;

    /// <summary>When true, the line spacing is the paragraph's <see cref="ParagraphStyle.AutoLeadingFactor"/> × size.</summary>
    public bool AutoLeading { get; init; } = true;

    /// <summary>Line spacing (baseline to baseline) when <see cref="AutoLeading"/> is off.</summary>
    public double Leading { get; init; }

    /// <summary>Tracking in 1/1000 em, added after every character.</summary>
    public int Tracking { get; init; }

    public TextKerning Kerning { get; init; } = TextKerning.Metrics;

    /// <summary>Manual kerning in 1/1000 em, applied before this run's characters (Photoshop's per-pair value).</summary>
    public int ManualKerning { get; init; }

    /// <summary>Baseline shift, positive up.</summary>
    public double BaselineShift { get; init; }

    public TextColor FillColor { get; init; } = TextColor.Black;

    public bool FauxBold { get; init; }
    public bool FauxItalic { get; init; }
    public TextCaps Caps { get; init; }
    public TextBaselinePosition BaselinePosition { get; init; }
    public bool Underline { get; init; }
    public bool Strikethrough { get; init; }

    /// <summary>Horizontal scale, 1 = 100%.</summary>
    public double HorizontalScale { get; init; } = 1;

    /// <summary>Vertical scale, 1 = 100%.</summary>
    public double VerticalScale { get; init; } = 1;

    /// <summary>Standard ligatures (fi, fl) on.</summary>
    public bool Ligatures { get; init; } = true;

    public bool DiscretionaryLigatures { get; init; }

    /// <summary>
    /// Format-specific data this style was read from (for PSD, the run's style dictionary), so properties the model
    /// does not cover survive an edit. Copied along by <c>with</c>; leave null for new styles.
    /// </summary>
    public object? SourceData { get; init; }

    /// <summary>The line spacing this style asks for, given its paragraph's auto-leading factor.</summary>
    public double LineSpacing(double autoLeadingFactor) => AutoLeading ? FontSize * autoLeadingFactor : Leading;
}

/// <summary>Paragraph formatting, with Photoshop's defaults. Distances are in text-space units.</summary>
public sealed record ParagraphStyle
{
    public TextJustification Justification { get; init; } = TextJustification.Left;
    public double FirstLineIndent { get; init; }
    public double StartIndent { get; init; }
    public double EndIndent { get; init; }
    public double SpaceBefore { get; init; }
    public double SpaceAfter { get; init; }
    public bool Hyphenate { get; init; } = true;

    /// <summary>Auto leading as a fraction of the font size (Photoshop's default is 120%).</summary>
    public double AutoLeadingFactor { get; init; } = 1.2;

    /// <summary>Format-specific data this style was read from; see <see cref="TextStyle.SourceData"/>.</summary>
    public object? SourceData { get; init; }
}

/// <summary>A run of <see cref="Length"/> characters sharing one style.</summary>
public readonly record struct TextRun<T>(int Length, T Style);

/// <summary>
/// The 2D transform from text space to the document: (x, y) → (XX·x + YX·y + TX, XY·x + YY·y + TY).
/// </summary>
public readonly record struct TextTransform(double XX, double XY, double YX, double YY, double TX, double TY)
{
    public static TextTransform Identity => new(1, 0, 0, 1, 0, 0);

    public static TextTransform Translation(double x, double y) => new(1, 0, 0, 1, x, y);

    public (double X, double Y) Apply(double x, double y) => (XX * x + YX * y + TX, XY * x + YY * y + TY);

    /// <summary>The inverse map (document → text space); throws when the transform is singular.</summary>
    public TextTransform Invert()
    {
        double det = XX * YY - XY * YX;
        if (Math.Abs(det) < 1e-12) throw new InvalidOperationException("The text transform cannot be inverted.");
        double ixx = YY / det, ixy = -XY / det, iyx = -YX / det, iyy = XX / det;
        return new(ixx, ixy, iyx, iyy, -(ixx * TX + iyx * TY), -(ixy * TX + iyy * TY));
    }

    /// <summary>True when only a translation (no scale, turn or skew).</summary>
    public bool IsTranslation => XX == 1 && YY == 1 && XY == 0 && YX == 0;
}

/// <summary>A rectangle in text space.</summary>
public readonly record struct TextRect(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;
    public double Height => Bottom - Top;
}
