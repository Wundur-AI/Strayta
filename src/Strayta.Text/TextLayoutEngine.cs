using HarfBuzzSharp;
using SkiaSharp;
using Strayta.Core.Text;

namespace Strayta.Text;

/// <summary>
/// Lays text out the way Photoshop's type engine does for roman text:
/// <list type="bullet">
/// <item>Each paragraph is shaped with HarfBuzz run by run (font, size, kerning and ligature features from the
/// style); tracking adds tracking/1000 em after every character, manual kerning adds its amount before the character
/// that carries it, and horizontal scale stretches advances.</item>
/// <item>Point text breaks lines only at paragraph ends and forced line breaks; paragraph text also wraps words
/// that do not fit between the indents (breaking after spaces and hyphens, anywhere in CJK text, and inside a word
/// only when it is wider than the line).</item>
/// <item>Point text puts its first baseline at the origin; paragraph text puts it one ascent (the largest on the
/// line) below the box top. Every further line sits one leading below the previous baseline, the leading being the
/// largest on the line (auto leading = size × the paragraph's auto-leading factor, 120% by default), plus space after
/// the previous paragraph and space before the next.</item>
/// <item>Lines align against the box (between the indents) or, for point text, around the origin (left text starts
/// there, right text ends there, centred text is centred on it). Justified box text spreads the extra room over the
/// spaces of every line but a paragraph's last (which aligns as the justification's suffix says; Justify All
/// spreads that too).</item>
/// </list>
/// </summary>
internal static class TextLayoutEngine
{
    private const double SmallCapSize = 0.7;
    private const double SuperscriptSize = 0.583, SuperscriptPosition = 0.333, SubscriptSize = 0.583, SubscriptPosition = 0.333;

    /// <summary>A shaped character group (one or more characters that shape together, e.g. a ligature).</summary>
    private sealed class Cluster
    {
        public int Start;          // index in the text
        public int Length;         // characters
        public double Advance;     // text-space units, tracking and kerning included
        public double KernBefore;  // manual kerning placed before the first glyph
        public readonly List<PlacedGlyph> Glyphs = [];  // X relative to the cluster start, Y relative to the baseline
        public double Ascent, Descent, Leading;
        public bool IsSpace, BreakAfter, ForcedBreak, IsCjk;
        public TextStyle Style = null!;
        public ShapingFace Face = null!;
        public double Size;
    }

    public static TextLayout Layout(TextLayerData data, FontCatalog fonts)
    {
        var missing = new List<string>();
        var warnings = new List<string>();
        if (data.Warp is not null) warnings.Add($"Warped text ({data.Warp}) is drawn unwarped.");
        if (data.Orientation == TextOrientation.Vertical) warnings.Add("Vertical text is drawn horizontally.");
        string? substituteName = null;
        SKTypeface? substitute = null;

        ShapingFace FaceFor(string postScriptName)
        {
            var tf = fonts.GetTypeface(postScriptName);
            if (tf is null)
            {
                if (!missing.Contains(postScriptName) && postScriptName.Length > 0) missing.Add(postScriptName);
                if (substitute is null)
                {
                    substituteName = fonts.FallbackPostScriptName;
                    substitute = (substituteName is null ? null : fonts.GetTypeface(substituteName)) ?? SKTypeface.Default;
                    substituteName ??= substitute.PostScriptName;
                }
                tf = substitute;
            }
            return ShapingFace.For(tf);
        }

        bool box = data.Kind == TextKind.Paragraph;
        string text = data.Text;
        var lines = new List<TextLine>();
        var carets = new List<double[]>();
        var glyphs = new List<PlacedGlyph>();
        var decorations = new List<Decoration>();
        double baseline = 0;
        bool firstLine = true;
        double previousSpaceAfter = 0;
        bool hideRest = false;

        foreach (var (pStart, pLength) in data.Paragraphs)
        {
            var para = data.ParagraphStyleAt(Math.Min(pStart, Math.Max(0, text.Length - 1)));
            var clusters = Shape(data, pStart, pLength, para, FaceFor);
            var paragraphLines = BreakLines(clusters, box ? data.Box.Width - para.StartIndent - para.EndIndent : double.PositiveInfinity, para.FirstLineIndent);
            if (paragraphLines.Count == 0) paragraphLines.Add((0, 0));
            for (int li = 0; li < paragraphLines.Count; li++)
            {
                var (from, count) = paragraphLines[li];
                var lineClusters = clusters.GetRange(from, count);
                // An empty line still has the height of the style at its position.
                var metricsStyle = lineClusters.Count > 0 ? null : data.StyleAt(Math.Min(pStart, Math.Max(0, text.Length - 1)));
                double ascent, descent, leading;
                if (metricsStyle is not null)
                {
                    var face = FaceFor(metricsStyle.FontPostScriptName);
                    ascent = face.Ascent * metricsStyle.FontSize;
                    descent = face.Descent * metricsStyle.FontSize;
                    leading = metricsStyle.LineSpacing(para.AutoLeadingFactor);
                }
                else
                {
                    ascent = lineClusters.Max(c => c.Ascent);
                    descent = lineClusters.Max(c => c.Descent);
                    leading = lineClusters.Max(c => c.Leading);
                }

                if (firstLine)
                    baseline = box ? data.Box.Top + FirstBaseline(ascent) : 0;
                else
                    baseline += leading + (li == 0 ? previousSpaceAfter + para.SpaceBefore : 0);
                firstLine = false;

                // Horizontal placement.
                bool firstOfParagraph = li == 0;
                bool lastOfParagraph = li == paragraphLines.Count - 1;
                double indent = para.StartIndent + (firstOfParagraph ? para.FirstLineIndent : 0);
                int visibleCount = lineClusters.Count;
                // Spaces at a wrap hang outside the box.
                if (box && !lastOfParagraph)
                    while (visibleCount > 0 && (lineClusters[visibleCount - 1].IsSpace || lineClusters[visibleCount - 1].ForcedBreak)) visibleCount--;
                double width = 0;
                for (int k = 0; k < lineClusters.Count; k++) width += lineClusters[k].Advance;
                double visibleWidth = 0;
                for (int k = 0; k < visibleCount; k++) visibleWidth += lineClusters[k].Advance;

                var align = para.Justification;
                bool forcedEnd = lineClusters.Count > 0 && lineClusters[^1].ForcedBreak;
                bool justify = box && align is TextJustification.JustifyAll
                    || box && align is TextJustification.JustifyLastLeft or TextJustification.JustifyLastRight or TextJustification.JustifyLastCenter
                        && !lastOfParagraph && !forcedEnd;
                var lineAlign = align switch
                {
                    TextJustification.JustifyLastLeft or TextJustification.JustifyAll => TextJustification.Left,
                    TextJustification.JustifyLastRight => TextJustification.Right,
                    TextJustification.JustifyLastCenter => TextJustification.Center,
                    _ => align,
                };
                double x0;
                double extraPerSpace = 0;
                if (box)
                {
                    double left = data.Box.Left + indent, right = data.Box.Right - para.EndIndent;
                    if (justify)
                    {
                        int spaces = 0;
                        for (int k = 0; k < visibleCount - 1; k++) if (lineClusters[k].IsSpace) spaces++;
                        if (spaces > 0) extraPerSpace = Math.Max(0, (right - left - visibleWidth) / spaces);
                        x0 = left;
                        if (spaces == 0) x0 = AlignX(lineAlign, left, right, visibleWidth);
                    }
                    else x0 = AlignX(lineAlign, left, right, visibleWidth);
                }
                else
                {
                    x0 = lineAlign switch
                    {
                        TextJustification.Right => -width - para.EndIndent,
                        TextJustification.Center => -width / 2 + (indent - para.EndIndent) / 2,
                        _ => indent,
                    };
                }

                bool visible = !hideRest && (!box || baseline + descent <= data.Box.Bottom + 0.5);
                if (!visible) hideRest = true;

                // Glyphs, carets and decorations.
                int lineStart = lineClusters.Count > 0 ? lineClusters[0].Start : pStart + pLength;
                int lineEnd = lineClusters.Count > 0 ? lineClusters[^1].Start + lineClusters[^1].Length : lineStart;
                var xs = new double[lineEnd - lineStart + 1];
                double x = x0;
                for (int k = 0; k < lineClusters.Count; k++)
                {
                    var c = lineClusters[k];
                    double start = x;
                    if (visible)
                        foreach (var g in c.Glyphs)
                        {
                            var placed = g with { X = x + g.X, Y = baseline + g.Y };
                            glyphs.Add(placed);
                        }
                    double advance = c.Advance + (c.IsSpace && k < visibleCount - 1 ? extraPerSpace : 0);
                    for (int j = 0; j < c.Length; j++)
                        xs[c.Start - lineStart + j] = start + c.KernBefore + (advance - c.KernBefore) * j / c.Length;
                    if (visible && (c.Style.Underline || c.Style.Strikethrough) && !c.IsSpace && !c.ForcedBreak)
                    {
                        double size = c.Size;
                        if (c.Style.Underline)
                        {
                            double y = baseline + c.Face.UnderlinePosition * size;
                            double t = Math.Max(c.Face.UnderlineThickness * size, 0.5);
                            decorations.Add(new Decoration(new TextRect(start, y - t / 2, start + advance, y + t / 2), c.Style.FillColor));
                        }
                        if (c.Style.Strikethrough)
                        {
                            double y = baseline + c.Face.StrikeoutPosition * size;
                            double t = Math.Max(c.Face.StrikeoutThickness * size, 0.5);
                            decorations.Add(new Decoration(new TextRect(start, y - t / 2, start + advance, y + t / 2), c.Style.FillColor));
                        }
                    }
                    x += advance;
                }
                xs[^1] = x;
                lines.Add(new TextLine(lineStart, lineEnd - lineStart, baseline, ascent, descent, x0, x, visible));
                carets.Add(xs);
            }
            previousSpaceAfter = para.SpaceAfter;
        }

        MergeDecorations(decorations);
        var visibleLines = lines.Where(l => l.Visible).ToList();
        TextRect bounds = visibleLines.Count == 0
            ? new TextRect(0, 0, 0, 0)
            : new TextRect(visibleLines.Min(l => l.Left), visibleLines[0].Top, visibleLines.Max(l => l.Right), visibleLines[^1].Bottom);
        if (box) bounds = data.Box;
        return new TextLayout(data, lines, carets, glyphs, decorations, bounds, InkBounds(glyphs, decorations), missing, warnings,
            missing.Count > 0 ? substituteName : null);
    }

    /// <summary>
    /// Paragraph text's first baseline below the box top: the line's ascent (Photoshop's default "Ascent" first
    /// baseline; measured to a fraction of a pixel on 130 px Calisto, where rounding it either way fits worse).
    /// </summary>
    private static double FirstBaseline(double ascent) => ascent;

    private static double AlignX(TextJustification align, double left, double right, double width) => align switch
    {
        TextJustification.Right => right - width,
        TextJustification.Center => (left + right - width) / 2,
        _ => left,
    };

    /// <summary>Neighbouring underline pieces of one color become one bar.</summary>
    private static void MergeDecorations(List<Decoration> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            var (a, b) = (list[i - 1], list[i]);
            if (a.Color == b.Color && Math.Abs(a.Rect.Right - b.Rect.Left) < 1e-6 && Math.Abs(a.Rect.Top - b.Rect.Top) < 1e-6 && Math.Abs(a.Rect.Bottom - b.Rect.Bottom) < 1e-6)
            {
                list[i - 1] = a with { Rect = a.Rect with { Right = b.Rect.Right } };
                list.RemoveAt(i);
            }
        }
    }

    private static TextRect InkBounds(List<PlacedGlyph> glyphs, List<Decoration> decorations)
    {
        double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, b = double.MinValue;
        foreach (var g in glyphs)
        {
            if (g.Face.Outline(g.Glyph) is not { } path) continue;
            var o = path.TightBounds;
            double sx = g.Size * g.ScaleX / 1000, sy = g.Size * g.ScaleY / 1000;
            double skew = g.FauxItalic ? FauxItalicSkew : 0;
            double x0 = g.X + o.Left * sx + Math.Min(0, -skew * o.Bottom * sy) - skew * 0;
            double x1 = g.X + o.Right * sx + Math.Max(0, -skew * o.Top * sy);
            double grow = g.FauxBold ? FauxBoldWidth(g.Size) / 2 : 0;
            l = Math.Min(l, x0 - grow);
            r = Math.Max(r, x1 + grow);
            t = Math.Min(t, g.Y + o.Top * sy - grow);
            b = Math.Max(b, g.Y + o.Bottom * sy + grow);
        }
        foreach (var d in decorations)
        {
            l = Math.Min(l, d.Rect.Left);
            r = Math.Max(r, d.Rect.Right);
            t = Math.Min(t, d.Rect.Top);
            b = Math.Max(b, d.Rect.Bottom);
        }
        return l > r ? new TextRect(0, 0, 0, 0) : new TextRect(l, t, r, b);
    }

    /// <summary>Faux italic slants glyphs by this much (x per unit of height, upwards to the right).</summary>
    internal const double FauxItalicSkew = 0.2;

    /// <summary>Faux bold outlines glyphs with a stroke this wide.</summary>
    internal static double FauxBoldWidth(double size) => size / 30;

    // ---- Shaping --------------------------------------------------------------------------------

    /// <summary>
    /// What makes characters shape together: same font, size, case scaling and features. Colors, tracking, kerning
    /// settings and baseline shifts may differ inside a piece (kerning between two characters follows the second one's
    /// setting, as in Photoshop, so pairs across such changes still kern).
    /// </summary>
    private readonly record struct PieceKey(string Font, double Size, double CaseScale, TextBaselinePosition Position, double ScaleX, double ScaleY,
        bool Ligatures, bool DiscretionaryLigatures, bool FauxBold, bool FauxItalic);

    private static List<Cluster> Shape(TextLayerData data, int pStart, int pLength, ParagraphStyle para, Func<string, ShapingFace> faceFor)
    {
        var clusters = new List<Cluster>();
        if (pLength == 0) return clusters;

        // Per character: its style, the character to shape (case applied; one per character so indices stay put)
        // and its piece key.
        var styles = new TextStyle[pLength];
        var chars = data.Text.Substring(pStart, pLength).ToCharArray();
        var keys = new PieceKey[pLength];
        int at = 0;
        foreach (var run in data.StyleRuns)
        {
            int a = Math.Max(at, pStart), b = Math.Min(at + run.Length, pStart + pLength);
            at += run.Length;
            for (int i = a; i < b; i++) styles[i - pStart] = run.Style;
        }
        for (int i = 0; i < pLength; i++)
        {
            var s = styles[i];
            double caseScale = 1;
            if (s.Caps == TextCaps.AllCaps) chars[i] = char.ToUpperInvariant(chars[i]);
            else if (s.Caps == TextCaps.SmallCaps && char.IsLower(chars[i]))
            {
                chars[i] = char.ToUpperInvariant(chars[i]);
                caseScale = SmallCapSize;
            }
            keys[i] = new PieceKey(s.FontPostScriptName, s.FontSize, caseScale, s.BaselinePosition, s.HorizontalScale, s.VerticalScale,
                s.Ligatures, s.DiscretionaryLigatures, s.FauxBold, s.FauxItalic);
        }
        string context = new(chars);

        int start = 0;
        while (start < pLength)
        {
            int end = start + 1;
            while (end < pLength && keys[end] == keys[start]) end++;
            ShapePiece(context, pStart, pStart + start, pStart + end, styles, keys[start], para, faceFor, clusters);
            start = end;
        }

        // Break opportunities.
        for (int i = 0; i < clusters.Count; i++)
        {
            var c = clusters[i];
            var next = i + 1 < clusters.Count ? clusters[i + 1] : null;
            if (next is null) continue;
            if (c.ForcedBreak) c.BreakAfter = true;
            else if (c.IsSpace && !next.IsSpace) c.BreakAfter = true;
            else if (!c.IsSpace && !next.IsSpace && (c.IsCjk || next.IsCjk)) c.BreakAfter = true;
            else if (IsHyphen(data.Text[c.Start + c.Length - 1]) && !next.IsSpace && c.Start > pStart) c.BreakAfter = true;
        }
        return clusters;
    }

    private static bool IsHyphen(char c) => c is '-' or '‐' or '‒' or '–' or '—';

    private static bool IsCjk(char c) =>
        c is >= '⺀' and <= '鿿' or >= '가' and <= '힯' or >= '豈' and <= '﫿' or >= '＀' and <= '￯';

    private static void ShapePiece(string context, int contextStart, int start, int end, TextStyle[] styles, PieceKey key, ParagraphStyle para,
        Func<string, ShapingFace> faceFor, List<Cluster> clusters)
    {
        var face = faceFor(key.Font);
        // Split off characters the font lacks and shape them with a font that has them.
        int i = start;
        while (i < end)
        {
            int j = i;
            bool fallback = NeedsFallback(face, context, i - contextStart);
            while (j < end && NeedsFallback(face, context, j - contextStart) == fallback) j += char.IsSurrogatePair(context, j - contextStart) ? 2 : 1;
            j = Math.Min(j, end);
            var pieceFace = fallback ? FallbackFace(face, context, i - contextStart) ?? face : face;
            ShapeSpan(context, contextStart, i, j, styles, key, para, pieceFace, clusters);
            i = j;
        }
    }

    private static bool NeedsFallback(ShapingFace face, string context, int index)
    {
        if (index >= context.Length) return false;
        char c = context[index];
        if (char.IsWhiteSpace(c) || char.IsControl(c)) return false;
        int cp = char.IsSurrogatePair(context, index) ? char.ConvertToUtf32(context, index) : c;
        return !face.HasGlyph(cp);
    }

    private static ShapingFace? FallbackFace(ShapingFace face, string context, int index)
    {
        int cp = char.IsSurrogatePair(context, index) ? char.ConvertToUtf32(context, index) : context[index];
        var tf = SKFontManager.Default.MatchCharacter(face.Typeface.FamilyName, face.Typeface.FontStyle, null, cp);
        return tf is null ? null : ShapingFace.For(CanonicalTypeface(tf));
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SKTypeface> FallbackTypefaces = new();

    /// <summary>One typeface object per font, so shaping data is built once per fallback font.</summary>
    private static SKTypeface CanonicalTypeface(SKTypeface tf)
    {
        string key = tf.PostScriptName ?? tf.FamilyName;
        var kept = FallbackTypefaces.GetOrAdd(key, tf);
        if (!ReferenceEquals(kept, tf)) tf.Dispose();
        return kept;
    }

    private static void ShapeSpan(string context, int contextStart, int start, int end, TextStyle[] styles, PieceKey key, ParagraphStyle para,
        ShapingFace face, List<Cluster> clusters)
    {
        TextStyle StyleOf(int textIndex) => styles[textIndex - contextStart];
        double positionScale = key.Position switch
        {
            TextBaselinePosition.Superscript => SuperscriptSize,
            TextBaselinePosition.Subscript => SubscriptSize,
            _ => 1,
        };
        double size = key.Size * key.CaseScale * positionScale;
        Feature[] Features(bool kern) =>
        [
            new(Tag.Parse("kern"), kern ? 1u : 0u),
            new(Tag.Parse("liga"), key.Ligatures ? 1u : 0u),
            new(Tag.Parse("clig"), key.Ligatures ? 1u : 0u),
            new(Tag.Parse("dlig"), key.DiscretionaryLigatures ? 1u : 0u),
        ];
        // Shape with and without kerning; each pair then takes what its second character's style asks for.
        bool anyKern = false, anyPlain = false;
        for (int i = start; i < end; i++)
        {
            if (StyleOf(i).Kerning == TextKerning.None) anyPlain = true;
            else anyKern = true;
        }
        var (infos, kerned) = face.Shape(context, start - contextStart, end - start, Features(anyKern));
        var plain = anyKern && anyPlain ? face.Shape(context, start - contextStart, end - start, Features(false)).Positions : null;
        if (plain is not null && plain.Length != kerned.Length) plain = null;

        double unit = size / face.UnitsPerEm;
        double scaleX = key.ScaleX, scaleY = key.ScaleY;

        // Group glyphs by cluster (the index of the first character each came from).
        var starts = infos.Select(g => (int)g.Cluster + contextStart).Distinct().Order().ToList();
        if (starts.Count == 0 || starts[0] != start) starts.Insert(0, start);
        var byStart = starts.ToDictionary(s => s, s => new Cluster { Start = s, Style = StyleOf(s), Face = face, Size = size });
        for (int k = 0; k < starts.Count; k++) byStart[starts[k]].Length = (k + 1 < starts.Count ? starts[k + 1] : end) - starts[k];
        var pen = new Dictionary<int, double>();
        for (int g = 0; g < infos.Length; g++)
        {
            int s = (int)infos[g].Cluster + contextStart;
            var c = byStart[s];
            var style = c.Style;
            // The pair after this glyph kerns only if the character after its cluster allows it.
            int next = s + c.Length;
            bool lastInCluster = g + 1 >= infos.Length || (int)infos[g + 1].Cluster + contextStart != s;
            var p = plain is not null && lastInCluster && next < end && StyleOf(next).Kerning == TextKerning.None ? plain[g] : kerned[g];
            double rise = style.BaselinePosition switch
            {
                TextBaselinePosition.Superscript => style.FontSize * SuperscriptPosition,
                TextBaselinePosition.Subscript => -style.FontSize * SubscriptPosition,
                _ => 0,
            } + style.BaselineShift;
            double penX = pen.GetValueOrDefault(s);
            c.Glyphs.Add(new PlacedGlyph(face, (ushort)infos[g].Codepoint, penX + p.XOffset * unit * scaleX, -(p.YOffset * unit + rise),
                size, scaleX, scaleY, key.FauxItalic, key.FauxBold, style.FillColor, s));
            pen[s] = penX + p.XAdvance * unit * scaleX;
        }
        foreach (int s in starts)
        {
            var c = byStart[s];
            var style = c.Style;
            string chars = context.Substring(s - contextStart, c.Length);
            c.ForcedBreak = chars.Contains(TextLayerData.LineBreak) || chars.Contains('\u2028');
            if (c.ForcedBreak) c.Glyphs.Clear();
            c.IsSpace = !c.ForcedBreak && chars.All(char.IsWhiteSpace);
            c.IsCjk = chars.Any(IsCjk);
            // Tracking follows each character; manual kerning sits before the character that carries it.
            double tracking = style.Tracking / 1000.0 * style.FontSize;
            c.KernBefore = c.ForcedBreak ? 0 : style.ManualKerning / 1000.0 * style.FontSize;
            double glyphAdvance = c.ForcedBreak ? 0 : pen.GetValueOrDefault(s);
            c.Advance = c.ForcedBreak ? 0 : glyphAdvance + tracking * c.Length + c.KernBefore;
            if (c.KernBefore != 0)
                for (int g = 0; g < c.Glyphs.Count; g++) c.Glyphs[g] = c.Glyphs[g] with { X = c.Glyphs[g].X + c.KernBefore };
            // Superscripts and baseline shifts do not change the line's ascent in Photoshop's roman composer.
            c.Ascent = face.Ascent * style.FontSize;
            c.Descent = face.Descent * style.FontSize;
            c.Leading = style.LineSpacing(para.AutoLeadingFactor);
            clusters.Add(c);
        }
    }
    // ---- Line breaking --------------------------------------------------------------------------

    /// <summary>Greedy line breaking. Returns (first cluster, cluster count) per line.</summary>
    private static List<(int From, int Count)> BreakLines(List<Cluster> clusters, double width, double firstIndent)
    {
        var lines = new List<(int, int)>();
        int lineStart = 0;
        while (lineStart < clusters.Count)
        {
            double available = width - (lines.Count == 0 ? firstIndent : 0);
            double x = 0;
            int lastBreak = -1;
            int end = clusters.Count;
            for (int i = lineStart; i < clusters.Count; i++)
            {
                var c = clusters[i];
                if (c.ForcedBreak)
                {
                    end = i + 1;
                    break;
                }
                if (!c.IsSpace && x + c.Advance > available + 1e-6 && i > lineStart)
                {
                    end = lastBreak >= lineStart ? lastBreak + 1 : i;
                    break;
                }
                x += c.Advance;
                if (c.BreakAfter) lastBreak = i;
            }
            lines.Add((lineStart, end - lineStart));
            lineStart = end;
        }
        // A forced break at the very end starts an empty last line.
        if (clusters.Count > 0 && clusters[^1].ForcedBreak) lines.Add((clusters.Count, 0));
        return lines;
    }
}
