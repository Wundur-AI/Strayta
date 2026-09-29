using Strayta.Core.Text;

namespace Strayta.Text;

/// <summary>One laid-out line, in text space (y down, baselines at <see cref="Baseline"/>).</summary>
/// <param name="Start">Index of the line's first character in <see cref="TextLayerData.Text"/>.</param>
/// <param name="Length">Characters on the line, not counting the paragraph separator that may follow it.</param>
/// <param name="Left">Where the line's first character starts.</param>
/// <param name="Right">Where its last character ends (trailing spaces included).</param>
/// <param name="Visible">False for lines that do not fit in a paragraph box (Photoshop hides them).</param>
public sealed record TextLine(int Start, int Length, double Baseline, double Ascent, double Descent, double Left, double Right, bool Visible)
{
    public double Top => Baseline - Ascent;
    public double Bottom => Baseline + Descent;
    public int End => Start + Length;
}

/// <summary>A caret position: the insertion point before character <see cref="Index"/>, drawn from Top to Bottom at X.</summary>
public readonly record struct TextCaret(int Index, double X, double Top, double Bottom, int Line);

/// <summary>
/// Text laid out as Photoshop lays out type: lines, glyph positions and caret stops, in text space (apply
/// <see cref="TextLayerData.Transform"/> for document coordinates, or use the *Document helpers). Built by
/// <see cref="Create"/>; immutable.
/// </summary>
public sealed class TextLayout
{
    internal TextLayout(TextLayerData data, List<TextLine> lines, List<double[]> carets, List<PlacedGlyph> glyphs, List<Decoration> decorations,
        TextRect bounds, TextRect ink, List<string> missing, List<string> warnings, string? substitute)
    {
        Data = data;
        Lines = lines;
        _carets = carets;
        Glyphs = glyphs;
        Decorations = decorations;
        Bounds = bounds;
        InkBounds = ink;
        MissingFonts = missing;
        Warnings = warnings;
        SubstituteFont = substitute;
    }

    private readonly List<double[]> _carets;

    public TextLayerData Data { get; }
    public IReadOnlyList<TextLine> Lines { get; }

    /// <summary>
    /// The lines' extent: from the leftmost line start to the rightmost line end, and from the first line's top
    /// (baseline − ascent) to the last line's bottom (baseline + descent). For paragraph text, the box.
    /// </summary>
    public TextRect Bounds { get; }

    /// <summary>The extent of the drawn glyphs (and underlines).</summary>
    public TextRect InkBounds { get; }

    /// <summary>PostScript names of fonts the text asks for that are not installed; they were drawn with <see cref="SubstituteFont"/>.</summary>
    public IReadOnlyList<string> MissingFonts { get; }

    /// <summary>The font used in place of missing ones, or null when none was needed.</summary>
    public string? SubstituteFont { get; }

    /// <summary>Things the layout does not reproduce (warped or vertical text).</summary>
    public IReadOnlyList<string> Warnings { get; }

    internal IReadOnlyList<PlacedGlyph> Glyphs { get; }
    internal IReadOnlyList<Decoration> Decorations { get; }

    /// <summary>Bounds for the file ('bounds' and 'boundingBox').</summary>
    public TextBounds ToTextBounds() => new(Data.Kind == TextKind.Paragraph ? Data.Box : Bounds, InkBounds);

    /// <summary>Lays out <paramref name="data"/> with fonts from <paramref name="fonts"/> (the installed fonts by default).</summary>
    public static TextLayout Create(TextLayerData data, FontCatalog? fonts = null) => TextLayoutEngine.Layout(data, fonts ?? FontCatalog.System);

    /// <summary>The line holding the caret before character <paramref name="index"/> (0..Text.Length).</summary>
    public int LineOf(int index)
    {
        index = Math.Clamp(index, 0, Data.Text.Length);
        for (int i = 0; i < Lines.Count; i++)
        {
            var line = Lines[i];
            bool last = i == Lines.Count - 1;
            // A line owns its start through its end; at a soft wrap the caret belongs to the next line.
            if (index < line.End || index == line.End && (last || Lines[i + 1].Start > index)) return i;
            if (index >= line.Start && index <= line.End && last) return i;
        }
        return Lines.Count - 1;
    }

    /// <summary>The caret before character <paramref name="index"/>.</summary>
    public TextCaret GetCaret(int index)
    {
        index = Math.Clamp(index, 0, Data.Text.Length);
        int li = LineOf(index);
        var line = Lines[li];
        var xs = _carets[li];
        double x = xs[Math.Clamp(index - line.Start, 0, xs.Length - 1)];
        return new TextCaret(index, x, line.Top, line.Bottom, li);
    }

    /// <summary>The character index whose caret is nearest to (<paramref name="x"/>, <paramref name="y"/>) in text space.</summary>
    public int HitTest(double x, double y)
    {
        if (Lines.Count == 0) return 0;
        int li = 0;
        for (int i = 0; i < Lines.Count; i++)
        {
            li = i;
            double boundary = i + 1 < Lines.Count ? (Lines[i].Bottom + Lines[i + 1].Top) / 2 : double.MaxValue;
            if (y < boundary) break;
        }
        var line = Lines[li];
        var xs = _carets[li];
        int best = 0;
        for (int k = 1; k < xs.Length; k++)
            if (Math.Abs(xs[k] - x) < Math.Abs(xs[best] - x)) best = k;
        return line.Start + best;
    }

    /// <summary>
    /// Rectangles covering the characters from <paramref name="start"/> for <paramref name="length"/>, one per line (a
    /// selection that runs past a line's end covers the rest of it).
    /// </summary>
    public IReadOnlyList<TextRect> GetSelectionRects(int start, int length)
    {
        var rects = new List<TextRect>();
        int end = Math.Clamp(start + length, 0, Data.Text.Length);
        start = Math.Clamp(start, 0, Data.Text.Length);
        if (end <= start) return rects;
        for (int i = 0; i < Lines.Count; i++)
        {
            var line = Lines[i];
            int a = Math.Max(start, line.Start), b = Math.Min(end, line.End);
            bool continues = end > line.End && start <= line.End && i + 1 < Lines.Count;
            if (a > b || a == b && !continues) continue;
            var xs = _carets[i];
            double x0 = xs[a - line.Start], x1 = xs[b - line.Start];
            if (continues) x1 += Math.Max(2, (line.Ascent + line.Descent) * 0.25); // show the selected line break
            rects.Add(new TextRect(Math.Min(x0, x1), line.Top, Math.Max(x0, x1), line.Bottom));
        }
        return rects;
    }

    /// <summary><see cref="HitTest"/> for a point in document coordinates.</summary>
    public int HitTestDocument(double x, double y)
    {
        var (tx, ty) = Data.Transform.Invert().Apply(x, y);
        return HitTest(tx, ty);
    }

    /// <summary>The caret as a document-space line segment (top, bottom).</summary>
    public ((double X, double Y) Top, (double X, double Y) Bottom) GetCaretDocument(int index)
    {
        var c = GetCaret(index);
        return (Data.Transform.Apply(c.X, c.Top), Data.Transform.Apply(c.X, c.Bottom));
    }
}

/// <summary>A glyph placed on its baseline, in text space.</summary>
internal sealed record PlacedGlyph(ShapingFace Face, ushort Glyph, double X, double Y, double Size, double ScaleX, double ScaleY, bool FauxItalic,
    bool FauxBold, TextColor Color, int Cluster);

/// <summary>An underline or strikethrough.</summary>
internal sealed record Decoration(TextRect Rect, TextColor Color);
