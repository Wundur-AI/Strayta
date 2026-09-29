using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Strayta.Core;
using Strayta.Core.Paths;
using Strayta.Editor.Controls;
using Strayta.Editor.Editing;

namespace Strayta.Editor.ViewModels;

/// <summary>What the canvas draws for the path tools: the edited path with its anchors, and a preview being drawn.</summary>
/// <param name="Path">The path the tools act on (a shape layer's outline, the Work Path or a saved path), or null.</param>
/// <param name="Selected">Its selected anchors (filled squares, with their direction handles showing).</param>
/// <param name="ShowAnchors">Anchors show with the pen and Direct Selection; Path Selection shows them all selected.</param>
/// <param name="Preview">A shape being dragged or the pen's rubber band.</param>
/// <param name="PreviewFill">The preview's fill color, or null to draw only its outline.</param>
/// <param name="PreviewStrokeWidth">The preview's stroke width in document pixels (0: none).</param>
public sealed record PathOverlay(VectorPath? Path, IReadOnlySet<KnotRef> Selected, bool ShowAnchors, VectorPath? Preview = null,
    RgbColor? PreviewFill = null, double PreviewStrokeWidth = 0, RgbColor? PreviewStrokeColor = null);

/// <summary>Which path the pen and selection tools edit.</summary>
public enum PathTargetKind
{
    None,
    Shape,
    Document,
}

/// <summary>
/// The shape tools, the Pen group and the path selection tools on a document. They act on the selected shape layer's
/// outline, or on the path selected in the Paths panel (the Work Path or a saved path). Drags show live (the overlay
/// follows every move; a shape layer's pixels are redrawn at background priority, so moves coalesce) and each gesture
/// is one undo step, named as Photoshop names it. Shape layers keep their live shapes: moving a whole component moves
/// its live shape's box, editing its anchors turns it into an ordinary path, as in Photoshop.
/// </summary>
public sealed partial class DocumentViewModel : IPathTools
{
    /// <summary>What the canvas draws for the path tools (null: nothing).</summary>
    [ObservableProperty] public partial PathOverlay? PathOverlay { get; private set; }

    /// <summary>The Paths panel's selected document path (index in <see cref="DocumentPathsEdit.Read"/>), or -1.</summary>
    [ObservableProperty] public partial int SelectedPathIndex { get; set; } = -1;

    /// <summary>True when the Paths panel's selection (not the selected layer's shape) is what the tools edit.</summary>
    private bool _documentPathFocus;

    private readonly HashSet<KnotRef> _selectedKnots = [];

    // A gesture in progress: the path as it looks now (not yet recorded) and, for shape layers, the state to restore.
    private VectorPath? _livePath;
    private VectorPath? _previewPath;
    private ShapeEdit.State? _dragBefore;
    private PixelLayer? _dragLayer;

    partial void OnSelectedPathIndexChanged(int value)
    {
        _documentPathFocus = value >= 0;
        _selectedKnots.Clear();
        _penSubpath = -1;
        RefreshPathOverlay();
    }

    /// <summary>Selecting a layer makes the tools edit its shape again (DocumentViewModel.Masks.cs calls this).</summary>
    private void OnPathLayerSelectionChanged(LayerItemViewModel? oldValue, LayerItemViewModel? newValue)
    {
        if (ReferenceEquals(oldValue?.Node, newValue?.Node)) return;
        FinishPen();
        _selectedKnots.Clear();
        if (ShapeLayers.IsShape(newValue?.Node))
        {
            _documentPathFocus = false;
            if (SelectedPathIndex >= 0) SelectedPathIndex = -1;
            if (Editor.IsShapeOrPenTool && ShapeLayers.Read(Model, newValue!.Node) is { } data) Editor.Shapes.LoadFrom(data);
        }
        RefreshPathOverlay();
        PathsChanged?.Invoke();
    }

    /// <summary>Raised when the document's paths or the path target change (the Paths panel listens).</summary>
    public event Action? PathsChanged;

    // ---- Target -------------------------------------------------------------------------------------------

    /// <summary>The path the tools act on now.</summary>
    public (PathTargetKind Kind, PixelLayer? Layer, int Index, VectorPath? Path) PathTarget()
    {
        var paths = DocumentPathsEdit.Read(Model);
        bool docValid = SelectedPathIndex >= 0 && SelectedPathIndex < paths.Count;
        if (docValid && _documentPathFocus) return (PathTargetKind.Document, null, SelectedPathIndex, paths[SelectedPathIndex].Path);
        if (SelectedLayer?.Node is PixelLayer layer && ShapeLayers.Read(Model, layer) is { } data)
            return (PathTargetKind.Shape, layer, -1, data.Path);
        if (docValid) return (PathTargetKind.Document, null, SelectedPathIndex, paths[SelectedPathIndex].Path);
        return (PathTargetKind.None, null, -1, null);
    }

    /// <summary>Redraws the overlay after a tool, selection or history change.</summary>
    public void RefreshPathOverlay()
    {
        if (!Editor.IsShapeOrPenTool && !Editor.IsPathSelectTool)
        {
            PathOverlay = null;
            return;
        }
        var target = PathTarget();
        var path = _livePath ?? target.Path;
        if (path is not null) _selectedKnots.RemoveWhere(r => r.Subpath >= path.Subpaths.Count || r.Knot >= path.Subpaths[r.Subpath].Knots.Count);
        bool anchors = Editor.Tool is CanvasTool.Pen or CanvasTool.AddAnchor or CanvasTool.DeleteAnchor or CanvasTool.ConvertPoint or CanvasTool.DirectSelect
            || Editor.Tool == CanvasTool.PathSelect;
        var (fill, strokeWidth, strokeColor) = PreviewStyle();
        PathOverlay = new PathOverlay(path, new HashSet<KnotRef>(_selectedKnots), anchors, _previewPath,
            _previewPath is not null && _previewFilled ? fill : null, _previewPath is not null && _previewFilled ? strokeWidth : 0, strokeColor);
    }

    private bool _previewFilled;

    private (RgbColor? Fill, double StrokeWidth, RgbColor? StrokeColor) PreviewStyle()
    {
        var o = Editor.Shapes;
        if (o.Mode == ShapeToolMode.Path) return (null, 0, null);
        RgbColor? fill = o.FillKindIndex switch
        {
            (int)ShapePaintKind.Color => ShapeOptions.ToRgb(o.FillColor),
            (int)ShapePaintKind.None => null,
            _ => new RgbColor(0.6f, 0.6f, 0.6f),
        };
        var stroke = o.Stroke;
        RgbColor? sc = stroke.Content is ShapeContent.SolidColor s ? s.Color : new RgbColor(0.3f, 0.3f, 0.3f);
        return (fill, stroke.Enabled ? stroke.Width : 0, sc);
    }

    /// <summary>
    /// Records <paramref name="path"/> as the target's new path, as one undo step. <paramref name="live"/> adjusts a shape
    /// layer's live shapes (moved boxes, invalidated ones).
    /// </summary>
    private void CommitPath(VectorPath path, string description, Func<ShapeLayerData, ShapeLayerData>? live = null)
    {
        var target = PathTarget();
        _livePath = null;
        switch (target.Kind)
        {
            case PathTargetKind.Shape when target.Layer is { } layer && ShapeLayers.Read(Model, layer) is var data && data is not null:
            {
                var before = _dragBefore is { } b && ReferenceEquals(_dragLayer, layer) ? b : ShapeEdit.State.Of(layer);
                if (_dragBefore is not null && ReferenceEquals(_dragLayer, layer)) ShapeEdit.Set(layer, before); // back to the recorded state first
                var original = ShapeLayers.Read(Model, layer)!;
                var next = original with { Path = path };
                if (live is not null) next = live(next);
                Apply(new ShapeEdit(layer, before, ShapeLayers.StateFor(Model, layer, next), description));
                break;
            }
            case PathTargetKind.Document:
            {
                var paths = DocumentPathsEdit.Read(Model);
                paths[target.Index] = paths[target.Index] with { Path = path };
                Apply(DocumentPathsEdit.To(Model, paths, description));
                break;
            }
        }
        _dragBefore = null;
        _dragLayer = null;
        RefreshPathOverlay();
        PathsChanged?.Invoke();
    }

    /// <summary>Shows a path change without recording it: the overlay, and a shape layer's pixels at background priority.</summary>
    private void PreviewPathChange(VectorPath path, Func<ShapeLayerData, ShapeLayerData>? live = null)
    {
        _livePath = path;
        RefreshPathOverlay();
        var target = PathTarget();
        if (target.Kind != PathTargetKind.Shape || target.Layer is not { } layer) return;
        if (_dragBefore is null || !ReferenceEquals(_dragLayer, layer))
        {
            _dragBefore = ShapeEdit.State.Of(layer);
            _dragLayer = layer;
        }
        _pendingShapePreview = (layer, path, live);
        if (_shapePreviewQueued) return;
        _shapePreviewQueued = true;
        Dispatcher.UIThread.Post(RenderShapePreview, DispatcherPriority.Background);
    }

    private (PixelLayer Layer, VectorPath Path, Func<ShapeLayerData, ShapeLayerData>? Live)? _pendingShapePreview;
    private bool _shapePreviewQueued;

    /// <summary>Time the last live shape redraw took (for the self-test).</summary>
    public double LastShapePreviewMs { get; private set; }

    private void RenderShapePreview()
    {
        _shapePreviewQueued = false;
        if (_pendingShapePreview is not var (layer, path, live) || _dragBefore is not { } before || !ReferenceEquals(_dragLayer, layer)) return;
        _pendingShapePreview = null;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        // Read from the recorded state, so the preview never builds on an earlier preview.
        var current = layer.SourceData;
        layer.SourceData = before.Source;
        var data = ShapeLayers.Read(Model, layer);
        layer.SourceData = current;
        if (data is null) return;
        data = data with { Path = path };
        if (live is not null) data = live(data);
        var render = ShapeLayers.Render(Model, data);
        layer.Pixels = render.Pixels;
        layer.Bounds = render.Pixels is null ? PixelRect.Empty : render.Bounds;
        layer.Mask = before.Mask is { AppliedToPixels: true } ? null : before.Mask;
        LastShapePreviewMs = clock.Elapsed.TotalMilliseconds;
        RequestRender();
    }

    /// <summary>Drops a gesture that was not recorded, putting a previewed shape layer back.</summary>
    private void CancelPathGesture()
    {
        if (_dragBefore is { } b && _dragLayer is { } layer)
        {
            ShapeEdit.Set(layer, b);
            RequestRender();
        }
        _dragBefore = null;
        _dragLayer = null;
        _livePath = null;
        _previewPath = null;
        _pendingShapePreview = null;
        RefreshPathOverlay();
    }

    // ---- Shape tools --------------------------------------------------------------------------------------

    private PathPoint _shapeStart;
    private LiveShape? _shapeLive;

    /// <summary>A shape tool drag starts at a document point.</summary>
    public void BeginShapeDrag(double x, double y)
    {
        FinishPen();
        _shapeStart = new PathPoint(x, y);
        _shapeLive = null;
    }

    /// <summary>
    /// The drag reaches (<paramref name="x"/>, <paramref name="y"/>) with <paramref name="start"/> as its (possibly
    /// moved: Space) start; Shift keeps proportions (lines: 45° steps), Option draws from the centre.
    /// </summary>
    public void UpdateShapeDrag(double startX, double startY, double x, double y, bool shift, bool alt)
    {
        _shapeStart = new PathPoint(startX, startY);
        var end = new PathPoint(x, y);
        var o = Editor.Shapes;
        if (Editor.Tool == CanvasTool.Line)
        {
            if (shift) end = PathEditing.Constrain45(_shapeStart, end);
            var a = alt ? _shapeStart * 2 - end : _shapeStart;
            _shapeLive = o.LiveShapeFor(CanvasTool.Line, Math.Min(a.X, end.X), Math.Min(a.Y, end.Y), Math.Max(a.X, end.X), Math.Max(a.Y, end.Y), a, end);
        }
        else
        {
            var (l, t, r, b) = PathEditing.DragBox(_shapeStart, end, shift, alt);
            _shapeLive = o.LiveShapeFor(Editor.Tool, l, t, r, b, default, default);
        }
        _previewPath = new VectorPath(_shapeLive.Build());
        _previewFilled = true;
        ShowSize(_shapeLive);
        RefreshPathOverlay();
    }

    private void ShowSize(LiveShape s)
    {
        var o = Editor.Shapes;
        o.ShowSizeWithoutResizing(Math.Round(s.Width, 2), Math.Round(s.Height, 2));
    }

    /// <summary>
    /// The drag ends: a new shape layer (or a component added to the selected shape with the chosen path operation),
    /// or in Path mode a component of the Work Path. A click without a drag makes a shape of the options' W × H there.
    /// </summary>
    public void EndShapeDrag()
    {
        var o = Editor.Shapes;
        var live = _shapeLive;
        _previewPath = null;
        _shapeLive = null;
        if (live is null || (Editor.Tool != CanvasTool.Line && (live.Width < 1 || live.Height < 1)) || (Editor.Tool == CanvasTool.Line && PathPoint.Distance(live.LineStart, live.LineEnd) < 1))
        {
            double w = Math.Max(1, o.Width), h = Math.Max(1, o.Height);
            live = Editor.Tool == CanvasTool.Line
                ? o.LiveShapeFor(CanvasTool.Line, _shapeStart.X, _shapeStart.Y, _shapeStart.X + w, _shapeStart.Y, _shapeStart, new PathPoint(_shapeStart.X + w, _shapeStart.Y))
                : o.LiveShapeFor(Editor.Tool, _shapeStart.X, _shapeStart.Y, _shapeStart.X + w, _shapeStart.Y + h, default, default);
        }
        AddShape(live);
        RefreshPathOverlay();
    }

    /// <summary>Adds a drawn shape where the options say: a new layer, the selected shape layer, or a path.</summary>
    public void AddShape(LiveShape live)
    {
        var o = Editor.Shapes;
        var op = o.Operation;
        if (o.Mode == ShapeToolMode.Path)
        {
            AddToDocumentPath(live.Build(op ?? PathOperation.Combine), Editor.Tool.ToString());
            return;
        }
        if (op is { } operation && SelectedLayer?.Node is PixelLayer layer && ShapeLayers.Read(Model, layer) is { } data)
        {
            int index = data.LiveShapes.Count == 0 ? data.Path.Subpaths.Select(s => s.OriginIndex).DefaultIfEmpty(-1).Max() + 1 : data.LiveShapes.Max(s => s.Index) + 1;
            var added = live with { Index = index };
            var next = data with { Path = data.Path.Append(added.Build(operation)), LiveShapes = [.. data.LiveShapes, added] };
            Apply(ShapeLayers.Edit(Model, layer, next, "Add Shape"));
            RefreshPathOverlay();
            return;
        }
        var shape = o.NewShape(new VectorPath(live.Build()) { InitialFillAll = false }, [live with { Index = 0 }]);
        var created = ShapeLayers.Create(Model, shape, LayerFactory.NextName(Model, ShapeLayers.BaseName(live.Kind)));
        var (parent, at) = InsertionPoint();
        Apply(new InsertEdit(created, parent, at, "New Shape Layer"));
        Select(created);
    }

    /// <summary>Adds components to the path the Paths panel has selected, or to the Work Path (made when there is none).</summary>
    private void AddToDocumentPath(IReadOnlyList<Subpath> subpaths, string what)
    {
        var paths = DocumentPathsEdit.Read(Model);
        int index = SelectedPathIndex >= 0 && SelectedPathIndex < paths.Count ? SelectedPathIndex : paths.FindIndex(p => p.Kind == DocumentPathKind.Work);
        string description;
        if (index < 0)
        {
            paths.Insert(0, new DocumentPath("Work Path", new VectorPath(subpaths) { InitialFillAll = false }, DocumentPathKind.Work));
            index = 0;
            description = "New Work Path";
        }
        else
        {
            paths[index] = paths[index] with { Path = paths[index].Path.Append(subpaths) };
            description = $"{what} Tool";
        }
        Apply(DocumentPathsEdit.To(Model, paths, description));
        _documentPathFocus = true;
        SelectedPathIndex = index;
        PathsChanged?.Invoke();
        RefreshPathOverlay();
    }

    /// <summary>Restyles the selected shape layer (the options bar's fill and stroke); a run of the same change merges.</summary>
    public void RestyleSelectedShape(Func<ShapeLayerData, ShapeLayerData> change, string description)
    {
        if (SelectedLayer?.Node is not PixelLayer layer || ShapeLayers.Read(Model, layer) is not { } data) return;
        var next = change(data);
        if (next == data) return;
        Apply(ShapeLayers.Edit(Model, layer, next, description));
    }

    /// <summary>The options bar's W / H: scales the selected shape about its top-left corner.</summary>
    public void ResizeSelectedShape(double width, double height)
    {
        if (SelectedLayer?.Node is not PixelLayer layer || ShapeLayers.Read(Model, layer) is not { } data || data.Path.CurveBounds() is not var (l, t, r, b)) return;
        double w = r - l, h = b - t;
        if (w <= 0 || h <= 0 || width <= 0 || height <= 0 || (Math.Abs(w - width) < 0.01 && Math.Abs(h - height) < 0.01)) return;
        double sx = width / w, sy = height / h;
        Apply(ShapeLayers.Edit(Model, layer, data.Map(p => new PathPoint(l + (p.X - l) * sx, t + (p.Y - t) * sy)), "Change Shape Size"));
    }

    /// <summary>The Properties panel's live shape settings: replaces one live shape and redraws its outline.</summary>
    public void EditLiveShape(PixelLayer layer, int liveIndex, LiveShape shape, string description)
    {
        if (ShapeLayers.Read(Model, layer) is not { } data || liveIndex < 0 || liveIndex >= data.LiveShapes.Count) return;
        var old = data.LiveShapes[liveIndex];
        // The subpaths this live shape drew are replaced by the new outline, with the same operation.
        var subs = data.Path.Subpaths.ToList();
        int first = subs.FindIndex(s => s.OriginIndex == old.Index);
        if (first < 0) return;
        var op = subs[first].Operation;
        subs.RemoveAll(s => s.OriginIndex == old.Index);
        var built = shape.Build(op);
        subs.InsertRange(Math.Min(first, subs.Count), built);
        var lives = data.LiveShapes.ToArray();
        lives[liveIndex] = shape with { Invalidated = false, SourceData = old.SourceData, Index = old.Index };
        Apply(ShapeLayers.Edit(Model, layer, data with { Path = data.Path with { Subpaths = subs }, LiveShapes = lives }, description));
        RefreshPathOverlay();
    }

    // ---- Pen ------------------------------------------------------------------------------------------------

    /// <summary>The subpath the pen is adding to (-1: none).</summary>
    private int _penSubpath = -1;
    private KnotRef _penKnot;
    private bool _penDragging, _penClosing;
    private PathPoint _penPress;
    private string _penDescription = "";

    /// <summary>True while the pen is extending an open subpath (the rubber band shows).</summary>
    public bool IsPenDrawing => _penSubpath >= 0;

    /// <summary>
    /// A pen click at a document point: the first click starts a path (a new shape layer, a component of the selected
    /// shape, or the Work Path), later ones add anchors; a click on the first anchor closes the path. Shift keeps the
    /// new segment at 45° steps. The press starts a drag that pulls out the new anchor's handles.
    /// </summary>
    public void PenPress(double x, double y, bool shift, double tolerance)
    {
        var p = new PathPoint(x, y);
        var target = PathTarget();
        var o = Editor.Shapes;
        if (_penSubpath >= 0 && target.Path is { } path && _penSubpath < path.Subpaths.Count && !path.Subpaths[_penSubpath].Closed)
        {
            var sub = path.Subpaths[_penSubpath];
            var first = sub.Knots[0];
            if (sub.Knots.Count >= 2 && PathPoint.Distance(first.Anchor, p) <= tolerance)
            {
                // Close: the first anchor's handles can still be pulled out by dragging.
                var closed = path with { Subpaths = path.Subpaths.Select((s, i) => i == _penSubpath ? s with { Closed = true } : s).ToArray() };
                _penKnot = new KnotRef(_penSubpath, 0);
                _penClosing = true;
                BeginPenDrag(closed, p, "Close Path");
                return;
            }
            if (shift) p = PathEditing.Constrain45(sub.Knots[^1].Anchor, p);
            var knots = sub.Knots.Append(PathKnot.Corner(p)).ToArray();
            var extended = path with { Subpaths = path.Subpaths.Select((s, i) => i == _penSubpath ? s with { Knots = knots } : s).ToArray() };
            _penKnot = new KnotRef(_penSubpath, knots.Length - 1);
            _penClosing = false;
            BeginPenDrag(extended, p, "Add Anchor Point");
            return;
        }

        // A click on an open end of the target continues that subpath.
        if (target.Path is { } existing)
            for (int s = 0; s < existing.Subpaths.Count; s++)
            {
                var sub = existing.Subpaths[s];
                if (sub.Closed || sub.Knots.Count == 0) continue;
                if (PathPoint.Distance(sub.Knots[^1].Anchor, p) <= tolerance)
                {
                    _penSubpath = s;
                    RefreshPathOverlay();
                    return;
                }
                if (PathPoint.Distance(sub.Knots[0].Anchor, p) <= tolerance)
                {
                    var reversed = existing with { Subpaths = existing.Subpaths.Select((x, i) => i == s ? ShapeGeometry.Reverse(x) : x).ToArray() };
                    CommitPath(reversed, "Reverse Path");
                    _penSubpath = s;
                    RefreshPathOverlay();
                    return;
                }
            }

        // A new subpath.
        var op = o.Operation ?? PathOperation.Combine;
        var start = new Subpath([PathKnot.Corner(p)], Closed: false, op);
        if (o.Mode == ShapeToolMode.Shape && (o.Operation is null || target.Kind != PathTargetKind.Shape))
        {
            // A new shape layer that the following clicks draw into.
            var data = o.NewShape(new VectorPath([start with { Operation = PathOperation.Combine }]) { InitialFillAll = false }, []);
            var layer = ShapeLayers.Create(Model, data, LayerFactory.NextName(Model, "Shape"));
            var (parent, at) = InsertionPoint();
            Apply(new InsertEdit(layer, parent, at, "New Shape Layer"));
            Select(layer);
            _documentPathFocus = false;
            _penSubpath = 0;
            _penKnot = new KnotRef(0, 0);
            _penClosing = false;
            BeginPenDrag(ShapeLayers.Read(Model, layer)!.Path, p, "Add Anchor Point");
            return;
        }
        if (target.Kind == PathTargetKind.None || (o.Mode == ShapeToolMode.Path && target.Kind == PathTargetKind.Shape && !_documentPathFocus))
        {
            AddToDocumentPath([start], "Pen");
            _penSubpath = PathTarget().Path!.Subpaths.Count - 1;
            _penKnot = new KnotRef(_penSubpath, 0);
            _penClosing = false;
            BeginPenDrag(PathTarget().Path!, p, "Add Anchor Point");
            return;
        }
        var appended = target.Path!.Append([start]);
        _penSubpath = appended.Subpaths.Count - 1;
        _penKnot = new KnotRef(_penSubpath, 0);
        _penClosing = false;
        BeginPenDrag(appended, p, "Add Anchor Point");
    }

    private void BeginPenDrag(VectorPath path, PathPoint p, string description)
    {
        _penPress = p;
        _penDragging = true;
        _penDescription = description;
        _selectedKnots.Clear();
        _selectedKnots.Add(_penKnot);
        _previewPath = null;
        PreviewPathChange(path);
    }

    /// <summary>
    /// Dragging after a pen click pulls out the anchor's handles: the leaving one follows the pointer, the arriving one
    /// mirrors it (a smooth point). Option moves only the leaving handle (a corner with a handle); Shift keeps 45° steps.
    /// </summary>
    public void PenDrag(double x, double y, bool shift, bool alt)
    {
        if (!_penDragging || _livePath is not { } path) return;
        var p = new PathPoint(x, y);
        var k = path.Subpaths[_penKnot.Subpath].Knots[_penKnot.Knot];
        if (shift) p = PathEditing.Constrain45(k.Anchor, p);
        if (PathPoint.Distance(p, k.Anchor) < 0.5 && !alt)
        {
            PreviewPathChange(Replace(path, _penKnot, PathKnot.Corner(k.Anchor) with { In = _penClosing ? k.In : k.Anchor }));
            return;
        }
        PathKnot next = alt ? k with { Out = p, Linked = false } : _penClosing
            // Closing: the drag shapes the curve arriving at the first point (its in handle), mirrored out.
            ? new PathKnot(k.Anchor * 2 - p, k.Anchor, p, true)
            : PathKnot.Smooth(k.Anchor, p);
        PreviewPathChange(Replace(path, _penKnot, next));
    }

    private static VectorPath Replace(VectorPath path, KnotRef r, PathKnot k) =>
        path with { Subpaths = path.Subpaths.Select((s, i) => i == r.Subpath ? s with { Knots = s.Knots.Select((x, j) => j == r.Knot ? k : x).ToArray() } : s).ToArray() };

    /// <summary>The pen's press ends: the new anchor (and its handles) become one undo step.</summary>
    public void PenRelease()
    {
        if (!_penDragging || _livePath is not { } path) return;
        _penDragging = false;
        CommitPath(path, _penDescription);
        if (_penClosing) _penSubpath = -1;
        _penClosing = false;
        RefreshPathOverlay();
    }

    /// <summary>The rubber band: where the next segment would go, from the last anchor to the pointer.</summary>
    public void PenHover(double x, double y, bool shift)
    {
        if (_penSubpath < 0 || _penDragging || PathTarget().Path is not { } path || _penSubpath >= path.Subpaths.Count)
        {
            if (_previewPath is not null)
            {
                _previewPath = null;
                RefreshPathOverlay();
            }
            return;
        }
        var last = path.Subpaths[_penSubpath].Knots[^1];
        var p = new PathPoint(x, y);
        if (shift) p = PathEditing.Constrain45(last.Anchor, p);
        _previewPath = new VectorPath([new Subpath([last with { In = last.Anchor }, PathKnot.Corner(p)], Closed: false)]);
        _previewFilled = false;
        RefreshPathOverlay();
    }

    /// <summary>Enter, Esc, another tool: the path being drawn stays as it is, open.</summary>
    public void FinishPen()
    {
        if (_penDragging && _livePath is { } path) CommitPath(path, _penDescription);
        _penDragging = false;
        _penSubpath = -1;
        _previewPath = null;
        RefreshPathOverlay();
    }

    // ---- Add / Delete / Convert Point -------------------------------------------------------------------

    /// <summary>Add Anchor Point: a click on a segment adds an anchor there without changing the curve.</summary>
    public bool AddAnchorAt(double x, double y, double tolerance)
    {
        if (PathTarget().Path is not { } path || PathEditing.SegmentAt(path, new PathPoint(x, y), tolerance) is not { } seg) return false;
        var (next, added) = PathEditing.InsertKnot(path, seg.Subpath, seg.Segment, seg.T);
        _selectedKnots.Clear();
        _selectedKnots.Add(added);
        CommitPath(next, "Add Anchor Point", InvalidateLive(path, [seg.Subpath]));
        return true;
    }

    /// <summary>Delete Anchor Point: a click on an anchor removes it.</summary>
    public bool DeleteAnchorAt(double x, double y, double tolerance)
    {
        if (PathTarget().Path is not { } path) return false;
        var hit = PathEditing.HitTest(path, new PathPoint(x, y), tolerance);
        if (hit.Part != PathPart.Anchor) return false;
        _selectedKnots.Clear();
        CommitPath(PathEditing.DeleteKnots(path, [hit.Knot]), "Delete Anchor Point", InvalidateLive(path, [hit.Knot.Subpath]));
        return true;
    }

    private enum PathDrag { None, Knots, Subpaths, Handle, Convert, Marquee }

    private PathDrag _pathDrag;
    private PathPoint _pathPress;
    private VectorPath? _pathAtPress;
    private KnotRef _dragKnot;
    private bool _dragOutHandle, _dragBreak, _pathMoved;
    private List<int> _dragSubpaths = [];

    /// <summary>
    /// Convert Point: a press on a handle drags it on its own (a corner with handles); on an anchor, a drag pulls out
    /// new smooth handles and a click without dragging makes it a corner.
    /// </summary>
    public bool ConvertPress(double x, double y, double tolerance)
    {
        if (PathTarget().Path is not { } path) return false;
        var p = new PathPoint(x, y);
        var hit = PathEditing.HitTest(path, p, tolerance, AllKnots(path));
        if (hit.Part == PathPart.None || hit.Part == PathPart.Segment) return false;
        _pathAtPress = path;
        _pathPress = p;
        _dragKnot = hit.Knot;
        _pathMoved = false;
        _selectedKnots.Clear();
        _selectedKnots.Add(hit.Knot);
        if (hit.Part is PathPart.InHandle or PathPart.OutHandle)
        {
            _pathDrag = PathDrag.Handle;
            _dragOutHandle = hit.Part == PathPart.OutHandle;
            _dragBreak = true;
        }
        else _pathDrag = PathDrag.Convert;
        RefreshPathOverlay();
        return true;
    }

    private static HashSet<KnotRef> AllKnots(VectorPath path) => PathEditing.KnotsOf(path, Enumerable.Range(0, path.Subpaths.Count)).ToHashSet();

    // ---- Path Selection / Direct Selection -------------------------------------------------------------

    /// <summary>
    /// A press with Path Selection (whole components) or Direct Selection (anchors and handles): selects what is under
    /// the pointer (Shift adds or toggles; on another shape layer, Path Selection selects that layer) and starts
    /// moving it; Option with Path Selection moves a copy. On nothing, a marquee starts (the canvas draws it).
    /// Returns false when the press started a marquee.
    /// </summary>
    public bool SelectPress(double x, double y, bool shift, bool alt, bool direct, double tolerance)
    {
        var p = new PathPoint(x, y);
        var path = PathTarget().Path;
        var handles = direct && path is not null ? HandleOwners(path) : null;
        var hit = path is null ? PathHit.None : PathEditing.HitTest(path, p, tolerance, handles);
        if (hit.Part == PathPart.None && !direct && OtherShapeAt(p, tolerance) is { } other)
        {
            // Path Selection picks up another shape layer's path, as in Photoshop.
            Select(other.Layer);
            _documentPathFocus = false;
            path = other.Path;
            hit = PathEditing.HitTest(path, p, tolerance);
        }
        _pathPress = p;
        _pathAtPress = path;
        _pathMoved = false;
        if (path is null || hit.Part == PathPart.None)
        {
            if (!shift) _selectedKnots.Clear();
            _pathDrag = PathDrag.Marquee;
            RefreshPathOverlay();
            return false;
        }
        if (direct)
        {
            switch (hit.Part)
            {
                case PathPart.InHandle or PathPart.OutHandle:
                    _pathDrag = PathDrag.Handle;
                    _dragKnot = hit.Knot;
                    _dragOutHandle = hit.Part == PathPart.OutHandle;
                    _dragBreak = alt;
                    break;
                case PathPart.Anchor:
                    if (shift && _selectedKnots.Contains(hit.Knot))
                    {
                        _selectedKnots.Remove(hit.Knot);
                        _pathDrag = PathDrag.None;
                        break;
                    }
                    if (!shift && !_selectedKnots.Contains(hit.Knot)) _selectedKnots.Clear();
                    _selectedKnots.Add(hit.Knot);
                    _pathDrag = PathDrag.Knots;
                    break;
                default: // a segment: its two anchors
                    if (!shift) _selectedKnots.Clear();
                    var sub = path.Subpaths[hit.Knot.Subpath];
                    _selectedKnots.Add(hit.Knot);
                    _selectedKnots.Add(new KnotRef(hit.Knot.Subpath, (hit.Knot.Knot + 1) % sub.Knots.Count));
                    _pathDrag = PathDrag.Knots;
                    break;
            }
        }
        else
        {
            var component = PathEditing.ComponentOf(path, hit.Knot.Subpath).ToList();
            var knots = PathEditing.KnotsOf(path, component).ToList();
            bool selected = knots.All(_selectedKnots.Contains);
            if (shift && selected)
            {
                foreach (var k in knots) _selectedKnots.Remove(k);
                _pathDrag = PathDrag.None;
            }
            else
            {
                if (!shift && !selected) _selectedKnots.Clear();
                foreach (var k in knots) _selectedKnots.Add(k);
                _pathDrag = PathDrag.Subpaths;
                _dragSubpaths = SelectedSubpaths(path);
                if (alt)
                {
                    // Option-drag moves a copy, leaving the original in place.
                    var copies = _dragSubpaths.Select(i => path.Subpaths[i]).ToList();
                    int first = path.Subpaths.Count;
                    _pathAtPress = path = path.Append(copies);
                    _selectedKnots.Clear();
                    _dragSubpaths = Enumerable.Range(first, copies.Count).ToList();
                    foreach (var k in PathEditing.KnotsOf(path, _dragSubpaths)) _selectedKnots.Add(k);
                    _pathMoved = true; // the copy is a change even if it is not moved
                    PreviewPathChange(path);
                }
            }
        }
        RefreshPathOverlay();
        return true;
    }

    /// <summary>Handles show on selected anchors and their neighbours (the handles of the segments they bound).</summary>
    private HashSet<KnotRef> HandleOwners(VectorPath path)
    {
        var set = new HashSet<KnotRef>();
        foreach (var r in _selectedKnots)
        {
            if (r.Subpath >= path.Subpaths.Count) continue;
            int n = path.Subpaths[r.Subpath].Knots.Count;
            set.Add(r);
            set.Add(new KnotRef(r.Subpath, (r.Knot + 1) % n));
            set.Add(new KnotRef(r.Subpath, (r.Knot - 1 + n) % n));
        }
        return set;
    }

    private List<int> SelectedSubpaths(VectorPath path) =>
        Enumerable.Range(0, path.Subpaths.Count).Where(s => _selectedKnots.Any(k => k.Subpath == s)).ToList();

    private (PixelLayer Layer, VectorPath Path)? OtherShapeAt(PathPoint p, double tolerance)
    {
        foreach (var node in Model.Root.Descendants().Reverse())
        {
            if (node is not PixelLayer { Visible: true } layer || ReferenceEquals(layer, SelectedLayer?.Node)) continue;
            if (ShapeLayers.Read(Model, layer) is not { } data) continue;
            if (PathEditing.HitTest(data.Path, p, tolerance).Part != PathPart.None) return (layer, data.Path);
            // Inside the filled area counts too.
            var cell = new PixelRect((int)Math.Floor(p.X), (int)Math.Floor(p.Y), (int)Math.Floor(p.X) + 1, (int)Math.Floor(p.Y) + 1);
            if (PathRasterizer.Rasterize(data.Path, cell)[0] > 127) return (layer, data.Path);
        }
        return null;
    }

    /// <summary>A drag of a selection or convert gesture reaches (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public void PathDragTo(double x, double y, bool shift)
    {
        if (_pathAtPress is not { } start) return;
        var p = new PathPoint(x, y);
        double dx = p.X - _pathPress.X, dy = p.Y - _pathPress.Y;
        if (shift && _pathDrag is PathDrag.Knots or PathDrag.Subpaths)
        {
            var c = PathEditing.Constrain45(_pathPress, p);
            (dx, dy) = (c.X - _pathPress.X, c.Y - _pathPress.Y);
        }
        switch (_pathDrag)
        {
            case PathDrag.Knots:
                _pathMoved = true;
                PreviewPathChange(PathEditing.MoveKnots(start, _selectedKnots, dx, dy), InvalidateLive(start, _selectedKnots.Select(k => k.Subpath)));
                break;
            case PathDrag.Subpaths:
                _pathMoved = true;
                PreviewPathChange(PathEditing.MoveSubpaths(start, _dragSubpaths, dx, dy), MoveLive(start, _dragSubpaths, dx, dy));
                break;
            case PathDrag.Handle:
                _pathMoved = true;
                PreviewPathChange(PathEditing.MoveHandle(start, _dragKnot, _dragOutHandle, shift ? PathEditing.Constrain45(start.Subpaths[_dragKnot.Subpath].Knots[_dragKnot.Knot].Anchor, p) : p, _dragBreak),
                    InvalidateLive(start, [_dragKnot.Subpath]));
                break;
            case PathDrag.Convert:
                if (!_pathMoved && PathPoint.Distance(p, _pathPress) < 1) return;
                _pathMoved = true;
                PreviewPathChange(PathEditing.PullHandles(start, _dragKnot, p), InvalidateLive(start, [_dragKnot.Subpath]));
                break;
        }
    }

    /// <summary>
    /// The gesture ends: a move is one step ("Drag Anchor" / "Drag Path"); a Convert Point click makes a corner; a
    /// marquee selects the anchors (Direct Selection) or components (Path Selection) it touches.
    /// </summary>
    public void PathRelease((double Left, double Top, double Right, double Bottom)? marquee, bool direct)
    {
        var drag = _pathDrag;
        _pathDrag = PathDrag.None;
        if (drag == PathDrag.Marquee)
        {
            if (marquee is var (l, t, r, b) && PathTarget().Path is { } path)
            {
                if (direct) foreach (var k in PathEditing.KnotsIn(path, l, t, r, b)) _selectedKnots.Add(k);
                else foreach (var k in PathEditing.KnotsOf(path, PathEditing.SubpathsIn(path, l, t, r, b))) _selectedKnots.Add(k);
            }
            RefreshPathOverlay();
            return;
        }
        if (_pathAtPress is not { } start) return;
        if (drag == PathDrag.Convert && !_pathMoved)
        {
            CommitPath(PathEditing.MakeCorner(start, _dragKnot), "Convert Point", InvalidateLive(start, [_dragKnot.Subpath]));
            return;
        }
        if (!_pathMoved || _livePath is not { } moved)
        {
            if (_pathMoved && drag == PathDrag.Subpaths) CommitPath(start, "Duplicate Path"); // an Option-click copy
            _livePath = null;
            RefreshPathOverlay();
            return;
        }
        string description = drag switch
        {
            PathDrag.Subpaths => "Drag Path",
            PathDrag.Handle => "Drag Direction Point",
            PathDrag.Convert => "Convert Point",
            _ => "Drag Anchor",
        };
        CommitPath(moved, description, drag == PathDrag.Subpaths
            ? MoveLive(_pathAtPress!, _dragSubpaths, DragOffset(moved, start).Dx, DragOffset(moved, start).Dy)
            : InvalidateLive(start, _selectedKnots.Select(k => k.Subpath).Append(_dragKnot.Subpath)));
    }

    private (double Dx, double Dy) DragOffset(VectorPath moved, VectorPath start)
    {
        int s = _dragSubpaths.FirstOrDefault();
        if (s >= moved.Subpaths.Count || moved.Subpaths[s].Knots.Count == 0) return (0, 0);
        var a = moved.Subpaths[s].Knots[0].Anchor;
        var b = start.Subpaths[s].Knots[0].Anchor;
        return (a.X - b.X, a.Y - b.Y);
    }

    /// <summary>Arrow keys: selected anchors (Direct Selection) or components (Path Selection) move by whole pixels.</summary>
    public bool NudgePath(double dx, double dy, bool direct)
    {
        if (_selectedKnots.Count == 0 || PathTarget().Path is not { } path) return false;
        if (direct) CommitPath(PathEditing.MoveKnots(path, _selectedKnots, dx, dy), "Nudge Anchor", InvalidateLive(path, _selectedKnots.Select(k => k.Subpath)));
        else
        {
            var subs = SelectedSubpaths(path);
            CommitPath(PathEditing.MoveSubpaths(path, subs, dx, dy), "Nudge Path", MoveLive(path, subs, dx, dy));
        }
        return true;
    }

    /// <summary>Delete: removes the selected anchors (Direct Selection) or components (Path Selection).</summary>
    public bool DeleteSelectedPathItems(bool direct)
    {
        if (_selectedKnots.Count == 0 || PathTarget().Path is not { } path) return false;
        var next = direct ? PathEditing.DeleteKnots(path, _selectedKnots) : PathEditing.DeleteSubpaths(path, SelectedSubpaths(path));
        var touched = _selectedKnots.Select(k => k.Subpath).ToList();
        _selectedKnots.Clear();
        CommitPath(next, direct ? "Delete Anchor Point" : "Delete Path", d => d with
        {
            LiveShapes = direct ? InvalidateLive(path, touched)(d).LiveShapes
                : d.LiveShapes.Where(s => d.Path.Subpaths.Any(sp => sp.OriginIndex == s.Index)).ToArray(),
        });
        return true;
    }

    /// <summary>Selects every anchor of the target path (⌘A with a path tool, or after drawing).</summary>
    public void SelectAllAnchors()
    {
        if (PathTarget().Path is not { } path) return;
        foreach (var k in AllKnots(path)) _selectedKnots.Add(k);
        RefreshPathOverlay();
    }

    /// <summary>Which anchors are selected (for tests and the overlay).</summary>
    public IReadOnlySet<KnotRef> SelectedKnots => _selectedKnots;

    /// <summary>Live shapes drawn by the given subpaths lose their parameters: their anchors were edited by hand.</summary>
    private static Func<ShapeLayerData, ShapeLayerData> InvalidateLive(VectorPath path, IEnumerable<int> subpaths)
    {
        var origins = subpaths.Where(s => s >= 0 && s < path.Subpaths.Count).Select(s => path.Subpaths[s].OriginIndex).ToHashSet();
        return d => d with { LiveShapes = d.LiveShapes.Select(s => origins.Contains(s.Index) ? s with { Invalidated = true } : s).ToArray() };
    }

    /// <summary>Live shapes whose components moved as a whole move their boxes with them.</summary>
    private static Func<ShapeLayerData, ShapeLayerData> MoveLive(VectorPath path, IEnumerable<int> subpaths, double dx, double dy)
    {
        var origins = subpaths.Where(s => s >= 0 && s < path.Subpaths.Count).Select(s => path.Subpaths[s].OriginIndex).ToHashSet();
        return d => d with
        {
            LiveShapes = d.LiveShapes.Select(s => origins.Contains(s.Index) ? s.WithBox(s.Left + dx, s.Top + dy, s.Right + dx, s.Bottom + dy) : s).ToArray(),
        };
    }

    /// <summary>A path gesture was abandoned (Esc, another tool): nothing is recorded.</summary>
    public void CancelPathDrag()
    {
        _pathDrag = PathDrag.None;
        CancelPathGesture();
    }
}
