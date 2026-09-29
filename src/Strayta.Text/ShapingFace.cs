using System.Collections.Concurrent;
using HarfBuzzSharp;
using SkiaSharp;

namespace Strayta.Text;

/// <summary>
/// A typeface ready for shaping and drawing: its HarfBuzz font (for glyph selection, kerning and ligatures), its
/// vertical metrics, and glyph outlines cached at 1 unit per em.
/// </summary>
internal sealed class ShapingFace
{
    private static readonly ConcurrentDictionary<SKTypeface, ShapingFace> Cache = new(ReferenceEqualityComparer.Instance);

    private readonly object _gate = new();
    private readonly HarfBuzzSharp.Font _font;
    private readonly ConcurrentDictionary<ushort, SKPath?> _outlines = new();
    private readonly SKFont _unitFont;

    public SKTypeface Typeface { get; }
    public int UnitsPerEm { get; }

    /// <summary>Metrics per unit of font size (multiply by the size in text-space units).</summary>
    public double Ascent { get; }
    public double Descent { get; }
    public double CapHeight { get; }
    public double XHeight { get; }
    public double UnderlinePosition { get; }
    public double UnderlineThickness { get; }
    public double StrikeoutPosition { get; }
    public double StrikeoutThickness { get; }

    private ShapingFace(SKTypeface typeface)
    {
        Typeface = typeface;
        using var stream = typeface.OpenStream(out int index) ?? throw new InvalidOperationException($"Font '{typeface.FamilyName}' has no data.");
        // HarfBuzz keeps pointing at the font data: give it its own copy, freed with the blob.
        using var data = SKData.Create(stream);
        var bytes = data.ToArray();
        IntPtr memory = System.Runtime.InteropServices.Marshal.AllocHGlobal(bytes.Length);
        System.Runtime.InteropServices.Marshal.Copy(bytes, 0, memory, bytes.Length);
        var blob = new Blob(memory, bytes.Length, MemoryMode.ReadOnly, () => System.Runtime.InteropServices.Marshal.FreeHGlobal(memory));
        var face = new Face(blob, index);
        UnitsPerEm = face.UnitsPerEm > 0 ? face.UnitsPerEm : 1000;
        _font = new HarfBuzzSharp.Font(face);
        _font.SetFunctionsOpenType();
        _font.SetScale(UnitsPerEm, UnitsPerEm);

        // Outlines and metrics at size 1 would lose precision in Skia's fixed-point paths; use 1000 and scale.
        _unitFont = new SKFont(typeface, 1000) { Hinting = SKFontHinting.None, Subpixel = true, LinearMetrics = true, Edging = SKFontEdging.Antialias };
        var m = _unitFont.Metrics;
        // Ascent and descent as Photoshop measures type: the font's typographic ascender and descender when the OS/2
        // table has them (Skia reports the hhea values, which some fonts pad for line spacing).
        var (typoAscent, typoDescent) = TypoMetrics(typeface);
        Ascent = typoAscent ?? -m.Ascent / 1000.0;
        Descent = typoDescent ?? m.Descent / 1000.0;
        CapHeight = m.CapHeight / 1000.0;
        XHeight = m.XHeight / 1000.0;
        UnderlinePosition = (m.UnderlinePosition ?? 100) / 1000.0;
        UnderlineThickness = (m.UnderlineThickness ?? 50) / 1000.0;
        StrikeoutPosition = (m.StrikeoutPosition ?? -m.XHeight / 2) / 1000.0;
        StrikeoutThickness = (m.StrikeoutThickness ?? m.UnderlineThickness ?? 50) / 1000.0;
    }

    public static ShapingFace For(SKTypeface typeface) => Cache.GetOrAdd(typeface, tf => new ShapingFace(tf));

    /// <summary>OS/2 sTypoAscender and sTypoDescender over units per em, when the table is there.</summary>
    private static (double?, double?) TypoMetrics(SKTypeface typeface)
    {
        uint os2 = 0x4F532F32; // 'OS/2'
        uint head = 0x68656164; // 'head'
        if (!typeface.TryGetTableData(os2, out var t) || t.Length < 72 || !typeface.TryGetTableData(head, out var h) || h.Length < 20)
            return (null, null);
        int upem = h[18] << 8 | h[19];
        if (upem <= 0) return (null, null);
        short ascender = (short)(t[68] << 8 | t[69]);
        short descender = (short)(t[70] << 8 | t[71]);
        if (ascender <= 0) return (null, null);
        return (ascender / (double)upem, -descender / (double)upem);
    }

    /// <summary>Shapes <paramref name="length"/> characters of <paramref name="text"/> at <paramref name="start"/> (the rest is context).</summary>
    public (GlyphInfo[] Infos, GlyphPosition[] Positions) Shape(string text, int start, int length, Feature[] features)
    {
        using var buffer = new HarfBuzzSharp.Buffer();
        buffer.ClusterLevel = ClusterLevel.MonotoneCharacters;
        buffer.AddUtf16(text, start, length);
        buffer.GuessSegmentProperties();
        lock (_gate) _font.Shape(buffer, features);
        return (buffer.GlyphInfos, buffer.GlyphPositions);
    }

    /// <summary>True when the font maps <paramref name="codepoint"/> to a glyph.</summary>
    public bool HasGlyph(int codepoint)
    {
        lock (_gate) return _font.TryGetGlyph((uint)codepoint, out uint glyph) && glyph != 0;
    }

    /// <summary>A glyph's outline at a size of 1000 units (y down), or null for blank glyphs. Shared: do not modify.</summary>
    public SKPath? Outline(ushort glyph) => _outlines.GetOrAdd(glyph, g =>
    {
        lock (_gate)
        {
            var path = _unitFont.GetGlyphPath(g);
            if (path is null || path.IsEmpty)
            {
                path?.Dispose();
                return null;
            }
            return path;
        }
    });
}
