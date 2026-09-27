using Strayta.Core;

namespace Strayta.Rendering.Transforms;

/// <summary>
/// Shows layers under a transform in progress (Free Transform) without touching the document: after a
/// <see cref="PreviewDocument"/> is synced, their proxies' pixels and masks are swapped for transformed copies.
/// Work happens at the preview's resolution, results are reused while the transform stays the same (so the
/// render cache keeps its snapshots), and sources are converted for resampling only once per session.
/// </summary>
public sealed class TransformPreview(IReadOnlyCollection<LayerNode> targets)
{
    private sealed record Output(object Input, PixelRect InputBounds, Affine Matrix, ResampleFilter Filter, object? Result, PixelRect Bounds);

    private readonly Dictionary<Raster, ResampleSource> _sources = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(LayerNode, bool Mask), Output> _outputs = [];

    /// <summary>The transform in document space. Set on the UI thread; each <see cref="Prepare"/> captures it.</summary>
    public Affine Matrix { get; set; } = Affine.Identity;

    /// <summary>
    /// Call on the UI thread right after <see cref="PreviewDocument.Sync"/>. Returns the work that transforms
    /// the proxies; run it on the rendering thread before rendering the proxy document.
    /// </summary>
    public Action<CancellationToken> Prepare(PreviewDocument preview, ResampleFilter filter)
    {
        var matrix = Matrix.Rescaled(preview.Factor);
        // Pixels far outside the canvas cannot show; clipping keeps big enlargements cheap.
        var clip = preview.Proxy.Bounds;
        var proxies = targets.Select(preview.ProxyOf).OfType<LayerNode>().ToList();
        return cancel =>
        {
            foreach (var p in proxies)
            {
                cancel.ThrowIfCancellationRequested();
                switch (p)
                {
                    case PixelLayer layer:
                        (layer.Pixels, layer.Bounds) = TransformPixels(layer, matrix, filter, clip, cancel);
                        layer.Mask = TransformMask(layer, layer.Mask, matrix, filter, cancel);
                        break;
                    case AdjustmentLayer a:
                        a.Mask = TransformMask(a, a.Mask, matrix, filter, cancel);
                        break;
                    case LayerGroup g:
                        g.Mask = TransformMask(g, g.Mask, matrix, filter, cancel);
                        break;
                }
            }
        };
    }

    private (Raster?, PixelRect) TransformPixels(PixelLayer layer, Affine m, ResampleFilter filter, PixelRect clip, CancellationToken cancel)
    {
        if (layer.Pixels is not { } raster || layer.Bounds.IsEmpty) return (null, PixelRect.Empty);
        if (Cached((layer, false), raster, layer.Bounds, m, filter) is { } hit) return ((Raster?)hit.Result, hit.Bounds);

        (Raster? Pixels, PixelRect Bounds) result = m.IsIntegerTranslation(out _, out _)
            ? Resampler.TransformRaster(raster, layer.Bounds, m, filter, clip, cancel)
            : Resampler.TransformRaster(SourceFor(raster), layer.Bounds, m, filter, clip, cancel);
        Store((layer, false), new Output(raster, layer.Bounds, m, filter, result.Pixels, result.Pixels is null ? PixelRect.Empty : result.Bounds));
        return result.Pixels is null ? (null, PixelRect.Empty) : result;
    }

    private LayerMask? TransformMask(LayerNode owner, LayerMask? mask, Affine m, ResampleFilter filter, CancellationToken cancel)
    {
        if (mask is null) return null;
        // Proxy masks are rebuilt on every sync around cached pixels, so key on the pixels and bounds.
        object input = (object?)mask.Pixels ?? mask;
        if (Cached((owner, true), input, mask.Bounds, m, filter) is { Result: LayerMask hit } && hit.Pixels == mask.Pixels
            && hit.DefaultColor == mask.DefaultColor && hit.Disabled == mask.Disabled)
            return hit;
        // Masks are not clipped: outside the canvas they still decide what the default color covers.
        var result = Resampler.TransformMask(mask, m, filter, clip: null, cancel);
        Store((owner, true), new Output(input, mask.Bounds, m, filter, result, result?.Bounds ?? PixelRect.Empty));
        return result;
    }

    private Output? Cached((LayerNode, bool) key, object input, PixelRect bounds, Affine m, ResampleFilter filter)
    {
        lock (_outputs)
            return _outputs.TryGetValue(key, out var o) && ReferenceEquals(o.Input, input) && o.InputBounds == bounds && o.Matrix == m && o.Filter == filter
                ? o : null;
    }

    private void Store((LayerNode, bool) key, Output output)
    {
        lock (_outputs) _outputs[key] = output;
    }

    private ResampleSource SourceFor(Raster raster)
    {
        lock (_sources)
        {
            if (!_sources.TryGetValue(raster, out var source)) _sources[raster] = source = ResampleSource.FromRaster(raster);
            return source;
        }
    }
}
