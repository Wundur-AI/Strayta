using System.Buffers;
using System.Numerics;
using Plane = Strayta.Core.Plane;
using System.Runtime.CompilerServices;
using Strayta.Core;

namespace Strayta.Rendering;

public sealed class RenderOptions
{
    /// <summary>Layers to treat as hidden regardless of their own visibility (e.g. toggled off in a viewer).</summary>
    public IReadOnlySet<LayerNode>? Hidden { get; init; }

    /// <summary>Layers to treat as visible even if hidden in the file.</summary>
    public IReadOnlySet<LayerNode>? Shown { get; init; }

    /// <summary>Checked between layers; a cancelled render throws <see cref="OperationCanceledException"/>.</summary>
    public CancellationToken Cancellation { get; init; }

    /// <summary>A brush or eraser stroke in progress, drawn over its target layer.</summary>
    public StrokeOverlay? ActiveStroke { get; init; }

    /// <summary>
    /// Photoshop's "Blend Text Colors Using Gamma" color setting (on, at 1.45, by default): type layers are mixed with
    /// what is under them in a space of this gamma rather than in the document's own encoding, which makes light type on
    /// a dark ground (and its anti-aliased edges) heavier. Null blends type like any other layer. Not used for 32-bit
    /// documents, which blend linearly.
    /// </summary>
    public float? TextGamma { get; init; } = 1.45f;

    /// <summary>
    /// Draw artboards as artboards (background, clipped to their bounds). Off, they are ordinary groups: exporting a
    /// single layer from an artboard shows just that layer.
    /// </summary>
    public bool DrawArtboards { get; init; } = true;
}

/// <summary>Result of rendering: the image plus notes about content that could not be reproduced.</summary>
/// <remarks>When rendered with a <see cref="RenderCache"/>, <see cref="Image"/> is owned by the cache and is
/// only valid until the next render that uses it.</remarks>
public sealed record RenderResult(RenderBuffer Image, IReadOnlyList<string> Warnings, bool LinearColor)
{
    public byte[] ToRgba8(CancellationToken cancel = default) => Image.ToRgba8(LinearColor, cancel);

    /// <summary>
    /// The image as a straight-alpha <see cref="Raster"/> in the document's own encoding (no sRGB conversion),
    /// suitable for saving. Grayscale documents take the red channel, which equals the others.
    /// </summary>
    public Raster ToRaster(ColorMode mode, int bitDepth)
    {
        int w = Image.Width, h = Image.Height, colorCount = mode == ColorMode.Grayscale ? 1 : 3;
        var planes = Enumerable.Range(0, colorCount + 1).Select(_ => Plane.Create(w, h, bitDepth)).ToArray();
        var px = Image.Pixels;
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x, t = i * 4;
                float a = px[t + 3];
                for (int c = 0; c <= colorCount; c++)
                {
                    float v = c == colorCount ? a : a > 0f ? px[t + c] / a : 0f;
                    var p = planes[c];
                    switch (bitDepth)
                    {
                        case 8: p.Data[i] = RgbaConverter.ToByte(v); break;
                        case 16: p.AsUInt16()[i] = (ushort)Math.Clamp(MathF.Round(v * 65535f), 0f, 65535f); break;
                        default: p.AsSingle()[i] = v; break;
                    }
                }
            }
        });
        return new Raster(mode, planes[..colorCount], planes[colorCount]);
    }
}

/// <summary>
/// Flattens a <see cref="Document"/> the way Photoshop does: bottom-to-top alpha compositing with
/// blend modes, layer masks, clipping groups, isolated groups and pass-through groups.
/// Colors are blended in the document's own encoding (gamma-encoded for 8/16-bit, linear for 32-bit).
/// </summary>
public sealed partial class Compositor
{
    private readonly Document _doc;
    private readonly RenderOptions _options;
    private readonly RenderCache? _cache;

    private Compositor(Document doc, RenderOptions options, RenderCache? cache)
    {
        _doc = doc;
        _options = options;
        _cache = cache;
        Parallelism = new ParallelOptions { CancellationToken = options.Cancellation };
    }

    /// <summary>Lets a cancelled render stop within a few rows instead of finishing the current layer.</summary>
    private ParallelOptions Parallelism { get; }

    /// <summary>A base layer plus the layers clipped to it, rendered as one unit.</summary>
    private readonly record struct Unit(LayerNode Base, IReadOnlyList<LayerNode> Clipped);

    /// <param name="cache">Optional snapshots for fast re-renders after visibility changes; see <see cref="CpuRenderer"/>.</param>
    public static RenderResult Render(Document doc, RenderOptions? options = null, RenderCache? cache = null)
    {
        var c = new Compositor(doc, options ?? new RenderOptions(), cache);
        var steps = new List<Unit>();
        c.Flatten(doc.Root.Children, steps);
        var canvas = c.RunSteps(steps);
        return new RenderResult(canvas, c.CollectWarnings(), doc.BitDepth == 32);
    }

    /// <summary>The gamma type layers blend with (see <see cref="RenderOptions.TextGamma"/>); 0 for other layers.</summary>
    private float TextGamma(LayerNode node) =>
        node.Tags.Contains("text") && _doc.BitDepth != 32 && _options.TextGamma is > 0f and var g ? g : 0f;

    private bool IsVisible(LayerNode node) =>
        _options.Hidden?.Contains(node) == true ? false :
        _options.Shown?.Contains(node) == true || node.Visible;

    private static List<Unit> Units(IReadOnlyList<LayerNode> children)
    {
        var units = new List<Unit>();
        for (int i = 0; i < children.Count; i++)
        {
            int end = i + 1;
            while (end < children.Count && children[end].Clipped) end++;
            units.Add(new Unit(children[i], children.Skip(i + 1).Take(end - i - 1).ToArray()));
            i = end - 1;
        }
        return units;
    }

    /// <summary>
    /// Splits the root into steps that each draw straight onto the canvas. Opaque, unmasked pass-through
    /// groups draw their children directly, so they are expanded into their children's steps.
    /// </summary>
    private void Flatten(IReadOnlyList<LayerNode> children, List<Unit> steps)
    {
        foreach (var unit in Units(children))
        {
            if (unit.Base is LayerGroup g && unit.Clipped.Count == 0 && IsVisible(g) && IsDirectPassThrough(g))
                Flatten(g.Children, steps);
            else
                steps.Add(unit);
        }
    }

    private static bool IsDirectPassThrough(LayerGroup g) =>
        g.Artboard is null && g.BlendMode == BlendMode.PassThrough && g.Opacity >= 1f && g.Mask is not { Disabled: false } && !EffectRenderer.HasRenderable(g.Effects);

    private RenderBuffer RunSteps(List<Unit> steps)
    {
        var cache = _cache;
        var signatures = steps.Select(s => ((object)s.Base, Signature(s))).ToList();
        long canvasBytes = (long)_doc.Width * _doc.Height * 16;

        var token = _options.Cancellation;
        if (cache is null || cache.MemoryBudgetBytes < canvasBytes)
        {
            var canvas = new RenderBuffer(_doc.Bounds);
            foreach (var s in steps)
            {
                token.ThrowIfCancellationRequested();
                RenderUnit(s, canvas);
            }
            return canvas;
        }

        int firstChanged = 0;
        while (firstChanged < steps.Count && firstChanged < cache.Steps.Count && signatures[firstChanged] == cache.Steps[firstChanged])
            firstChanged++;
        if (firstChanged == steps.Count && steps.Count == cache.Steps.Count && cache.LastResult is { } unchanged)
            return unchanged;

        // Resume from the latest snapshot at or below the first changed step.
        foreach (var stale in cache.Checkpoints.Keys.Where(k => k > firstChanged).ToList())
        {
            cache.Recycle(cache.Checkpoints[stale]);
            cache.Checkpoints.Remove(stale);
        }
        int start = cache.Checkpoints.Count > 0 ? cache.Checkpoints.Keys.Max() : 0;
        var target = cache.LastResult is { } reuse && reuse.Bounds == _doc.Bounds ? reuse : new RenderBuffer(_doc.Bounds);
        if (start > 0) cache.Checkpoints[start].CopyTo(target);
        else Array.Clear(target.Pixels);

        // Always snapshot right below the step that just changed: it is the one most likely to change again
        // (every frame of a drag restarts there). Spread further snapshots across the stack only on a cold
        // render; refreshing them on every incremental render would cost more than it saves.
        int slots = (int)Math.Clamp(cache.MemoryBudgetBytes / canvasBytes - 1, 1, Math.Max(1, steps.Count));
        var wanted = new HashSet<int> { firstChanged };
        if (start == 0 && cache.Checkpoints.Count == 0)
            for (int k = 1; k < slots; k++) wanted.Add(steps.Count * k / slots);

        for (int i = start; i < steps.Count; i++)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                if (i > 0 && wanted.Contains(i) && !cache.Checkpoints.ContainsKey(i))
                {
                    if (cache.Checkpoints.Count >= slots)
                    {
                        int evict = cache.Checkpoints.Keys.Where(k => k != firstChanged).DefaultIfEmpty(firstChanged).Max();
                        cache.Recycle(cache.Checkpoints[evict]);
                        cache.Checkpoints.Remove(evict);
                    }
                    var snapshot = cache.Rent(target.Bounds);
                    target.CopyTo(snapshot);
                    cache.Checkpoints[i] = snapshot;
                }
                RenderUnit(steps[i], target);
            }
            catch (OperationCanceledException)
            {
                // Steps before i are complete and their snapshots valid; step i may be half drawn, so the next
                // render must redo it (it restores from a snapshot, never from this partial image).
                cache.Steps = signatures.Take(i).ToList();
                cache.LastResult = target;
                throw;
            }
        }

        cache.Steps = signatures;
        cache.LastResult = target;
        return target;
    }

    /// <summary>
    /// Changes whenever anything inside the unit that affects rendering changes: visibility, order, opacity,
    /// blend mode, clipping, position or masks. Pixel edits are not detected; callers invalidate the cache.
    /// </summary>
    private int Signature(Unit unit)
    {
        var h = new HashCode();
        foreach (var node in unit.Clipped.Prepend(unit.Base))
        {
            Add(ref h, node);
            if (node is LayerGroup g)
                foreach (var d in g.Descendants()) Add(ref h, d);
        }
        return h.ToHashCode();

        void Add(ref HashCode hash, LayerNode n)
        {
            hash.Add(RuntimeHelpers.GetHashCode(n));
            hash.Add(IsVisible(n));
            hash.Add(n.Opacity);
            hash.Add(n.FillOpacity);
            hash.Add(n.BlendMode);
            hash.Add(n.Clipped);
            hash.Add(RuntimeHelpers.GetHashCode(n.Effects));
            if (_options.ActiveStroke is { } stroke && ReferenceEquals(stroke.Target, n)) hash.Add(stroke.Version);
            switch (n)
            {
                case PixelLayer p:
                    hash.Add(p.Bounds);
                    hash.Add(RuntimeHelpers.GetHashCode(p.Pixels));
                    AddMask(ref hash, p.Mask);
                    break;
                case LayerGroup g:
                    hash.Add(g.Children.Count);
                    hash.Add(g.Artboard);
                    hash.Add(_options.DrawArtboards);
                    AddMask(ref hash, g.Mask);
                    break;
                case AdjustmentLayer a:
                    hash.Add(RuntimeHelpers.GetHashCode(a.Adjustment));
                    AddMask(ref hash, a.Mask);
                    break;
            }
        }

        static void AddMask(ref HashCode hash, LayerMask? m)
        {
            if (m is null) return;
            hash.Add(m.Bounds);
            hash.Add(m.Disabled);
            hash.Add(RuntimeHelpers.GetHashCode(m.Pixels));
        }
    }

    private void RenderChildren(IReadOnlyList<LayerNode> children, RenderBuffer target)
    {
        foreach (var unit in Units(children))
            RenderUnit(unit, target);
    }

    private void RenderUnit(Unit unit, RenderBuffer target)
    {
        var baseNode = unit.Base;
        if (!IsVisible(baseNode)) return; // clipped layers disappear with their base

        switch (baseNode)
        {
            case PixelLayer layer:
                bool styled = EffectRenderer.HasRenderable(layer.Effects);
                // A style's glow or shadow near the canvas edge comes from pixels beyond it too.
                var wanted = styled ? Inflate(_doc.Bounds, EffectRenderer.Influence(layer.Effects)) : _doc.Bounds;
                if (LayerSource.From(layer, _doc, _options.ActiveStroke, wanted) is not { } source) return;
                if (styled)
                    CompositeWithEffects(layer, source, target, clip: null, clipOpacity: 1f);
                else
                    Composite(target, source, layer.BlendMode, layer.Opacity, layer.FillOpacity, clip: null, clipOpacity: 1f, TextGamma(layer));

                // Clipped layers are limited to the base layer's shape and opacity.
                foreach (var node in unit.Clipped.Where(IsVisible))
                {
                    if (node is PixelLayer cl && LayerSource.From(cl, _doc, _options.ActiveStroke) is { } cs)
                        Composite(target, cs, cl.BlendMode, cl.Opacity, cl.FillOpacity, source, layer.Opacity);
                    else if (node is LayerGroup cg)
                        RenderGroup(cg, target, source, layer.Opacity);
                    else if (node is AdjustmentLayer ca)
                        ApplyAdjustment(target, ca, source, layer.Opacity);
                }
                break;

            case AdjustmentLayer adjustment:
                ApplyAdjustment(target, adjustment, clip: null, clipOpacity: 1f);
                break;

            case LayerGroup group when unit.Clipped.Count == 0:
                RenderGroup(group, target, clip: null, clipOpacity: 1f);
                break;

            case LayerGroup group:
            {
                // Layers clipped to a group are clipped to the group's flattened result.
                var bounds = GroupBounds(group).Intersect(target.Bounds);
                if (bounds.IsEmpty) return;
                var isolated = new RenderBuffer(bounds);
                RenderChildren(group.Children, isolated);
                var groupSource = new BufferSource(isolated, group.Mask, MaskStroke(group)) { Bounds = bounds };
                var mode = group.BlendMode == BlendMode.PassThrough ? BlendMode.Normal : group.BlendMode;
                if (EffectRenderer.HasRenderable(group.Effects)) CompositeWithEffects(group, groupSource, target, clip: null, clipOpacity: 1f);
                else Composite(target, groupSource, mode, group.Opacity, 1f, clip: null, clipOpacity: 1f);

                foreach (var node in unit.Clipped.Where(IsVisible))
                {
                    if (node is PixelLayer cl && LayerSource.From(cl, _doc, _options.ActiveStroke) is { } cs)
                        Composite(target, cs, cl.BlendMode, cl.Opacity, cl.FillOpacity, groupSource, group.Opacity);
                    else if (node is LayerGroup cg)
                        RenderGroup(cg, target, groupSource, group.Opacity);
                    else if (node is AdjustmentLayer ca)
                        ApplyAdjustment(target, ca, groupSource, group.Opacity);
                }
                break;
            }
        }
    }

    private void RenderGroup(LayerGroup group, RenderBuffer target, Source? clip, float clipOpacity)
    {
        if (group.Artboard is { } artboard && clip is null && _options.DrawArtboards && !EffectRenderer.HasRenderable(group.Effects))
        {
            RenderArtboard(group, artboard, target); // Compositor.Artboards.cs
            return;
        }
        var bounds = GroupBounds(group).Intersect(target.Bounds);
        if (bounds.IsEmpty) return;
        var mask = group.Mask is { Disabled: false } m ? m : null;
        if (EffectRenderer.HasRenderable(group.Effects))
        {
            RenderGroupWithEffects(group, bounds, target, clip, clipOpacity);
            return;
        }

        if (group.BlendMode == BlendMode.PassThrough && clip is null)
        {
            if (IsDirectPassThrough(group))
            {
                RenderChildren(group.Children, target);
                return;
            }

            // Render onto a copy of the backdrop, then fade between backdrop and result.
            var copy = target.CopyRegion(bounds);
            RenderChildren(group.Children, copy);
            Lerp(target, copy, group.Opacity, mask, MaskStroke(group));
            return;
        }

        var isolated = new RenderBuffer(bounds);
        RenderChildren(group.Children, isolated);
        var mode = group.BlendMode == BlendMode.PassThrough ? BlendMode.Normal : group.BlendMode;
        Composite(target, new BufferSource(isolated, mask, MaskStroke(group)) { Bounds = bounds }, mode, group.Opacity, 1f, clip, clipOpacity);
    }

    /// <summary>
    /// The area a group can change. Adjustment layers reach everything under their mask, which for a
    /// pass-through group extends beyond the group's own pixels.
    /// </summary>
    private PixelRect GroupBounds(LayerGroup group)
    {
        int l = int.MaxValue, t = int.MaxValue, r = int.MinValue, b = int.MinValue;
        foreach (var node in group.Descendants())
        {
            var rect = node switch
            {
                PixelLayer p => Inflate(LayerSource.VisibleBounds(p, _doc.Bounds, _options.ActiveStroke), EffectRenderer.Reach(p.Effects)),
                LayerGroup g when EffectRenderer.HasRenderable(g.Effects) => Inflate(GroupBounds(g), EffectRenderer.Reach(g.Effects)),
                AdjustmentLayer a => AdjustmentReach(a, _doc.Bounds),
                _ => PixelRect.Empty,
            };
            if (rect.IsEmpty) continue;
            l = Math.Min(l, rect.Left);
            t = Math.Min(t, rect.Top);
            r = Math.Max(r, rect.Right);
            b = Math.Max(b, rect.Bottom);
        }
        return l == int.MaxValue ? PixelRect.Empty : new PixelRect(l, t, r, b);
    }

    private static PixelRect Inflate(PixelRect r, int by) =>
        by == 0 || r.IsEmpty ? r : new PixelRect(r.Left - by, r.Top - by, r.Right + by, r.Bottom + by);

    /// <summary>Where an adjustment can have any effect: everywhere, unless its mask is black outside its bounds.</summary>
    private PixelRect AdjustmentReach(AdjustmentLayer a, PixelRect area) =>
        Coverage.MaskReach(a.Mask, area, MaskStroke(a)) is { } reach ? reach : area;

    /// <summary>The brush stroke being painted into <paramref name="node"/>'s layer mask, if any.</summary>
    private StrokeOverlay? MaskStroke(LayerNode node) => StrokeOverlay.ForMaskOf(_options.ActiveStroke, node);

    /// <summary>
    /// Applies an adjustment to the pixels already in <paramref name="target"/>:
    /// color = lerp(C, B(C, adjust(C)), opacity × fill × mask × clip). Alpha is unchanged.
    /// </summary>
    private void ApplyAdjustment(RenderBuffer target, AdjustmentLayer layer, Source? clip, float clipOpacity)
    {
        if (ColorTransform.Create(layer.Adjustment) is not { } transform) return;
        var region = AdjustmentReach(layer, target.Bounds);
        if (clip is not null) region = region.Intersect(clip.Bounds);
        float opacity = layer.Opacity * layer.FillOpacity * (clip is null ? 1f : clipOpacity);
        if (region.IsEmpty || opacity <= 0f) return;

        var mask = layer.Mask is { Disabled: false } m ? m : null;
        var maskStroke = MaskStroke(layer);
        var mode = layer.BlendMode;
        bool separable = BlendFunctions.IsSeparable(mode);
        int w = region.Width;
        var px = target.Pixels;

        Parallel.For(region.Top, region.Bottom, Parallelism, y =>
        {
            float[] buffer = ArrayPool<float>.Shared.Rent(w * 8);
            try
            {
                var original = buffer.AsSpan(0, w * 3);
                var adjusted = buffer.AsSpan(w * 3, w * 3);
                var amount = buffer.AsSpan(w * 6, w);
                var clipCov = buffer.AsSpan(w * 7, w);

                int t0 = target.IndexOf(region.Left, y);
                for (int i = 0; i < w; i++)
                {
                    int t = t0 + i * 4;
                    float a = px[t + 3];
                    float inv = a > 0f ? 1f / a : 0f;
                    original[i * 3] = px[t] * inv;
                    original[i * 3 + 1] = px[t + 1] * inv;
                    original[i * 3 + 2] = px[t + 2] * inv;
                }
                original.CopyTo(adjusted);
                transform.ApplyRow(adjusted);

                amount.Fill(opacity);
                Source.ApplyMask(mask, maskStroke, y, region.Left, amount);
                if (clip is not null)
                {
                    clip.FillRow(y, region.Left, region.Right, Span<float>.Empty, clipCov);
                    for (int i = 0; i < w; i++) amount[i] *= clipCov[i];
                }

                for (int i = 0; i < w; i++)
                {
                    int t = t0 + i * 4;
                    float a = px[t + 3], f = amount[i];
                    if (a <= 0f || f <= 0f) continue;
                    float br = original[i * 3], bg = original[i * 3 + 1], bb = original[i * 3 + 2];
                    float sr = adjusted[i * 3], sg = adjusted[i * 3 + 1], sb = adjusted[i * 3 + 2];
                    if (mode is not (BlendMode.Normal or BlendMode.PassThrough))
                    {
                        if (separable)
                            (sr, sg, sb) = (BlendFunctions.Separable(mode, br, sr), BlendFunctions.Separable(mode, bg, sg), BlendFunctions.Separable(mode, bb, sb));
                        else
                            (sr, sg, sb) = BlendFunctions.NonSeparable(mode, br, bg, bb, sr, sg, sb);
                    }
                    px[t] = (br + (sr - br) * f) * a;
                    px[t + 1] = (bg + (sg - bg) * f) * a;
                    px[t + 2] = (bb + (sb - bb) * f) * a;
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(buffer);
            }
        });
    }

    /// <summary>
    /// Standard compositing: ao = as + ab(1 - as); co = as(1 - ab)Cs + as·ab·B(Cb, Cs) + (1 - as)ab·Cb.
    /// </summary>
    /// <param name="opacity">Fades the blended result (layer opacity).</param>
    /// <param name="fill">Fill opacity; identical to opacity except for the special-eight modes.</param>
    private void Composite(RenderBuffer target, Source source, BlendMode mode, float opacity, float fill, Source? clip, float clipOpacity,
        float textGamma = 0f)
    {
        var region = source.Bounds.Intersect(target.Bounds);
        if (clip is not null) region = region.Intersect(clip.Bounds);
        if (region.IsEmpty || opacity <= 0f || fill <= 0f) return;

        bool fillScaling = fill < 1f && BlendFunctions.UsesFillScaling(mode);
        if (!fillScaling)
        {
            opacity *= fill;
            fill = 1f;
        }

        int w = region.Width;
        bool separable = BlendFunctions.IsSeparable(mode);
        var px = target.Pixels;

        Parallel.For(region.Top, region.Bottom, Parallelism, y =>
        {
            float[] buffer = ArrayPool<float>.Shared.Rent(w * 5);
            try
            {
                var rgb = buffer.AsSpan(0, w * 3);
                var cov = buffer.AsSpan(w * 3, w);
                var clipCov = buffer.AsSpan(w * 4, w);

                source.FillRow(y, region.Left, region.Right, rgb, cov);
                if (clip is not null)
                {
                    clip.FillRow(y, region.Left, region.Right, Span<float>.Empty, clipCov);
                    float f = opacity * clipOpacity;
                    for (int i = 0; i < w; i++) cov[i] *= clipCov[i] * f;
                }
                else if (opacity < 1f)
                {
                    for (int i = 0; i < w; i++) cov[i] *= opacity;
                }

                int t = target.IndexOf(region.Left, y);
                if (mode == BlendMode.Normal && textGamma > 0f)
                    GammaNormalRow(px.AsSpan(t, w * 4), rgb, cov, textGamma);
                else if (mode == BlendMode.Normal)
                    NormalRow(px.AsSpan(t, w * 4), rgb, cov);
                else
                    BlendRow(px.AsSpan(t, w * 4), rgb, cov, mode, separable, region.Left, y, fill);
            }
            finally
            {
                ArrayPool<float>.Shared.Return(buffer);
            }
        });
    }

    /// <summary>Normal mode reduces to premultiplied "over": dst = src·a + dst·(1 - a).</summary>
    private static void NormalRow(Span<float> dst, ReadOnlySpan<float> rgb, ReadOnlySpan<float> cov)
    {
        for (int i = 0; i < cov.Length; i++)
        {
            float sa = cov[i];
            if (sa <= 0f) continue;
            int t = i * 4;
            var d = new Vector4(dst[t], dst[t + 1], dst[t + 2], dst[t + 3]);
            var s = new Vector4(rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2], 1f) * sa;
            var o = s + d * (1f - sa);
            dst[t] = o.X;
            dst[t + 1] = o.Y;
            dst[t + 2] = o.Z;
            dst[t + 3] = o.W;
        }
    }

    /// <summary>
    /// "Over" with the colors mixed in a space of the given gamma (see <see cref="TextMix"/>): the result's alpha is as
    /// usual, its color the mix of source and backdrop there.
    /// </summary>
    private static void GammaNormalRow(Span<float> dst, ReadOnlySpan<float> rgb, ReadOnlySpan<float> cov, float gamma)
    {
        for (int i = 0; i < cov.Length; i++)
        {
            float sa = cov[i];
            if (sa <= 0f) continue;
            int t = i * 4;
            float ba = dst[t + 3], oa = sa + ba * (1f - sa);
            float wb = ba * (1f - sa) / oa, ws = sa / oa;
            for (int c = 0; c < 3; c++)
            {
                float b = ba > 0f ? Math.Clamp(dst[t + c] / ba, 0f, 1f) : 0f;
                float s = Math.Clamp(rgb[i * 3 + c], 0f, 1f);
                float mixed = TextMix.Decode(ws * TextMix.Encode(s, gamma) + wb * TextMix.Encode(b, gamma), gamma);
                dst[t + c] = mixed * oa;
            }
            dst[t + 3] = oa;
        }
    }

    private static void BlendRow(Span<float> dst, ReadOnlySpan<float> rgb, ReadOnlySpan<float> cov,
        BlendMode mode, bool separable, int x0, int y, float fill)
    {
        for (int i = 0; i < cov.Length; i++)
        {
            float sa = cov[i];
            if (mode == BlendMode.Dissolve) sa = DissolveNoise(x0 + i, y) < sa ? 1f : 0f;
            if (sa <= 0f) continue;

            float sr = rgb[i * 3], sg = rgb[i * 3 + 1], sb = rgb[i * 3 + 2];
            int t = i * 4;
            float ba = dst[t + 3];

            float mr = sr, mg = sg, mb = sb;
            if (ba > 0f && mode != BlendMode.Dissolve)
            {
                float inv = 1f / ba;
                float br = dst[t] * inv, bg = dst[t + 1] * inv, bb = dst[t + 2] * inv;
                if (separable)
                {
                    if (fill < 1f)
                    {
                        mr = BlendFunctions.ScaleForFill(mode, sr, fill);
                        mg = BlendFunctions.ScaleForFill(mode, sg, fill);
                        mb = BlendFunctions.ScaleForFill(mode, sb, fill);
                    }
                    mr = BlendFunctions.Separable(mode, br, mr);
                    mg = BlendFunctions.Separable(mode, bg, mg);
                    mb = BlendFunctions.Separable(mode, bb, mb);
                }
                else
                {
                    (mr, mg, mb) = BlendFunctions.NonSeparable(mode, br, bg, bb, sr, sg, sb);
                }
            }

            // Over transparent backdrop, fill behaves like ordinary opacity.
            float k1 = sa * (1f - ba) * fill, k2 = sa * ba, k3 = 1f - sa;
            dst[t] = k1 * sr + k2 * mr + k3 * dst[t];
            dst[t + 1] = k1 * sg + k2 * mg + k3 * dst[t + 1];
            dst[t + 2] = k1 * sb + k2 * mb + k3 * dst[t + 2];
            dst[t + 3] = k1 + k2 + ba * k3;
        }
    }

    /// <summary>target = lerp(target, result, opacity × mask) over result's bounds (both premultiplied).</summary>
    private void Lerp(RenderBuffer target, RenderBuffer result, float opacity, LayerMask? mask, StrokeOverlay? maskStroke)
    {
        var region = result.Bounds;
        int w = region.Width;
        Parallel.For(region.Top, region.Bottom, Parallelism, y =>
        {
            float[] buffer = ArrayPool<float>.Shared.Rent(w);
            try
            {
                var amount = buffer.AsSpan(0, w);
                amount.Fill(opacity);
                Source.ApplyMask(mask, maskStroke, y, region.Left, amount);
                var dst = target.Pixels.AsSpan(target.IndexOf(region.Left, y), w * 4);
                var src = result.Pixels.AsSpan(result.IndexOf(region.Left, y), w * 4);
                for (int i = 0; i < w; i++)
                {
                    float f = amount[i];
                    if (f <= 0f) continue;
                    int t = i * 4;
                    var d = new Vector4(dst[t], dst[t + 1], dst[t + 2], dst[t + 3]);
                    var s = new Vector4(src[t], src[t + 1], src[t + 2], src[t + 3]);
                    var o = d + (s - d) * f;
                    dst[t] = o.X; dst[t + 1] = o.Y; dst[t + 2] = o.Z; dst[t + 3] = o.W;
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(buffer);
            }
        });
    }

    private static float DissolveNoise(int x, int y)
    {
        uint h = (uint)x * 374761393u + (uint)y * 668265263u;
        h = (h ^ (h >> 13)) * 1274126177u;
        return (h ^ (h >> 16)) / (float)uint.MaxValue;
    }

    private List<string> CollectWarnings()
    {
        var warnings = new List<string>();
        if (_doc.ColorMode is not (ColorMode.Rgb or ColorMode.Grayscale))
            warnings.Add($"{_doc.ColorMode} documents are blended in RGB; Photoshop blends in {_doc.ColorMode}.");

        void Walk(IReadOnlyList<LayerNode> children)
        {
            foreach (var unit in Units(children))
            {
                if (!IsVisible(unit.Base)) continue;
                foreach (var node in unit.Clipped.Where(IsVisible).Prepend(unit.Base))
                {
                    Note(node, warnings);
                    if (node is LayerGroup g) Walk(g.Children);
                }
                if (unit.Base is LayerGroup bg && unit.Clipped.Count > 0 && bg.BlendMode == BlendMode.PassThrough)
                    warnings.Add($"Layers clipped to pass-through group \"{bg.Name}\" are rendered as if the group were isolated.");
            }
        }

        Walk(_doc.Root.Children);
        return warnings;
    }

    private static void Note(LayerNode node, List<string> warnings)
    {
        if (node is AdjustmentLayer adj)
        {
            var transform = ColorTransform.Create(adj.Adjustment);
            if (transform is null)
                warnings.Add($"{adj.Kind} adjustment \"{node.Name}\" is not applied yet.");
            else if (transform.Approximate || adj.Adjustment is HueSaturationAdjustment { HasColorRangeEdits: true })
                warnings.Add($"{adj.Kind} adjustment \"{node.Name}\" is approximated.");
        }
        foreach (var fx in node.Effects?.Visible.OfType<UnsupportedEffect>() ?? [])
            warnings.Add($"{fx.Name} on \"{node.Name}\" is not rendered yet.");
        GroupEffectWarnings(node, warnings);
        // A fill layer whose pixels carry Photoshop's own rasterization of its vector mask renders exactly; one without
        // (a shape that has been resampled, or a file without the rasterized mask) shows its pixels' edges instead.
        if (node.Tags.Contains("vector-mask") && node.Tags.Contains("fill") && node is not PixelLayer { Mask.AppliedToPixels: true })
            warnings.Add($"Vector mask edges on \"{node.Name}\" are approximated.");
    }
}

/// <summary>
/// Photoshop's "Blend Text Colors Using Gamma": type is mixed with what is under it in a space of the given gamma. The
/// document's colors (taken as sRGB) are linearized and re-encoded with that gamma, mixed, and converted back.
/// </summary>
internal static class TextMix
{
    public static float Encode(float v, float gamma) => MathF.Pow(SrgbToLinear(Math.Clamp(v, 0f, 1f)), 1f / gamma);

    public static float Decode(float w, float gamma) => RgbaConverter.LinearToSrgb(MathF.Pow(MathF.Max(w, 0f), gamma));

    private static float SrgbToLinear(float v) => v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
}
