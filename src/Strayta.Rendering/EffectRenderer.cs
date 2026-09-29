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
/// Renders a layer (or a group's flattened content) together with its layer styles, in Photoshop's stacking order
/// (the order of its Layer Style dialog, read bottom up): drop shadow and outer glow below the layer; pattern overlay,
/// gradient overlay, color overlay, satin, inner glow, inner shadow, stroke and bevel &amp; emboss above it. Effects
/// follow the layer's shape (pixel transparency × layer mask) and are not affected by fill opacity. Hidden effects,
/// and all of them when the master switch is off, are skipped. Several effects of one kind draw in list order, the
/// last on top.
/// </summary>
internal static partial class EffectRenderer
{
    /// <summary>How far outside the layer's pixels its effects can reach.</summary>
    public static int Reach(LayerEffects? effects)
    {
        if (effects is null) return 0;
        float reach = 0;
        var visible = effects.Visible.ToList();
        float stroke = visible.OfType<StrokeEffect>().Where(s => s.Position != StrokePosition.Inside)
            .Select(s => s.Position == StrokePosition.Center ? s.Size / 2f : s.Size).DefaultIfEmpty(0f).Max();
        foreach (var e in visible)
        {
            reach = MathF.Max(reach, e switch
            {
                DropShadowEffect d => d.Distance + d.Size,
                OuterGlowEffect g => g.Size,
                StrokeEffect { Position: not StrokePosition.Inside } s => s.Size,
                BevelEffect { Style: BevelStyle.OuterBevel or BevelStyle.Emboss or BevelStyle.PillowEmboss } b => b.Size + b.Soften + 2,
                BevelEffect { Style: BevelStyle.StrokeEmboss } b => stroke + b.Soften + 2,
                _ => 0f,
            });
        }
        return (int)MathF.Ceiling(reach) + 2;
    }

    public static bool HasRenderable(LayerEffects? effects) =>
        effects?.Visible.Any(e => e is not UnsupportedEffect) == true;

    /// <param name="layer">The layer (or group) whose effects, opacity, fill opacity and blend mode apply.</param>
    /// <param name="content">The layer's pixels (for a group, its flattened content), which the effects follow.</param>
    /// <param name="targetBounds">Where the result can go (the canvas, or the buffer being rendered into).</param>
    /// <param name="composite">
    /// Composites a source with (blend mode, opacity, fill). An effect's own opacity acts as fill, which
    /// matters for Photoshop's special-eight blend modes; the layer's opacity fades everything.
    /// </param>
    public static void Render(LayerNode layer, Source content, PixelRect targetBounds,
        Action<Source, BlendMode, float, float> composite)
    {
        var effects = layer.Effects!.Visible.Where(e => e is not UnsupportedEffect).ToList();
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
            if (x1 <= x0) continue;
            content.FillRow(y, x0, x1, Span<float>.Empty, shape.AsSpan((y - area.Top) * w + (x0 - area.Left), x1 - x0));
        }

        var field = new EffectField(shape, w, h, area, content.Bounds, targetBounds);
        float layerOpacity = layer.Opacity;

        foreach (var shadow in effects.OfType<DropShadowEffect>())
        {
            var alpha = Shadow(field, shadow);
            if (shadow.Knockout && layer.FillOpacity < 1f)
                for (int i = 0; i < alpha.Length; i++) alpha[i] *= 1f - shape[i];
            composite(new FieldSource(area, alpha, shadow.Color), shadow.BlendMode, layerOpacity, shadow.Opacity);
        }

        foreach (var glow in effects.OfType<OuterGlowEffect>())
        {
            var (alpha, rgb) = OuterGlow(field, glow);
            composite(new FieldSource(area, alpha, glow.Color, rgb), glow.BlendMode, layerOpacity, glow.Opacity);
        }

        composite(content, layer.BlendMode, layerOpacity, layer.FillOpacity);

        foreach (var overlay in effects.OfType<PatternOverlayEffect>())
        {
            if (overlay.Fill is not { Pattern.Resolved: not null } fill) continue;
            var (rgb, alpha) = PatternFillField(fill, shape, field);
            composite(new FieldSource(area, alpha, default, rgb), overlay.BlendMode, layerOpacity, overlay.Opacity);
        }

        foreach (var overlay in effects.OfType<GradientOverlayEffect>())
        {
            var fill = new GradientFill(overlay.Gradient)
            {
                Style = overlay.Style, Angle = overlay.Angle, Scale = overlay.Scale, Reverse = overlay.Reverse,
                AlignWithLayer = overlay.AlignWithLayer, OffsetX = overlay.OffsetX, OffsetY = overlay.OffsetY,
            };
            var (rgb, alpha) = GradientFillField(fill, shape, field, shape);
            composite(new FieldSource(area, alpha, default, rgb), overlay.BlendMode, layerOpacity, overlay.Opacity);
        }

        foreach (var overlay in effects.OfType<ColorOverlayEffect>())
            composite(new FieldSource(area, shape, overlay.Color), overlay.BlendMode, layerOpacity, overlay.Opacity);

        foreach (var satin in effects.OfType<SatinEffect>())
            composite(new FieldSource(area, Satin(field, satin), satin.Color), satin.BlendMode, layerOpacity, satin.Opacity);

        foreach (var glow in effects.OfType<InnerGlowEffect>())
        {
            var (alpha, rgb) = InnerGlow(field, glow);
            composite(new FieldSource(area, alpha, glow.Color, rgb), glow.BlendMode, layerOpacity, glow.Opacity);
        }

        foreach (var shadow in effects.OfType<InnerShadowEffect>())
            composite(new FieldSource(area, InnerShadow(field, shadow), shadow.Color), shadow.BlendMode, layerOpacity, shadow.Opacity);

        float[]? strokes = null;
        foreach (var stroke in effects.OfType<StrokeEffect>())
        {
            var alpha = Stroke(field, stroke);
            if (strokes is null) strokes = (float[])alpha.Clone();
            else for (int i = 0; i < alpha.Length; i++) strokes[i] = MathF.Max(strokes[i], alpha[i]);

            switch (stroke.FillType)
            {
                case StrokeFillType.Gradient when stroke.GradientFill is { } gradient:
                {
                    var (rgb, a) = GradientFillField(gradient, alpha, field, shape, stroke);
                    composite(new FieldSource(area, a, default, rgb), stroke.BlendMode, layerOpacity, stroke.Opacity);
                    break;
                }
                case StrokeFillType.Pattern when stroke.PatternFill is { Pattern.Resolved: not null } pattern:
                {
                    var (rgb, a) = PatternFillField(pattern, alpha, field);
                    composite(new FieldSource(area, a, default, rgb), stroke.BlendMode, layerOpacity, stroke.Opacity);
                    break;
                }
                case StrokeFillType.Gradient or StrokeFillType.Pattern:
                    break; // no gradient, or a pattern the file does not contain: nothing to draw
                default:
                    composite(new FieldSource(area, alpha, stroke.Color), stroke.BlendMode, layerOpacity, stroke.Opacity);
                    break;
            }
        }

        foreach (var bevel in effects.OfType<BevelEffect>())
        {
            if (Bevel(field, bevel, strokes) is not var (highlight, shadow)) continue;
            composite(new FieldSource(area, shadow, bevel.ShadowColor), bevel.ShadowMode, layerOpacity, bevel.ShadowOpacity);
            composite(new FieldSource(area, highlight, bevel.HighlightColor), bevel.HighlightMode, layerOpacity, bevel.HighlightOpacity);
        }
    }

    /// <summary>Spread grows the shape, the rest of the size blurs it, then it is offset away from the light.</summary>
    private static float[] Shadow(EffectField f, DropShadowEffect s)
    {
        var grown = FieldOps.Dilate(f.Shape, f.W, f.H, s.Spread * s.Size);
        var blurred = FieldOps.Blur(grown, f.W, f.H, s.Size * (1f - s.Spread));
        var (dx, dy) = Offset(s.Angle, s.Distance);
        var shifted = Shift(blurred, f.W, f.H, dx, dy, outside: 0f);
        Quality(shifted, s.Contour, s.AntiAliased, s.Noise, f);
        return shifted;
    }

    private static float[] InnerShadow(EffectField f, InnerShadowEffect s)
    {
        var (dx, dy) = Offset(s.Angle, s.Distance);
        var outside = Outside(f.Shape, f.W, f.H, s.Choke * s.Size, s.Size * (1f - s.Choke), dx, dy);
        Quality(outside, s.Contour, s.AntiAliased, s.Noise, f);
        for (int i = 0; i < outside.Length; i++) outside[i] *= f.Shape[i];
        return outside;
    }

    /// <summary>Shadows fall away from the light: the offset for a light at <paramref name="angle"/> degrees.</summary>
    internal static (int Dx, int Dy) Offset(float angle, float distance)
    {
        double a = angle * Math.PI / 180.0;
        return ((int)Math.Round(-Math.Cos(a) * distance), (int)Math.Round(Math.Sin(a) * distance));
    }

    /// <summary>The field moved by (dx, dy); what comes in from beyond the edge is <paramref name="outside"/>.</summary>
    internal static float[] Shift(float[] src, int w, int h, int dx, int dy, float outside)
    {
        var result = new float[src.Length];
        Parallel.For(0, h, y =>
        {
            int sy = y - dy;
            for (int x = 0; x < w; x++)
            {
                int sx = x - dx;
                result[y * w + x] = sx >= 0 && sx < w && sy >= 0 && sy < h ? src[sy * w + sx] : outside;
            }
        });
        return result;
    }

    /// <summary>
    /// The area outside the layer's shape, grown inwards by <paramref name="choke"/> pixels, blurred by
    /// <paramref name="blur"/> and shifted by (<paramref name="dx"/>, <paramref name="dy"/>): what inner shadows and
    /// glows are made of. Beyond the field counts as outside, so it is computed as the complement of the blurred
    /// (choked) shape, whose zero padding is exactly that.
    /// </summary>
    private static float[] Outside(float[] shape, int w, int h, float choke, float blur, int dx, int dy)
    {
        var inside = shape;
        if (choke > 0f)
        {
            var outside = new float[shape.Length];
            for (int i = 0; i < outside.Length; i++) outside[i] = 1f - shape[i];
            var grown = FieldOps.Dilate(outside, w, h, choke);
            inside = new float[shape.Length];
            for (int i = 0; i < inside.Length; i++) inside[i] = 1f - grown[i];
        }
        var blurred = FieldOps.Blur(inside, w, h, blur);
        var result = Shift(blurred, w, h, dx, dy, outside: 0f);
        for (int i = 0; i < result.Length; i++) result[i] = 1f - result[i];
        return result;
    }

    /// <summary>
    /// Distances are measured between pixel centers, so the edge of the shape sits half a pixel closer
    /// than the nearest inside pixel: a pixel is covered while distance - 0.5 is within the stroke width.
    /// </summary>
    private static float[] Stroke(EffectField f, StrokeEffect s)
    {
        var shape = f.Shape;
        var alpha = new float[shape.Length];
        float outer = s.Position switch { StrokePosition.Outside => s.Size, StrokePosition.Center => s.Size / 2f, _ => 0f };
        float inner = s.Position switch { StrokePosition.Inside => s.Size, StrokePosition.Center => s.Size / 2f, _ => 0f };

        if (outer > 0f)
        {
            var toShape = f.DistanceToShape;
            for (int i = 0; i < alpha.Length; i++)
                alpha[i] = Math.Clamp(outer + 1f - toShape[i], 0f, 1f) * (1f - shape[i]);
        }
        if (inner > 0f)
        {
            var toOutside = f.DistanceToOutside;
            for (int i = 0; i < alpha.Length; i++)
                alpha[i] += Math.Clamp(inner + 1f - toOutside[i], 0f, 1f) * shape[i];
        }
        return alpha;
    }
}

/// <summary>
/// The layer's shape over the area its effects cover, with the distance fields several effects share computed once.
/// </summary>
internal sealed class EffectField(float[] shape, int w, int h, PixelRect area, PixelRect contentBounds, PixelRect target)
{
    public float[] Shape { get; } = shape;
    public int W { get; } = w;
    public int H { get; } = h;

    /// <summary>The field's place in the document.</summary>
    public PixelRect Area { get; } = area;

    /// <summary>The layer's own bounds in the document (where "Link with Layer" patterns start).</summary>
    public PixelRect ContentBounds { get; } = contentBounds;

    /// <summary>What is being rendered (normally the canvas): the extent of gradients not aligned with the layer.</summary>
    public PixelRect Target { get; } = target;

    /// <summary>Distance from each pixel center to the nearest pixel at least half inside the shape.</summary>
    public float[] DistanceToShape => _toShape ??= FieldOps.DistanceTo(Shape, W, H, v => v >= 0.5f);

    /// <summary>Distance from each pixel center to the nearest pixel less than half inside the shape.</summary>
    public float[] DistanceToOutside => _toOutside ??= FieldOps.DistanceTo(Shape, W, H, v => v < 0.5f);

    /// <summary>Signed distance to the shape's edge, positive inside; see <see cref="EffectRenderer.SignedDistance(float[], int, int)"/>.</summary>
    public float[] SignedDistance => _signed ??= EffectRenderer.SignedDistance(Shape, DistanceToShape, DistanceToOutside);

    private float[]? _toShape, _toOutside, _signed;
}
