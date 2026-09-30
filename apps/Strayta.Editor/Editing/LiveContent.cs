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
/// <item>Smart objects from their embedded or linked file (the file's own composite, or a render of it), placed through
/// the new corners and their warp with the bicubic resampler, so enlarging stays sharp, and their smart filters applied
/// again (<see cref="SmartObjects"/>). Unreadable content and filters Strayta does not have keep the resampled
/// pixels.</item>
/// <item>Solid color fill layers ('SoCo') without a vector mask are filled again, covering the canvas they
/// covered before.</item>
/// <item>Type is drawn again by the text engine through its new transform (sharp at any scale, as Photoshop redraws
/// it) when every font it uses is installed and it is not vertical (warped type is bent again, TextWarp.cs); otherwise the resampled pixels
/// stand in.</item>
/// <item>Shapes (fills with a vector mask) are drawn again from their moved outline, fill and stroke
/// (<see cref="ShapeRenderer"/>). Gradient and pattern fills without an outline keep the resampled pixels; Photoshop
/// redraws them from the updated data when they are edited.</item>
/// </list>
/// </summary>
internal static class LiveContent
{
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
        if (Psd.Text.PsdTypeLayer.Read(record) is not { Orientation: Core.Text.TextOrientation.Horizontal } data) return null;
        if (data.FontsUsed.Any(f => !Text.FontCatalog.System.Contains(f))) return null;
        // Warped type is bent again by Strayta once it has changed it (Warp Text, TextWarp.cs); a warp from Photoshop keeps
        // Photoshop's resampled pixels, which match its own drawing better than Strayta's approximation of the styles.
        if (data.Warp is not null)
            return record.FindBlock("TySh")?.Data is { } tySh && Psd.Text.PsdTypeLayer.IsRegenerated(tySh) ? TextWarp.Render(record, data, doc) : null;
        var render = Text.TextRenderer.Render(data, new Text.TextRenderOptions { ColorMode = doc.ColorMode, BitDepth = doc.BitDepth });
        return (render.Pixels, render.Pixels is null ? PixelRect.Empty : render.Bounds);
    }

    private static (Raster?, PixelRect)? RedrawSmartObject(PsdLayerRecord record, PsdFile file, Document doc, PixelRect canvas, CancellationToken cancel)
    {
        // Like Free Transform, keep what lands off the canvas, within a canvas-sized margin.
        var clip = new PixelRect(canvas.Left - canvas.Width, canvas.Top - canvas.Height, canvas.Right + canvas.Width, canvas.Bottom + canvas.Height);
        return SmartObjects.Draw(record, file, doc, clip, cancel: cancel);
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
