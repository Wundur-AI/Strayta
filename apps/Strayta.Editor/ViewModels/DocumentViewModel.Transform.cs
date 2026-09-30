using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Strayta.Core;
using Strayta.Editor.Editing;
using Strayta.Rendering;
using Strayta.Rendering.Transforms;

namespace Strayta.Editor.ViewModels;

// Free Transform (⌘T) and Edit › Transform: while a transform is open the render lanes draw the target layers through a
// TransformPreview (affine: scale, rotate, skew) or a DeformPreview (distort, perspective, warp), bilinear at preview
// resolution while dragging and bicubic at full resolution once idle; nothing in the document changes until the
// transform is committed as one undoable edit.
public sealed partial class DocumentViewModel
{
    private TransformPreview? _transformPreview;
    private DeformPreview? _deformPreview;
    private int _deformVersion;
    private IReadOnlyList<LayerNode> _transformTargets = [];
    private LayerNode? _transformNode;
    private string? _transformDescription;

    /// <summary>The open Free Transform, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTransforming))]
    public partial FreeTransform? FreeTransform { get; private set; }

    public bool IsTransforming => FreeTransform is not null;

    /// <summary>Layers whose live data cannot follow a perspective or warp (type, shapes, fills, vector masks).</summary>
    private static bool NeedsRasterizing(LayerNode n) => RasterizeEdit.CanRasterize(n) && !n.Tags.Contains("smart-object");

    /// <summary>Starts Free Transform on the selected layer or group; returns false (with a notice) if it cannot be transformed.</summary>
    public bool BeginFreeTransform()
    {
        if (IsTransforming) return true;
        CommitType(); // ⌘T while typing transforms the committed type (DocumentViewModel.TypeTool.cs)
        var node = SelectedLayer?.Node;
        var targets = node is LayerGroup g ? g.Descendants().Prepend(g).ToList() : node is null ? [] : [node];
        var content = targets.OfType<PixelLayer>().Select(Resampler.ContentBounds).Where(b => !b.IsEmpty).ToList();
        string? problem = node switch
        {
            null => "Select a layer to transform.",
            { Visible: false } => $"\"{node.Name}\" is hidden. Show it to transform it.",
            AdjustmentLayer => "Adjustment layers have no pixels to transform.",
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
        _transformDescription = null;
        _transformPreview = new TransformPreview(targets);
        _deformPreview = new DeformPreview(targets);
        var transform = new FreeTransform(box) { AllowsPerspective = !targets.Any(NeedsRasterizing) };
        transform.Changed += OnTransformChanged;
        FreeTransform = transform;
        PropertyChanged += CommitOnSelectionChange;
        return true;
    }

    private void OnTransformChanged()
    {
        if (_transformPreview is null || _deformPreview is null || FreeTransform is not { } ft) return;
        if (ft.ContentAware)
        {
            UpdateContentAwareTarget(ft); // DocumentViewModel.ContentAwareScale.cs
            RequestRender();
            return;
        }
        if (ft.HasWarp || !ft.IsAffine)
        {
            try
            {
                _deformPreview.Current = CurrentDeform(ft);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                return; // a degenerate box in the middle of a drag: keep the last preview
            }
        }
        else
        {
            _deformPreview.Current = null;
            _transformPreview.Matrix = ft.AffineMap;
        }
        RequestRender();
    }

    /// <summary>The transform as a document-space deform for previews and commits (distort, perspective, warp).</summary>
    private Deform CurrentDeform(FreeTransform ft)
    {
        int version = Interlocked.Increment(ref _deformVersion);
        if (!ft.HasWarp) return new Deform(version, ft.Map, null);
        var warp = ft.Warp!;
        if (LiveWarpDeform(version, ft, warp) is { } live) return live; // smart objects and type (DocumentViewModel.Warp.cs)
        var box = ft.Original;
        var docMesh = warp.DocumentMesh(Projective.Translation(box.Left, box.Top).Then(ft.Map));
        return new Deform(version, null, (x, y) => docMesh.Map(x - box.Left, y - box.Top));
    }

    /// <summary>Choosing another layer applies the open transform first, as Photoshop does.</summary>
    private void CommitOnSelectionChange(object? sender, PropertyChangedEventArgs e)
    {
        // Rebuilding the Layers panel re-selects the same layer through a new row; that is not a new choice.
        if (e.PropertyName == nameof(SelectedLayer) && IsTransforming && SelectedLayer?.Node != _transformNode) _ = CommitTransformAsync();
    }

    /// <summary>What the history calls the committed transform.</summary>
    private string TransformDescription(FreeTransform t) => _transformDescription ?? (t.HasWarp ? "Warp" : t.Mode switch
    {
        TransformMode.Scale => "Scale",
        TransformMode.Rotate => "Rotate",
        TransformMode.Skew => "Skew",
        TransformMode.Distort => "Distort",
        TransformMode.Perspective => "Perspective",
        _ => "Free Transform",
    });

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
        if (transform.ContentAware)
        {
            await CommitContentAwareScaleAsync(transform);
            return;
        }
        transform.Locked = true;
        string description = TransformDescription(transform);
        var inputs = _transformTargets.Select(n => (Node: n, State: TransformEdit.Read(n))).ToList();
        // Photoshop keeps pixels pushed off the canvas; keep a canvas-sized margin all round so a huge enlargement
        // cannot exhaust memory.
        var clip = new PixelRect(-Model.Width, -Model.Height, 2 * Model.Width, 2 * Model.Height);
        var doc = Model;
        try
        {
            List<(LayerNode, TransformEdit.State)> results;
            if (!transform.HasWarp && transform.IsAffine)
            {
                var matrix = transform.AffineMap;
                results = await Task.Run(() => inputs.Select(i => (i.Node, AffineResult(i.Node, i.State, matrix, clip, doc))).ToList());
            }
            else
            {
                // The preview's deform is the current state; its full-resolution results are taken as they are when ready.
                var deform = _deformPreview?.Current ?? CurrentDeform(transform);
                var map = transform.Map;
                var warped = transform.HasWarp ? WarpCommit(transform) : null;
                var ready = inputs.Where(i => i.State.Pixels is not null && !i.Node.Tags.Contains("smart-object"))
                    .Select(i => (i.Node, Result: _deformPreview?.FullResult(i.Node, i.State.Pixels!, i.State.Bounds, deform.Version)))
                    .Where(r => r.Result is not null).ToDictionary(r => r.Node, r => r.Result!.Value);
                results = await Task.Run(() => inputs.Select(i => (i.Node, DeformResult(i.Node, i.State, deform, map, warped, clip, doc,
                    ready.TryGetValue(i.Node, out var r) ? r : null))).ToList());
            }
            EndTransform();
            Apply(new TransformEdit(results, description));
        }
        catch (Exception ex)
        {
            transform.Locked = false;
            Notice = $"Could not apply the transform: {ex.Message}";
        }
    }

    /// <summary>One layer through an affine transform: resampled, its live content moved and redrawn.</summary>
    private static TransformEdit.State AffineResult(LayerNode node, TransformEdit.State s, Affine matrix, PixelRect clip, Document doc)
    {
        var (pixels, bounds) = node is PixelLayer && s.Pixels is not null
            ? Resampler.TransformRaster(s.Pixels, s.Bounds, matrix, ResampleFilter.Bicubic, clip)
            : (s.Pixels, s.Bounds);
        var mask = Resampler.TransformMask(s.Mask, matrix, ResampleFilter.Bicubic);
        // Type, smart objects, shapes and fills stay live: their data follows (LiveContent.cs).
        var source = TransformedSource(s.Source, matrix, doc);
        if (node is PixelLayer layer && s.Pixels is not null && LiveContent.IsLive(layer) && source is Psd.PsdLayerRecord record
            && LiveContent.Redraw(layer, record, doc.SourceData as Psd.PsdFile, doc, s.Bounds, doc.Bounds, doc.Bounds, matrix) is { } redrawn)
            (pixels, bounds) = redrawn;
        return new TransformEdit.State(pixels, pixels is null ? PixelRect.Empty : bounds, mask, source);
    }

    /// <summary>
    /// One layer through a distort, perspective or warp. Smart objects keep their content: their corners (and warp) are
    /// rewritten and they are drawn again from it; other layers are resampled. Data that cannot take a perspective
    /// (paths, guides) follows the map's best affine fit at the box's center.
    /// </summary>
    private static TransformEdit.State DeformResult(LayerNode node, TransformEdit.State s, Deform deform, Projective map, WarpCommitInfo? warp,
        PixelRect clip, Document doc, (Raster? Pixels, PixelRect Bounds)? ready = null)
    {
        var (cx, cy) = (s.Bounds.Left + s.Bounds.Width / 2.0, s.Bounds.Top + s.Bounds.Height / 2.0);
        var near = map.Linearize(cx, cy);
        if (warp is { TypeSpec: { } typeSpec, Layer: var typeLayer } && typeLayer == node && WarpedTypeResult(s, near, typeSpec, doc) is { } typed)
            return typed;
        object? source = TransformedSource(s.Source, near, doc);
        (Raster? Pixels, PixelRect Bounds) result = (s.Pixels, s.Bounds);
        bool done = false;
        if (node is PixelLayer layer && s.Pixels is not null && layer.Tags.Contains("smart-object") && s.Source is Psd.PsdLayerRecord original
            && Psd.PsdLiveContent.ReadSmartObject(original) is { } so)
        {
            var corners = warp is { Layer: var l } w && l == node ? w.Corners : so.Corners.Select(c => map.Apply(c.X, c.Y)).ToArray();
            var spec = warp is { Layer: var l2 } w2 && l2 == node ? w2.Spec : null;
            // Its other live data (a vector mask) follows the map's affine fit; the corners and warp are set exactly.
            if (SmartObjectTransform.WithPlacement(source as Psd.PsdLayerRecord ?? original, corners, spec) is { } moved)
            {
                source = moved;
                if (doc.SourceData is Psd.PsdFile file && LiveContent.Redraw(layer, moved, file, doc, s.Bounds, doc.Bounds, doc.Bounds, near) is { } redrawn)
                {
                    result = redrawn;
                    done = true;
                }
            }
        }
        if (!done && ready is { } taken)
        {
            result = taken;
            done = true;
        }
        if (!done && node is PixelLayer && s.Pixels is not null)
        {
            var src = ResampleSource.FromRaster(s.Pixels);
            var b = s.Bounds;
            result = deform.Map is { } m
                ? MeshResampler.TransformRaster(src, (u, v) => m(b.Left + u, b.Top + v), ResampleFilter.Bicubic, clip)
                : deform.Perspective is { } p ? ProjectiveResampler.TransformRaster(src, b, p, ResampleFilter.Bicubic, clip)
                : deform.Contents?.GetValueOrDefault(node) is { } c ? MeshResampler.TransformRaster(c.Content, c.Map, ResampleFilter.Bicubic, clip)
                : result;
        }
        var mask = deform.Map is { } dm ? MeshResampler.TransformMask(s.Mask, dm, ResampleFilter.Bicubic)
            : deform.Perspective is { } dp ? ProjectiveResampler.TransformMask(s.Mask, dp, ResampleFilter.Bicubic)
            : s.Mask;
        return new TransformEdit.State(result.Pixels, result.Pixels is null ? PixelRect.Empty : result.Bounds, mask, source);
    }

    /// <summary>A layer's file data with its live content (vector outlines, type, smart object corners) moved by <paramref name="m"/>.</summary>
    private static object? TransformedSource(object? source, Affine m, Document doc)
    {
        var map = new Psd.CanvasMap(m.M11, m.M12, m.M21, m.M22, m.Dx, m.Dy);
        return source switch
        {
            Psd.PsdLayerRecord r => Psd.PsdCanvas.WithCanvas(r, doc.Width, doc.Height, doc.Width, doc.Height, map),
            Psd.PsdGroupRecords g => g with { Folder = Psd.PsdCanvas.WithCanvas(g.Folder, doc.Width, doc.Height, doc.Width, doc.Height, map) },
            _ => source,
        };
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
        if (FreeTransform is { } t)
        {
            t.Changed -= OnTransformChanged;
            if (t.Warp is { } w) w.Changed -= t.RaiseChanged;
        }
        PropertyChanged -= CommitOnSelectionChange;
        FreeTransform = null;
        _transformPreview = null;
        _deformPreview = null;
        _transformTargets = [];
        _transformNode = null;
        _warpSmartObject = null;
        _warpType = null;
        _casPreview = null;
        OnPropertyChanged(nameof(IsContentAwareScaling));
    }

    /// <summary>
    /// Called by a render lane on the UI thread right after syncing its proxy document: the work that shows
    /// the open transform in it, to run on the render thread before rendering (null when not transforming).
    /// </summary>
    private Action<CancellationToken>? PrepareTransform(PreviewDocument proxy, bool full)
    {
        var filter = full ? ResampleFilter.Bicubic : ResampleFilter.Bilinear;
        if (_puppet is not null) return PreparePuppet(proxy, filter); // DocumentViewModel.PuppetWarp.cs
        if (_casPreview is { } cas) return cas.Prepare(proxy, full); // DocumentViewModel.ContentAwareScale.cs
        if (_deformPreview is { Current: not null } deform) return deform.Prepare(proxy, filter);
        return _transformPreview?.Prepare(proxy, filter);
    }

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
