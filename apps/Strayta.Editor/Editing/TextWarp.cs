using Strayta.Core;
using Strayta.Core.Text;
using Strayta.Psd;
using Strayta.Psd.Text;
using Strayta.Rendering.Transforms;
using Strayta.Text;

namespace Strayta.Editor.Editing;

/// <summary>
/// Warped type (Warp Text): the text is drawn flat in its own text space, at a resolution matching how large it lands,
/// then bent through its warp style over the text's bounds and placed by the text transform. The style's envelope is
/// evaluated over the bounds as they are (type is not framed in its box the way warped smart objects are).
/// </summary>
internal static class TextWarp
{
    /// <summary>The flat text and the map from its pixels to text-space positions.</summary>
    public sealed record Flat(Raster Pixels, Func<double, double, (double X, double Y)> ToText);

    /// <summary>Draws <paramref name="data"/> flat, <paramref name="scale"/> pixels per text unit.</summary>
    public static Flat? DrawFlat(TextLayerData data, Document doc, double scale)
    {
        var flat = data.WithTransform(new TextTransform(scale, 0, 0, scale, 0, 0));
        var render = TextRenderer.Render(flat, new TextRenderOptions { ColorMode = doc.ColorMode, BitDepth = doc.BitDepth });
        if (render.Pixels is not { } pixels) return null;
        var b = render.Bounds;
        return new Flat(pixels, (u, v) => ((b.Left + u) / scale, (b.Top + v) / scale));
    }

    /// <summary>How many pixels per text unit keep the text sharp once it is placed by <paramref name="place"/> (text space to document).</summary>
    public static double ScaleFor(Projective place, TextRect bounds)
    {
        var a = place.Linearize((bounds.Left + bounds.Right) / 2, (bounds.Top + bounds.Bottom) / 2);
        return Math.Clamp(Math.Sqrt(Math.Abs(a.Determinant)), 1, 8);
    }

    /// <summary>The text transform as a projective map (text space to document).</summary>
    public static Projective Placement(TextTransform t) => new(t.XX, t.YX, t.TX, t.XY, t.YY, t.TY, 0, 0, 1);

    /// <summary>
    /// The warped type layer's pixels: its warp (from the record) bending the flat text, placed by its transform. Null
    /// when the text or its warp cannot be read or its fonts are missing.
    /// </summary>
    public static (Raster? Pixels, PixelRect Bounds)? Render(PsdLayerRecord record, TextLayerData data, Document doc, CancellationToken cancel = default)
    {
        if (PsdTypeWarp.Read(record) is not { } spec || PsdTypeWarp.TextBounds(record) is not { } bounds) return null;
        if (WarpMesh.From(spec) is not { } mesh) return null;
        var place = Placement(data.Transform);
        if (DrawFlat(data, doc, ScaleFor(place, bounds)) is not { } flat) return (null, PixelRect.Empty);
        var clip = new PixelRect(-doc.Width, -doc.Height, 2 * doc.Width, 2 * doc.Height);
        return MeshResampler.TransformRaster(flat.Pixels, (u, v) =>
        {
            var (tx, ty) = flat.ToText(u, v);
            var (wx, wy) = mesh.Map(tx, ty);
            return place.Apply(wx, wy);
        }, ResampleFilter.Bicubic, clip, cancel);
    }
}
