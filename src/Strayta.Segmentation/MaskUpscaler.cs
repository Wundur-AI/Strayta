using Strayta.Core;
using Strayta.Core.Selection;

namespace Strayta.Segmentation;

/// <summary>
/// A model's low-resolution mask logits (positive inside the object) covering <see cref="Placement"/>, which the
/// model saw squashed to a square. <see cref="Score"/> is the model's own quality estimate, when it gives one.
/// </summary>
public sealed record MaskLogits(float[] Values, int Width, int Height, PixelRect Placement, float Score = float.NaN)
{
    /// <summary>The logit at a document position (continuous coordinates, pixel centers at +0.5), bilinearly interpolated.</summary>
    public float Sample(double docX, double docY)
    {
        var (u, v) = MaskUpscaler.ToMask(this, docX, docY);
        return MaskUpscaler.Bilinear(Values, Width, Height, u, v, out _, out _);
    }

    /// <summary>
    /// Signed distance in document pixels from a document position (continuous coordinates) to the mask's edge,
    /// positive inside: the logit over its gradient, as <see cref="MaskUpscaler"/> upscales. Negative infinity
    /// outside <see cref="Placement"/>, ±infinity where the logits are flat.
    /// </summary>
    public float SignedDistance(double docX, double docY) =>
        docX < Placement.Left || docY < Placement.Top || docX >= Placement.Right || docY >= Placement.Bottom
            ? float.NegativeInfinity
            : MaskUpscaler.Distance(this, docX - 0.5, docY - 0.5);
}

/// <summary>
/// Brings mask logits back to document resolution as a <see cref="SelectionMask"/>.
/// </summary>
/// <remarks>
/// Upscaling a 256×256 (SAM) or 512×512 (BiRefNet) mask to a 4000-pixel document by interpolating probabilities
/// gives edges tens of pixels wide. Instead the logits are interpolated and treated as a signed distance field:
/// coverage = 0.5 + logit / |∇logit|, with the gradient measured in document pixels. The zero crossing lands where
/// the model put it and the edge is anti-aliased over <c>softness</c> pixels no matter how far the mask is
/// scaled, which is what a selection edge should look like.
/// </remarks>
public static class MaskUpscaler
{
    /// <summary>Mask coordinates (continuous, cell centers at integers) of a document position.</summary>
    internal static (double U, double V) ToMask(MaskLogits m, double docX, double docY) =>
        ((docX - m.Placement.Left) * m.Width / m.Placement.Width - 0.5,
         (docY - m.Placement.Top) * m.Height / m.Placement.Height - 0.5);

    /// <summary>
    /// Converts <paramref name="mask"/> to a selection clipped to <paramref name="canvas"/>. <paramref name="softness"/>
    /// is the width of the anti-aliased edge in document pixels (1 = crisp, larger = feathered). With a
    /// <paramref name="guide"/> (the image the model saw), edges are snapped to the image's own color edges; see
    /// <see cref="EdgeRefiner"/>.
    /// </summary>
    public static SelectionMask? ToSelection(MaskLogits mask, PixelRect canvas, float softness = 1f, RgbaImage? guide = null)
    {
        var region = PositiveRegion(mask).Intersect(canvas);
        if (region.IsEmpty) return null;
        if (guide is not null)
        {
            // Refinement may move the edge outward by a cell and needs background samples beyond that.
            int grow = (int)Math.Ceiling(3.0 * Math.Max((double)mask.Placement.Width / mask.Width, (double)mask.Placement.Height / mask.Height));
            region = new PixelRect(region.Left - grow, region.Top - grow, region.Right + grow, region.Bottom + grow)
                .Intersect(mask.Placement).Intersect(guide.Placement).Intersect(canvas);
        }
        var distance = guide is null ? null : new float[region.Width * region.Height];
        var coverage = Rasterize(mask, region, softness, distance);
        if (guide is not null) EdgeRefiner.Refine(mask, guide, region, coverage, distance!);
        return SelectionMask.FromCoverage(region, coverage, canvas);
    }

    /// <summary>
    /// Coverage bytes over <paramref name="region"/> (document space), row-major; optionally also each pixel's
    /// signed distance to the edge, which edge refinement needs.
    /// </summary>
    public static byte[] Rasterize(MaskLogits mask, PixelRect region, float softness = 1f, float[]? distance = null)
    {
        int w = region.Width;
        var coverage = new byte[w * region.Height];
        float inv = 1f / Math.Max(softness, 1e-3f);
        Parallel.For(region.Top, region.Bottom, y =>
        {
            int row = (y - region.Top) * w - region.Left;
            for (int x = region.Left; x < region.Right; x++)
            {
                float d = Distance(mask, x, y);
                if (distance is not null) distance[row + x] = d;
                coverage[row + x] = ToByte(0.5f + d * inv);
            }
        });
        return coverage;
    }

    /// <summary>
    /// Signed distance in document pixels from the center of pixel (x, y) to the mask edge, positive inside:
    /// the interpolated logit divided by its gradient. Infinite where the logits are flat.
    /// </summary>
    internal static float Distance(MaskLogits mask, int x, int y) => Distance(mask, (double)x, y);

    /// <summary><see cref="Distance(MaskLogits, int, int)"/> at a fractional pixel position.</summary>
    internal static float Distance(MaskLogits mask, double x, double y)
    {
        var p = mask.Placement;
        double sx = (double)mask.Width / p.Width, sy = (double)mask.Height / p.Height;
        double u = (x + 0.5 - p.Left) * sx - 0.5, v = (y + 0.5 - p.Top) * sy - 0.5;
        float l = Bilinear(mask.Values, mask.Width, mask.Height, u, v, out float du, out float dv);
        float gx = (float)(du * sx), gy = (float)(dv * sy); // logits per document pixel
        float g = MathF.Sqrt(gx * gx + gy * gy);
        return g > 1e-6f ? l / g : l > 0 ? float.PositiveInfinity : float.NegativeInfinity;
    }

    internal static byte ToByte(float c) => c <= 0f ? (byte)0 : c >= 1f ? (byte)255 : (byte)(c * 255f + 0.5f);

    /// <summary>
    /// Document rectangle that can contain selected pixels: the positive mask cells grown by one cell (the
    /// interpolated edge reaches that far), limited to the placement.
    /// </summary>
    public static PixelRect PositiveRegion(MaskLogits mask)
    {
        int w = mask.Width, h = mask.Height;
        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
        {
            var row = mask.Values.AsSpan(y * w, w);
            int first = -1, last = -1;
            for (int x = 0; x < w; x++)
            {
                if (row[x] <= 0f) continue;
                if (first < 0) first = x;
                last = x;
            }
            if (first < 0) continue;
            minX = Math.Min(minX, first);
            maxX = Math.Max(maxX, last);
            minY = Math.Min(minY, y);
            maxY = y;
        }
        if (maxX < 0) return PixelRect.Empty;
        var p = mask.Placement;
        double cw = (double)p.Width / w, ch = (double)p.Height / h;
        var r = new PixelRect(
            p.Left + (int)Math.Floor((minX - 1) * cw), p.Top + (int)Math.Floor((minY - 1) * ch),
            p.Left + (int)Math.Ceiling((maxX + 2) * cw), p.Top + (int)Math.Ceiling((maxY + 2) * ch));
        return r.Intersect(p);
    }

    /// <summary>Bilinear sample with edge clamping; also returns the derivatives along u and v.</summary>
    internal static float Bilinear(float[] values, int w, int h, double u, double v, out float du, out float dv)
    {
        // Beyond the outer cell centers the value is held constant, so the derivative across that edge is zero.
        bool clampU = u < 0 || u > w - 1, clampV = v < 0 || v > h - 1;
        u = Math.Clamp(u, 0, w - 1);
        v = Math.Clamp(v, 0, h - 1);
        int x0 = Math.Min((int)u, Math.Max(0, w - 2)), y0 = Math.Min((int)v, Math.Max(0, h - 2));
        int x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
        float fx = (float)(u - x0), fy = (float)(v - y0);
        float a = values[y0 * w + x0], b = values[y0 * w + x1], c = values[y1 * w + x0], d = values[y1 * w + x1];
        float top = a + (b - a) * fx, bottom = c + (d - c) * fx;
        du = clampU ? 0f : (b - a) * (1 - fy) + (d - c) * fy;
        dv = clampV ? 0f : bottom - top;
        return top + (bottom - top) * fy;
    }
}
