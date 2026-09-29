using Strayta.Core;

namespace Strayta.Rendering;

// Layer styles on groups: the group's flattened content is the shape its effects follow.
public sealed partial class Compositor
{
    /// <summary>
    /// A group with layer styles renders its children in isolation (as Photoshop does once a group has effects, even
    /// in Pass Through), and that composite, with the group's mask, is drawn with the group's effects exactly like a
    /// layer's pixels: shadows and glows around it, overlays on it, fill opacity fading the content only.
    /// </summary>
    private void RenderGroupWithEffects(LayerGroup group, PixelRect bounds, RenderBuffer target, Source? clip, float clipOpacity)
    {
        var isolated = new RenderBuffer(bounds);
        RenderChildren(group.Children, isolated);
        var content = new BufferSource(isolated, group.Mask, MaskStroke(group)) { Bounds = bounds };
        CompositeWithEffects(group, content, target, clip, clipOpacity);
    }

    /// <summary>
    /// Draws <paramref name="content"/> with <paramref name="node"/>'s effects; Pass Through draws as Normal. The node's
    /// opacity fades the layer and its effects as one: a 50% layer with a glow shows the glow faded where it is uncovered,
    /// not through the faded layer, as Photoshop draws it.
    /// </summary>
    private void CompositeWithEffects(LayerNode node, Source content, RenderBuffer target, Source? clip, float clipOpacity)
    {
        // Fading the parts separately is the same when the effects are all inside the shape (the interior is faded as
        // one), so only styles that also draw around it need the faded copy.
        if (node.Opacity >= 1f || !EffectRenderer.DrawsOutside(node.Effects))
        {
            EffectRenderer.Render(node, content, new EffectTarget(this, target, clip, clipOpacity, TextGamma(node)));
            return;
        }
        // Opacity fades the layer and its effects together: draw them at full opacity onto a copy of the backdrop, then
        // fade from the backdrop to that.
        int reach = EffectRenderer.Reach(node.Effects);
        var region = Inflate(content.Bounds, reach).Intersect(target.Bounds);
        if (region.IsEmpty || node.Opacity <= 0f) return;
        var copy = target.CopyRegion(region);
        EffectRenderer.Render(node, content, new EffectTarget(this, copy, clip: null, 1f, TextGamma(node)), opacity: 1f);
        LerpBy(target, copy, new ConstantCoverage { Bounds = region }, node.Opacity, clip, clipOpacity, TextGamma(node));
    }

    /// <summary>Full coverage everywhere.</summary>
    private sealed class ConstantCoverage : Source
    {
        public override void FillRow(int y, int x0, int x1, Span<float> rgb, Span<float> coverage) => coverage[..(x1 - x0)].Fill(1f);
    }

    /// <summary>Draws effects into a buffer, limited to a clipping base when there is one.</summary>
    private sealed class EffectTarget(Compositor compositor, RenderBuffer target, Source? clip, float clipOpacity, float textGamma) : IEffectTarget
    {
        public PixelRect Bounds => target.Bounds;

        public void Composite(Source source, BlendMode mode, float opacity, float fill) =>
            compositor.Composite(target, source, mode, opacity, fill, clip, clipOpacity);

        public void CompositeContent(Source source, BlendMode mode, float opacity, float fill) =>
            compositor.Composite(target, source, mode, opacity, fill, clip, clipOpacity, textGamma);

        public RenderBuffer BeginInterior(PixelRect region, bool covered) =>
            covered ? new RenderBuffer(region.Intersect(target.Bounds)) : target.CopyRegion(region.Intersect(target.Bounds));

        public void CompositeInterior(RenderBuffer interior, Source source, BlendMode mode, float fill) =>
            compositor.Composite(interior, source, mode, 1f, fill, clip: null, clipOpacity: 1f);

        public void EndInterior(RenderBuffer interior, Source shape, float opacity) =>
            compositor.LerpBy(target, interior, shape, opacity, clip, clipOpacity, textGamma);
    }

    /// <summary>
    /// target = lerp(target, result, coverage × opacity × clip) over result's bounds (both premultiplied); with a
    /// <paramref name="gamma"/> the colors are mixed as type is (see <see cref="RenderOptions.TextGamma"/>).
    /// </summary>
    private void LerpBy(RenderBuffer target, RenderBuffer result, Source coverage, float opacity, Source? clip, float clipOpacity, float gamma = 0f)
    {
        var region = result.Bounds.Intersect(coverage.Bounds);
        if (clip is not null) region = region.Intersect(clip.Bounds);
        if (region.IsEmpty || opacity <= 0f) return;
        int w = region.Width;
        float scale = opacity * (clip is null ? 1f : clipOpacity);
        Parallel.For(region.Top, region.Bottom, Parallelism, y =>
        {
            float[] buffer = System.Buffers.ArrayPool<float>.Shared.Rent(w * 2);
            try
            {
                var amount = buffer.AsSpan(0, w);
                coverage.FillRow(y, region.Left, region.Right, Span<float>.Empty, amount);
                if (clip is not null)
                {
                    var clipCov = buffer.AsSpan(w, w);
                    clip.FillRow(y, region.Left, region.Right, Span<float>.Empty, clipCov);
                    for (int i = 0; i < w; i++) amount[i] *= clipCov[i];
                }
                var dst = target.Pixels.AsSpan(target.IndexOf(region.Left, y), w * 4);
                var src = result.Pixels.AsSpan(result.IndexOf(region.Left, y), w * 4);
                for (int i = 0; i < w; i++)
                {
                    float f = amount[i] * scale;
                    if (f <= 0f) continue;
                    int t = i * 4;
                    if (gamma <= 0f)
                    {
                        for (int c = 0; c < 4; c++) dst[t + c] += (src[t + c] - dst[t + c]) * f;
                        continue;
                    }
                    float da = dst[t + 3], sa = src[t + 3], oa = da + (sa - da) * f;
                    if (oa <= 0f) { dst[t] = dst[t + 1] = dst[t + 2] = dst[t + 3] = 0f; continue; }
                    float wd = da * (1f - f) / oa, ws = sa * f / oa;
                    for (int c = 0; c < 3; c++)
                    {
                        float d = da > 0f ? Math.Clamp(dst[t + c] / da, 0f, 1f) : 0f, s = sa > 0f ? Math.Clamp(src[t + c] / sa, 0f, 1f) : 0f;
                        dst[t + c] = TextMix.Decode(wd * TextMix.Encode(d, gamma) + ws * TextMix.Encode(s, gamma), gamma) * oa;
                    }
                    dst[t + 3] = oa;
                }
            }
            finally
            {
                System.Buffers.ArrayPool<float>.Shared.Return(buffer);
            }
        });
    }

    /// <summary>What cannot be reproduced exactly about a layer's styles.</summary>
    private static void GroupEffectWarnings(LayerNode node, List<string> warnings)
    {
        if (node is LayerGroup { BlendMode: BlendMode.PassThrough } && EffectRenderer.HasRenderable(node.Effects))
            warnings.Add($"Group \"{node.Name}\" has layer styles and is rendered isolated, as if its mode were Normal.");
        foreach (var e in node.Effects?.Visible ?? [])
        {
            var pattern = e switch
            {
                PatternOverlayEffect o => o.Fill?.Pattern,
                StrokeEffect { FillType: StrokeFillType.Pattern } s => s.PatternFill?.Pattern,
                BevelEffect { UseTexture: true } b => b.Texture?.Pattern,
                _ => null,
            };
            if (pattern is { Resolved: null })
                warnings.Add($"Pattern \"{pattern.Name}\" used on \"{node.Name}\" is not in the file, so it is not drawn.");
        }
    }
}
