using Strayta.Core;

namespace Strayta.Rendering;

/// <summary>A source whose color comes from a per-pixel (or constant) color and a precomputed alpha field.</summary>
internal sealed class FieldSource : Source
{
    private readonly float[] _alpha;
    private readonly float[]? _rgb;
    private readonly RgbColor _color;

    [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
    public FieldSource(PixelRect bounds, float[] alpha, RgbColor color, float[]? rgb = null)
    {
        Bounds = bounds;
        _alpha = alpha;
        _color = color;
        _rgb = rgb;
    }

    public override void FillRow(int y, int x0, int x1, Span<float> rgb, Span<float> coverage)
    {
        int n = x1 - x0;
        int start = (y - Bounds.Top) * Bounds.Width + (x0 - Bounds.Left);
        _alpha.AsSpan(start, n).CopyTo(coverage);
        if (rgb.IsEmpty) return;
        if (_rgb is not null)
        {
            _rgb.AsSpan(start * 3, n * 3).CopyTo(rgb);
            return;
        }
        for (int i = 0; i < n; i++)
        {
            rgb[i * 3] = _color.R;
            rgb[i * 3 + 1] = _color.G;
            rgb[i * 3 + 2] = _color.B;
        }
    }
}

/// <summary>
/// Renders a layer together with its layer styles, in Photoshop's stacking order:
/// drop shadow and outer glow below the layer; gradient overlay, color overlay and stroke above it.
/// Effects follow the layer's shape (pixel transparency × layer mask) and are not affected by fill opacity.
/// </summary>
internal static class EffectRenderer
{
    /// <summary>How far outside the layer's pixels its effects can reach.</summary>
    public static int Reach(LayerEffects? effects)
    {
        if (effects is null) return 0;
        float reach = 0;
        foreach (var e in effects.Items)
        {
            reach = MathF.Max(reach, e switch
            {
                DropShadowEffect d => d.Distance + d.Size,
                OuterGlowEffect g => g.Size,
                StrokeEffect { Position: not StrokePosition.Inside } s => s.Size,
                _ => 0f,
            });
        }
        return (int)MathF.Ceiling(reach) + 2;
    }

    public static bool HasRenderable(LayerEffects? effects) =>
        effects?.Items.Any(e => e is not UnsupportedEffect) == true;

    /// <param name="composite">
    /// Composites a source with (blend mode, opacity, fill). An effect's own opacity acts as fill, which
    /// matters for Photoshop's special-eight blend modes; the layer's opacity fades everything.
    /// </param>
    public static void Render(PixelLayer layer, LayerSource content, PixelRect targetBounds,
        Action<Source, BlendMode, float, float> composite)
    {
        var effects = layer.Effects!.Items;
        int reach = Reach(layer.Effects);
        var area = new PixelRect(content.Bounds.Left - reach, content.Bounds.Top - reach,
            content.Bounds.Right + reach, content.Bounds.Bottom + reach).Intersect(targetBounds);
        if (area.IsEmpty) return;

        int w = area.Width, h = area.Height;
        var shape = new float[w * h];
        for (int y = content.Bounds.Top; y < content.Bounds.Bottom; y++)
        {
            if (y < area.Top || y >= area.Bottom) continue;
            int x0 = Math.Max(content.Bounds.Left, area.Left), x1 = Math.Min(content.Bounds.Right, area.Right);
            content.FillRow(y, x0, x1, Span<float>.Empty, shape.AsSpan((y - area.Top) * w + (x0 - area.Left), x1 - x0));
        }


        float layerOpacity = layer.Opacity;

        foreach (var shadow in effects.OfType<DropShadowEffect>())
        {
            var alpha = Shadow(shape, w, h, shadow);
            if (shadow.Knockout && layer.FillOpacity < 1f)
                for (int i = 0; i < alpha.Length; i++) alpha[i] *= 1f - shape[i];
            composite(new FieldSource(area, alpha, shadow.Color), shadow.BlendMode, layerOpacity, shadow.Opacity);
        }

        foreach (var glow in effects.OfType<OuterGlowEffect>())
        {
            var alpha = FieldOps.Blur(FieldOps.Dilate(shape, w, h, glow.Spread * glow.Size), w, h, glow.Size * (1f - glow.Spread));
            composite(new FieldSource(area, alpha, glow.Color), glow.BlendMode, layerOpacity, glow.Opacity);
        }

        composite(content, layer.BlendMode, layerOpacity, layer.FillOpacity);

        foreach (var overlay in effects.OfType<GradientOverlayEffect>())
        {
            var (rgb, alpha) = GradientFill(overlay, shape, area);
            composite(new FieldSource(area, alpha, default, rgb), overlay.BlendMode, layerOpacity, overlay.Opacity);
        }

        foreach (var overlay in effects.OfType<ColorOverlayEffect>())
            composite(new FieldSource(area, shape, overlay.Color), overlay.BlendMode, layerOpacity, overlay.Opacity);

        foreach (var stroke in effects.OfType<StrokeEffect>())
            composite(new FieldSource(area, Stroke(shape, w, h, stroke), stroke.Color), stroke.BlendMode, layerOpacity, stroke.Opacity);
    }

    /// <summary>Spread grows the shape, the rest of the size blurs it, then it is offset away from the light.</summary>
    private static float[] Shadow(float[] shape, int w, int h, DropShadowEffect s)
    {
        var grown = FieldOps.Dilate(shape, w, h, s.Spread * s.Size);
        var blurred = FieldOps.Blur(grown, w, h, s.Size * (1f - s.Spread));
        double a = s.Angle * Math.PI / 180.0;
        int dx = (int)Math.Round(-Math.Cos(a) * s.Distance);
        int dy = (int)Math.Round(Math.Sin(a) * s.Distance);

        var shifted = new float[shape.Length];
        for (int y = 0; y < h; y++)
        {
            int sy = y - dy;
            if (sy < 0 || sy >= h) continue;
            for (int x = 0; x < w; x++)
            {
                int sx = x - dx;
                if (sx >= 0 && sx < w) shifted[y * w + x] = blurred[sy * w + sx];
            }
        }
        return shifted;
    }

    /// <summary>
    /// Distances are measured between pixel centers, so the edge of the shape sits half a pixel closer
    /// than the nearest inside pixel: a pixel is covered while distance - 0.5 is within the stroke width.
    /// </summary>
    private static float[] Stroke(float[] shape, int w, int h, StrokeEffect s)
    {
        var alpha = new float[shape.Length];
        float outer = s.Position switch { StrokePosition.Outside => s.Size, StrokePosition.Center => s.Size / 2f, _ => 0f };
        float inner = s.Position switch { StrokePosition.Inside => s.Size, StrokePosition.Center => s.Size / 2f, _ => 0f };

        if (outer > 0f)
        {
            var toShape = FieldOps.DistanceTo(shape, w, h, v => v >= 0.5f);
            for (int i = 0; i < alpha.Length; i++)
                alpha[i] = Math.Clamp(outer + 1f - toShape[i], 0f, 1f) * (1f - shape[i]);
        }
        if (inner > 0f)
        {
            var toOutside = FieldOps.DistanceTo(shape, w, h, v => v < 0.5f);
            for (int i = 0; i < alpha.Length; i++)
                alpha[i] += Math.Clamp(inner + 1f - toOutside[i], 0f, 1f) * shape[i];
        }
        return alpha;
    }

    /// <summary>
    /// Evaluates a gradient overlay over the layer's shape. When aligned with the layer, the gradient spans
    /// the bounding box of the layer's visible pixels.
    /// </summary>
    private static (float[] Rgb, float[] Alpha) GradientFill(GradientOverlayEffect g, float[] shape, PixelRect area)
    {
        int w = area.Width, h = area.Height;
        var box = g.AlignWithLayer ? ShapeBounds(shape, w, h) : new PixelRect(-area.Left, -area.Top, -area.Left + 1, -area.Top + 1);
        if (!g.AlignWithLayer) box = new PixelRect(0, 0, w, h);
        float bw = box.Width, bh = box.Height;
        float cx = box.Left + bw / 2f + g.OffsetX * bw;
        float cy = box.Top + bh / 2f + g.OffsetY * bh;

        double a = g.Angle * Math.PI / 180.0;
        float dirX = (float)Math.Cos(a), dirY = (float)-Math.Sin(a);
        float length = (MathF.Abs(bw * dirX) + MathF.Abs(bh * dirY)) * g.Scale;
        float radius = MathF.Sqrt(bw * bw + bh * bh) / 2f * g.Scale;
        if (length <= 0f) length = 1f;
        if (radius <= 0f) radius = 1f;

        var rgb = new float[w * h * 3];
        var alpha = new float[w * h];
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (shape[i] <= 0f) continue;
                float px = x + 0.5f - cx, py = y + 0.5f - cy;
                float along = px * dirX + py * dirY, across = -px * dirY + py * dirX;
                float t = g.Style switch
                {
                    GradientStyle.Radial => MathF.Sqrt(px * px + py * py) / radius,
                    GradientStyle.Reflected => MathF.Abs(along) / (length / 2f),
                    GradientStyle.Diamond => (MathF.Abs(along) + MathF.Abs(across)) / (length / 2f),
                    GradientStyle.Angle => (float)((Math.Atan2(-across, along) / (2 * Math.PI) + 1) % 1),
                    _ => along / length + 0.5f,
                };
                if (g.Reverse) t = 1f - t;
                var (color, opacity) = g.Gradient.Sample(t);
                rgb[i * 3] = color.R;
                rgb[i * 3 + 1] = color.G;
                rgb[i * 3 + 2] = color.B;
                alpha[i] = shape[i] * opacity;
            }
        });
        return (rgb, alpha);
    }

    private static PixelRect ShapeBounds(float[] shape, int w, int h)
    {
        int l = w, t = h, r = 0, b = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (shape[y * w + x] > 0.01f)
                {
                    l = Math.Min(l, x); r = Math.Max(r, x + 1);
                    t = Math.Min(t, y); b = Math.Max(b, y + 1);
                }
        return r > l ? new PixelRect(l, t, r, b) : new PixelRect(0, 0, w, h);
    }
}
