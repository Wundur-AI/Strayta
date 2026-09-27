using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Strayta.Core;
using Strayta.Core.Painting;
using Strayta.Editor.Controls;
using Strayta.Editor.Editing;
using Strayta.Psd;
using Strayta.Rendering;

namespace Strayta.Editor.ViewModels;

public enum ViewMode
{
    Strayta,
    Photoshop,
    Difference,
}

/// <summary>One open document: its model, undo history, render state and panels' view of its layers.</summary>
public sealed partial class DocumentViewModel : Dock.Model.Mvvm.Controls.Document
{
    private readonly IRenderer _renderer = Renderers.CreateDefault();
    private readonly IRenderer _previewRenderer = Renderers.CreateDefault();
    private readonly PreviewDocument _snapshot;
    private PreviewDocument? _preview;
    private double _viewZoom = 1;
    private int _modelVersion;
    private bool _previewRunning, _previewPending;
    private CancellationTokenSource? _fullCancel;
    private Task _fullTask = Task.CompletedTask;
    private int _displayedVersion = -1;
    private bool _displayedIsFull;
    private readonly UndoStack _undo = new();
    private byte[]? _reference;
    private Bitmap? _renderBitmap, _referenceBitmap, _diffBitmap;
    private byte[]? _lastRender;

    public DocumentViewModel(Strayta.Core.Document model, string? path, EditorViewModel editor)
    {
        Editor = editor;
        Model = model;
        _snapshot = new PreviewDocument(model, 1);
        FilePath = path;
        Id = Guid.NewGuid().ToString("N");
        Title = path is null ? $"Untitled-{++_untitledCount}" : System.IO.Path.GetFileName(path);
        CanFloat = true;

        _reference = model.Composite is { } comp ? RgbaConverter.ToRgba8(comp, model.Palette) : null;
        _referenceBitmap = _reference is null ? null : BitmapFactory.FromRgba(_reference, model.Width, model.Height);
        HasReference = _reference is not null;
        Display = _referenceBitmap;
        Status = $"{model.Width}×{model.Height} · {model.ColorMode} {model.BitDepth}-bit";
        RebuildLayers();
        WarmPreviews();
    }

    private static int _untitledCount;

    /// <summary>App-wide settings (current tool, brush, color) shared by every document.</summary>
    public EditorViewModel Editor { get; }

    public Strayta.Core.Document Model { get; }
    public Avalonia.PixelSize DocumentSize => new(Model.Width, Model.Height);
    public string? FilePath { get; private set; }

    public ObservableCollection<LayerItemViewModel> Layers { get; } = [];

    /// <summary>The Layers panel's visible rows: the tree flattened top to bottom, skipping collapsed groups.</summary>
    public ObservableCollection<LayerItemViewModel> Rows { get; } = [];

    /// <summary>Rebuilds <see cref="Rows"/> after a group is expanded or collapsed or the tree changes.</summary>
    public void RefreshRows()
    {
        var rows = new List<LayerItemViewModel>();
        void Add(IEnumerable<LayerItemViewModel> items)
        {
            foreach (var item in items)
            {
                rows.Add(item);
                if (item.IsExpanded) Add(item.Children);
            }
        }
        Add(Layers);

        // Update in place so the list keeps its scroll position and selection.
        for (int i = 0; i < rows.Count; i++)
        {
            if (i < Rows.Count && ReferenceEquals(Rows[i], rows[i])) continue;
            int existing = Rows.IndexOf(rows[i]);
            if (existing > i) Rows.Move(existing, i);
            else Rows.Insert(i, rows[i]);
        }
        while (Rows.Count > rows.Count) Rows.RemoveAt(Rows.Count - 1);
    }
    public ObservableCollection<string> Warnings { get; } = [];
    public string WarningsText => string.Join(Environment.NewLine, Warnings);
    public bool HasWarnings => Warnings.Count > 0;

    [ObservableProperty] public partial LayerItemViewModel? SelectedLayer { get; set; }
    [ObservableProperty] public partial Bitmap? Display { get; set; }
    [ObservableProperty] public partial ViewMode Mode { get; set; } = ViewMode.Strayta;
    [ObservableProperty] public partial string Status { get; set; } = "";

    /// <summary>A short message about the last action (e.g. why painting is not possible), shown in the status bar.</summary>
    [ObservableProperty] public partial string Notice { get; set; } = "";

    /// <summary>Shown over the canvas when the brush cannot paint on the selected layer, with ways to fix it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPaintBlock))]
    public partial PaintBlock? PaintBlock { get; set; }

    public bool HasPaintBlock => PaintBlock is not null;

    public void DismissPaintBlock() => PaintBlock = null;

    /// <summary>Layer > Rasterize: make the selected text/fill/shape/smart-object layer plain pixels.</summary>
    public void RasterizeSelected()
    {
        if (SelectedLayer?.Node is { } node && RasterizeEdit.CanRasterize(node))
        {
            Apply(new RasterizeEdit(node));
            Notice = $"\"{node.Name}\" is now a pixel layer.";
        }
        PaintBlock = null;
    }

    public void ShowSelected()
    {
        if (SelectedLayer is { } item) item.IsVisible = true;
        PaintBlock = null;
    }

    public void NewLayerForPainting()
    {
        NewLayer();
        PaintBlock = null;
    }
    [ObservableProperty] public partial string RenderInfo { get; set; } = "";
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool HasReference { get; set; }

    public bool CanUndo => _undo.CanUndo;
    public bool CanRedo => _undo.CanRedo;
    public string UndoText => _undo.UndoDescription is { } d ? $"Undo {d}" : "Undo";
    public string RedoText => _undo.RedoDescription is { } d ? $"Redo {d}" : "Redo";

    /// <summary>True for formats and modes the PSD writer supports.</summary>
    public bool CanSave => Model.ColorMode is ColorMode.Rgb or ColorMode.Grayscale;

    partial void OnModeChanged(ViewMode value)
    {
        if (value == ViewMode.Difference && _diffBitmap is null && _displayedIsFull) _ = BuildDiffAsync();
        UpdateDisplay();
    }

    // ---- Editing ---------------------------------------------------------------------------------

    public void Apply(IEdit edit)
    {
        _undo.Push(edit);
        AfterChange(edit);
    }

    public void Undo()
    {
        if (IsTransforming)
        {
            CancelTransform(); // like Photoshop, undo inside Free Transform steps back out of it
            return;
        }
        if (_undo.Undo() is { } edit) AfterChange(edit);
    }

    public void Redo()
    {
        if (_undo.Redo() is { } edit) AfterChange(edit);
    }

    // ---- Painting ---------------------------------------------------------------------------------

    private PaintStroke? _stroke;
    private bool _baking;

    /// <summary>Starts a brush or eraser stroke on the selected layer; returns false (with a notice) if it cannot be painted.</summary>
    public bool BeginStroke(float x, float y, BrushSettings brush, RgbColor color, bool erase)
    {
        if (_baking) return false;
        if (EditMask && SelectedLayer?.Node is { } maskOwner && maskOwner.GetMask() is not null)
            return BeginMaskStroke(maskOwner, x, y, brush, color, erase); // see DocumentViewModel.Masks.cs
        string? problem = SelectedLayer?.Node switch
        {
            null => "Select a layer to paint on, or create a new one.",
            LayerGroup => "Groups cannot be painted on. Select a layer inside the group.",
            AdjustmentLayer => "Adjustment layers have no pixels to paint on.",
            { Visible: false } => $"\"{SelectedLayer!.Node.Name}\" is hidden.",
            var n when n.Tags.Contains("text") || n.Tags.Contains("smart-object") || n.Tags.Contains("fill") || n.Tags.Contains("shape")
                => $"\"{n.Name}\" is a {Kind(n)} layer. Photoshop redraws these from their own data, so painting on it would be lost. Rasterize it to paint on it, or paint on a new layer.",
            _ when Model.ColorMode is not (ColorMode.Rgb or ColorMode.Grayscale) => $"Painting in {Model.ColorMode} documents is not supported yet.",
            _ => null,
        };
        if (problem is not null)
        {
            Notice = problem;
            var node = SelectedLayer?.Node;
            PaintBlock = new PaintBlock(problem,
                CanRasterize: node is not null && RasterizeEdit.CanRasterize(node),
                CanShow: node is { Visible: false },
                CanNewLayer: true);
            return false;
        }

        Notice = "";
        PaintBlock = null;
        _stroke = new PaintStroke((PixelLayer)SelectedLayer!.Node, brush, color, erase, Model.Bounds, Selection);
        _stroke.StrokeTo(x, y);
        RequestRender();
        return true;

        static string Kind(LayerNode n) =>
            n.Tags.Contains("text") ? "text" : n.Tags.Contains("smart-object") ? "smart object" : n.Tags.Contains("fill") ? "fill" : "shape";
    }

    public void ContinueStroke(float x, float y)
    {
        if (_stroke is null) return;
        _stroke.StrokeTo(x, y);
        RequestRender();
    }

    /// <summary>
    /// Commits the stroke as one undoable edit. The new pixels are computed in the background; the live
    /// overlay keeps showing the stroke until they are ready, so nothing flickers.
    /// </summary>
    public async Task EndStrokeAsync()
    {
        if (_stroke is not { } stroke) return;
        if (stroke.Bounds.IsEmpty)
        {
            _stroke = null;
            return;
        }
        _baking = true;
        try
        {
            if (stroke.TargetsMask)
            {
                await CommitMaskStrokeAsync(stroke);
                return;
            }
            var doc = Model;
            var (pixels, bounds) = await Task.Run(() => StrokeBaker.Bake(stroke.Target, stroke, doc.ColorMode, doc.BitDepth));
            _stroke = null;
            Apply(new PixelsEdit(stroke.Target, pixels, bounds, stroke.Erase ? "Eraser" : "Brush"));
        }
        finally
        {
            _baking = false;
        }
    }

    // ---- Layers -----------------------------------------------------------------------------------

    /// <summary>Where new layers go: just above the selected layer, or at the top of the document.</summary>
    private (LayerGroup Parent, int Index) InsertionPoint()
    {
        if (SelectedLayer?.Node is { Parent: { } parent } node) return (parent, parent.IndexOf(node) + 1);
        return (Model.Root, Model.Root.Children.Count);
    }

    public void NewLayer()
    {
        var (parent, index) = InsertionPoint();
        var layer = new PixelLayer { Name = LayerFactory.NextName(Model, "Layer") };
        Apply(new InsertEdit(layer, parent, index, "New Layer"));
        Select(layer);
    }

    public void NewGroup()
    {
        var (parent, index) = InsertionPoint();
        var group = new LayerGroup { Name = LayerFactory.NextName(Model, "Group") };
        Apply(new InsertEdit(group, parent, index, "New Group"));
        Select(group);
    }

    public void DuplicateSelected()
    {
        if (SelectedLayer?.Node is not { Parent: { } parent } node) return;
        var copy = LayerFactory.Duplicate(node);
        Apply(new InsertEdit(copy, parent, parent.IndexOf(node) + 1, "Duplicate Layer"));
        Select(copy);
    }

    /// <summary>Moves <paramref name="node"/> to <paramref name="index"/> in <paramref name="parent"/> (drag and drop in the Layers panel).</summary>
    public void MoveLayer(LayerNode node, LayerGroup parent, int index)
    {
        if (node.Parent is not { } from) return;
        for (var g = parent; g is not null; g = g.Parent)
            if (ReferenceEquals(g, node)) return; // cannot drop a group into itself
        // Removing the node first shifts later siblings down by one.
        if (ReferenceEquals(from, parent) && from.IndexOf(node) < index) index--;
        if (ReferenceEquals(from, parent) && from.IndexOf(node) == index) return;
        Apply(new ReparentEdit(node, parent, index));
    }

    private void Select(LayerNode node) =>
        SelectedLayer = Layers.SelectMany(l => l.SelfAndDescendants()).FirstOrDefault(i => i.Node == node);

    public void MoveSelected(int dx, int dy)
    {
        if (SelectedLayer?.Node is { } node && node.Visible) Apply(new MoveEdit(node, dx, dy));
    }

    /// <summary>Moves the selected layer one step up (+1) or down (-1) within its group.</summary>
    public void Restack(int direction)
    {
        if (SelectedLayer?.Node is not { Parent: { } parent } node) return;
        int index = parent.IndexOf(node) + direction;
        if (index < 0 || index >= parent.Children.Count) return;
        Apply(new ReparentEdit(node, parent, index));
    }

    public void DeleteSelected()
    {
        if (SelectedLayer?.Node is { Parent: not null } node) Apply(new DeleteEdit(node));
    }

    private void AfterChange(IEdit edit)
    {
        IsModified = _undo.DistanceFromSave != 0;
        switch (edit)
        {
            case { ChangesStructure: true }:
                RebuildLayers();
                break;
            case MoveEdit:
                break; // nothing the panels show changes while dragging
            case SelectionEdit:
                break; // only the canvas outline changes
            case ILayerPropertyEdit p:
                Layers.SelectMany(l => l.SelfAndDescendants()).FirstOrDefault(i => i.Node == p.Node)?.Refresh();
                break;
            default:
                foreach (var item in Layers) item.Refresh();
                break;
        }
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(UndoText));
        OnPropertyChanged(nameof(RedoText));
        if (edit is not SelectionEdit) RequestRender();
    }

    /// <summary>Recreates the panel's layer items, keeping the selection and folder state.</summary>
    private void RebuildLayers()
    {
        var selected = SelectedLayer?.Node;
        Layers.Clear();
        foreach (var child in Model.Root.Children.Reverse())
            Layers.Add(new LayerItemViewModel(child, this));
        Rows.Clear();
        RefreshRows();
        SelectedLayer = selected is null ? null : Layers.SelectMany(l => l.SelfAndDescendants()).FirstOrDefault(i => i.Node == selected);
    }

    // ---- Rendering ----------------------------------------------------------------------------------
    //
    // Two independent lanes. The preview lane renders a screen-resolution copy after every change and never
    // waits for anything else. The full lane renders at full resolution once changes stop, is cancelled by
    // the next change, and only replaces the display if nothing changed meanwhile. Both lanes render a
    // snapshot synced on the UI thread, so background threads never read the document while it is edited.

    public void RequestRender()
    {
        _modelVersion++;
        _fullCancel?.Cancel();
        _ = RunPreviewLaneAsync();
    }

    /// <summary>
    /// Builds the downsampled layers for typical zoom levels in the background right after opening, so the
    /// first drag does not pay for them.
    /// </summary>
    private void WarmPreviews()
    {
        var doc = Model;
        int largest = Math.Max(doc.Width, doc.Height);
        _ = Task.Run(() =>
        {
            try
            {
                foreach (int factor in new[] { 2, 4, 8 })
                    if (largest / factor >= 256)
                        new PreviewDocument(doc, factor).Sync();
            }
            catch (InvalidOperationException)
            {
                // The user started editing the layer tree; the caches fill on demand instead.
            }
        });
    }

    public void SetViewZoom(double zoom)
    {
        int oldFactor = PreviewDocument.FactorForZoom(_viewZoom);
        _viewZoom = zoom;
        if (PreviewDocument.FactorForZoom(zoom) != oldFactor && !_displayedIsFull)
            ScheduleFullRender(TimeSpan.FromMilliseconds(150));
    }

    /// <summary>Initial render after opening: full resolution, so the Photoshop comparison is available.</summary>
    public Task RenderAsync()
    {
        ScheduleFullRender(TimeSpan.Zero);
        return _fullTask;
    }

    private async Task RunPreviewLaneAsync()
    {
        if (_previewRunning)
        {
            _previewPending = true;
            return;
        }
        _previewRunning = true;
        try
        {
            do
            {
                _previewPending = false;
                // Zoomed in to 100% or more, the preview is simply a full-resolution render.
                int factor = PreviewDocument.FactorForZoom(_viewZoom);
                if (_preview?.Factor != factor) _preview = new PreviewDocument(Model, factor);

                int version = _modelVersion;
                var sw = Stopwatch.StartNew();
                var proxy = _preview.Sync();
                var overlay = _preview.MapStroke(_stroke);
                var transform = PrepareTransform(_preview, full: false);
                double syncMs = sw.Elapsed.TotalMilliseconds;
                var (rgba, warnings, renderMs, convertMs) = await Task.Run(() =>
                {
                    var t = Stopwatch.StartNew();
                    transform?.Invoke(CancellationToken.None);
                    var r = _previewRenderer.Render(proxy, new RenderOptions { ActiveStroke = overlay });
                    double render = t.Elapsed.TotalMilliseconds;
                    var px = r.ToRgba8();
                    return (px, r.Warnings, render, t.Elapsed.TotalMilliseconds - render);
                });
                double beforeShow = sw.Elapsed.TotalMilliseconds;
                Show(rgba, proxy.Width, proxy.Height, warnings, version, full: factor == 1);
                LastFrameTimings = (syncMs, renderMs, convertMs, sw.Elapsed.TotalMilliseconds - beforeShow, sw.Elapsed.TotalMilliseconds);
                RenderInfo = factor == 1 ? $"full resolution · {sw.ElapsedMilliseconds} ms" : $"preview 1/{factor} · {sw.ElapsedMilliseconds} ms";
            }
            while (_previewPending);
        }
        catch (Exception ex)
        {
            RenderInfo = $"Render failed: {ex.Message}";
        }
        finally
        {
            _previewRunning = false;
        }
        ScheduleFullRender(TimeSpan.FromMilliseconds(400));
    }

    private void ScheduleFullRender(TimeSpan delay)
    {
        _fullCancel?.Cancel();
        var cts = _fullCancel = new CancellationTokenSource();
        var previous = _fullTask;
        _fullTask = RunFullAsync(previous, delay, cts.Token);
    }

    private async Task RunFullAsync(Task previous, TimeSpan delay, CancellationToken cancel)
    {
        Action? busyStop = null;
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancel);
            await previous.ConfigureAwait(true); // the snapshot must not be synced while an old render reads it
            cancel.ThrowIfCancellationRequested();

            int version = _modelVersion;
            var doc = Model;
            var snapshot = _snapshot.Sync();
            var strokeOverlay = _snapshot.MapStroke(_stroke);
            var transform = PrepareTransform(_snapshot, full: true);
            var reference = _reference;
            bool untouched = !_undo.CanUndo && !_undo.CanRedo;
            var sw = Stopwatch.StartNew();
            // Show the spinner only if this render is still running after half a second.
            var busyTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (t, _) =>
            {
                ((DispatcherTimer)t!).Stop();
                IsBusy = true;
            });
            busyTimer.Start();
            busyStop = busyTimer.Stop;

            var (rgba, warnings, report) = await Task.Run(() =>
            {
                transform?.Invoke(cancel);
                var result = _renderer.Render(snapshot, new RenderOptions { Cancellation = cancel, ActiveStroke = strokeOverlay });
                var pixels = result.ToRgba8(cancel);
                var fid = reference is null || !untouched
                    ? null
                    : FidelityReport.Compare(pixels, reference, doc.Width, doc.Height, flattenOverWhite: doc.Composite!.Alpha is null, includeDiff: false);
                return (pixels, result.Warnings, fid);
            }, cancel);

            if (cancel.IsCancellationRequested || version != _modelVersion) return;
            _lastRender = rgba;
            _lastRenderVersion = version; // lets the selection tools reuse this render
            Show(rgba, doc.Width, doc.Height, warnings, version, full: true);
            string against = Model.SourceData is PsdFile { CompositeIsFromPhotoshop: true } ? "Photoshop" : "the image stored in the file";
            RenderInfo = report is not null
                ? $"{report.MatchPercent:F2}% match with {against} · {sw.ElapsedMilliseconds} ms"
                : $"full resolution · {sw.ElapsedMilliseconds} ms";
            if (Mode == ViewMode.Difference) _ = BuildDiffAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            RenderInfo = $"Render failed: {ex.Message}";
        }
        finally
        {
            busyStop?.Invoke();
            IsBusy = false;
        }
    }

    private void Show(byte[] rgba, int width, int height, IReadOnlyList<string> warnings, int version, bool full)
    {
        // Never replace a newer image with an older one.
        if (version < _displayedVersion || (version == _displayedVersion && _displayedIsFull && !full)) return;
        _displayedVersion = version;
        _displayedIsFull = full;
        _renderBitmap = BitmapFactory.FromRgba(rgba, width, height);
        _diffBitmap = null;

        var distinct = warnings.Distinct().ToList();
        if (!distinct.SequenceEqual(Warnings))
        {
            Warnings.Clear();
            foreach (var w in distinct) Warnings.Add(w);
            OnPropertyChanged(nameof(WarningsText));
            OnPropertyChanged(nameof(HasWarnings));
        }
        UpdateDisplay();
        FrameDisplayed?.Invoke(full);
    }

    /// <summary>Breakdown of the last preview frame in milliseconds, for diagnostics.</summary>
    public (double Sync, double Render, double Convert, double Show, double Total) LastFrameTimings { get; private set; }

    /// <summary>Raised whenever a new image is shown (true for full resolution); used by the drag benchmark.</summary>
    public event Action<bool>? FrameDisplayed;

    /// <summary>
    /// Simulates a two-second brush stroke on a new layer with 120 Hz input and reports frames reaching the
    /// screen and the time to commit (STRAYTA_PAINTBENCH=1).
    /// </summary>
    public async Task RunPaintBenchmarkAsync()
    {
        NewLayer();
        int frames = 0;
        var times = new List<double>();
        var clock = Stopwatch.StartNew();
        double last = 0;
        void OnFrame(bool full)
        {
            frames++;
            times.Add(clock.Elapsed.TotalMilliseconds - last);
            last = clock.Elapsed.TotalMilliseconds;
        }
        FrameDisplayed += OnFrame;
        var brush = new BrushSettings(80, 0.5f, 1f);
        BeginStroke(Model.Width * 0.1f, Model.Height * 0.5f, brush, new RgbColor(0.9f, 0.2f, 0.3f), erase: false);
        for (int i = 1; i <= 240; i++)
        {
            float t = i / 240f;
            ContinueStroke(Model.Width * (0.1f + 0.8f * t), Model.Height * (0.5f + 0.3f * MathF.Sin(t * 12)));
            await Task.Delay(8);
        }
        double input = clock.Elapsed.TotalMilliseconds;
        var commit = Stopwatch.StartNew();
        await EndStrokeAsync();
        long commitMs = commit.ElapsedMilliseconds;
        FrameDisplayed -= OnFrame;
        times.Sort();
        Console.WriteLine($"PAINTBENCH frames={frames} fps={frames / (input / 1000):F1} median-frame={times[times.Count / 2]:F0}ms " +
                          $"p90={times[(int)(times.Count * 0.9)]:F0}ms commit={commitMs}ms factor={PreviewDocument.FactorForZoom(_viewZoom)}");
        while (CanUndo) Undo();
    }

    /// <summary>
    /// Simulates dragging the largest visible layer for about two seconds with 120 Hz input and reports how
    /// many frames reached the screen. Enabled with STRAYTA_DRAGBENCH=1.
    /// </summary>
    public async Task RunDragBenchmarkAsync()
    {
        var target = Layers.SelectMany(l => l.SelfAndDescendants())
            .Where(i => i.Node is PixelLayer { Visible: true, Pixels: not null })
            .OrderByDescending(i => ((PixelLayer)i.Node).Bounds.Width * ((PixelLayer)i.Node).Bounds.Height)
            .Skip(1).FirstOrDefault(); // skip the background
        if (target is null) return;
        SelectedLayer = target;

        int frames = 0;
        var frameTimes = new List<double>();
        var clock = Stopwatch.StartNew();
        double last = 0;
        var parts = new List<(double Sync, double Render, double Convert, double Show, double Total)>();
        void OnFrame(bool full)
        {
            if (full) return;
            frames++;
            parts.Add(LastFrameTimings);
            frameTimes.Add(clock.Elapsed.TotalMilliseconds - last);
            last = clock.Elapsed.TotalMilliseconds;
        }
        FrameDisplayed += OnFrame;
        int steps = 240;
        for (int i = 0; i < steps; i++)
        {
            MoveSelected(i % 120 < 60 ? 3 : -3, 0);
            await Task.Delay(8);
        }
        double inputMs = clock.Elapsed.TotalMilliseconds;
        var fullShown = new TaskCompletionSource<double>();
        void OnFull(bool full) { if (full) fullShown.TrySetResult(clock.Elapsed.TotalMilliseconds - inputMs); }
        FrameDisplayed += OnFull;
        var settle = await Task.WhenAny(fullShown.Task, Task.Delay(5000));
        FrameDisplayed -= OnFull;
        FrameDisplayed -= OnFrame;
        Console.WriteLine(settle == fullShown.Task
            ? $"DRAGBENCH full-resolution image {fullShown.Task.Result:F0} ms after the last move (includes the 400 ms idle delay); busy={IsBusy}"
            : $"DRAGBENCH full-resolution image did not appear within 5 s; busy={IsBusy}");

        frameTimes.Sort();
        double median = frameTimes.Count > 0 ? frameTimes[frameTimes.Count / 2] : double.NaN;
        double p90 = frameTimes.Count > 0 ? frameTimes[(int)(frameTimes.Count * 0.9)] : double.NaN;
        Console.WriteLine($"DRAGBENCH layer=\"{target.Name}\" input={inputMs:F0}ms frames={frames} fps={frames / (inputMs / 1000):F1} " +
                          $"median-frame={median:F0}ms p90={p90:F0}ms zoom={_viewZoom:F3} factor={PreviewDocument.FactorForZoom(_viewZoom)}");
        double Med(Func<(double Sync, double Render, double Convert, double Show, double Total), double> f)
        {
            var v = parts.Skip(1).Select(f).OrderBy(x => x).ToList();
            return v.Count == 0 ? double.NaN : v[v.Count / 2];
        }
        Console.WriteLine($"DRAGBENCH parts (median ms): sync={Med(p => p.Sync):F1} render={Med(p => p.Render):F1} " +
                          $"convert={Med(p => p.Convert):F1} show={Med(p => p.Show):F1} total={Med(p => p.Total):F1}");
        while (CanUndo) Undo();
    }

    private async Task BuildDiffAsync()
    {
        if (_reference is not { } reference || _lastRender is not { } rendered) return;
        var doc = Model;
        var report = await Task.Run(() => FidelityReport.Compare(rendered, reference, doc.Width, doc.Height,
            flattenOverWhite: doc.Composite!.Alpha is null, includeDiff: true));
        if (!ReferenceEquals(rendered, _lastRender)) return;
        _diffBitmap = BitmapFactory.FromRgba(report.DiffRgba, doc.Width, doc.Height);
        UpdateDisplay();
    }

    private void UpdateDisplay() => Display = Mode switch
    {
        ViewMode.Photoshop => _referenceBitmap ?? _renderBitmap,
        ViewMode.Difference => _diffBitmap ?? _renderBitmap,
        _ => _renderBitmap ?? _referenceBitmap,
    };

    // ---- Saving ----------------------------------------------------------------------------------

    /// <summary>Renders a fresh composite (so other apps see the edits) and writes the file safely.</summary>
    public async Task SaveAsync(string path)
    {
        if (!CanSave) throw new NotSupportedException($"Saving {Model.ColorMode} documents is not supported yet.");
        if (IsTransforming) await CommitTransformAsync();
        IsBusy = true;
        try
        {
            var doc = Model;
            await Task.Run(() =>
            {
                using var renderer = new CpuRenderer();
                var composite = renderer.Render(doc).ToRaster(doc.ColorMode, doc.BitDepth);
                PsdWriter.Save(doc, path, new PsdWriteOptions { Composite = composite });
            });
            FilePath = path;
            Title = System.IO.Path.GetFileName(path);
            _undo.MarkSaved();
            IsModified = false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Asks the document's view to change zoom ("fit" or "actual").</summary>
    public event Action<string>? ZoomRequested;

    public void RequestZoom(string kind) => ZoomRequested?.Invoke(kind);

    // ---- Closing ---------------------------------------------------------------------------------

    /// <summary>Set by the editor: asks the user about unsaved changes; returns true if closing may proceed.</summary>
    public Func<DocumentViewModel, Task<bool>>? ConfirmClose { get; set; }

    private bool _closeConfirmed;

    public override bool OnClose()
    {
        if (!IsModified || _closeConfirmed || ConfirmClose is null)
        {
            _fullCancel?.Cancel();
            _renderer.Dispose();
            _previewRenderer.Dispose();
            return true;
        }

        // Dock's close hook is synchronous: cancel now, ask, then close again if the user agrees.
        _ = Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (!await ConfirmClose(this)) return;
            _closeConfirmed = true;
            Factory?.CloseDockable(this);
        });
        return false;
    }
}

/// <summary>Why the brush cannot paint on the selected layer, and which fixes apply.</summary>
public sealed record PaintBlock(string Message, bool CanRasterize, bool CanShow, bool CanNewLayer);
