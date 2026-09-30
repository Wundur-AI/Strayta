using Strayta.Core;

namespace Strayta.Rendering;

// Artboards (Strayta.Core.Artboard): each one draws its background inside its bounds, then its layers as a
// pass-through group clipped to those bounds. The canvas outside every artboard is pasteboard: only layers that are
// not in an artboard draw there, which is how Photoshop flattens an artboard document.
public sealed partial class Compositor
{
    private void RenderArtboard(LayerGroup group, Artboard artboard, RenderBuffer target)
    {
        var bounds = artboard.Rect.Intersect(target.Bounds);
        if (bounds.IsEmpty) return;
        var copy = target.CopyRegion(bounds);
        if (artboard.Fill is { } fill) FillOpaque(copy, fill);
        RenderChildren(group.Children, copy);
        Lerp(target, copy, group.Opacity, group.Mask is { Disabled: false } m ? m : null, MaskStroke(group));
    }

    /// <summary>Covers the buffer with an opaque color given as 8-bit sRGB (converted for 32-bit, linear documents).</summary>
    private void FillOpaque(RenderBuffer buffer, (byte R, byte G, byte B) color)
    {
        float r = color.R / 255f, g = color.G / 255f, b = color.B / 255f;
        if (_doc.ColorMode == ColorMode.Grayscale)
            r = g = b = 0.299f * r + 0.587f * g + 0.114f * b;
        if (_doc.BitDepth == 32)
            (r, g, b) = (ToLinear(r), ToLinear(g), ToLinear(b));
        var px = buffer.Pixels;
        for (long i = 0; i < px.LongLength; i += 4)
        {
            px[i] = r;
            px[i + 1] = g;
            px[i + 2] = b;
            px[i + 3] = 1f;
        }

        static float ToLinear(float v) => v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
    }
}
