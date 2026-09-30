using Strayta.Core;
using Strayta.Rendering;
using Strayta.Rendering.Transforms;

namespace Strayta.Editor.Editing;

/// <summary>
/// A snapshot of a non-affine transform in progress, in document space: a projective map (distort, perspective), or a
/// general map (warp, puppet warp) from original document positions to transformed ones. Smart objects being warped
/// may instead be drawn from their content (<see cref="Contents"/>), so the preview matches what the committed
/// warp draws. Snapshots are immutable; <see cref="Version"/> tells cached results apart.
/// </summary>
internal sealed record Deform(int Version, Projective? Perspective, Func<double, double, (double X, double Y)>? Map,
    IReadOnlyDictionary<LayerNode, DeformContent>? Contents = null, Func<ResampleSource, PixelRect, int, TriangleMap?>? Triangles = null);

/// <summary>A smart object drawn from its content: content pixels to document positions.</summary>
internal sealed record DeformContent(Raster Content, Func<double, double, (double X, double Y)> Map);

/// <summary>
/// Shows layers under a distort, perspective, warp or puppet warp in progress without touching the document, like
/// <see cref="TransformPreview"/> does for affine transforms: after a <see cref="PreviewDocument"/> is synced, the target
/// proxies' pixels and masks are swapped for mapped copies, at the preview's resolution (bilinear while dragging,
/// bicubic once idle). Sources are converted for resampling once per session and results are reused while the
/// deform stays the same.
/// </summary>
internal sealed class DeformPreview(IReadOnlyCollection<LayerNode> targets)
{
    private sealed record Output(object Input, PixelRect InputBounds, int Version, int Factor, ResampleFilter Filter, object? Result, PixelRect Bounds);

    private readonly Dictionary<object, ResampleSource> _sources = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(LayerNode, bool Mask), Output> _outputs = [];
    private readonly Dictionary<(Raster, int), Raster> _contentProxies = [];

    /// <summary>The deform to show. Set on the UI thread; each <see cref="Prepare"/> captures it.</summary>
    public Deform? Current { get; set; }

    public Action<CancellationToken>? Prepare(PreviewDocument preview, ResampleFilter filter)
    {
        if (Current is not { } deform) return null;
        int f = preview.Factor;
        // At full resolution keep what a commit keeps (a canvas-sized margin), so the commit can take the result as it is.
        var canvas = preview.Proxy.Bounds;
        var clip = f == 1 ? CommitClip(canvas) : canvas;
        var proxies = targets.Select(t => (Target: t, Proxy: preview.ProxyOf(t))).Where(p => p.Proxy is not null).ToList();
        return cancel =>
        {
            foreach (var (target, p) in proxies)
            {
                cancel.ThrowIfCancellationRequested();
                switch (p)
                {
                    case PixelLayer layer:
                        (layer.Pixels, layer.Bounds) = deform.Contents?.GetValueOrDefault(target) is { } content
                            ? FromContent(target, content, deform, f, filter, clip, cancel)
                            : Pixels(target, layer, deform, f, filter, clip, cancel);
                        layer.Mask = Mask(target, layer.Mask, deform, f, filter, cancel);
                        break;
                    case AdjustmentLayer a:
                        a.Mask = Mask(target, a.Mask, deform, f, filter, cancel);
                        break;
                    case LayerGroup g:
                        g.Mask = Mask(target, g.Mask, deform, f, filter, cancel);
                        break;
                }
            }
        };
    }

    /// <summary>What committed transforms keep of a layer: the canvas and a canvas-sized margin all round.</summary>
    public static PixelRect CommitClip(PixelRect canvas) =>
        new(canvas.Left - canvas.Width, canvas.Top - canvas.Height, canvas.Right + canvas.Width, canvas.Bottom + canvas.Height);

    /// <summary>
    /// The full-resolution bicubic result the preview already computed for <paramref name="node"/>'s pixels
    /// <paramref name="input"/> under deform <paramref name="version"/>, or null: a commit right after the preview settled
    /// takes it instead of computing it again.
    /// </summary>
    public (Raster? Pixels, PixelRect Bounds)? FullResult(LayerNode node, Raster input, PixelRect bounds, int version) =>
        Cached((node, false), input, bounds, version, 1, ResampleFilter.Bicubic) is { } hit ? ((Raster?)hit.Result, hit.Bounds) : null;

    /// <summary>The map in a preview's space (scaled down by <paramref name="f"/>).</summary>
    private static Func<double, double, (double X, double Y)> Scaled(Func<double, double, (double X, double Y)> map, int f) =>
        f == 1 ? map : (x, y) =>
        {
            var (px, py) = map(x * f, y * f);
            return (px / f, py / f);
        };

    private (Raster?, PixelRect) Pixels(LayerNode key, PixelLayer layer, Deform d, int f, ResampleFilter filter, PixelRect clip, CancellationToken cancel)
    {
        if (layer.Pixels is not { } raster || layer.Bounds.IsEmpty) return (null, PixelRect.Empty);
        if (Cached((key, false), raster, layer.Bounds, d.Version, f, filter) is { } hit) return ((Raster?)hit.Result, hit.Bounds);
        var source = SourceFor(raster, () => ResampleSource.FromRaster(raster));
        var b = layer.Bounds;
        (Raster? Pixels, PixelRect Bounds) result;
        if (d.Triangles is { } triangles)
            result = triangles(source, b, f) is { } mesh ? MeshResampler.TransformRaster(source, mesh, filter, clip, cancel) : (null, PixelRect.Empty);
        else if (d.Map is { } map)
        {
            var m = Scaled(map, f);
            result = MeshResampler.TransformRaster(source, (u, v) => m(b.Left + u, b.Top + v), filter, clip, cancel);
        }
        else if (d.Perspective is { } p)
        {
            try
            {
                result = ProjectiveResampler.TransformRaster(source, b, p.Rescaled(f), filter, clip, cancel);
            }
            catch (ArgumentException)
            {
                result = (null, PixelRect.Empty); // past the horizon
            }
        }
        else result = (raster, b);
        var bounds = result.Pixels is null ? PixelRect.Empty : result.Bounds;
        Store((key, false), new Output(raster, layer.Bounds, d.Version, f, filter, result.Pixels, bounds));
        return (result.Pixels, bounds);
    }

    private (Raster?, PixelRect) FromContent(LayerNode key, DeformContent content, Deform d, int f, ResampleFilter filter, PixelRect clip, CancellationToken cancel)
    {
        var small = ContentProxy(content.Content, f);
        if (Cached((key, false), small, PixelRect.FromSize(small.Width, small.Height), d.Version, f, filter) is { } hit) return ((Raster?)hit.Result, hit.Bounds);
        var source = SourceFor(small, () => ResampleSource.FromRaster(small));
        double kx = (double)content.Content.Width / small.Width, ky = (double)content.Content.Height / small.Height;
        var m = Scaled(content.Map, f);
        var result = MeshResampler.TransformRaster(source, (u, v) => m(u * kx, v * ky), filter, clip, cancel);
        var bounds = result.Pixels is null ? PixelRect.Empty : result.Bounds;
        Store((key, false), new Output(small, PixelRect.FromSize(small.Width, small.Height), d.Version, f, filter, result.Pixels, bounds));
        return (result.Pixels, bounds);
    }

    /// <summary>Smart object content reduced for a preview at <paramref name="factor"/> (kept per session).</summary>
    private Raster ContentProxy(Raster content, int factor)
    {
        if (factor == 1) return content;
        lock (_contentProxies)
        {
            if (_contentProxies.TryGetValue((content, factor), out var hit)) return hit;
            var (pixels, _) = Resampler.TransformRaster(content, PixelRect.FromSize(content.Width, content.Height), Affine.Scale(1.0 / factor, 1.0 / factor),
                ResampleFilter.Bilinear);
            var small = pixels ?? content;
            _contentProxies[(content, factor)] = small;
            return small;
        }
    }

    private LayerMask? Mask(LayerNode owner, LayerMask? mask, Deform d, int f, ResampleFilter filter, CancellationToken cancel)
    {
        if (mask is null || mask.PositionRelativeToLayer) return mask;
        object input = (object?)mask.Pixels ?? mask;
        if (Cached((owner, true), input, mask.Bounds, d.Version, f, filter) is { Result: LayerMask hit } && hit.Pixels == mask.Pixels
            && hit.DefaultColor == mask.DefaultColor && hit.Disabled == mask.Disabled)
            return hit;
        LayerMask? result = mask;
        if (mask.Pixels is { } plane)
        {
            if (d.Map is { } map) result = MeshResampler.TransformMask(mask, Scaled(map, f), filter, cancel);
            else if (d.Perspective is { } p)
            {
                try
                {
                    result = ProjectiveResampler.TransformMask(mask, p.Rescaled(f), filter, null, cancel);
                }
                catch (ArgumentException)
                {
                    result = mask;
                }
            }
            else if (d.Triangles is { } triangles && triangles(SourceFor(plane, () => ResampleSource.FromPlane(plane)), mask.Bounds, f) is { } mesh)
                result = MeshResampler.TransformMask(mask, SourceFor(plane, () => ResampleSource.FromPlane(plane)), mesh, filter, cancel);
        }
        Store((owner, true), new Output(input, mask.Bounds, d.Version, f, filter, result, result?.Bounds ?? PixelRect.Empty));
        return result;
    }

    private Output? Cached((LayerNode, bool) key, object input, PixelRect bounds, int version, int factor, ResampleFilter filter)
    {
        lock (_outputs)
            return _outputs.TryGetValue(key, out var o) && ReferenceEquals(o.Input, input) && o.InputBounds == bounds && o.Version == version
                   && o.Factor == factor && o.Filter == filter
                ? o : null;
    }

    private void Store((LayerNode, bool) key, Output output)
    {
        lock (_outputs) _outputs[key] = output;
    }

    private ResampleSource SourceFor(object key, Func<ResampleSource> make)
    {
        lock (_sources)
        {
            if (!_sources.TryGetValue(key, out var source)) _sources[key] = source = make();
            return source;
        }
    }
}
