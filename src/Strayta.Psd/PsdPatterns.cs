using System.Text;
using Strayta.Core;

namespace Strayta.Psd;

/// <summary>
/// The document's patterns: the 'Patt' (8-bit), 'Pat2' (16-bit) and 'Pat3' (32-bit) global blocks that Pattern
/// Overlay, pattern strokes and bevel textures refer to by id. Each pattern is a header (version, color mode, size,
/// Unicode name, Pascal-string id, palette for indexed patterns) and its pixels as a "virtual memory array list": one
/// array per color channel plus a user mask and a sheet mask, each written or not, raw or PackBits-compressed.
/// Following Adobe's file format specification; transparency is the first written array after the color channels.
/// </summary>
public static class PsdPatterns
{
    public static readonly string[] BlockKeys = ["Patt", "Pat2", "Pat3"];

    /// <summary>Every pattern in the file's pattern blocks; unreadable patterns are skipped.</summary>
    public static List<Pattern> Read(PsdFile file)
    {
        var patterns = new List<Pattern>();
        foreach (var block in file.GlobalBlocks)
            if (BlockKeys.Contains(block.Key) && block.Data is { Length: > 0 } data)
                patterns.AddRange(Parse(data));
        return patterns;
    }

    /// <summary>The patterns in one block's data.</summary>
    public static List<Pattern> Parse(byte[] data)
    {
        var patterns = new List<Pattern>();
        var r = new BigEndianReader(new MemoryStream(data, writable: false));
        while (r.Position + 4 <= data.Length)
        {
            long length = r.ReadUInt32();
            long start = r.Position, end = start + length;
            if (length == 0 || end > data.Length) break;
            try
            {
                patterns.Add(ParseOne(r, (int)length));
            }
            catch (Exception ex) when (ex is PsdFormatException or ArgumentException or InvalidDataException or IndexOutOfRangeException)
            {
                // Keep the rest: one odd pattern should not hide the others.
            }
            r.Position = (end + 3) & ~3L;
        }
        return patterns;
    }

    private static Pattern ParseOne(BigEndianReader r, int length)
    {
        long end = r.Position + length;
        r.ReadUInt32(); // version 1
        var mode = (ColorMode)r.ReadUInt32();
        r.ReadInt16(); // height
        r.ReadInt16(); // width
        string name = r.ReadUnicodeString();
        string id = r.ReadPascalString(1);
        byte[]? palette = mode == ColorMode.Indexed ? r.ReadBytes(768) : null;

        // Virtual memory array list.
        r.ReadUInt32(); // version 3
        r.ReadUInt32(); // length
        int top = r.ReadInt32(), left = r.ReadInt32(), bottom = r.ReadInt32(), right = r.ReadInt32();
        int width = right - left, height = bottom - top;
        int channelCount = (int)r.ReadUInt32();
        var planes = new List<Plane>();
        for (int i = 0; i < channelCount + 2 && r.Position < end; i++)
        {
            if (r.ReadUInt32() == 0) continue; // not written
            long arrayLength = r.ReadUInt32();
            if (arrayLength == 0) continue;
            long next = r.Position + arrayLength;
            int depth = (int)r.ReadUInt32();
            int t = r.ReadInt32(), l = r.ReadInt32(), b = r.ReadInt32(), rr = r.ReadInt32();
            r.ReadUInt16(); // depth again
            var compression = (PsdCompression)r.ReadByte();
            var bytes = r.ReadBytes(next - r.Position);
            planes.Add(ChannelDecoder.Decode(bytes, compression, rr - l, b - t, depth, psb: false));
            r.Position = next;
        }

        int colorCount = mode switch { ColorMode.Rgb => 3, ColorMode.Cmyk => 4, _ => 1 };
        var tile = planes.Count < colorCount || width <= 0 || height <= 0 ? null : ToRgb(mode, planes, colorCount, palette);
        return new Pattern(id, name) { Pixels = tile };
    }

    /// <summary>The tile as 8-bit RGB (grayscale stays grayscale), with the extra plane, if any, as transparency.</summary>
    private static Raster ToRgb(ColorMode mode, List<Plane> planes, int colorCount, byte[]? palette)
    {
        var first = planes[0];
        int n = first.Width * first.Height;
        Plane To8(Plane p)
        {
            if (p.BitDepth == 8) return p;
            var o = Plane.Create(p.Width, p.Height, 8);
            for (int i = 0; i < n; i++) o.Data[i] = RgbaConverter.ToByte(p.GetNormalized(i));
            return o;
        }
        var color = planes.Take(colorCount).Select(To8).ToList();
        var alpha = planes.Count > colorCount && planes[colorCount].Width == first.Width && planes[colorCount].Height == first.Height
            ? To8(planes[colorCount]) : null;

        switch (mode)
        {
            case ColorMode.Grayscale:
                return new Raster(ColorMode.Grayscale, color, alpha);
            case ColorMode.Indexed when palette is not null:
            {
                var rgb = Enumerable.Range(0, 3).Select(_ => Plane.Create(first.Width, first.Height, 8)).ToArray();
                for (int i = 0; i < n; i++)
                {
                    int index = color[0].Data[i];
                    for (int c = 0; c < 3; c++) rgb[c].Data[i] = palette[index * 3 + c];
                }
                return new Raster(ColorMode.Rgb, rgb, alpha);
            }
            case ColorMode.Cmyk:
            {
                // Photoshop stores CMYK inverted (0 = full ink); a plain conversion is enough for a tile preview and fill.
                var rgb = Enumerable.Range(0, 3).Select(_ => Plane.Create(first.Width, first.Height, 8)).ToArray();
                for (int i = 0; i < n; i++)
                {
                    float k = color[3].Data[i] / 255f;
                    for (int c = 0; c < 3; c++) rgb[c].Data[i] = (byte)MathF.Round(color[c].Data[i] * k);
                }
                return new Raster(ColorMode.Rgb, rgb, alpha);
            }
            default:
                return new Raster(ColorMode.Rgb, color.Count >= 3 ? color.Take(3).ToList() : [color[0], color[0], color[0]], alpha);
        }
    }

    // ---- Writing -----------------------------------------------------------------------------------

    /// <summary>
    /// The patterns <paramref name="doc"/> has or its effects use (with pixels) that the source file's pattern blocks
    /// do not hold yet: what saving must add so every pattern effect finds its pattern when Photoshop opens the file.
    /// </summary>
    public static List<Pattern> Missing(Document doc, PsdFile? source)
    {
        var known = source is null ? [] : Read(source).Select(p => p.Id).ToHashSet();
        var used = doc.Root.Descendants().SelectMany(n => n.Effects?.Items ?? []).Select(e => e switch
        {
            PatternOverlayEffect o => o.Fill?.Pattern,
            StrokeEffect { FillType: StrokeFillType.Pattern } s => s.PatternFill?.Pattern,
            BevelEffect b => b.Texture?.Pattern,
            _ => null,
        }).OfType<Pattern>();
        return doc.Patterns.Concat(used).Where(p => p.Pixels is not null && !known.Contains(p.Id)).DistinctBy(p => p.Id).ToList();
    }

    /// <summary>
    /// A 'Patt' block holding <paramref name="patterns"/> (those with pixels), as RGB or grayscale tiles with
    /// PackBits-compressed channels and transparency in the user-mask slot.
    /// </summary>
    public static byte[] Encode(IEnumerable<Pattern> patterns)
    {
        var o = new MemoryStream();
        var w = new BigEndianWriter(o);
        foreach (var p in patterns)
        {
            if (p.Pixels is not { } px) continue;
            var body = EncodeOne(p, px);
            w.U32((uint)body.Length);
            w.Bytes(body);
            while (o.Length % 4 != 0) o.WriteByte(0);
        }
        return o.ToArray();
    }

    private static byte[] EncodeOne(Pattern p, Raster px)
    {
        bool gray = px.ColorMode == ColorMode.Grayscale;
        var o = new MemoryStream();
        var w = new BigEndianWriter(o);
        w.U32(1);
        w.U32(gray ? 1u : 3u);
        w.U16((ushort)px.Height);
        w.U16((ushort)px.Width);
        w.U32((uint)p.Name.Length + 1);
        w.Bytes(Encoding.BigEndianUnicode.GetBytes(p.Name + "\0"));
        w.PascalString(p.Id, 1);

        var arrays = new MemoryStream();
        var a = new BigEndianWriter(arrays);
        a.I32(0); a.I32(0); a.I32(px.Height); a.I32(px.Width);
        var color = px.ColorPlanes.Take(gray ? 1 : 3).ToList();
        a.U32((uint)color.Count);
        foreach (var plane in color) WriteArray(a, Eight(plane));
        if (px.Alpha is { } alpha) WriteArray(a, Eight(alpha));
        else a.U32(0); // user mask: not written
        a.U32(0); // sheet mask: not written

        w.U32(3);
        w.U32((uint)arrays.Length);
        w.Bytes(arrays.ToArray());
        return o.ToArray();
    }

    private static Plane Eight(Plane p)
    {
        if (p.BitDepth == 8) return p;
        var o = Plane.Create(p.Width, p.Height, 8);
        for (int i = 0; i < p.Width * p.Height; i++) o.Data[i] = RgbaConverter.ToByte(p.GetNormalized(i));
        return o;
    }

    private static void WriteArray(BigEndianWriter a, Plane plane)
    {
        // EncodeLayerChannel gives the compression field and PackBits rows with a 16-bit row table.
        var encoded = ChannelEncoder.EncodeLayerChannel(plane, psb: false);
        a.U32(1); // written
        a.U32((uint)(4 + 16 + 2 + 1 + encoded.Length - 2));
        a.U32(8);
        a.I32(0); a.I32(0); a.I32(plane.Height); a.I32(plane.Width);
        a.U16(8);
        a.U8(encoded[1]);
        a.Bytes(encoded.AsSpan(2));
    }
}
