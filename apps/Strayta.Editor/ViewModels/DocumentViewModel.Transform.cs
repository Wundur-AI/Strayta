using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Strayta.Core;
using Strayta.Editor.Editing;
using Strayta.Rendering;
using Strayta.Rendering.Transforms;

namespace Strayta.Editor.ViewModels;

// Free Transform (⌘T): while a transform is open the render lanes draw the target layers through a
// TransformPreview (bilinear at preview resolution while dragging, bicubic at full resolution once idle);
// nothing in the document changes until the transform is committed as one undoable edit.
public sealed partial class DocumentViewModel
{
    private TransformPreview? _transformPreview;
    private IReadOnlyList<LayerNode> _transformTargets = [];
    private LayerNode? _transformNode;

    /// <summary>The open Free Transform, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTransforming))]
    public partial FreeTransform? FreeTransform { get; private set; }

    public bool IsTransforming => FreeTransform is not null;

    /// <summary>Starts Free Transform on the selected layer or group; returns false (with a notice) if it cannot be transformed.</summary>
    public bool BeginFreeTransform()
    {
        if (IsTransforming) return true;
        var node = SelectedLayer?.Node;
        var targets = node is LayerGroup g ? g.Descendants().Prepend(g).ToList() : node is null ? [] : [node];
        var live = targets.FirstOrDefault(n => n.Tags.Contains("text") || n.Tags.Contains("smart-object") || n.Tags.Contains("fill") || n.Tags.Contains("shape"));
        var content = targets.OfType<PixelLayer>().Select(Resampler.ContentBounds).Where(b => !b.IsEmpty).ToList();
        string? problem = node switch
        {
            null => "Select a layer to transform.",
            { Visible: false } => $"\"{node.Name}\" is hidden. Show it to transform it.",
            AdjustmentLayer => "Adjustment layers have no pixels to transform.",
            _ when live is not null => $"\"{live.Name}\" is a {LiveKind(live)} layer. Photoshop redraws these from their own data, so a transform of its pixels would be lost. Rasterize it first.",
            _ when content.Count == 0 => $"\"{node.Name}\" has no pixels to transform.",
            _ => null,
        };
        if (problem is not null)
        {
            Notice = problem;
            return false;
        }

        var box = content.Aggregate((a, b) => new PixelRect(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom)));
        Notice = "";
        PaintBlock = null;
        _transformTargets = targets;
        _transformNode = node;
        _transformPreview = new TransformPreview(targets);
        var transform = new FreeTransform(box);
        transform.Changed += OnTransformChanged;
        FreeTransform = transform;
        PropertyChanged += CommitOnSelectionChange;
        return true;

        static string LiveKind(LayerNode n) =>
            n.Tags.Contains("text") ? "text" : n.Tags.Contains("smart-object") ? "smart object" : n.Tags.Contains("fill") ? "fill" : "shape";
    }

    private void OnTransformChanged()
    {
        if (_transformPreview is null || FreeTransform is null) return;
        _transformPreview.Matrix = FreeTransform.Matrix;
        RequestRender();
    }

    /// <summary>Choosing another layer applies the open transform first, as Photoshop does.</summary>
    private void CommitOnSelectionChange(object? sender, PropertyChangedEventArgs e)
    {
        // Rebuilding the Layers panel re-selects the same layer through a new row; that is not a new choice.
        if (e.PropertyName == nameof(SelectedLayer) && IsTransforming && SelectedLayer?.Node != _transformNode) _ = CommitTransformAsync();
    }

    /// <summary>
    /// Applies the transform at full quality (bicubic, with area filtering for reductions) and records it as
    /// one undoable edit. The live preview stays on screen while the pixels are computed in the background.
    /// </summary>
    public async Task CommitTransformAsync()
    {
        if (FreeTransform is not { Locked: false } transform) return;
        if (transform.IsIdentity)
        {
            EndTransform();
            return;
        }
        transform.Locked = true;
        var matrix = transform.Matrix;
        var inputs = _transformTargets.Select(n => (Node: n, State: TransformEdit.Read(n))).ToList();
        // Photoshop keeps pixels pushed off the canvas; keep a canvas-sized margin all round so a huge enlargement
        // cannot exhaust memory.
        var clip = new PixelRect(-Model.Width, -Model.Height, 2 * Model.Width, 2 * Model.Height);
        try
        {
            var results = await Task.Run(() => inputs.Select(i =>
            {
                var s = i.State;
                var (pixels, bounds) = i.Node is PixelLayer && s.Pixels is not null
                    ? Resampler.TransformRaster(s.Pixels, s.Bounds, matrix, ResampleFilter.Bicubic, clip)
                    : (s.Pixels, s.Bounds);
                var mask = Resampler.TransformMask(s.Mask, matrix, ResampleFilter.Bicubic);
                return (i.Node, new TransformEdit.State(pixels, pixels is null ? PixelRect.Empty : bounds, mask));
            }).ToList());
            EndTransform();
            Apply(new TransformEdit(results));
        }
        catch (Exception ex)
        {
            transform.Locked = false;
            Notice = $"Could not apply the transform: {ex.Message}";
        }
    }

    /// <summary>Esc: closes the transform without changing anything.</summary>
    public void CancelTransform()
    {
        if (FreeTransform is not { Locked: false }) return;
        EndTransform();
        RequestRender();
    }

    private void EndTransform()
    {
        if (FreeTransform is { } t) t.Changed -= OnTransformChanged;
        PropertyChanged -= CommitOnSelectionChange;
        FreeTransform = null;
        _transformPreview = null;
        _transformTargets = [];
        _transformNode = null;
    }

    /// <summary>
    /// Called by a render lane on the UI thread right after syncing its proxy document: the work that shows
    /// the open transform in it, to run on the render thread before rendering (null when not transforming).
    /// </summary>
    private Action<CancellationToken>? PrepareTransform(PreviewDocument proxy, bool full) =>
        _transformPreview?.Prepare(proxy, full ? ResampleFilter.Bicubic : ResampleFilter.Bilinear);

    /// <summary>
    /// Simulates a two-second Free Transform drag (scaling and rotating the largest layer) with 120 Hz input and
    /// reports frames reaching the screen and the time to commit (STRAYTA_TRANSFORMBENCH=1).
    /// </summary>
    public async Task RunTransformBenchmarkAsync()
    {
        var target = Layers.SelectMany(l => l.SelfAndDescendants())
            .Where(i => i.Node is PixelLayer { Visible: true, Pixels: not null } && !i.Node.Tags.Overlaps(["text", "smart-object", "fill", "shape"]))
            .OrderByDescending(i => ((PixelLayer)i.Node).Bounds.Width * ((PixelLayer)i.Node).Bounds.Height)
            .Skip(1).FirstOrDefault(); // skip the background
        if (target is null) return;
        SelectedLayer = target;
        if (!BeginFreeTransform()) return;
        var ft = FreeTransform!;

        int frames = 0;
        var frameTimes = new List<double>();
        var parts = new List<(double Sync, double Render, double Convert, double Show, double Total)>();
        var clock = Stopwatch.StartNew();
        double last = 0;
        void OnFrame(bool full)
        {
            if (full) return;
            frames++;
            parts.Add(LastFrameTimings);
            frameTimes.Add(clock.Elapsed.TotalMilliseconds - last);
            last = clock.Elapsed.TotalMilliseconds;
        }
        FrameDisplayed += OnFrame;

        // One second dragging the bottom-right corner in and out, one second rotating from outside the box.
        var (cx, cy) = ft.Center;
        var corner = ft.HandlePosition(TransformHandle.BottomRight);
        ft.BeginDrag(TransformHandle.BottomRight, corner.X, corner.Y);
        for (int i = 1; i <= 120; i++)
        {
            double t = 0.75 + 0.35 * Math.Sin(i / 120.0 * 2 * Math.PI);
            ft.DragTo(cx + (corner.X - cx) * t, cy + (corner.Y - cy) * t, shift: false, alt: true);
            await Task.Delay(8);
        }
        ft.EndDrag();
        var grip = (X: ft.Corners[1].X + 40, Y: ft.Corners[1].Y - 40);
        ft.BeginDrag(ft.HitTest(grip.X, grip.Y, 1), grip.X, grip.Y);
        for (int i = 1; i <= 120; i++)
        {
            double a = i / 120.0 * 0.6;
            var (sin, cos) = Math.SinCos(a);
            double dx = grip.X - cx, dy = grip.Y - cy;
            ft.DragTo(cx + cos * dx - sin * dy, cy + sin * dx + cos * dy, shift: false, alt: false);
            await Task.Delay(8);
        }
        ft.EndDrag();
        double inputMs = clock.Elapsed.TotalMilliseconds;

        var fullShown = new TaskCompletionSource<double>();
        void OnFull(bool full) { if (full) fullShown.TrySetResult(clock.Elapsed.TotalMilliseconds - inputMs); }
        FrameDisplayed += OnFull;
        var settle = await Task.WhenAny(fullShown.Task, Task.Delay(5000));
        FrameDisplayed -= OnFull;
        FrameDisplayed -= OnFrame;

        var commit = Stopwatch.StartNew();
        await CommitTransformAsync();
        long commitMs = commit.ElapsedMilliseconds;

        frameTimes.Sort();
        double median = frameTimes.Count > 0 ? frameTimes[frameTimes.Count / 2] : double.NaN;
        double p90 = frameTimes.Count > 0 ? frameTimes[(int)(frameTimes.Count * 0.9)] : double.NaN;
        var layer = (PixelLayer)target.Node;
        Console.WriteLine($"TRANSFORMBENCH layer=\"{target.Name}\" {layer.Bounds.Width}x{layer.Bounds.Height} in {Model.Width}x{Model.Height} input={inputMs:F0}ms " +
                          $"frames={frames} fps={frames / (inputMs / 1000):F1} median-frame={median:F0}ms p90={p90:F0}ms zoom={_viewZoom:F3} factor={PreviewDocument.FactorForZoom(_viewZoom)}");
        double Med(Func<(double Sync, double Render, double Convert, double Show, double Total), double> f)
        {
            var v = parts.Skip(1).Select(f).OrderBy(x => x).ToList();
            return v.Count == 0 ? double.NaN : v[v.Count / 2];
        }
        Console.WriteLine($"TRANSFORMBENCH parts (median ms): sync={Med(p => p.Sync):F1} render={Med(p => p.Render):F1} " +
                          $"convert={Med(p => p.Convert):F1} show={Med(p => p.Show):F1} total={Med(p => p.Total):F1}");
        Console.WriteLine((settle == fullShown.Task
            ? $"TRANSFORMBENCH full-resolution bicubic preview {fullShown.Task.Result:F0} ms after the last move (includes the 400 ms idle delay)"
            : "TRANSFORMBENCH full-resolution preview did not appear within 5 s") + $"; commit={commitMs}ms");
        while (CanUndo) Undo();
    }
}
