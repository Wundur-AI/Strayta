using System.Diagnostics;
using Strayta.Core;
using Strayta.Core.Painting;
using Strayta.Core.Selection;
using Strayta.Editor.Editing;
using Strayta.Rendering;
using Strayta.Rendering.Filters;

namespace Strayta.Editor.ViewModels;

/// <summary>What an open filter works on: a layer's pixels or its mask, and the selection when the filter began.</summary>
public sealed record FilterTarget(LayerNode Owner, bool Mask, SelectionMask? Selection, bool LockTransparency);

// The Filter menu (Gaussian Blur, Motion Blur, Box Blur, Unsharp Mask, Add Noise, High Pass) on the selected layer or its
// targeted mask, limited to the selection.
//
// While a filter dialog is open nothing in the document changes. With Preview on, each render lane swaps its proxy of
// the target layer (or mask) for a filtered copy right after syncing, the way Free Transform and the Gradient preview:
// the preview lane filters its screen-resolution proxy with the radius scaled to match, so a slider answers within a
// frame or two; the full lane filters at full resolution once changes pause and is cancelled by the next one. Results
// are kept per scale, so re-renders (and OK, when the full-resolution result is already there) do not filter again.
public sealed partial class DocumentViewModel
{
    /// <summary>The open filter: its target, the filter the canvas previews (null with Preview off), and computed results.</summary>
    private sealed class FilterSession(FilterTarget target)
    {
        public FilterTarget Target { get; } = target;
        public ImageFilter? Filter { get; set; }
        public FilterResults Results { get; } = new();
    }

    private FilterSession? _filterSession;

    /// <summary>The target of the open filter dialog, or null.</summary>
    public FilterTarget? OpenFilter => _filterSession?.Target;

    /// <summary>Time from OK (or Last Filter) to the filtered pixels being in the document, for the self-test and benchmark.</summary>
    public double LastFilterApplyMs { get; private set; }

    /// <summary>How many times a render lane filtered the preview (a cached result does not count); for the self-test.</summary>
    public int FilterPreviewComputations => _filterPreviewComputations;

    private int _filterPreviewComputations;

    /// <summary>The filter the screen-resolution lane drew most recently, for the latency benchmark.</summary>
    internal ImageFilter? LastPreviewedFilter { get; private set; }

    /// <summary>
    /// Photoshop's prompt when the selected layer is a type, shape, fill or smart-object layer that a filter would have
    /// to rasterize first (null when it is not). Its mask can be filtered without rasterizing.
    /// </summary>
    public string? FilterRasterizePrompt()
    {
        if (EditMask && SelectedLayer?.Node.GetMask() is not null) return null;
        if (SelectedLayer?.Node is not { Visible: true } node || !RasterizeEdit.CanRasterize(node)) return null;
        return node.Tags.Contains("text") ? "This type layer must be rasterized before proceeding. Its text will no longer be editable."
            : node.Tags.Contains("smart-object") ? "This smart object must be rasterized before proceeding. Its contents will no longer be editable (Strayta has no Smart Filters yet)."
            : node.Tags.Contains("shape") ? "This shape layer must be rasterized before proceeding. It will no longer be editable as a shape."
            : "This layer must be rasterized before proceeding. Its fill will no longer be editable.";
    }

    /// <summary>
    /// Starts a filter (<paramref name="name"/> is for messages) on the selected layer's pixels, or on its mask when the
    /// mask is targeted; false, with a notice, when there is nothing it can filter. Nothing is previewed until
    /// <see cref="PreviewFilter"/> is called.
    /// </summary>
    public bool BeginFilter(string name)
    {
        if (_baking || _filterSession is not null) return false;
        if (IsTransforming)
        {
            Notice = "Apply or cancel the transform first.";
            return false;
        }
        LayerNode owner;
        bool mask = EditMask && SelectedLayer?.Node is { } n && n.GetMask() is not null;
        if (mask)
        {
            owner = SelectedLayer!.Node;
            string? problem = owner.Visible ? null : $"Could not complete the {name} command because the target layer is hidden.";
            if (Model.ColorMode is not (ColorMode.Rgb or ColorMode.Grayscale)) problem = $"Filters in {Model.ColorMode} documents are not supported yet.";
            if (problem is not null)
            {
                Notice = problem;
                return false;
            }
            Notice = "";
            PaintBlock = null;
        }
        else if (PaintableLayer() is { } layer)
        {
            owner = layer;
            var area = layer.Pixels is null ? PixelRect.Empty
                : layer.Bounds.Intersect(Model.Bounds).Intersect(GradientPainter.Region(Model.Bounds, Selection));
            if (area.IsEmpty)
            {
                Notice = $"Could not complete the {name} command because the selected area is empty.";
                return false;
            }
        }
        else return false;

        _filterSession = new FilterSession(new FilterTarget(owner, mask, Selection, !mask && owner is PixelLayer { TransparencyLocked: true }));
        return true;
    }

    /// <summary>Shows <paramref name="filter"/> on the canvas, or the unfiltered image for null (Preview off).</summary>
    public void PreviewFilter(ImageFilter? filter)
    {
        if (_filterSession is not { } session || Equals(session.Filter, filter)) return;
        session.Filter = filter;
        RequestRender();
    }

    /// <summary>Cancel: closes the filter without changing anything.</summary>
    public void CancelFilter()
    {
        if (_filterSession is null) return;
        _filterSession = null;
        RequestRender();
    }

    /// <summary>
    /// OK: filters at full resolution in the background (or takes the result the full lane already computed) and records
    /// one undoable edit named after the filter. The preview stays on screen until then, so nothing flickers.
    /// </summary>
    public async Task ApplyFilterAsync(ImageFilter filter)
    {
        if (_filterSession is not { } session) return;
        var clock = Stopwatch.StartNew();
        _baking = true;
        try
        {
            var target = session.Target;
            var scope = new FilterScope(Model.Bounds) { Selection = target.Selection, PreserveTransparency = target.LockTransparency };
            int depth = Model.BitDepth;
            if (target.Mask)
            {
                var mask = target.Owner.GetMask()!;
                var result = await Task.Run(() => session.Results.Get(1, filter, mask.Pixels, mask.Bounds,
                    c => FilterEngine.ApplyToMask(mask, filter, scope, depth, c), CancellationToken.None));
                _filterSession = null;
                if (ReferenceEquals(result, mask)) RequestRender();
                else Apply(new MaskEdit(target.Owner, result, filter.Name));
            }
            else
            {
                var layer = (PixelLayer)target.Owner;
                var (pixels, bounds) = (layer.Pixels, layer.Bounds);
                var result = await Task.Run(() => session.Results.Get(1, filter, pixels, bounds,
                    c => FilterEngine.ApplyToLayer(pixels, bounds, filter, scope, c), CancellationToken.None));
                _filterSession = null;
                if (ReferenceEquals(result.Pixels, pixels)) RequestRender();
                else Apply(new PixelsEdit(layer, result.Pixels, result.Bounds, filter.Name));
            }
            LastFilterApplyMs = clock.Elapsed.TotalMilliseconds;
        }
        catch (Exception ex)
        {
            _filterSession = null;
            Notice = $"Could not apply {filter.Name}: {ex.Message}";
            RequestRender();
        }
        finally
        {
            _baking = false;
        }
    }

    /// <summary>
    /// Called by a render lane on the UI thread right after syncing its proxy document: filters the proxy's copy of the
    /// target (at the proxy's scale) on the render thread before rendering; null when no filter is being previewed.
    /// </summary>
    private Action<CancellationToken>? PrepareFilter(PreviewDocument proxy, bool full)
    {
        if (_filterSession is not { Filter: { } filter } session || proxy.ProxyOf(session.Target.Owner) is not { } node) return null;
        var target = session.Target;
        int factor = proxy.Factor, depth = Model.BitDepth;
        var scaled = factor == 1 ? filter : filter.Scaled(1.0 / factor);
        var scope = new FilterScope(proxy.Proxy.Bounds) { Selection = target.Selection, Factor = factor, PreserveTransparency = target.LockTransparency };
        void Computed()
        {
            Interlocked.Increment(ref _filterPreviewComputations);
        }
        void Drawn()
        {
            if (!full) LastPreviewedFilter = filter;
        }
        // The proxy was just reset by Sync, so reading it here sees the layer as it is.
        if (target.Mask)
        {
            if (node.GetMask() is not { } mask) return null;
            return cancel =>
            {
                node.SetMask(session.Results.Get(factor, filter, mask.Pixels, mask.Bounds, c =>
                {
                    Computed();
                    return FilterEngine.ApplyToMask(mask, scaled, scope, depth, c);
                }, cancel));
                Drawn();
            };
        }
        if (node is not PixelLayer layer) return null;
        var (pixels, bounds) = (layer.Pixels, layer.Bounds);
        return cancel =>
        {
            (layer.Pixels, layer.Bounds) = session.Results.Get(factor, filter, pixels, bounds, c =>
            {
                Computed();
                return FilterEngine.ApplyToLayer(pixels, bounds, scaled, scope, c);
            }, cancel);
            Drawn();
        };
    }

    /// <summary>
    /// The latest filtered result per scale, keyed by the filter and the pixels it was computed from. A computation in
    /// progress is shared: a second caller (OK while the full lane is still filtering) waits for it instead of starting
    /// over, and takes over if the first one is cancelled.
    /// </summary>
    private sealed class FilterResults
    {
        private sealed record Entry(ImageFilter Filter, object? Source, PixelRect Bounds, Task<object> Result);

        private readonly Dictionary<int, Entry> _byFactor = [];

        public T Get<T>(int factor, ImageFilter filter, object? source, PixelRect bounds, Func<CancellationToken, T> compute, CancellationToken cancel)
            where T : notnull
        {
            while (true)
            {
                TaskCompletionSource<object>? mine = null;
                Entry entry;
                lock (_byFactor)
                {
                    if (!_byFactor.TryGetValue(factor, out entry!) || !entry.Filter.Equals(filter) || !ReferenceEquals(entry.Source, source)
                        || entry.Bounds != bounds || entry.Result.IsCanceled || entry.Result.IsFaulted)
                    {
                        mine = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
                        entry = _byFactor[factor] = new Entry(filter, source, bounds, mine.Task);
                    }
                }
                if (mine is not null)
                {
                    try
                    {
                        var value = compute(cancel);
                        mine.SetResult(value);
                        return value;
                    }
                    catch (OperationCanceledException)
                    {
                        mine.SetCanceled(CancellationToken.None);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        mine.SetException(ex);
                        throw;
                    }
                }
                try
                {
                    entry.Result.Wait(cancel);
                    return (T)entry.Result.Result;
                }
                catch (AggregateException) when (entry.Result.IsCanceled)
                {
                    // Whoever was computing it gave up; compute it here instead.
                }
            }
        }
    }

    // ---- Benchmark -------------------------------------------------------------------------------------

    /// <summary>
    /// Times each filter at full resolution (typical and large settings) and the time from a slider change to the canvas
    /// showing the new preview (STRAYTA_FILTERBENCH=new on a generated 4000×3000 document).
    /// </summary>
    public async Task RunFilterBenchmarkAsync(Func<ImageFilter, FilterSessionViewModel> openSession)
    {
        int w = Model.Width, h = Model.Height;
        static double Median(List<double> v) => v.Count == 0 ? double.NaN : v.OrderBy(t => t).ElementAt(v.Count / 2);
        (string Label, ImageFilter Filter)[] cases =
        [
            ("Gaussian Blur 2 px", new GaussianBlurFilter(2)),
            ("Gaussian Blur 20 px", new GaussianBlurFilter(20)),
            ("Gaussian Blur 250 px", new GaussianBlurFilter(250)),
            ("Gaussian Blur 1000 px", new GaussianBlurFilter(1000)),
            ("Box Blur 10 px", new BoxBlurFilter(10)),
            ("Box Blur 500 px", new BoxBlurFilter(500)),
            ("Motion Blur 0° 20 px", new MotionBlurFilter(0, 20)),
            ("Motion Blur 30° 200 px", new MotionBlurFilter(30, 200)),
            ("Motion Blur 70° 2000 px", new MotionBlurFilter(70, 2000)),
            ("Unsharp Mask 100% 1 px", new UnsharpMaskFilter(100, 1, 0)),
            ("Unsharp Mask 150% 50 px", new UnsharpMaskFilter(150, 50, 3)),
            ("Add Noise 12.5% Gaussian", new AddNoiseFilter(12.5, NoiseDistribution.Gaussian, false, 1)),
            ("High Pass 10 px", new HighPassFilter(10)),
            ("High Pass 500 px", new HighPassFilter(500)),
        ];
        foreach (var (label, filter) in cases)
        {
            var times = new List<double>();
            for (int i = 0; i < 3; i++)
            {
                if (!BeginFilter(filter.Name)) return;
                await ApplyFilterAsync(filter);
                times.Add(LastFilterApplyMs);
                Undo();
            }
            Console.WriteLine($"FILTERBENCH {label}: full-resolution apply median {Median(times):F0} ms (first {times[0]:F0}) on {w}×{h}");
        }

        // Slider changes: time from a new value to the screen-resolution preview showing it.
        foreach (var kind in new[] { FilterKind.GaussianBlur, FilterKind.MotionBlur, FilterKind.UnsharpMask, FilterKind.AddNoise })
        {
            using var session = openSession(FilterSessionViewModel.DefaultFilter(kind));
            await Task.Delay(1500); // the first preview, and the full-resolution one after it
            var latencies = new List<double>();
            for (int i = 0; i < 12; i++)
            {
                var clock = Stopwatch.StartNew();
                switch (kind)
                {
                    case FilterKind.GaussianBlur: session.Radius = 3 + i * 7; break;
                    case FilterKind.MotionBlur: session.Distance = 30 + i * 20; break;
                    case FilterKind.UnsharpMask: session.Radius = 2 + i; break;
                    default: session.Amount = 5 + i * 5; break;
                }
                var wanted = session.Filter;
                var shown = new TaskCompletionSource();
                void OnFrame(bool fullFrame)
                {
                    if (!fullFrame && Equals(LastPreviewedFilter, wanted)) shown.TrySetResult();
                }
                FrameDisplayed += OnFrame;
                await Task.WhenAny(shown.Task, Task.Delay(5000));
                FrameDisplayed -= OnFrame;
                latencies.Add(clock.Elapsed.TotalMilliseconds);
                await Task.Delay(i % 3 == 2 ? 700 : 60); // mostly quick steps, sometimes a pause that lets full resolution run
            }
            Console.WriteLine($"FILTERBENCH {session.Title}: slider change to screen preview median {Median(latencies):F0} ms, " +
                              $"worst {latencies.Max():F0} ms (preview 1/{PreviewDocument.FactorForZoom(_viewZoom)} of {w}×{h})");
            session.Cancel();
        }
    }
}
