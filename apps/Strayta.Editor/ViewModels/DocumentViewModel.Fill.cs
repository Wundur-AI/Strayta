using System.Diagnostics;
using System.Numerics;
using Strayta.Core;
using Strayta.Core.Painting;
using Strayta.Core.Selection;
using Strayta.Editor.Editing;
using Strayta.Rendering;

namespace Strayta.Editor.ViewModels;

// Paint Bucket and Gradient (G). Both paint the selected layer under the brush's rules (PaintableLayer), limited to the
// selection, and each click or drag is one undo step.
//
// While a gradient is dragged nothing in the document changes: after each render lane syncs its proxy document, the
// target proxy's pixels (or mask) are swapped for a copy with the gradient drawn in, at the proxy's resolution, the
// same way Free Transform shows its preview. Release draws it once at full resolution and records the edit.
public sealed partial class DocumentViewModel
{
    // ---- Paint Bucket ---------------------------------------------------------------------------------

    /// <summary>How long the last Paint Bucket click took from click to new pixels, for the self-test and benchmark.</summary>
    public double LastBucketMs { get; private set; }

    /// <summary>A Paint Bucket click at image pixel (<paramref name="x"/>, <paramref name="y"/>): fills similar colors with the foreground color or a pattern, in the bucket's mode and opacity.</summary>
    public async Task PaintBucketAsync(int x, int y)
    {
        if (_baking || IsTransforming || x < 0 || y < 0 || x >= Model.Width || y >= Model.Height) return;
        if (BlockedByChannelTarget("Paint Bucket")) return; // DocumentViewModel.Channels.cs
        if (EditMask && SelectedLayer?.Node.GetMask() is not null)
        {
            Notice = "The Paint Bucket fills layer pixels. Click the layer thumbnail to target them, or use the Gradient or Brush on the mask.";
            return;
        }
        if (PaintableLayer() is not { } layer) return;
        var clock = Stopwatch.StartNew();
        var options = Editor.CurrentBucketOptions;
        var selection = Selection;
        var fill = Editor.CurrentBucketFill;
        _baking = true;
        try
        {
            if (await SampleImageAsync(Editor.BucketSampleAllLayers) is not { } sample)
            {
                Notice = "Nothing to sample yet. Wait for the document to finish rendering.";
                return;
            }
            var doc = Model;
            var result = await Task.Run(() => PaintBucket.Fill(layer, sample, x, y, options, selection, fill, doc.ColorMode, doc.BitDepth));
            if (result is { } r) Apply(new PixelsEdit(layer, r.Pixels, r.Bounds, "Paint Bucket"));
            LastBucketMs = clock.Elapsed.TotalMilliseconds;
        }
        finally
        {
            _baking = false;
        }
    }

    // ---- Gradient -------------------------------------------------------------------------------------

    /// <summary>A gradient drag in progress: what it paints (a layer's pixels or its mask) and the gradient so far.</summary>
    private sealed class GradientDrag(LayerNode owner, bool mask, Vector2 start, SelectionMask? selection)
    {
        public LayerNode Owner { get; } = owner;
        public bool Mask { get; } = mask;
        public Vector2 Start { get; } = start;
        public SelectionMask? Selection { get; } = selection;
        public required GradientSpec Spec { get; set; }
    }

    private GradientDrag? _gradient;

    /// <summary>Time from release to the gradient being in the document, for the self-test and benchmark.</summary>
    public double LastGradientBakeMs { get; private set; }

    /// <summary>
    /// Starts a gradient drag at (<paramref name="x"/>, <paramref name="y"/>) on the selected layer, or on its mask when
    /// the mask is targeted (the usual way to fade a layer out); false, with a notice, when it cannot be painted.
    /// </summary>
    public bool BeginGradient(float x, float y)
    {
        if (_baking || IsTransforming || _gradient is not null) return false;
        LayerNode owner;
        var channel = ChannelMaskOwner(); // a targeted channel is painted as a mask (DocumentViewModel.Channels.cs)
        bool mask = channel is not null || EditMask && SelectedLayer?.Node is { } n && n.GetMask() is not null;
        if (mask)
        {
            owner = channel ?? SelectedLayer!.Node;
            string? problem = owner.Visible ? null : $"\"{owner.Name}\" is hidden.";
            if (Model.ColorMode is not (ColorMode.Rgb or ColorMode.Grayscale)) problem = $"Painting in {Model.ColorMode} documents is not supported yet.";
            if (problem is not null)
            {
                Notice = problem;
                PaintBlock = new PaintBlock(problem, CanRasterize: false, CanShow: !owner.Visible, CanNewLayer: false);
                return false;
            }
            Notice = "";
            PaintBlock = null;
        }
        else if (PaintableLayer() is { } layer) owner = layer;
        else return false;

        var start = new Vector2(x, y);
        _gradient = new GradientDrag(owner, mask, start, Selection) { Spec = Editor.GradientFor(start, start) };
        return true;
    }

    /// <summary>The drag moved: the gradient now ends at (<paramref name="x"/>, <paramref name="y"/>); the render lanes redraw it.</summary>
    public void MoveGradient(float x, float y)
    {
        if (_gradient is not { } drag) return;
        drag.Spec = Editor.GradientFor(drag.Start, new Vector2(x, y));
        RequestRender();
    }

    /// <summary>
    /// Release: draws the gradient at full resolution in the background and records it as one undoable edit. The
    /// preview stays on screen until then, so nothing flickers. A click without a drag does nothing, as in Photoshop.
    /// </summary>
    public async Task EndGradientAsync()
    {
        if (_gradient is not { } drag) return;
        if (drag.Spec.IsDegenerate)
        {
            _gradient = null;
            RequestRender();
            return;
        }
        var clock = Stopwatch.StartNew();
        _baking = true;
        try
        {
            var doc = Model;
            var spec = drag.Spec;
            var canvas = doc.Bounds;
            if (drag.Mask)
            {
                var mask = drag.Owner.GetMask()!;
                var baked = await Task.Run(() => GradientPainter.ApplyToMask(mask, spec, drag.Selection, canvas, doc.BitDepth));
                _gradient = null;
                Apply(new MaskEdit(drag.Owner, baked, "Gradient"));
            }
            else
            {
                var layer = (PixelLayer)drag.Owner;
                var (old, oldBounds) = (layer.Pixels, layer.Bounds);
                var (pixels, bounds) = await Task.Run(() => GradientPainter.Apply(old, oldBounds, spec, drag.Selection, canvas, doc.ColorMode, doc.BitDepth));
                _gradient = null;
                Apply(new PixelsEdit(layer, pixels, bounds, "Gradient"));
            }
            LastGradientBakeMs = clock.Elapsed.TotalMilliseconds;
        }
        catch (Exception ex)
        {
            _gradient = null;
            Notice = $"Could not draw the gradient: {ex.Message}";
            RequestRender();
        }
        finally
        {
            _baking = false;
        }
    }

    /// <summary>
    /// Called by a render lane on the UI thread right after syncing its proxy document: the work that shows the open
    /// Free Transform and the gradient being dragged, to run on the render thread before rendering (null when neither).
    /// </summary>
    private Action<CancellationToken>? PrepareOverlays(PreviewDocument proxy, bool full) =>
        PrepareTransform(proxy, full) + PrepareGradient(proxy) + PrepareFilter(proxy, full); // Filters: DocumentViewModel.Filters.cs

    // ---- Benchmark -------------------------------------------------------------------------------------

    /// <summary>
    /// A two-second gradient drag with 120 Hz input on a new layer (frames reaching the screen, and the time from
    /// release to the gradient being in the document), Paint Bucket clicks (first and later) and Eyedropper samples
    /// (STRAYTA_TOOLBENCH=new on a generated 4000×3000 document).
    /// </summary>
    public async Task RunEverydayToolsBenchmarkAsync()
    {
        int w = Model.Width, h = Model.Height;
        static double Median(List<double> v) => v.Count == 0 ? double.NaN : v.OrderBy(t => t).ElementAt(v.Count / 2);
        static double P90(List<double> v) => v.Count == 0 ? double.NaN : v.OrderBy(t => t).ElementAt((int)(v.Count * 0.9));

        // Two-color Classic (the old tool) and a seven-stop Perceptual Spectrum, plain and in Multiply mode.
        var runs = new (GradientType Type, Gradient Gradient, GradientMethod Method, PaintMode Mode)[]
        {
            (GradientType.Linear, Editing.GradientPresets.ForegroundToBackground, GradientMethod.Classic, PaintMode.Normal),
            (GradientType.Radial, Editing.GradientPresets.ForegroundToBackground, GradientMethod.Classic, PaintMode.Normal),
            (GradientType.Linear, Editing.GradientPresets.BuiltIn.First(g => g.Name == "Spectrum"), GradientMethod.Perceptual, PaintMode.Normal),
            (GradientType.Radial, Editing.GradientPresets.BuiltIn.First(g => g.Name == "Spectrum"), GradientMethod.Perceptual, PaintMode.Multiply),
        };
        var (savedGradient, savedMethod, savedMode) = (Editor.ToolGradient, Editor.GradientMethod, Editor.GradientMode);
        foreach (var (type, gradient, method, mode) in runs)
        {
            NewLayer();
            Editor.GradientType = type;
            (Editor.ToolGradient, Editor.GradientMethod, Editor.GradientMode) = (gradient, method, mode);
            int frames = 0;
            var times = new List<double>();
            var parts = new List<double>();
            var clock = Stopwatch.StartNew();
            double last = 0;
            void OnFrame(bool full)
            {
                if (full) return;
                frames++;
                times.Add(clock.Elapsed.TotalMilliseconds - last);
                parts.Add(LastFrameTimings.Total);
                last = clock.Elapsed.TotalMilliseconds;
            }
            FrameDisplayed += OnFrame;
            BeginGradient(w * 0.2f, h * 0.5f);
            for (int i = 1; i <= 240; i++)
            {
                float t = i / 240f;
                MoveGradient(w * (0.3f + 0.5f * t), h * (0.5f + 0.3f * MathF.Sin(t * 6)));
                await Task.Delay(8);
            }
            double inputMs = clock.Elapsed.TotalMilliseconds;
            FrameDisplayed -= OnFrame;
            var release = Stopwatch.StartNew();
            await EndGradientAsync();
            double releaseMs = release.Elapsed.TotalMilliseconds;
            Console.WriteLine($"TOOLBENCH gradient {type} {gradient.Name} {method} {PaintModeNamesOf(mode)} drag {inputMs:F0} ms: frames={frames} fps={frames / (inputMs / 1000):F1} " +
                              $"median-frame={Median(times):F0}ms p90={P90(times):F0}ms preview-render median={Median(parts):F1}ms " +
                              $"factor={PreviewDocument.FactorForZoom(_viewZoom)}; release to edit {releaseMs:F0} ms (bake {LastGradientBakeMs:F0} ms) on {w}×{h}");
        }

        (Editor.ToolGradient, Editor.GradientMethod, Editor.GradientMode) = (savedGradient, savedMethod, savedMode);

        // Paint Bucket on the layer below the gradients (the photo): the first click prepares the layer's sample image.
        var photo = Layers.SelectMany(l => l.SelfAndDescendants()).FirstOrDefault(i => i.Node is PixelLayer { Pixels: not null } && i.Name != "Background");
        if (photo is not null)
        {
            SelectedLayer = photo;
            var clicks = new List<double>();
            foreach (bool contiguous in new[] { true, false })
            {
                Editor.BucketContiguous = contiguous;
                _layerSample = null;
                var times = new List<double>();
                for (int i = 0; i < 4; i++)
                {
                    await PaintBucketAsync(w / 2, h / 2);
                    times.Add(LastBucketMs);
                    Undo();
                }
                Console.WriteLine($"TOOLBENCH paint bucket {(contiguous ? "contiguous" : "non-contiguous")}: first click {times[0]:F0} ms (includes preparing the layer), " +
                                  $"then {Median(times.Skip(1).ToList()):F0} ms; tolerance {Editor.BucketTolerance}");
            }
            Editor.BucketContiguous = true;

            var samples = new List<double>();
            BeginEyedropper(w / 3, h / 3, background: false);
            await Task.Delay(300);
            for (int i = 0; i < 50; i++)
            {
                SampleEyedropper(w / 3 + i, h / 3);
                samples.Add(LastEyedropperMs);
            }
            EndEyedropper();
            Console.WriteLine($"TOOLBENCH eyedropper {Editor.EyedropperSizeNames[Editor.EyedropperSizeIndex]}, {Editor.EyedropperSampleNames[Editor.EyedropperSampleIndex]}: " +
                              $"median sample {Median(samples):F3} ms");
        }
        while (CanUndo) Undo();
    }

    private static string PaintModeNamesOf(PaintMode mode) => Controls.PaintModeNames.Of(mode);

    private Action<CancellationToken>? PrepareGradient(PreviewDocument proxy)
    {
        if (_gradient is not { Spec.IsDegenerate: false } drag || proxy.ProxyOf(drag.Owner) is not { } target) return null;
        int factor = proxy.Factor;
        var spec = factor == 1 ? drag.Spec : drag.Spec.Scaled(1f / factor);
        var canvas = proxy.Proxy.Bounds;
        var (mode, depth, selection) = (Model.ColorMode, Model.BitDepth, drag.Selection);
        // The proxy's pixels and mask were just reset by Sync, so reading them here sees the layer as it is.
        var mask = drag.Mask ? target.GetMask() : null;
        var pixels = target is PixelLayer p ? (p.Pixels, p.Bounds) : default;
        return cancel =>
        {
            if (drag.Mask)
            {
                if (mask is not null) target.SetMask(GradientPainter.ApplyToMask(mask, spec, selection, canvas, depth, factor, cancel));
            }
            else if (target is PixelLayer layer)
            {
                (layer.Pixels, layer.Bounds) = GradientPainter.Apply(pixels.Pixels, pixels.Bounds, spec, selection, canvas, mode, depth, factor, cancel);
            }
        };
    }
}
