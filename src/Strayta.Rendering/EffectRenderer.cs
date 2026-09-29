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

/// <summary>Another source's colors at full coverage: a layer's colors inside its shape, whatever its edge.</summary>
internal sealed class OpaqueSource(Source inner) : Source
{
    public override void FillRow(int y, int x0, int x1, Span<float> rgb, Span<float> coverage)
    {
        inner.FillRow(y, x0, x1, rgb, coverage);
        coverage[..(x1 - x0)].Fill(1f);
    }
}

/// <summary>Where <see cref="EffectRenderer.Render"/> draws a layer and its effects.</summary>
internal interface IEffectTarget
{
    /// <summary>The area that can be drawn into.</summary>
    PixelRect Bounds { get; }

    /// <summary>Composites a source with (blend mode, opacity, fill); see <see cref="EffectRenderer.Render"/>.</summary>
    void Composite(Source source, BlendMode mode, float opacity, float fill);

    /// <summary>Composites the layer's own content, as <see cref="Composite"/> but blending text the way Photoshop blends text.</summary>
    void CompositeContent(Source source, BlendMode mode, float opacity, float fill);

    /// <summary>
    /// A buffer over <paramref name="region"/> to build a layer's interior on: a copy of what is drawn so far, or, when
    /// the layer's content will cover it completely (<paramref name="covered"/>), an empty one.
    /// </summary>
    RenderBuffer BeginInterior(PixelRect region, bool covered);

    /// <summary>Composites a source onto an interior buffer at full opacity; <paramref name="fill"/> as in <see cref="Composite"/>.</summary>
    void CompositeInterior(RenderBuffer interior, Source source, BlendMode mode, float fill);

    /// <summary>Replaces what is drawn by the finished interior by <paramref name="shape"/>'s coverage × <paramref name="opacity"/>.</summary>
    void EndInterior(RenderBuffer interior, Source shape, float opacity);
}

/// <summary>
/// Renders a layer (or a group's flattened content) together with its layer styles, in Photoshop's stacking order
/// (the order of its Layer Style dialog, read bottom up): drop shadow and outer glow below the layer; pattern overlay,
/// gradient overlay, color overlay, satin, inner glow, inner shadow, stroke and bevel &amp; emboss above it. Effects
/// follow the layer's shape (pixel transparency × layer mask) and are not affected by fill opacity, except that below
/// 100% fill the shape hides its own shadows and glows (see KnockOut). Interior effects are drawn inside the shape (see
/// DrawInterior). An effect's own opacity acts as fill, which matters for Photoshop's special-eight blend modes; the
/// layer's opacity fades everything. Hidden effects, and all of them when the master switch is off, are skipped. Several
/// effects of one kind draw in list order, the last on top.
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

    /// <summary>
    /// How far beyond a pixel the layer's shape can influence its effects there, outwards or inwards: a glow near the
    /// canvas edge needs the layer's pixels that far beyond the edge.
    /// </summary>
    public static int Influence(LayerEffects? effects)
    {
        if (effects is null) return 0;
        float inward = effects.Visible.Select(e => e switch
        {
            InnerGlowEffect g => g.Size,
            InnerShadowEffect s => s.Size + s.Distance,
            SatinEffect s => s.Size + s.Distance,
            StrokeEffect s => s.Size,
            BevelEffect b => b.Size + b.Soften + 2,
            _ => 0f,
        }).DefaultIfEmpty(0f).Max();
        return Math.Max(Reach(effects), (int)MathF.Ceiling(inward) + 2);
    }

    /// <summary>True when the style draws more than the layer's interior: shadows, glows, strokes or bevels.</summary>
    public static bool DrawsOutside(LayerEffects? effects) =>
        effects?.Visible.Any(e => e is not UnsupportedEffect && !IsInterior(e)) == true;

    public static bool HasRenderable(LayerEffects? effects) =>
        effects?.Visible.Any(e => e is not UnsupportedEffect) == true;

    /// <param name="layer">The layer (or group) whose effects, opacity, fill opacity and blend mode apply.</param>
    /// <param name="content">The layer's pixels (for a group, its flattened content), which the effects follow.</param>
    /// <param name="target">Where the result goes (the canvas, or the buffer being rendered into).</param>
    /// <param name="opacity">The opacity to draw with, when not the layer's own (1 when the caller fades the result).</param>
    public static void Render(LayerNode layer, Source content, IEffectTarget target, float? opacity = null)
    {
        var effects = layer.Effects!.Visible.Where(e => e is not UnsupportedEffect).ToList();
        int reach = Reach(layer.Effects), influence = Influence(layer.Effects);
        // The field reaches as far beyond the target as the shape there can influence the effects inside it.
        var within = new PixelRect(target.Bounds.Left - influence, target.Bounds.Top - influence,
            target.Bounds.Right + influence, target.Bounds.Bottom + influence);
        var area = new PixelRect(content.Bounds.Left - reach, content.Bounds.Top - reach,
            content.Bounds.Right + reach, content.Bounds.Bottom + reach).Intersect(within);
        if (area.IsEmpty || area.Intersect(target.Bounds).IsEmpty) return;

        int w = area.Width, h = area.Height;
        var shape = new float[w * h];
        for (int y = content.Bounds.Top; y < content.Bounds.Bottom; y++)
        {
            if (y < area.Top || y >= area.Bottom) continue;
            int x0 = Math.Max(content.Bounds.Left, area.Left), x1 = Math.Min(content.Bounds.Right, area.Right);
            if (x1 <= x0) continue;
            content.FillRow(y, x0, x1, Span<float>.Empty, shape.AsSpan((y - area.Top) * w + (x0 - area.Left), x1 - x0));
        }

        // Patterns linked with the layer start at its bounds (for a shape, its outline's bounds), not at its first
        // visible pixel.
        var origin = layer is PixelLayer { Pixels: not null } pl ? pl.Bounds : content.Bounds;
        var field = EffectField.For(layer, shape, w, h, area, origin, target.Bounds);
        field.Rasterized = !layer.Tags.Contains("vector-mask") && !layer.Tags.Contains("text");
        float layerOpacity = opacity ?? layer.Opacity, fill = layer.FillOpacity;
        var mode = layer.BlendMode == BlendMode.PassThrough ? BlendMode.Normal : layer.BlendMode;

        foreach (var shadow in effects.OfType<DropShadowEffect>())
        {
            var alpha = Shadow(field, shadow);
            if (shadow.Knockout) KnockOut(alpha, shape, fill);
            target.Composite(new FieldSource(area, alpha, shadow.Color), shadow.BlendMode, layerOpacity, shadow.Opacity);
        }

        foreach (var glow in effects.OfType<OuterGlowEffect>())
        {
            var (alpha, rgb) = OuterGlow(field, glow);
            KnockOut(alpha, shape, fill);
            target.Composite(new FieldSource(area, alpha, glow.Color, rgb), glow.BlendMode, layerOpacity, glow.Opacity);
        }

        DrawInterior(layer, content, effects, field, target, mode, layerOpacity);

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
                    target.Composite(new FieldSource(area, a, default, rgb), stroke.BlendMode, layerOpacity, stroke.Opacity);
                    break;
                }
                case StrokeFillType.Pattern when stroke.PatternFill is { Pattern.Resolved: not null } pattern:
                {
                    var (rgb, a) = PatternFillField(pattern, alpha, field);
                    target.Composite(new FieldSource(area, a, default, rgb), stroke.BlendMode, layerOpacity, stroke.Opacity);
                    break;
                }
                case StrokeFillType.Gradient or StrokeFillType.Pattern:
                    break; // no gradient, or a pattern the file does not contain: nothing to draw
                default:
                    target.Composite(new FieldSource(area, alpha, stroke.Color), stroke.BlendMode, layerOpacity, stroke.Opacity);
                    break;
            }
        }

        foreach (var bevel in effects.OfType<BevelEffect>())
        {
            if (Bevel(field, bevel, strokes) is not var (highlight, shadow)) continue;
            target.Composite(new FieldSource(area, shadow, bevel.ShadowColor), bevel.ShadowMode, layerOpacity, bevel.ShadowOpacity);
            target.Composite(new FieldSource(area, highlight, bevel.HighlightColor), bevel.HighlightMode, layerOpacity, bevel.HighlightOpacity);
        }
    }

    /// <summary>
    /// Once fill opacity is below 100%, the layer's shape hides its own shadows and glows beneath it, completely,
    /// whatever the fill (as Photoshop renders them: a knockout proportional to 1 − fill leaves glow visible inside shapes
    /// at 42% fill that Photoshop does not show). At 100% the layer's pixels cover them anyway, and they still show
    /// through its soft or anti-aliased edges.
    /// </summary>
    private static void KnockOut(float[] alpha, float[] shape, float fill)
    {
        if (fill >= 1f) return;
        for (int i = 0; i < alpha.Length; i++) alpha[i] *= 1f - shape[i];
    }

    private static bool IsInterior(LayerEffect e) => InteriorRank(e) >= 0;

    /// <summary>Stacking order of the interior effects, bottom up; -1 for the others.</summary>
    private static int InteriorRank(LayerEffect e) => e switch
    {
        PatternOverlayEffect => 0,
        GradientOverlayEffect => 1,
        ColorOverlayEffect => 2,
        SatinEffect => 3,
        InnerGlowEffect => 4,
        InnerShadowEffect => 5,
        _ => -1,
    };

    /// <summary>
    /// The layer's content and its interior effects (overlays, satin, inner glow, inner shadow), the way Photoshop
    /// combines them: inside the shape, the content is blended onto the backdrop at fill opacity, then each interior
    /// effect onto that result, each at its own opacity; the finished interior then covers the backdrop by the shape's
    /// coverage (and the layer's opacity). So interior effects never add coverage at soft or anti-aliased edges, and
    /// they still show (over the backdrop) at fill 0%.
    /// </summary>
    private static void DrawInterior(LayerNode layer, Source content, List<LayerEffect> effects, EffectField field,
        IEffectTarget target, BlendMode mode, float layerOpacity)
    {
        if (!effects.Any(IsInterior) || mode == BlendMode.Dissolve)
        {
            // Nothing inside the shape but the content: plain compositing is the same, and cheaper. Dissolve scatters the
            // shape's coverage, so its interior effects are drawn over it, limited to the shape.
            target.CompositeContent(content, mode, layerOpacity, layer.FillOpacity);
            foreach (var e in effects.Where(IsInterior).OrderBy(InteriorRank))
                DrawInteriorEffect(e, field, (src, m, fx) => target.Composite(src, m, layerOpacity, fx), clipped: true);
            return;
        }

        var region = content.Bounds.Intersect(field.Area);
        if (region.IsEmpty) return;
        // Normal content at full fill hides the backdrop inside the shape, so the interior starts from the content alone.
        var interior = target.BeginInterior(region, covered: mode == BlendMode.Normal && layer.FillOpacity >= 1f);
        target.CompositeInterior(interior, new OpaqueSource(content) { Bounds = region }, mode, layer.FillOpacity);
        foreach (var e in effects.Where(IsInterior).OrderBy(InteriorRank))
            DrawInteriorEffect(e, field, (src, m, fx) => target.CompositeInterior(interior, src, m, fx), clipped: false);
        target.EndInterior(interior, new FieldSource(field.Area, field.Shape, default), layerOpacity);
    }

    /// <summary>
    /// Draws one interior effect with <paramref name="draw"/> (source, blend mode, the effect's opacity). Unclipped, its
    /// coverage is the effect's own strength inside the shape (1 for overlays), leaving the shape's edge to the caller.
    /// </summary>
    private static void DrawInteriorEffect(LayerEffect e, EffectField field, Action<Source, BlendMode, float> draw, bool clipped)
    {
        var area = field.Area;
        var shape = field.Shape;
        float[] Coverage(float[] alpha) => clipped ? alpha : Unclip(alpha, shape);
        switch (e)
        {
            case PatternOverlayEffect overlay when overlay.Fill is { Pattern.Resolved: not null } fill:
            {
                var (rgb, alpha) = PatternFillField(fill, shape, field);
                draw(new FieldSource(area, Coverage(alpha), default, rgb), overlay.BlendMode, overlay.Opacity);
                break;
            }
            case GradientOverlayEffect overlay:
            {
                var fill = new GradientFill(overlay.Gradient)
                {
                    Style = overlay.Style, Angle = overlay.Angle, Scale = overlay.Scale, Reverse = overlay.Reverse,
                    AlignWithLayer = overlay.AlignWithLayer, OffsetX = overlay.OffsetX, OffsetY = overlay.OffsetY,
                };
                var (rgb, alpha) = GradientFillField(fill, shape, field, shape);
                draw(new FieldSource(area, Coverage(alpha), default, rgb), overlay.BlendMode, overlay.Opacity);
                break;
            }
            case ColorOverlayEffect overlay:
                draw(new FieldSource(area, clipped ? shape : Ones(shape.Length), overlay.Color), overlay.BlendMode, overlay.Opacity);
                break;
            case SatinEffect satin:
                draw(new FieldSource(area, Satin(field, satin, clipped), satin.Color), satin.BlendMode, satin.Opacity);
                break;
            case InnerGlowEffect glow:
            {
                var (alpha, rgb) = InnerGlow(field, glow, clipped);
                draw(new FieldSource(area, alpha, glow.Color, rgb), glow.BlendMode, glow.Opacity);
                break;
            }
            case InnerShadowEffect shadow:
                draw(new FieldSource(area, InnerShadow(field, shadow, clipped), shadow.Color), shadow.BlendMode, shadow.Opacity);
                break;
        }
    }

    /// <summary>An effect's alpha (already limited to the shape) as its strength relative to the shape's coverage.</summary>
    private static float[] Unclip(float[] alpha, float[] shape)
    {
        for (int i = 0; i < alpha.Length; i++)
            alpha[i] = shape[i] > 0f ? MathF.Min(1f, alpha[i] / shape[i]) : 0f;
        return alpha;
    }

    private static float[] Ones(int n)
    {
        var a = new float[n];
        a.AsSpan().Fill(1f);
        return a;
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

    /// <param name="clip">Limit the shadow to the shape; otherwise it is its strength wherever the shape is.</param>
    private static float[] InnerShadow(EffectField f, InnerShadowEffect s, bool clip = true)
    {
        var (dx, dy) = Offset(s.Angle, s.Distance);
        var outside = Outside(f.Shape, f.W, f.H, s.Choke * s.Size, s.Size * (1f - s.Choke), dx, dy);
        Quality(outside, s.Contour, s.AntiAliased, s.Noise, f);
        if (clip)
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
        if (dx == 0 && dy == 0) return (float[])src.Clone();
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
    private static float[] Outside(float[] shape, int w, int h, float choke, float blur, int dx, int dy, bool glow = false)
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
        var blurred = glow ? GlowBlur(inside, w, h, blur) : FieldOps.Blur(inside, w, h, blur);
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

    /// <summary>
    /// The shape comes from pixels (a pixel layer or a group's content) rather than from a vector outline or type,
    /// whose edges are exact.
    /// </summary>
    public bool Rasterized { get; set; }

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
    private (object Key, float[] Value)? _profile;

    /// <summary>
    /// A value derived from the shape alone (a bevel's profile), kept for the next render with the same key: while a
    /// light, depth or color slider moves, the shape and its profile do not change.
    /// </summary>
    public float[] Derived(object key, Func<float[]> compute)
    {
        lock (this)
            if (_profile is { } p && p.Key.Equals(key)) return p.Value;
        var value = compute();
        lock (this) _profile = (key, value);
        return value;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<LayerNode, EffectField> Cache = new();

    /// <summary>
    /// The field for <paramref name="layer"/>'s shape: the one kept from its previous render when the shape and area
    /// are the same (so distance fields and profiles are not computed again while a style slider is dragged), else a
    /// new one, kept for next time. Comparing the shape costs far less than the distance transforms it saves.
    /// </summary>
    public static EffectField For(LayerNode layer, float[] shape, int w, int h, PixelRect area, PixelRect contentBounds, PixelRect target)
    {
        if (Cache.TryGetValue(layer, out var kept) && kept.Area == area && kept.ContentBounds == contentBounds && kept.Target == target
            && kept.Shape.AsSpan().SequenceEqual(shape))
            return kept;
        var field = new EffectField(shape, w, h, area, contentBounds, target);
        Cache.AddOrUpdate(layer, field);
        return field;
    }
}
