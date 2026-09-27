using Strayta.Core;

namespace Strayta.Rendering.Transforms;

/// <summary>How Image Size computes new pixels.</summary>
public enum ResampleMethod
{
    /// <summary>
    /// Photoshop's "Bicubic Automatic": a Catmull-Rom cubic when enlarging, and the same kernel widened over every
    /// covered source pixel when reducing, so reductions are area-averaged (no aliasing) yet stay sharp.
    /// </summary>
    Bicubic,

    /// <summary>Tent filter, area-widened when reducing: softer than bicubic.</summary>
    Bilinear,

    /// <summary>Copies the nearest source pixel: hard edges, exact colors (pixel art, masks).</summary>
    NearestNeighbor,
}

/// <summary>What happens to the Background layer (an opaque bottom layer covering the canvas) on a canvas change.</summary>
public enum BackgroundRule
{
    /// <summary>It stays a Background: exactly the new canvas, new area filled with the fill color, the rest cut off.</summary>
    Fill,

    /// <summary>
    /// It keeps every pixel, including those now outside the canvas. If it no longer covers the canvas exactly it
    /// becomes an ordinary layer ("Layer 0", with transparency), as Photoshop does with Delete Cropped Pixels off.
    /// </summary>
    Float,
}

/// <summary>A layer's pixels, bounds, mask and name: everything a canvas change can alter. Rasters and masks are immutable.</summary>
public sealed record LayerGeometry(Raster? Pixels, PixelRect Bounds, LayerMask? Mask, string Name)
{
    public static LayerGeometry Of(LayerNode node) => node is PixelLayer p
        ? new LayerGeometry(p.Pixels, p.Bounds, p.Mask, p.Name)
        : new LayerGeometry(null, PixelRect.Empty, node.GetMask(), node.Name);

    public void ApplyTo(LayerNode node)
    {
        node.Name = Name;
        if (node is PixelLayer p)
        {
            p.Pixels = Pixels;
            p.Bounds = Bounds;
        }
        if (node.CanHaveMask()) node.SetMask(Mask);
    }
}

/// <summary>
/// The result of Crop, Canvas Size or Image Size, computed without touching the document: the new canvas size,
/// where the old canvas maps to, and the new geometry of every layer and of the stored composite. Editors apply
/// it as one undoable step; <see cref="CanvasOperations.Apply"/> applies it directly.
/// </summary>
public sealed class CanvasChange
{
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>Old document coordinates to new ones.</summary>
    public required Affine Map { get; init; }

    public required ResampleMethod Method { get; init; }

    /// <summary>Every layer, group and adjustment layer of the document with its new geometry.</summary>
    public required IReadOnlyList<(LayerNode Node, LayerGeometry Geometry)> Layers { get; init; }

    /// <summary>The stored flattened image mapped to the new canvas, or null if the document had none.</summary>
    public Raster? Composite { get; init; }

    /// <summary>
    /// Text, smart-object, fill and shape layers whose pixels were resampled (rotation or Image Size). Their live
    /// data would redraw the old pixels, so the caller should rasterize them.
    /// </summary>
    public required IReadOnlyList<LayerNode> ResampledLiveLayers { get; init; }

    /// <summary>True when layers only moved by whole pixels (no resampling).</summary>
    public bool IsWholePixelMove => Map.IsIntegerTranslation(out _, out _);

    /// <summary>Maps another full-canvas plane (a saved selection or spot channel) the same way; new area gets <paramref name="fill"/> (0..1).</summary>
    public Plane MapPlane(Plane plane, float fill)
    {
        var raster = new Raster(ColorMode.Grayscale, [plane], null);
        var (mapped, bounds) = CanvasOperations.Transform(raster, PixelRect.FromSize(plane.Width, plane.Height), Map, Method,
            PixelRect.FromSize(Width, Height), default);
        return CanvasOperations.ToCanvas(mapped, bounds, Width, Height, [fill], ColorMode.Grayscale, plane.BitDepth).ColorPlanes[0];
    }
}

/// <summary>
/// Crop, Canvas Size and Image Size for whole documents. Every layer, mask and the stored composite follow the
/// canvas; whole-pixel moves never resample, rotations and size changes use the Free Transform resampler
/// (premultiplied, area-filtered when reducing). Layer bounds may extend past the canvas, which PSD supports, so
/// pixels outside it are kept unless asked otherwise.
/// </summary>
public static class CanvasOperations
{
    private static readonly string[] LiveTags = ["text", "smart-object", "fill", "shape"];

    /// <summary>True for layers Photoshop redraws from their own data (type, smart objects, fills, shapes).</summary>
    public static bool IsLive(LayerNode node) => LiveTags.Any(node.Tags.Contains);

    /// <summary>
    /// Crop to <paramref name="rect"/> (which may extend past the canvas). With <paramref name="deleteCroppedPixels"/>
    /// layers are cut to the new canvas (type and smart objects are never cut); without it they keep pixels outside.
    /// </summary>
    public static CanvasChange Crop(Document doc, PixelRect rect, bool deleteCroppedPixels, IReadOnlyList<float>? fill = null, CancellationToken cancel = default)
    {
        if (rect.IsEmpty) throw new ArgumentException("The crop rectangle is empty.", nameof(rect));
        return Crop(doc, Affine.Translation(-rect.Left, -rect.Top), rect.Width, rect.Height, deleteCroppedPixels, fill, cancel);
    }

    /// <summary>
    /// A general (possibly rotated) crop: <paramref name="map"/> takes old document coordinates to the new canvas of
    /// <paramref name="width"/>×<paramref name="height"/>. The Background layer is filled with <paramref name="fill"/>
    /// where the crop reaches past the image (or, without <paramref name="deleteCroppedPixels"/>, becomes a layer).
    /// </summary>
    public static CanvasChange Crop(Document doc, Affine map, int width, int height, bool deleteCroppedPixels,
        IReadOnlyList<float>? fill = null, CancellationToken cancel = default) =>
        Build(doc, width, height, map, ResampleMethod.Bicubic, clipPixels: deleteCroppedPixels,
            deleteCroppedPixels ? BackgroundRule.Fill : BackgroundRule.Float, fill, cancel);

    /// <summary>
    /// Canvas Size: a new canvas of <paramref name="width"/>×<paramref name="height"/> with the old one placed at
    /// (<paramref name="offsetX"/>, <paramref name="offsetY"/>). Layers only move; the Background is extended with
    /// <paramref name="fill"/> (or cut, when the canvas shrinks).
    /// </summary>
    public static CanvasChange ResizeCanvas(Document doc, int width, int height, int offsetX, int offsetY, IReadOnlyList<float>? fill = null) =>
        Build(doc, width, height, Affine.Translation(offsetX, offsetY), ResampleMethod.Bicubic, clipPixels: false, BackgroundRule.Fill, fill, default);

    /// <summary>Image Size: resamples every layer, mask and the composite to a <paramref name="width"/>×<paramref name="height"/> canvas.</summary>
    public static CanvasChange ResizeImage(Document doc, int width, int height, ResampleMethod method, CancellationToken cancel = default) =>
        Build(doc, width, height, Affine.Scale(width / (double)doc.Width, height / (double)doc.Height), method, clipPixels: false,
            BackgroundRule.Fill, null, cancel);

    /// <summary>Applies <paramref name="change"/> to the document it was computed for.</summary>
    public static void Apply(Document doc, CanvasChange change)
    {
        doc.SetCanvasSize(change.Width, change.Height);
        foreach (var (node, geometry) in change.Layers) geometry.ApplyTo(node);
        doc.Composite = change.Composite;
    }

    /// <summary>The opaque bottom layer covering the whole canvas, which Photoshop treats as the Background, if any.</summary>
    public static PixelLayer? FindBackground(Document doc) =>
        doc.Root.Children.FirstOrDefault() is PixelLayer { Pixels: { Alpha: null } } p && p.Bounds == doc.Bounds && !IsLive(p) ? p : null;

    private static CanvasChange Build(Document doc, int width, int height, Affine map, ResampleMethod method, bool clipPixels,
        BackgroundRule backgroundRule, IReadOnlyList<float>? fill, CancellationToken cancel)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var canvas = PixelRect.FromSize(width, height);
        var background = FindBackground(doc);
        bool wholePixels = map.IsIntegerTranslation(out _, out _);
        int colors = doc.ColorMode == ColorMode.Multichannel ? 0 : doc.ColorMode.ColorChannelCount();
        var fillColor = fill is not null && fill.Count == colors ? fill : Enumerable.Repeat(1f, Math.Max(colors, 1)).ToArray();

        var nodes = doc.Root.Descendants().ToList();
        var results = new LayerGeometry[nodes.Count];
        void Map(int i)
        {
            var node = nodes[i];
            bool clip = clipPixels && !IsLive(node);
            var mask = MapMask(node.GetMask(), map, method, clip ? canvas : null, cancel);
            results[i] = node is PixelLayer p
                ? MapLayer(p, ReferenceEquals(p, background), mask, map, method, clip ? canvas : null, canvas, backgroundRule, fillColor, cancel)
                : new LayerGeometry(null, PixelRect.Empty, mask, node.Name);
        }
        // Whole-pixel moves are cheap copies and run side by side; resampling is parallel inside, one layer at a time.
        if (wholePixels) Parallel.For(0, nodes.Count, new ParallelOptions { CancellationToken = cancel }, Map);
        else
            for (int i = 0; i < nodes.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();
                Map(i);
            }

        Raster? composite = null;
        if (doc.Composite is { } c && c.Width == doc.Width && c.Height == doc.Height)
        {
            var (mapped, bounds) = Transform(c, doc.Bounds, map, method, canvas, cancel);
            composite = ToCanvas(mapped, bounds, width, height, c.Alpha is null ? CompositeFill(c, fillColor) : null, c.ColorMode, c.BitDepth);
        }

        return new CanvasChange
        {
            Width = width,
            Height = height,
            Map = map,
            Method = method,
            Layers = nodes.Select((n, i) => (n, results[i])).ToList(),
            Composite = composite,
            ResampledLiveLayers = wholePixels ? [] : nodes.Where(n => n is PixelLayer { Pixels: not null } && IsLive(n)).ToList(),
        };
    }

    private static IReadOnlyList<float> CompositeFill(Raster c, IReadOnlyList<float> fill) =>
        c.ColorPlanes.Count == fill.Count ? fill : Enumerable.Repeat(1f, c.ColorPlanes.Count).ToArray();

    private static LayerGeometry MapLayer(PixelLayer layer, bool isBackground, LayerMask? mask, Affine map, ResampleMethod method,
        PixelRect? clip, PixelRect canvas, BackgroundRule rule, IReadOnlyList<float> fill, CancellationToken cancel)
    {
        if (layer.Pixels is not { } raster || layer.Bounds.IsEmpty) return new LayerGeometry(null, PixelRect.Empty, mask, layer.Name);

        if (isBackground && rule == BackgroundRule.Fill)
        {
            var (px, b) = Transform(raster, layer.Bounds, map, method, canvas, cancel);
            return new LayerGeometry(ToCanvas(px, b, canvas.Width, canvas.Height, fill, raster.ColorMode, raster.BitDepth), canvas, mask, layer.Name);
        }

        var (pixels, bounds) = Transform(raster, layer.Bounds, map, method, clip, cancel);
        if (pixels is null) return new LayerGeometry(null, PixelRect.Empty, mask, layer.Name);
        // Resampling adds transparency; an opaque layer that stayed fully opaque keeps none.
        if (raster.Alpha is null && pixels.Alpha is { } a && IsOpaque(a)) pixels = new Raster(pixels.ColorMode, pixels.ColorPlanes, null);

        string name = layer.Name;
        if (isBackground && (bounds != canvas || pixels.Alpha is not null))
        {
            // No longer exactly the canvas: an ordinary layer now, which can hold pixels outside it and transparency.
            pixels = pixels.Alpha is null ? new Raster(pixels.ColorMode, pixels.ColorPlanes, Opaque(pixels.Width, pixels.Height, pixels.BitDepth)) : pixels;
            if (name == "Background") name = "Layer 0";
        }
        return new LayerGeometry(pixels, bounds, mask, name);
    }

    /// <summary>
    /// Moves or resamples layer pixels with <paramref name="map"/>, clipped to <paramref name="clip"/>. Whole-pixel
    /// moves copy (or keep) the pixels; nearest-neighbor scaling copies samples exactly.
    /// </summary>
    internal static (Raster? Pixels, PixelRect Bounds) Transform(Raster raster, PixelRect bounds, Affine map, ResampleMethod method,
        PixelRect? clip, CancellationToken cancel)
    {
        if (method == ResampleMethod.NearestNeighbor && !map.IsIntegerTranslation(out _, out _) && map.M12 == 0 && map.M21 == 0)
        {
            var target = Resampler.TransformBounds(bounds, map);
            if (clip is { } c) target = target.Intersect(c);
            if (target.IsEmpty) return (null, PixelRect.Empty);
            var (cols, rows) = NearestIndices(bounds, map, target);
            return (new Raster(raster.ColorMode, raster.ColorPlanes.Select(p => Nearest(p, cols, rows)).ToArray(),
                raster.Alpha is null ? null : Nearest(raster.Alpha, cols, rows)), target);
        }
        var filter = method == ResampleMethod.Bilinear ? ResampleFilter.Bilinear : ResampleFilter.Bicubic;
        var (pixels, b) = !map.IsIntegerTranslation(out _, out _) && SeparableScale.Applies(map)
            ? SeparableScale.Raster(raster, bounds, map, filter, clip, cancel)
            : Resampler.TransformRaster(raster, bounds, map, filter, clip, cancel);
        return pixels is null ? (null, PixelRect.Empty) : (pixels, b);
    }

    private static LayerMask? MapMask(LayerMask? mask, Affine map, ResampleMethod method, PixelRect? clip, CancellationToken cancel)
    {
        if (mask is null) return null;
        if (mask.Pixels is not { } plane || mask.Bounds.IsEmpty)
            return With(mask, null, mask.Bounds.IsEmpty ? PixelRect.Empty : Resampler.TransformBounds(mask.Bounds, map));

        // Canvas changes move unlinked masks too: they are placed in document space, like everything else.
        if (map.IsIntegerTranslation(out _, out _) || method == ResampleMethod.NearestNeighbor && map.M12 == 0 && map.M21 == 0)
        {
            var (moved, bounds) = Transform(new Raster(ColorMode.Grayscale, [plane], null), mask.Bounds, map, method, clip, cancel);
            return moved is null ? With(mask, null, PixelRect.Empty) : With(mask, moved.ColorPlanes[0], bounds);
        }
        var filter = method == ResampleMethod.Bilinear ? ResampleFilter.Bilinear : ResampleFilter.Bicubic;
        var result = SeparableScale.Applies(map)
            ? SeparableScale.Mask(mask, plane, map, filter, clip, cancel)
            : Resampler.TransformMask(mask, ResampleSource.FromPlane(plane), map, filter, clip, cancel);
        return result.Pixels is null ? With(mask, null, PixelRect.Empty) : result;
    }

    private static LayerMask With(LayerMask m, Plane? pixels, PixelRect bounds) => new()
    {
        Bounds = bounds,
        Pixels = pixels,
        DefaultColor = m.DefaultColor,
        Disabled = m.Disabled,
        PositionRelativeToLayer = m.PositionRelativeToLayer,
    };

    // ---- Nearest neighbor ------------------------------------------------------------------------

    private static (int[] Cols, int[] Rows) NearestIndices(PixelRect src, Affine map, PixelRect target)
    {
        var inv = map.Invert();
        var cols = new int[target.Width];
        var rows = new int[target.Height];
        for (int x = 0; x < cols.Length; x++)
            cols[x] = Math.Clamp((int)Math.Floor(inv.M11 * (target.Left + x + 0.5) + inv.Dx) - src.Left, 0, src.Width - 1);
        for (int y = 0; y < rows.Length; y++)
            rows[y] = Math.Clamp((int)Math.Floor(inv.M22 * (target.Top + y + 0.5) + inv.Dy) - src.Top, 0, src.Height - 1);
        return (cols, rows);
    }

    private static Plane Nearest(Plane p, int[] cols, int[] rows)
    {
        int bpp = p.BitDepth / 8, w = cols.Length;
        var o = Plane.Create(w, rows.Length, p.BitDepth);
        byte[] src = p.Data, dst = o.Data;
        Parallel.For(0, rows.Length, y =>
        {
            long row = (long)rows[y] * p.Width, outRow = (long)y * w;
            if (bpp == 1)
                for (int x = 0; x < w; x++) dst[outRow + x] = src[row + cols[x]];
            else
                for (int x = 0; x < w; x++) Buffer.BlockCopy(src, (int)((row + cols[x]) * bpp), dst, (int)((outRow + x) * bpp), bpp);
        });
        return o;
    }

    // ---- Canvas-sized results --------------------------------------------------------------------

    /// <summary>
    /// Places pixels at <paramref name="bounds"/> onto a <paramref name="width"/>×<paramref name="height"/> canvas:
    /// flattened over <paramref name="fill"/> (0..1 per color plane) without transparency, or, when
    /// <paramref name="fill"/> is null, with transparency around them.
    /// </summary>
    internal static Raster ToCanvas(Raster? pixels, PixelRect bounds, int width, int height, IReadOnlyList<float>? fill, ColorMode mode, int bitDepth)
    {
        var canvas = PixelRect.FromSize(width, height);
        if (pixels is not null && bounds == canvas && (pixels.Alpha is null || fill is null)) return pixels;

        int colors = pixels?.ColorPlanes.Count ?? fill?.Count ?? 1;
        var planes = Enumerable.Range(0, colors).Select(_ => Plane.Create(width, height, bitDepth)).ToArray();
        var alpha = fill is null ? Plane.Create(width, height, bitDepth) : null;
        var inside = pixels is null ? PixelRect.Empty : bounds.Intersect(canvas);
        Parallel.For(0, height, y =>
        {
            for (int c = 0; c < colors; c++)
            {
                float f = fill is null ? 0f : fill[Math.Min(c, fill.Count - 1)];
                var dst = planes[c];
                if (f != 0) for (int x = 0, i = y * width; x < width; x++, i++) Store(dst, i, f);
                if (y < inside.Top || y >= inside.Bottom) continue;
                var src = pixels!.ColorPlanes[c];
                int srcRow = (y - bounds.Top) * bounds.Width - bounds.Left;
                if (pixels.Alpha is null || fill is null)
                {
                    int bpp = bitDepth / 8;
                    Buffer.BlockCopy(src.Data, (srcRow + inside.Left) * bpp, dst.Data, (y * width + inside.Left) * bpp, inside.Width * bpp);
                }
                else
                {
                    for (int x = inside.Left; x < inside.Right; x++)
                    {
                        int s = srcRow + x;
                        float a = pixels.Alpha.GetNormalized(s);
                        Store(dst, y * width + x, src.GetNormalized(s) * a + f * (1 - a));
                    }
                }
            }
            if (alpha is null || y < inside.Top || y >= inside.Bottom) return;
            int abpp = bitDepth / 8, aRow = (y - bounds.Top) * bounds.Width - bounds.Left;
            if (pixels!.Alpha is { } srcAlpha)
                Buffer.BlockCopy(srcAlpha.Data, (aRow + inside.Left) * abpp, alpha.Data, (y * width + inside.Left) * abpp, inside.Width * abpp);
            else
                for (int x = inside.Left; x < inside.Right; x++) Store(alpha, y * width + x, 1f);
        });
        return new Raster(mode, planes, alpha);
    }

    private static bool IsOpaque(Plane alpha)
    {
        switch (alpha.BitDepth)
        {
            case 8: return !alpha.Data.AsSpan().ContainsAnyExcept((byte)255);
            case 16: return !alpha.AsUInt16().ContainsAnyExcept((ushort)65535);
            default:
                foreach (float v in alpha.AsSingle()) if (v < 1f) return false;
                return true;
        }
    }

    private static Plane Opaque(int width, int height, int bitDepth)
    {
        var p = Plane.Create(width, height, bitDepth);
        switch (bitDepth)
        {
            case 8: p.Data.AsSpan().Fill(255); break;
            case 16: p.AsUInt16().Fill(65535); break;
            default: p.AsSingle().Fill(1f); break;
        }
        return p;
    }

    private static void Store(Plane p, int i, float v)
    {
        switch (p.BitDepth)
        {
            case 8: p.Data[i] = RgbaConverter.ToByte(Math.Clamp(v, 0f, 1f)); break;
            case 16: p.AsUInt16()[i] = (ushort)Math.Clamp(MathF.Round(v * 65535f), 0f, 65535f); break;
            default: p.AsSingle()[i] = v; break;
        }
    }

    // ---- Trim --------------------------------------------------------------------------------------

    /// <summary>
    /// Image › Trim: the part of a rendered image (straight RGBA, 8-bit) to keep when trimming away transparent
    /// pixels, or pixels of the top-left or bottom-right corner's color, from the chosen sides. Empty when
    /// everything would be trimmed.
    /// </summary>
    public static PixelRect TrimBounds(byte[] rgba, int width, int height, TrimBasis basis, bool top = true, bool left = true, bool bottom = true, bool right = true)
    {
        int corner = basis == TrimBasis.BottomRightColor ? ((height - 1) * width + width - 1) * 4 : 0;
        uint key = BitConverter.ToUInt32(rgba, corner);
        bool Trimmable(int i) => basis == TrimBasis.Transparent ? rgba[i * 4 + 3] == 0 : BitConverter.ToUInt32(rgba, i * 4) == key;

        var rowKeep = new bool[height];
        var colMin = new int[height];
        var colMax = new int[height];
        Parallel.For(0, height, y =>
        {
            int lo = -1, hi = -1;
            for (int x = 0, i = y * width; x < width; x++, i++)
            {
                if (Trimmable(i)) continue;
                if (lo < 0) lo = x;
                hi = x;
            }
            rowKeep[y] = lo >= 0;
            colMin[y] = lo;
            colMax[y] = hi;
        });
        int t = Array.IndexOf(rowKeep, true);
        if (t < 0) return PixelRect.Empty;
        int b = Array.LastIndexOf(rowKeep, true) + 1;
        int l = int.MaxValue, r = 0;
        for (int y = t; y < b; y++)
            if (rowKeep[y])
            {
                l = Math.Min(l, colMin[y]);
                r = Math.Max(r, colMax[y] + 1);
            }
        return new PixelRect(left ? l : 0, top ? t : 0, right ? r : width, bottom ? b : height);
    }
}

/// <summary>What Image › Trim removes.</summary>
public enum TrimBasis
{
    Transparent,
    TopLeftColor,
    BottomRightColor,
}
