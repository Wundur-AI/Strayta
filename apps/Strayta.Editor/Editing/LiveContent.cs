using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Strayta.Core;
using Strayta.Core.Paths;
using Strayta.Imaging;
using Strayta.Psd;
using Strayta.Psd.Descriptors;
using Strayta.Rendering;
using Strayta.Rendering.Transforms;

namespace Strayta.Editor.Editing;

/// <summary>
/// Keeps type, smart objects, shapes and fill layers editable through Free Transform, turned crops and Image Size.
/// Their file data moves with the map (<see cref="PsdCanvas.WithCanvas(PsdLayerRecord, int, int, int, int, CanvasMap)"/>:
/// the type transform, placed-layer corners, vector mask points and live shape boxes), and their pixels, which only
/// show that data, are redrawn where Strayta can:
/// <list type="bullet">
/// <item>Smart objects from their embedded file (the file's own composite, or a render of it), placed through the
/// new corners with the bicubic (projective when in perspective) resampler, so enlarging stays sharp. Warped
/// smart objects, ones with smart filters, linked files and unreadable content keep the resampled pixels.</item>
/// <item>Solid color fill layers ('SoCo') without a vector mask are filled again, covering the canvas they
/// covered before.</item>
/// <item>Type is drawn again by the text engine through its new transform (sharp at any scale, as Photoshop redraws
/// it) when every font it uses is installed and it is neither warped nor vertical; otherwise the resampled pixels
/// stand in.</item>
/// <item>Shapes (fills with a vector mask) are drawn again from their moved outline, fill and stroke
/// (<see cref="ShapeRenderer"/>). Gradient and pattern fills without an outline keep the resampled pixels; Photoshop
/// redraws them from the updated data when they are edited.</item>
/// </list>
/// </summary>
internal static class LiveContent
{
    private static readonly ConditionalWeakTable<PsdFile, ConcurrentDictionary<string, Raster?>> Embedded = new();

    /// <summary>True for layers whose file data holds their content (type, smart objects, fills, shapes).</summary>
    public static bool IsLive(LayerNode node) => CanvasOperations.IsLive(node);

    /// <summary>
    /// The redrawn pixels of a live layer whose record has already been moved to <paramref name="record"/>, or null
    /// to keep the resampled ones. <paramref name="oldBounds"/> and <paramref name="oldCanvas"/> are the layer's and
    /// canvas's before the change, <paramref name="map"/> the change itself.
    /// </summary>
    public static (Raster? Pixels, PixelRect Bounds)? Redraw(PixelLayer layer, PsdLayerRecord record, PsdFile? file, Document doc,
        PixelRect oldBounds, PixelRect oldCanvas, PixelRect newCanvas, Affine map, CancellationToken cancel = default)
    {
        try
        {
            if (layer.Tags.Contains("smart-object") && file is not null)
                return RedrawSmartObject(record, file, doc, newCanvas, cancel);
            if (layer.Tags.Contains("text")) return RedrawType(record, doc);
            // Shapes are drawn again from their moved outline, fill and stroke (ShapeLayers.cs).
            if (PsdShapeLayer.Read(record, newCanvas.Width, newCanvas.Height, doc.Patterns, doc.Resolution) is { } shape)
            {
                int mx = Math.Max(64, newCanvas.Width / 4), my = Math.Max(64, newCanvas.Height / 4);
                var clip = new PixelRect(newCanvas.Left - mx, newCanvas.Top - my, newCanvas.Right + mx, newCanvas.Bottom + my);
                var render = ShapeRenderer.Render(shape, newCanvas, clip, doc.ColorMode, doc.BitDepth, cancel);
                return (render.Pixels, render.Pixels is null ? PixelRect.Empty : render.Bounds);
            }
            if (layer.Tags.Contains("fill") && record.FindBlock("SoCo") is { Data: { } soco }
                && record.FindBlock("vmsk") is null && record.FindBlock("vsms") is null)
                return RedrawSolidFill(soco, doc, oldBounds, oldCanvas, newCanvas, map);
        }
        catch (Exception e) when (e is PsdFormatException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            // Unreadable or unusual content: the resampled pixels stand in, as for type.
        }
        return null;
    }

    private static (Raster?, PixelRect)? RedrawType(PsdLayerRecord record, Document doc)
    {
        if (Psd.Text.PsdTypeLayer.Read(record) is not { Warp: null, Orientation: Core.Text.TextOrientation.Horizontal } data) return null;
        if (data.FontsUsed.Any(f => !Text.FontCatalog.System.Contains(f))) return null;
        var render = Text.TextRenderer.Render(data, new Text.TextRenderOptions { ColorMode = doc.ColorMode, BitDepth = doc.BitDepth });
        return (render.Pixels, render.Pixels is null ? PixelRect.Empty : render.Bounds);
    }

    private static (Raster?, PixelRect)? RedrawSmartObject(PsdLayerRecord record, PsdFile file, Document doc, PixelRect canvas, CancellationToken cancel)
    {
        if (PsdLiveContent.ReadSmartObject(record) is not { Warped: false, HasFilters: false } so) return null;
        var content = Embedded.GetOrCreateValue(file).GetOrAdd(so.UniqueId, id => Decode(file, id, doc));
        if (content is null) return null;
        var place = Projective.FromAffine(Affine.Scale(so.Width / content.Width, so.Height / content.Height))
            .Then(Projective.RectToQuad(so.Width, so.Height, so.Corners));
        // Like Free Transform, keep what lands off the canvas, within a canvas-sized margin.
        var clip = new PixelRect(canvas.Left - canvas.Width, canvas.Top - canvas.Height, canvas.Right + canvas.Width, canvas.Bottom + canvas.Height);
        var (pixels, bounds) = ProjectiveResampler.TransformRaster(content, PixelRect.FromSize(content.Width, content.Height), place,
            ResampleFilter.Bicubic, clip, cancel);
        return (pixels, pixels is null ? PixelRect.Empty : bounds);
    }

    /// <summary>The embedded file's image in the host document's color mode and depth, or null if it cannot be shown.</summary>
    private static Raster? Decode(PsdFile file, string uniqueId, Document host)
    {
        if (PsdLiveContent.FindEmbeddedFile(file, uniqueId) is not { } embedded) return null;
        try
        {
            Raster? image;
            if (embedded.FileType is "8BPB" or "8BPS")
            {
                using var stream = new MemoryStream(embedded.Data, writable: false);
                var inner = PsdFile.Read(stream);
                var doc = inner.ToDocument();
                if (doc.Composite is { } composite && inner.HasRealMergedData != false && composite.Width == doc.Width && composite.Height == doc.Height)
                    image = composite;
                else
                {
                    using var renderer = new CpuRenderer();
                    image = renderer.Render(doc).ToRaster(doc.ColorMode, doc.BitDepth);
                }
            }
            else
            {
                var doc = ImageImporter.Decode(embedded.Data);
                image = doc.Root.Descendants().OfType<PixelLayer>().FirstOrDefault()?.Pixels;
            }
            return image is null ? null : Convert(image, host);
        }
        catch (Exception e) when (e is PsdFormatException or IOException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Brings an image to the host's color mode and bit depth (same mode only; 32-bit is linear, so only to and from itself).</summary>
    private static Raster? Convert(Raster image, Document host)
    {
        if (image.ColorMode != host.ColorMode) return null;
        if (image.BitDepth == host.BitDepth) return image;
        if (image.BitDepth == 32 || host.BitDepth == 32) return null;
        Plane To(Plane p)
        {
            var o = Plane.Create(p.Width, p.Height, host.BitDepth);
            int n = p.Width * p.Height;
            for (int i = 0; i < n; i++)
            {
                float v = p.GetNormalized(i);
                if (host.BitDepth == 8) o.Data[i] = RgbaConverter.ToByte(v);
                else o.AsUInt16()[i] = (ushort)Math.Clamp(MathF.Round(v * 65535f), 0f, 65535f);
            }
            return o;
        }
        return new Raster(image.ColorMode, image.ColorPlanes.Select(To).ToArray(), image.Alpha is null ? null : To(image.Alpha));
    }

    /// <summary>
    /// A solid color fill ('SoCo': a versioned descriptor whose "Clr " is RGBC with 0..255 components, or Grsc) is
    /// the color everywhere; its stored pixels cover the canvas, or its mask's area. After the change it covers the
    /// new canvas, or the moved area.
    /// </summary>
    private static (Raster?, PixelRect)? RedrawSolidFill(byte[] soco, Document doc, PixelRect oldBounds, PixelRect oldCanvas, PixelRect canvas, Affine map)
    {
        var color = DescriptorReader.ReadVersioned(soco).Object("Clr ");
        float[]? components = (doc.ColorMode, color?.ClassId) switch
        {
            (ColorMode.Rgb, "RGBC") => [(float)(color!.Number("Rd  ") ?? 0) / 255f, (float)(color.Number("Grn ") ?? 0) / 255f, (float)(color.Number("Bl  ") ?? 0) / 255f],
            (ColorMode.Grayscale, "Grsc") => [1f - (float)(color!.Number("Gry ") ?? 0) / 100f],
            _ => null,
        };
        if (components is null || doc.BitDepth == 32) return null;
        var covered = oldBounds.Intersect(oldCanvas) == oldCanvas;
        var bounds = covered ? canvas : Resampler.TransformBounds(oldBounds, map);
        if (bounds.IsEmpty) return (null, PixelRect.Empty);
        var planes = components.Select(v =>
        {
            var p = Plane.Create(bounds.Width, bounds.Height, doc.BitDepth);
            if (doc.BitDepth == 8) p.Data.AsSpan().Fill(RgbaConverter.ToByte(v));
            else p.AsUInt16().Fill((ushort)MathF.Round(v * 65535f));
            return p;
        }).ToArray();
        return (new Raster(doc.ColorMode, planes, null), bounds);
    }
}
