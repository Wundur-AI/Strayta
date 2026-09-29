namespace Strayta.Core.Text;

/// <summary>
/// The editable content of a type layer: its characters with style and paragraph runs, whether it is point or
/// paragraph (box) text, and where it sits. Immutable: the editing methods return a new instance, which keeps undo
/// simple and lets a writer tell an edited layer from an untouched one by comparing with what it read.
/// <para>
/// <see cref="Text"/> separates paragraphs with '\n' (Photoshop stores '\r'); a forced line break inside a paragraph
/// (Shift+Enter) is U+0003, as in Photoshop. Style and paragraph runs cover the text exactly (their lengths add up to
/// <see cref="Text"/>'s length); an empty text has one run of length 0 that holds the style new characters get.
/// Paragraph runs must not split a paragraph.
/// </para>
/// </summary>
public sealed class TextLayerData : IEquatable<TextLayerData>
{
    /// <summary>A forced line break within a paragraph (Shift+Enter).</summary>
    public const char LineBreak = '\u0003';

    public TextLayerData(string text, IReadOnlyList<TextRun<TextStyle>> styleRuns, IReadOnlyList<TextRun<ParagraphStyle>> paragraphRuns)
    {
        ArgumentNullException.ThrowIfNull(text);
        Text = text;
        StyleRuns = Normalize(styleRuns, text.Length, nameof(styleRuns));
        ParagraphRuns = Normalize(paragraphRuns, text.Length, nameof(paragraphRuns));
    }

    /// <summary>A new point text with one style and paragraph style.</summary>
    public static TextLayerData CreatePoint(string text, TextStyle style, double x, double y, ParagraphStyle? paragraph = null) =>
        new(text, [new(text.Length, style)], [new(text.Length, paragraph ?? new ParagraphStyle())])
        {
            Transform = TextTransform.Translation(x, y),
        };

    /// <summary>A new paragraph text wrapping inside <paramref name="box"/> (document pixels, unrotated).</summary>
    public static TextLayerData CreateParagraph(string text, TextStyle style, TextRect box, ParagraphStyle? paragraph = null) =>
        new(text, [new(text.Length, style)], [new(text.Length, paragraph ?? new ParagraphStyle())])
        {
            Kind = TextKind.Paragraph,
            Transform = TextTransform.Translation(box.Left, box.Top),
            Box = new TextRect(0, 0, box.Width, box.Height),
        };

    public string Text { get; }
    public IReadOnlyList<TextRun<TextStyle>> StyleRuns { get; }
    public IReadOnlyList<TextRun<ParagraphStyle>> ParagraphRuns { get; }

    public TextKind Kind { get; init; } = TextKind.Point;

    /// <summary>For paragraph text, the box in text space (usually starting at 0, 0); ignored for point text.</summary>
    public TextRect Box { get; init; }

    /// <summary>Text space to document. For point text the origin is the first line's baseline anchor.</summary>
    public TextTransform Transform { get; init; } = TextTransform.Identity;

    public TextOrientation Orientation { get; init; } = TextOrientation.Horizontal;
    public TextAntiAlias AntiAlias { get; init; } = TextAntiAlias.Sharp;

    /// <summary>The warp style name when the text is warped ("warpArc", ...), or null. Warps are kept, not rendered.</summary>
    public string? Warp { get; init; }

    /// <summary>Format-specific data the layer was read from (for PSD, the parsed 'TySh' block).</summary>
    public object? SourceData { get; init; }

    /// <summary>Paragraphs as (start, length) ranges of <see cref="Text"/>, without their separators.</summary>
    public IReadOnlyList<(int Start, int Length)> Paragraphs
    {
        get
        {
            var list = new List<(int, int)>();
            int start = 0;
            for (int i = 0; i <= Text.Length; i++)
                if (i == Text.Length || Text[i] == '\n')
                {
                    list.Add((start, i - start));
                    start = i + 1;
                }
            return list;
        }
    }

    /// <summary>The style of the character at <paramref name="index"/> (the last run's style at the end of the text).</summary>
    public TextStyle StyleAt(int index) => At(StyleRuns, index);

    /// <summary>The paragraph style of the character at <paramref name="index"/>.</summary>
    public ParagraphStyle ParagraphStyleAt(int index) => At(ParagraphRuns, index);

    /// <summary>Distinct fonts the text uses (PostScript names), in first-use order.</summary>
    public IReadOnlyList<string> FontsUsed => StyleRuns.Select(r => r.Style.FontPostScriptName).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// Replaces <paramref name="length"/> characters at <paramref name="start"/> with <paramref name="insert"/>. Inserted
    /// characters take <paramref name="style"/>, or the style of the character before the edit (the first character's
    /// at the start), as typing in Photoshop does. Paragraph runs are rebuilt so each paragraph has one style.
    /// </summary>
    public TextLayerData ReplaceText(int start, int length, string insert, TextStyle? style = null)
    {
        ArgumentNullException.ThrowIfNull(insert);
        if (start < 0 || length < 0 || start + length > Text.Length) throw new ArgumentOutOfRangeException(nameof(start));
        var inserted = style ?? StyleAt(start > 0 ? start - 1 : 0);
        string text = string.Concat(Text.AsSpan(0, start), insert, Text.AsSpan(start + length));

        var styles = Expand(StyleRuns);
        styles.RemoveRange(start, length);
        styles.InsertRange(start, Enumerable.Repeat(inserted, insert.Length));

        // Each resulting paragraph keeps the style of the paragraph it starts in (or, for text inserted before
        // the first paragraph's end, the paragraph it was typed into).
        var oldParagraphs = Expand(ParagraphRuns);
        var fallback = ParagraphStyleAt(start);
        var paragraphs = new List<ParagraphStyle>(text.Length);
        int p = 0;
        while (p <= text.Length)
        {
            int end = text.IndexOf('\n', p);
            if (end < 0) end = text.Length;
            int oldIndex = p < start ? p : p >= start + insert.Length ? p - insert.Length + length : start;
            var ps = oldIndex < oldParagraphs.Count ? oldParagraphs[oldIndex] : oldParagraphs.Count > 0 ? oldParagraphs[^1] : fallback;
            for (int i = p; i < Math.Min(end + 1, text.Length); i++) paragraphs.Add(ps);
            if (text.Length == 0) paragraphs.Clear();
            p = end + 1;
        }
        return With(text, Compress(styles, text.Length == 0 ? inserted : null), Compress(paragraphs, text.Length == 0 ? fallback : null));
    }

    /// <summary>The same content with other text, every character in the first character's style.</summary>
    public TextLayerData WithText(string text) => ReplaceText(0, Text.Length, text, StyleAt(0));

    /// <summary>Applies <paramref name="change"/> to the character styles in the range.</summary>
    public TextLayerData ApplyStyle(int start, int length, Func<TextStyle, TextStyle> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (start < 0 || length < 0 || start + length > Text.Length) throw new ArgumentOutOfRangeException(nameof(start));
        if (Text.Length == 0) return With(Text, [new(0, change(StyleRuns[0].Style))], ParagraphRuns);
        var styles = Expand(StyleRuns);
        var cache = new Dictionary<TextStyle, TextStyle>(ReferenceEqualityComparer.Instance as IEqualityComparer<TextStyle>);
        for (int i = start; i < start + length; i++)
        {
            if (!cache.TryGetValue(styles[i], out var changed)) cache[styles[i]] = changed = change(styles[i]);
            styles[i] = changed;
        }
        return With(Text, Compress(styles, null), ParagraphRuns);
    }

    /// <summary>Applies <paramref name="change"/> to every character style.</summary>
    public TextLayerData ApplyStyle(Func<TextStyle, TextStyle> change) => ApplyStyle(0, Text.Length, change);

    /// <summary>Applies <paramref name="change"/> to every paragraph the range touches (whole paragraphs, as Photoshop does).</summary>
    public TextLayerData ApplyParagraphStyle(int start, int length, Func<ParagraphStyle, ParagraphStyle> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (Text.Length == 0) return With(Text, StyleRuns, [new(0, change(ParagraphRuns[0].Style))]);
        var paragraphs = Expand(ParagraphRuns);
        foreach (var (ps, pl) in Paragraphs)
        {
            bool touches = ps <= start + length && ps + pl >= start;
            if (!touches) continue;
            int end = Math.Min(ps + pl + 1, Text.Length);
            var changed = change(paragraphs[Math.Min(ps, paragraphs.Count - 1)]);
            for (int i = ps; i < end; i++) paragraphs[i] = changed;
        }
        return With(Text, StyleRuns, Compress(paragraphs, null));
    }

    /// <summary>Applies <paramref name="change"/> to every paragraph.</summary>
    public TextLayerData ApplyParagraphStyle(Func<ParagraphStyle, ParagraphStyle> change) => ApplyParagraphStyle(0, Text.Length, change);

    /// <summary>A copy with other text and runs, keeping every other property.</summary>
    public TextLayerData With(string text, IReadOnlyList<TextRun<TextStyle>> styleRuns, IReadOnlyList<TextRun<ParagraphStyle>> paragraphRuns) =>
        new(text, styleRuns, paragraphRuns)
        {
            Kind = Kind, Box = Box, Transform = Transform, Orientation = Orientation, AntiAlias = AntiAlias, Warp = Warp, SourceData = SourceData,
        };

    /// <summary>A copy with another position and orientation in the document.</summary>
    public TextLayerData WithTransform(TextTransform transform) =>
        new(Text, StyleRuns, ParagraphRuns)
        {
            Kind = Kind, Box = Box, Transform = transform, Orientation = Orientation, AntiAlias = AntiAlias, Warp = Warp, SourceData = SourceData,
        };

    /// <summary>A copy with other layout settings; null keeps the current value.</summary>
    public TextLayerData WithLayout(TextKind? kind = null, TextRect? box = null, TextAntiAlias? antiAlias = null, TextOrientation? orientation = null) =>
        new(Text, StyleRuns, ParagraphRuns)
        {
            Kind = kind ?? Kind, Box = box ?? Box, Transform = Transform, Orientation = orientation ?? Orientation,
            AntiAlias = antiAlias ?? AntiAlias, Warp = Warp, SourceData = SourceData,
        };

    /// <summary>
    /// Same text, runs and settings (<see cref="SourceData"/> is not compared; styles compare by value, including
    /// their own source data by reference).
    /// </summary>
    public bool Equals(TextLayerData? other) =>
        other is not null && Text == other.Text && Kind == other.Kind && Box == other.Box && Transform == other.Transform
        && Orientation == other.Orientation && AntiAlias == other.AntiAlias && Warp == other.Warp
        && StyleRuns.SequenceEqual(other.StyleRuns) && ParagraphRuns.SequenceEqual(other.ParagraphRuns);

    public override bool Equals(object? obj) => Equals(obj as TextLayerData);

    /// <summary>
    /// Same text, formatting and settings, ignoring where styles came from (<see cref="TextStyle.SourceData"/>): true
    /// for text read back after writing it.
    /// </summary>
    public bool SameContent(TextLayerData? other, double tolerance = 1e-5)
    {
        if (other is null || Text != other.Text || Kind != other.Kind || Orientation != other.Orientation || AntiAlias != other.AntiAlias
            || Warp != other.Warp || !Near(Box, other.Box, tolerance) || !Near(Transform, other.Transform, tolerance)
            || StyleRuns.Count != other.StyleRuns.Count || ParagraphRuns.Count != other.ParagraphRuns.Count)
            return false;
        for (int i = 0; i < StyleRuns.Count; i++)
            if (StyleRuns[i].Length != other.StyleRuns[i].Length || !Near(StyleRuns[i].Style, other.StyleRuns[i].Style, tolerance)) return false;
        for (int i = 0; i < ParagraphRuns.Count; i++)
            if (ParagraphRuns[i].Length != other.ParagraphRuns[i].Length || !Near(ParagraphRuns[i].Style, other.ParagraphRuns[i].Style, tolerance))
                return false;
        return true;
    }

    private static bool Near(TextRect a, TextRect b, double t) =>
        Math.Abs(a.Left - b.Left) <= t && Math.Abs(a.Top - b.Top) <= t && Math.Abs(a.Right - b.Right) <= t && Math.Abs(a.Bottom - b.Bottom) <= t;

    private static bool Near(TextTransform a, TextTransform b, double t) =>
        Math.Abs(a.XX - b.XX) <= t && Math.Abs(a.XY - b.XY) <= t && Math.Abs(a.YX - b.YX) <= t && Math.Abs(a.YY - b.YY) <= t
        && Math.Abs(a.TX - b.TX) <= t && Math.Abs(a.TY - b.TY) <= t;

    private static bool Near(TextStyle a, TextStyle b, double t)
    {
        var (fa, fb) = (a.FillColor, b.FillColor);
        bool color = Math.Abs(fa.R - fb.R) <= t && Math.Abs(fa.G - fb.G) <= t && Math.Abs(fa.B - fb.B) <= t && Math.Abs(fa.A - fb.A) <= t;
        return color && Math.Abs(a.FontSize - b.FontSize) <= t && Math.Abs(a.Leading - b.Leading) <= t
            && Math.Abs(a.BaselineShift - b.BaselineShift) <= t && Math.Abs(a.HorizontalScale - b.HorizontalScale) <= t
            && Math.Abs(a.VerticalScale - b.VerticalScale) <= t
            && a with { SourceData = null, FillColor = default, FontSize = 0, Leading = 0, BaselineShift = 0, HorizontalScale = 0, VerticalScale = 0 }
               == b with { SourceData = null, FillColor = default, FontSize = 0, Leading = 0, BaselineShift = 0, HorizontalScale = 0, VerticalScale = 0 };
    }

    private static bool Near(ParagraphStyle a, ParagraphStyle b, double t) =>
        Math.Abs(a.FirstLineIndent - b.FirstLineIndent) <= t && Math.Abs(a.StartIndent - b.StartIndent) <= t
        && Math.Abs(a.EndIndent - b.EndIndent) <= t && Math.Abs(a.SpaceBefore - b.SpaceBefore) <= t && Math.Abs(a.SpaceAfter - b.SpaceAfter) <= t
        && Math.Abs(a.AutoLeadingFactor - b.AutoLeadingFactor) <= t && a.Justification == b.Justification && a.Hyphenate == b.Hyphenate;

    public override int GetHashCode() => HashCode.Combine(Text, Kind, Box, Transform, StyleRuns.Count);

    public override string ToString() => $"{Kind} text \"{(Text.Length > 40 ? Text[..40] + "…" : Text)}\" ({StyleRuns.Count} style runs)";

    private static T At<T>(IReadOnlyList<TextRun<T>> runs, int index)
    {
        int at = 0;
        foreach (var run in runs)
        {
            if (index < at + run.Length) return run.Style;
            at += run.Length;
        }
        return runs[^1].Style;
    }

    private static List<T> Expand<T>(IReadOnlyList<TextRun<T>> runs)
    {
        var list = new List<T>();
        foreach (var run in runs)
            for (int i = 0; i < run.Length; i++) list.Add(run.Style);
        return list;
    }

    /// <summary>Joins neighbouring characters with equal styles into runs.</summary>
    private static List<TextRun<T>> Compress<T>(List<T> perChar, T? empty) where T : class
    {
        var runs = new List<TextRun<T>>();
        if (perChar.Count == 0)
        {
            runs.Add(new(0, empty!));
            return runs;
        }
        int start = 0;
        for (int i = 1; i <= perChar.Count; i++)
            if (i == perChar.Count || !Equals(perChar[i], perChar[start]))
            {
                runs.Add(new(i - start, perChar[start]));
                start = i;
            }
        return runs;
    }

    private static IReadOnlyList<TextRun<T>> Normalize<T>(IReadOnlyList<TextRun<T>> runs, int length, string name)
    {
        ArgumentNullException.ThrowIfNull(runs, name);
        if (runs.Count == 0) throw new ArgumentException("At least one run is needed (it holds the style of new text).", name);
        if (runs.Any(r => r.Length < 0 || r.Style is null)) throw new ArgumentException("Runs need a style and a non-negative length.", name);
        if (runs.Sum(r => r.Length) != length) throw new ArgumentException($"Runs cover {runs.Sum(r => r.Length)} characters, the text has {length}.", name);
        var kept = runs.Where(r => r.Length > 0).ToList();
        return kept.Count > 0 ? kept : [runs[^1]];
    }
}
