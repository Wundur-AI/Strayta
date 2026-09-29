using System.Buffers.Binary;
using Strayta.Core;

namespace Strayta.Psd;

/// <summary>
/// Where the old canvas lands on the new one: an affine map of document coordinates,
/// (x, y) → (M11·x + M12·y + Dx, M21·x + M22·y + Dy). Crop (possibly rotated), Canvas Size and Image Size are all of
/// this form.
/// </summary>
public readonly record struct CanvasMap(double M11, double M12, double M21, double M22, double Dx, double Dy)
{
    /// <summary>A move by (<paramref name="dx"/>, <paramref name="dy"/>).</summary>
    public static CanvasMap Translation(double dx, double dy) => new(1, 0, 0, 1, dx, dy);

    public (double X, double Y) Apply(double x, double y) => (M11 * x + M12 * y + Dx, M21 * x + M22 * y + Dy);

    /// <summary>True for a move by whole pixels, which layer pixels follow without resampling.</summary>
    public bool IsWholePixelMove => M11 == 1 && M22 == 1 && M12 == 0 && M21 == 0 && Dx == Math.Round(Dx) && Dy == Math.Round(Dy);
}

/// <summary>
/// Keeps the document-level data Strayta passes through untouched (saved selections and spot channels, guides, saved
/// paths, slices) consistent with a new canvas size, so a cropped or resized file still opens correctly in
/// Photoshop.
/// </summary>
public static class PsdCanvas
{
    private const int GuidesResource = 1032, SlicesResource = 1050, WorkPathResource = 1025;
    private const int FirstSavedPath = 2000, LastSavedPath = 2997;

    /// <summary>
    /// A copy of <paramref name="file"/> for a canvas of <paramref name="width"/>×<paramref name="height"/>:
    /// saved selections and spot channels go through <paramref name="mapChannel"/>, guides and saved paths move with
    /// <paramref name="map"/>, and slices are dropped (Photoshop rebuilds the default slice from the canvas).
    /// The flattened image's own channels (color, then transparency) are taken from <paramref name="composite"/>
    /// when the caller has already mapped the image, and mapped too otherwise.
    /// </summary>
    public static PsdFile WithCanvas(PsdFile file, int width, int height, CanvasMap map, Func<Plane, Plane> mapChannel, Raster? composite = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        int imageChannels = file.Header.ColorMode == ColorMode.Multichannel ? 0 : file.Header.ColorMode.ColorChannelCount();
        imageChannels = Math.Min(file.CompositeChannels.Count, imageChannels + (file.CompositeHasTransparency ? 1 : 0));
        var given = composite is null ? [] : composite.ColorPlanes.Concat(composite.Alpha is { } a ? [a] : []).ToList();
        bool reuse = given.Count == imageChannels && given.All(p => p.Width == width && p.Height == height && p.BitDepth == file.Header.BitDepth);
        var channels = file.CompositeChannels
            .Select((plane, i) => (plane, i))
            .AsParallel().AsOrdered()
            .Select(c => reuse && c.i < imageChannels ? given[c.i] : mapChannel(c.plane))
            .ToList();
        int oldW = file.Header.Width, oldH = file.Header.Height;
        var resources = new List<ImageResource>();
        foreach (var r in file.Resources)
        {
            if (r.Id == SlicesResource) continue;
            var data = r.Id switch
            {
                GuidesResource => RemapGuides(r.Data, width, height, map),
                WorkPathResource or (>= FirstSavedPath and <= LastSavedPath) => RemapPath(r.Data, 0, oldW, oldH, width, height, map),
                _ => r.Data,
            };
            resources.Add(r with { Data = data });
        }
        return file.With(file.Header with { Width = width, Height = height }, resources, channels);
    }

    /// <summary>
    /// Moves the live content stored in a layer's blocks with the canvas: vector masks and shape outlines, live shape
    /// rectangles ('vogk'), the transform of type and the corners of placed smart objects (see
    /// <see cref="PsdLiveContent"/>). Free Transform uses it too, with the canvas size unchanged. Returns
    /// <paramref name="record"/> itself when nothing needed changing.
    /// </summary>
    public static PsdLayerRecord WithCanvas(PsdLayerRecord record, int oldWidth, int oldHeight, int width, int height, CanvasMap map)
    {
        ArgumentNullException.ThrowIfNull(record);
        bool changed = false;
        var blocks = record.Blocks.Select(b =>
        {
            if (b.Data is not { } data) return b;
            byte[]? mapped = b.Key switch
            {
                // Vector masks: 4-byte version, 4-byte flags, then path records.
                "vmsk" or "vsms" when data.Length >= 8 => RemapPath(data, 8, oldWidth, oldHeight, width, height, map),
                "TySh" or "tySh" => RemapTypeTransform(data, map),
                "SoLd" or "SoLE" => PsdLiveContent.TransformPlacedDescriptor(data, map),
                "PlLd" or "plLd" => PsdLiveContent.TransformPlacedLegacy(data, map),
                "vogk" => PsdLiveContent.TransformOrigination(data, map),
                _ => null,
            };
            if (mapped is null || ReferenceEquals(mapped, data)) return b;
            changed = true;
            return b with { Data = mapped, Length = mapped.Length };
        }).ToList();
        // The combined vector-and-pixel ("real") mask is written back from the source record: move it along with a
        // whole-pixel shift, and drop it when the canvas is resampled (the layer's own mask still applies).
        var mask = record.Mask;
        if (mask is { RealRect: { } real })
        {
            changed = true;
            mask = map.IsWholePixelMove
                ? new PsdLayerMaskData
                {
                    Rect = mask.Rect, DefaultColor = mask.DefaultColor, Flags = mask.Flags, RealFlags = mask.RealFlags, RealDefaultColor = mask.RealDefaultColor,
                    RealRect = real.IsEmpty ? real : new PixelRect(real.Left + (int)map.Dx, real.Top + (int)map.Dy, real.Right + (int)map.Dx, real.Bottom + (int)map.Dy),
                }
                : new PsdLayerMaskData { Rect = mask.Rect, DefaultColor = mask.DefaultColor, Flags = mask.Flags };
        }
        return changed ? record.WithBlocks(blocks, mask) : record;
    }

    /// <summary>
    /// Guides (resource 1032, see <see cref="PsdGuides"/>), each moved as <see cref="PsdGuides.Remap(Guide, int, int, CanvasMap)"/>
    /// describes. Only the guide entries change; the header and anything after them stay as they were.
    /// </summary>
    internal static byte[] RemapGuides(byte[] data, int newW, int newH, CanvasMap map)
    {
        if (data.Length < 16) return data;
        var o = (byte[])data.Clone();
        int count = BinaryPrimitives.ReadInt32BigEndian(o.AsSpan(12));
        for (int i = 0, at = 16; i < count && at + 5 <= o.Length; i++, at += 5)
        {
            var guide = new Guide(o[at + 4] != 0 ? GuideOrientation.Horizontal : GuideOrientation.Vertical,
                BinaryPrimitives.ReadInt32BigEndian(o.AsSpan(at)) / 32.0);
            var moved = PsdGuides.Remap(guide, newW, newH, map);
            BinaryPrimitives.WriteInt32BigEndian(o.AsSpan(at), (int)Math.Clamp(Math.Round(moved.Position * 32), int.MinValue, int.MaxValue));
            o[at + 4] = moved.IsHorizontal ? (byte)1 : (byte)0;
        }
        return o;
    }

    /// <summary>
    /// Path records are 26 bytes each. Knot records (selectors 1, 2, 4, 5) hold three points, each a vertical then a
    /// horizontal 8.24 fixed-point fraction of the canvas height and width, so they depend on the canvas size too.
    /// </summary>
    private static byte[] RemapPath(byte[] data, int start, int oldW, int oldH, int newW, int newH, CanvasMap map)
    {
        var o = (byte[])data.Clone();
        for (int at = start; at + 26 <= o.Length; at += 26)
        {
            int selector = BinaryPrimitives.ReadInt16BigEndian(o.AsSpan(at));
            if (selector is not (1 or 2 or 4 or 5)) continue;
            for (int p = 0; p < 3; p++)
            {
                var y = o.AsSpan(at + 2 + p * 8);
                var x = o.AsSpan(at + 6 + p * 8);
                var (nx, ny) = map.Apply(ReadFixed(x) * oldW, ReadFixed(y) * oldH);
                WriteFixed(y, ny / newH);
                WriteFixed(x, nx / newW);
            }
        }
        return o;
    }

    private static double ReadFixed(ReadOnlySpan<byte> b) => BinaryPrimitives.ReadInt32BigEndian(b) / (double)(1 << 24);

    private static void WriteFixed(Span<byte> b, double v) =>
        BinaryPrimitives.WriteInt32BigEndian(b, (int)Math.Clamp(Math.Round(v * (1 << 24)), int.MinValue, int.MaxValue));

    /// <summary>
    /// Type layers ('TySh') start with a 2-byte version and a 2D transform of six doubles (xx, xy, yx, yy, tx, ty),
    /// mapping text space (u, v) to the document as (xx·u + yx·v + tx, xy·u + yy·v + ty); the text's own bounds are
    /// relative to it, so following the canvas only needs this transform composed with the map.
    /// </summary>
    private static byte[] RemapTypeTransform(byte[] data, CanvasMap m)
    {
        if (data.Length < 50) return data;
        var o = (byte[])data.Clone();
        double[] t = new double[6];
        for (int i = 0; i < 6; i++) t[i] = BinaryPrimitives.ReadDoubleBigEndian(o.AsSpan(2 + i * 8));
        var (tx, ty) = m.Apply(t[4], t[5]);
        double[] mapped =
        [
            m.M11 * t[0] + m.M12 * t[1], m.M21 * t[0] + m.M22 * t[1],
            m.M11 * t[2] + m.M12 * t[3], m.M21 * t[2] + m.M22 * t[3],
            tx, ty,
        ];
        for (int i = 0; i < 6; i++) BinaryPrimitives.WriteDoubleBigEndian(o.AsSpan(2 + i * 8), mapped[i]);
        if (Text.PsdTypeLayer.IsRegenerated(data)) Text.PsdTypeLayer.MarkRegenerated(o);
        return o;
    }
}
