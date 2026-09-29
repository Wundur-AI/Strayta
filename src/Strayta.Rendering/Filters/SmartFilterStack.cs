using Strayta.Core;

namespace Strayta.Rendering.Filters;

/// <summary>One smart filter as drawn: the filter and how its result blends onto what it filtered.</summary>
public sealed record SmartFilterStep(ImageFilter Filter, BlendMode BlendMode = BlendMode.Normal, float Opacity = 1f);

/// <summary>
/// Smart filters, applied non-destructively to a smart object's placed pixels the way Photoshop stacks them: each
/// filter works on the result of the ones below it, and its result is blended back onto that input with the filter's
/// blending options (mode and opacity, like Edit › Fade); the filter mask then limits the whole stack, mixing the
/// filtered and unfiltered pixels by its value (black keeps the unfiltered pixels).
/// </summary>
public static class SmartFilterStack
{
    /// <param name="pixels">The placed pixels over <paramref name="bounds"/>.</param>
    /// <param name="canvas">The document bounds (filters see beyond them as the edge repeated, as the Filter menu does).</param>
    /// <param name="mask">The smart filter mask in document coordinates, or null.</param>
    public static (Raster? Pixels, PixelRect Bounds) Apply(Raster? pixels, PixelRect bounds, IReadOnlyList<SmartFilterStep> steps, PixelRect canvas,
        LayerMask? mask = null, CancellationToken cancel = default)
    {
        if (pixels is null || steps.Count == 0) return (pixels, bounds);
        var scope = new FilterScope(canvas);
        var (current, currentBounds) = (pixels, bounds);
        foreach (var step in steps)
        {
            cancel.ThrowIfCancellationRequested();
            var (filtered, filteredBounds) = FilterEngine.ApplyToLayer(current, currentBounds, step.Filter, scope, cancel);
            if (filtered is null) continue;
            (current, currentBounds) = step.BlendMode == BlendMode.Normal && step.Opacity >= 1f
                ? (filtered, filteredBounds)
                : Mix(current, currentBounds, filtered, filteredBounds, (a, b, o) => Blend(step.BlendMode, a, b), step.Opacity, null);
        }
        if (mask is { Disabled: false })
            (current, currentBounds) = Mix(pixels, bounds, current, currentBounds, (_, b, _) => b, 1f, mask);
        return (current, currentBounds);
    }

    /// <summary>The straight color a blend mode gives for the input <paramref name="b"/> under the filtered <paramref name="s"/>.</summary>
    private static float[] Blend(BlendMode mode, float[] b, float[] s)
    {
        if (b.Length == 3 && !BlendFunctions.IsSeparable(mode))
        {
            var (r, g, bl) = BlendFunctions.NonSeparable(mode, b[0], b[1], b[2], s[0], s[1], s[2]);
            return [r, g, bl];
        }
        var o = new float[b.Length];
        for (int i = 0; i < b.Length; i++) o[i] = BlendFunctions.Separable(mode, b[i], s[i]);
        return o;
    }

    /// <summary>
    /// Mixes <paramref name="under"/> (the input) and <paramref name="over"/> (the filtered result) over the union of
    /// their bounds: alpha and color fade from the input to the blend by <paramref name="opacity"/> times the mask.
    /// </summary>
    private static (Raster, PixelRect) Mix(Raster under, PixelRect ub, Raster over, PixelRect ob, Func<float[], float[], float, float[]> blend,
        float opacity, LayerMask? mask)
    {
        var bounds = new PixelRect(Math.Min(ub.Left, ob.Left), Math.Min(ub.Top, ob.Top), Math.Max(ub.Right, ob.Right), Math.Max(ub.Bottom, ob.Bottom));
        int w = bounds.Width, h = bounds.Height, colors = under.ColorPlanes.Count, depth = under.BitDepth;
        var planes = Enumerable.Range(0, colors + 1).Select(_ => Plane.Create(w, h, depth)).ToArray();
        Parallel.For(0, h, y =>
        {
            var b = new float[colors];
            var s = new float[colors];
            for (int x = 0; x < w; x++)
            {
                int dx = bounds.Left + x, dy = bounds.Top + y;
                float ba = Sample(under, ub, dx, dy, b), sa = Sample(over, ob, dx, dy, s);
                float k = opacity * (mask is null ? 1f : MaskAt(mask, dx, dy));
                float a = ba + (sa - ba) * k;
                int i = y * w + x;
                if (a <= 1e-6f)
                {
                    for (int c = 0; c <= colors; c++) Resample(planes[c], i, 0);
                    continue;
                }
                // Where only one side has color, that side's color is used as the other.
                if (ba <= 0) Array.Copy(s, b, colors);
                if (sa <= 0) Array.Copy(b, s, colors);
                var mixed = blend(b, s, k);
                for (int c = 0; c < colors; c++)
                {
                    // Premultiplied fade from the input to the blended color.
                    float pm = b[c] * ba * (1 - k) + mixed[c] * sa * k;
                    Resample(planes[c], i, pm / a);
                }
                Resample(planes[colors], i, a);
            }
        });
        return (new Raster(under.ColorMode, planes[..colors], planes[colors]), bounds);
    }

    private static void Resample(Plane p, int i, float v) => Transforms.Resampler.Store(p, i, v);

    private static float Sample(Raster r, PixelRect rb, int x, int y, float[] color)
    {
        if (x < rb.Left || y < rb.Top || x >= rb.Right || y >= rb.Bottom)
        {
            Array.Clear(color);
            return 0;
        }
        int i = (y - rb.Top) * rb.Width + x - rb.Left;
        for (int c = 0; c < color.Length; c++) color[c] = r.ColorPlanes[c].GetNormalized(i);
        return r.Alpha?.GetNormalized(i) ?? 1f;
    }

    private static float MaskAt(LayerMask m, int x, int y)
    {
        if (m.Pixels is not { } p || x < m.Bounds.Left || y < m.Bounds.Top || x >= m.Bounds.Right || y >= m.Bounds.Bottom) return m.DefaultColor / 255f;
        return p.GetNormalized((y - m.Bounds.Top) * m.Bounds.Width + x - m.Bounds.Left);
    }
}
