using System.Diagnostics;
using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using Strayta.Core;
using Strayta.Core.Selection;

namespace Strayta.Editor.ViewModels;

// Magic Wand and Quick Selection: the pixels they sample, the wand click, the Quick Selection drag, and a benchmark.
public sealed partial class DocumentViewModel
{
    /// <summary>The model version the last full-resolution render (<c>_lastRender</c>) shows.</summary>
    private int _lastRenderVersion = -1;

    // Sample images are built once and reused while the layer or the document stays the same, so only the first
    // click after an edit pays for converting pixels (tens of milliseconds on large documents).
    private ((PixelLayer Layer, Raster? Pixels, PixelRect Bounds) Key, SampleImage Image)? _layerSample;
    private (byte[] Render, SampleImage Image)? _compositeSample;
    private (SampleImage Source, QuickSelectionImage Image)? _working;

    /// <summary>The live outline of a Quick Selection drag (image coordinates), or null when none is in progress.</summary>
    [ObservableProperty] public partial IReadOnlyList<Vector2[]>? QuickSelectionOutline { get; private set; }

    /// <summary>
    /// The pixels the wand and Quick Selection look at: the selected layer's own pixels, or with Sample All Layers
    /// (or when the selection is a group or adjustment layer, which have no pixels of their own) the flattened
    /// document at full resolution.
    /// </summary>
    private async Task<SampleImage?> SampleImageAsync(bool sampleAll)
    {
        int w = Model.Width, h = Model.Height;
        var palette = Model.Palette;
        if (!sampleAll && SelectedLayer?.Node is PixelLayer layer)
        {
            var key = (layer, layer.Pixels, layer.Bounds);
            if (_layerSample is { } cached && cached.Key.Equals(key)) return cached.Image;
            var image = await Task.Run(() => SampleImage.FromPixels(key.Pixels, key.Bounds, w, h, palette));
            _layerSample = (key, image);
            return image;
        }

        if (await CompositeAsync() is not { } render) return null;
        if (_compositeSample is { } c && ReferenceEquals(c.Render, render)) return c.Image;
        var flat = await Task.Run(() => SampleImage.FromStraightRgba(render, w, h));
        _compositeSample = (render, flat);
        return flat;
    }

    /// <summary>The full-resolution render of the current document, rendering it now if the one on screen is out of date.</summary>
    private async Task<byte[]?> CompositeAsync()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (_lastRender is { } render && _lastRenderVersion == _modelVersion) return render;
            ScheduleFullRender(TimeSpan.Zero);
            await _fullTask;
        }
        return _lastRender; // still editing: an image a moment old beats none
    }

    // ---- Magic Wand -----------------------------------------------------------------------------------

    /// <summary>How long the last wand click took from click to new selection, for the benchmark.</summary>
    public double LastWandMs { get; private set; }

    /// <summary>A Magic Wand click at image pixel (<paramref name="x"/>, <paramref name="y"/>), combined with the selection by <paramref name="mode"/>.</summary>
    public async Task MagicWandAsync(int x, int y, SelectionMode mode)
    {
        if (x < 0 || y < 0 || x >= Model.Width || y >= Model.Height) return;
        var clock = Stopwatch.StartNew();
        var options = Editor.CurrentWandOptions;
        var current = Selection;
        int request = ++_selectionRequest;
        if (await SampleImageAsync(Editor.WandSampleAllLayers) is not { } sample) return;
        var next = await Task.Run(() => SelectionMask.Combine(current, MagicWand.Select(sample, x, y, options), mode));
        // Dropped if another click finished first or the selection changed meanwhile (as for marquee drags).
        if (request != _selectionRequest || !ReferenceEquals(current, Selection)) return;
        SetSelection(next, "Magic Wand");
        LastWandMs = clock.Elapsed.TotalMilliseconds;
    }

    // ---- Quick Selection ------------------------------------------------------------------------------

    /// <summary>
    /// One drag. Pointer positions queue up on the UI thread; a pump brushes them into the stroke on a background
    /// thread, all that arrived meanwhile at once, and publishes the new outline, so the drag never waits for the
    /// computation and the outline is as fresh as the machine allows.
    /// </summary>
    private sealed class QuickSelectDrag
    {
        public required Task<QuickSelectionStroke?> Stroke { get; init; }
        public required SelectionMask? Before { get; init; }
        public readonly List<Vector2> Pending = [];
        public Vector2 Last;
        public bool Pumping;
        public Task Pump = Task.CompletedTask;
        public int Frames;
        public readonly List<double> UpdateMs = [];
    }

    private QuickSelectDrag? _quickDrag;
    private bool _quickFinishing;

    /// <summary>Timings of the last finished drag: outline updates shown, per-update compute times, and the mouse-up refinement.</summary>
    public (int Frames, IReadOnlyList<double> UpdateMs, double FinishMs) LastQuickSelectStats { get; private set; }

    public bool BeginQuickSelection(float x, float y, SelectionMode mode)
    {
        if (_quickDrag is not null || _quickFinishing || IsTransforming) return false;
        bool sampleAll = Editor.QuickSelectSampleAllLayers;
        float size = (float)Editor.QuickSelectSize;
        var before = Selection;
        var drag = new QuickSelectDrag { Stroke = StartStrokeAsync(), Before = before, Last = new Vector2(x, y) };
        drag.Pending.Add(drag.Last);
        _quickDrag = drag;
        Notice = "";
        drag.Pump = PumpAsync(drag);
        return true;

        async Task<QuickSelectionStroke?> StartStrokeAsync()
        {
            if (await SampleImageAsync(sampleAll) is not { } sample) return null;
            var working = _working is { } w && ReferenceEquals(w.Source, sample) ? w.Image : null;
            if (working is null)
            {
                working = await Task.Run(() => QuickSelectionImage.Build(sample));
                _working = (sample, working);
            }
            return await Task.Run(() => new QuickSelectionStroke(working, size, before, mode));
        }
    }

    public void ContinueQuickSelection(float x, float y)
    {
        if (_quickDrag is not { } drag) return;
        drag.Pending.Add(new Vector2(x, y));
        if (!drag.Pumping) drag.Pump = PumpAsync(drag);
    }

    private async Task PumpAsync(QuickSelectDrag drag)
    {
        drag.Pumping = true;
        try
        {
            if (await drag.Stroke is not { } stroke) return;
            while (drag.Pending.Count > 0)
            {
                var points = drag.Pending.ToArray();
                drag.Pending.Clear();
                var from = drag.Last;
                drag.Last = points[^1];
                var (loops, ms) = await Task.Run(() =>
                {
                    var clock = Stopwatch.StartNew();
                    bool grew = false;
                    var a = from;
                    foreach (var b in points)
                    {
                        grew |= stroke.AddSegment(a, b);
                        a = b;
                    }
                    return (grew ? stroke.PreviewOutline() : null, clock.Elapsed.TotalMilliseconds);
                });
                if (loops is null || !ReferenceEquals(drag, _quickDrag)) continue;
                QuickSelectionOutline = loops;
                drag.Frames++;
                drag.UpdateMs.Add(ms);
            }
        }
        finally
        {
            drag.Pumping = false;
        }
    }

    /// <summary>Mouse-up: refines the region at full resolution and applies it as one undoable step.</summary>
    public async Task EndQuickSelectionAsync()
    {
        if (_quickDrag is not { } drag) return;
        _quickFinishing = true;
        try
        {
            if (!drag.Pumping && drag.Pending.Count > 0) drag.Pump = PumpAsync(drag);
            await drag.Pump;
            var stroke = await drag.Stroke;
            _quickDrag = null;
            if (stroke is null)
            {
                Notice = "Nothing to sample yet. Wait for the document to finish rendering.";
                QuickSelectionOutline = null;
                return;
            }
            if (stroke.IsEmpty)
            {
                QuickSelectionOutline = null;
                return;
            }
            bool autoEnhance = Editor.QuickSelectAutoEnhance;
            var clock = Stopwatch.StartNew();
            var next = await Task.Run(() => stroke.Finish(autoEnhance));
            LastQuickSelectStats = (drag.Frames, drag.UpdateMs, clock.Elapsed.TotalMilliseconds);
            if (ReferenceEquals(drag.Before, Selection)) SetSelection(next, "Quick Selection");
            QuickSelectionOutline = null; // the canvas keeps showing it until the new selection's outline is ready
        }
        finally
        {
            _quickFinishing = false;
        }
    }

    // ---- Benchmark -------------------------------------------------------------------------------------

    /// <summary>
    /// Magic Wand clicks (flattened and current layer, cold and warm, contiguous and not) and a two-second Quick
    /// Selection drag with 120 Hz input, reporting click times, live outline updates per second and the mouse-up
    /// refinement (STRAYTA_WANDBENCH=1 on the first opened file, =new on a generated 4000×3000 document).
    /// </summary>
    public async Task RunWandBenchmarkAsync(Vector2? dragFrom = null, Vector2? dragTo = null)
    {
        int w = Model.Width, h = Model.Height;
        var wand = (Editor.WandSampleAllLayers, Editor.WandContiguous, Editor.QuickSelectSampleAllLayers, Editor.QuickSelectSize);
        _layerSample = null;
        _compositeSample = null;
        _working = null;

        async Task<double> Click(int x, int y)
        {
            Deselect();
            await MagicWandAsync(x, y, SelectionMode.Replace);
            return LastWandMs;
        }
        static double Median(List<double> v) => v.Count == 0 ? double.NaN : v.OrderBy(t => t).ElementAt(v.Count / 2);
        async Task<(double Cold, double Warm)> Clicks(int x, int y)
        {
            double cold = await Click(x, y);
            var warm = new List<double>();
            for (int i = 0; i < 5; i++) warm.Add(await Click(x, y));
            return (cold, Median(warm));
        }

        // A corner of the canvas: usually background, the largest region to fill.
        int cx = Math.Max(1, w / 40), cy = Math.Max(1, h / 40);
        Editor.WandSampleAllLayers = true;
        Editor.WandContiguous = true;
        var all = await Clicks(cx, cy);
        var bounds = Selection?.Bounds;
        Editor.WandContiguous = false;
        var allNc = await Clicks(cx, cy);
        Editor.WandContiguous = true;
        Console.WriteLine($"WANDBENCH sample-all contiguous: first click {all.Cold:F0} ms (includes preparing the flattened image), then {all.Warm:F1} ms; " +
                          $"non-contiguous {allNc.Warm:F1} ms; {w}×{h}, selected {bounds}");

        var layer = Layers.SelectMany(l => l.SelfAndDescendants())
            .Where(i => i.Node is PixelLayer { Pixels: not null })
            .OrderByDescending(i => ((PixelLayer)i.Node).Bounds.Width * (long)((PixelLayer)i.Node).Bounds.Height)
            .FirstOrDefault();
        if (layer is not null)
        {
            SelectedLayer = layer;
            Editor.WandSampleAllLayers = false;
            var b = ((PixelLayer)layer.Node).Bounds.Intersect(Model.Bounds);
            var own = await Clicks((b.Left + b.Right) / 2, (b.Top + b.Bottom) / 2);
            Console.WriteLine($"WANDBENCH current layer \"{layer.Name}\": first click {own.Cold:F0} ms (includes converting the layer), then {own.Warm:F1} ms");
        }

        // Quick Selection: a zig-zag drag with pointer events every 8 ms, like a 120 Hz mouse.
        Editor.QuickSelectSampleAllLayers = true;
        Editor.QuickSelectSize = Math.Max(10, Math.Round(Math.Max(w, h) / 80.0));
        var a = dragFrom ?? new Vector2(w * 0.4f, h * 0.45f);
        var z = dragTo ?? new Vector2(w * 0.6f, h * 0.55f);
        Deselect();
        await SampleImageAsync(true); // measured above; the drag should measure the growing, not the first render
        var prepare = Stopwatch.StartNew();
        BeginQuickSelection(a.X, a.Y, SelectionMode.Replace);
        await _quickDrag!.Stroke;
        double prepareMs = prepare.Elapsed.TotalMilliseconds;
        var input = Stopwatch.StartNew();
        const int steps = 240;
        for (int i = 1; i <= steps; i++)
        {
            float t = i / (float)steps;
            var p = Vector2.Lerp(a, z, t) + new Vector2(0, MathF.Sin(t * MathF.PI * 6) * (z.Y - a.Y) * 0.5f);
            ContinueQuickSelection(p.X, p.Y);
            await Task.Delay(8);
        }
        double inputMs = input.Elapsed.TotalMilliseconds;
        await EndQuickSelectionAsync();
        var (frames, updates, finishMs) = LastQuickSelectStats;
        var sorted = updates.OrderBy(t => t).ToList();
        Console.WriteLine($"QSBENCH drag {inputMs:F0} ms: {frames} outline updates = {frames / (inputMs / 1000):F1} fps; update compute median " +
                          $"{(sorted.Count > 0 ? sorted[sorted.Count / 2] : double.NaN):F1} ms, p90 {(sorted.Count > 0 ? sorted[(int)(sorted.Count * 0.9)] : double.NaN):F1} ms; " +
                          $"mouse-up refine {finishMs:F0} ms; stroke setup {prepareMs:F0} ms; brush {Editor.QuickSelectSize:F0} px; selected {Selection?.Bounds}");

        (Editor.WandSampleAllLayers, Editor.WandContiguous, Editor.QuickSelectSampleAllLayers, Editor.QuickSelectSize) = wand;
        while (CanUndo) Undo();
    }
}
