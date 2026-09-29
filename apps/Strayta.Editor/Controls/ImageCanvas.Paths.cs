using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Strayta.Core.Paths;
using Strayta.Editor.Editing;
using Strayta.Editor.ViewModels;

namespace Strayta.Editor.Controls;

/// <summary>What the path and shape tools ask of the document (DocumentViewModel.Paths.cs), in document coordinates.</summary>
public interface IPathTools
{
    void BeginShapeDrag(double x, double y);
    void UpdateShapeDrag(double startX, double startY, double x, double y, bool shift, bool alt);
    void EndShapeDrag();
    void PenPress(double x, double y, bool shift, double tolerance);
    void PenDrag(double x, double y, bool shift, bool alt);
    void PenRelease();
    void PenHover(double x, double y, bool shift);
    void FinishPen();
    bool AddAnchorAt(double x, double y, double tolerance);
    bool DeleteAnchorAt(double x, double y, double tolerance);
    bool ConvertPress(double x, double y, double tolerance);
    bool SelectPress(double x, double y, bool shift, bool alt, bool direct, double tolerance);
    void PathDragTo(double x, double y, bool shift);
    void PathRelease((double Left, double Top, double Right, double Bottom)? marquee, bool direct);
    bool NudgePath(double dx, double dy, bool direct);
    bool DeleteSelectedPathItems(bool direct);
    void CancelPathDrag();
}

// The shape tools (U), the Pen group (P) and Path / Direct Selection (A) on the canvas: drags become calls on the
// document (IPathTools), snapped through View › Snap; the canvas draws the edited path, its anchors and handles, the
// shape being dragged and the pen's rubber band from the document's PathOverlay, and a marquee for the selection tools.
public sealed partial class ImageCanvas
{
    public static readonly StyledProperty<PathOverlay?> PathOverlayProperty =
        AvaloniaProperty.Register<ImageCanvas, PathOverlay?>(nameof(PathOverlay));

    /// <summary>The path tools' overlay (DocumentViewModel.PathOverlay).</summary>
    public PathOverlay? PathOverlay { get => GetValue(PathOverlayProperty); set => SetValue(PathOverlayProperty, value); }

    /// <summary>The document the path tools act on (set by DocumentView).</summary>
    public IPathTools? PathTools { get; set; }

    private const double AnchorGrab = 5; // screen pixels
    private const double AnchorSize = 6;

    private enum PathGesture { None, Shape, Pen, Select, Marquee }

    private PathGesture _pathGesture;
    private Point _shapeStart, _shapeLast, _marqueeFrom, _marqueeTo;

    private static readonly IBrush PathBrush = new SolidColorBrush(Color.FromRgb(0x14, 0x73, 0xE6));
    private static readonly IPen PathPen = new Pen(PathBrush, 1);

    private bool IsShapeCanvasTool => Tool is CanvasTool.Rectangle or CanvasTool.Ellipse or CanvasTool.Triangle or CanvasTool.Polygon or CanvasTool.Line or CanvasTool.CustomShape;

    private bool IsPathCanvasTool => IsShapeCanvasTool || Tool is CanvasTool.Pen or CanvasTool.AddAnchor or CanvasTool.DeleteAnchor or CanvasTool.ConvertPoint
        or CanvasTool.PathSelect or CanvasTool.DirectSelect;

    private double GrabTolerance => AnchorGrab / Math.Max(Zoom, 1e-6);

    private Point SnappedImagePoint(PointerEventArgs e) => SnapDrag(ToImage(e.GetPosition(this)), e.KeyModifiers);

    // ---- Pointer ------------------------------------------------------------------------------------------

    /// <summary>Handles a press with a path or shape tool; false for other tools (or to pan).</summary>
    private bool PathToolPressed(PointerPressedEventArgs e, PointerPointProperties props)
    {
        if (!IsPathCanvasTool || PathTools is not { } tools) return false;
        _dragStart = null; // never falls through to moving the layer
        if (!props.IsLeftButtonPressed) return true;
        var mods = e.KeyModifiers;
        bool shift = mods.HasFlag(KeyModifiers.Shift), alt = mods.HasFlag(KeyModifiers.Alt);
        _dragSnapper = BeginSnap();
        var raw = ToImage(e.GetPosition(this));
        var p = SnappedImagePoint(e);
        switch (Tool)
        {
            case var _ when IsShapeCanvasTool:
                _pathGesture = PathGesture.Shape;
                _shapeStart = _shapeLast = p;
                tools.BeginShapeDrag(p.X, p.Y);
                break;
            case CanvasTool.Pen:
                _pathGesture = PathGesture.Pen;
                tools.PenPress(p.X, p.Y, shift, GrabTolerance);
                break;
            case CanvasTool.AddAnchor:
                tools.AddAnchorAt(raw.X, raw.Y, GrabTolerance);
                _pathGesture = PathGesture.None;
                break;
            case CanvasTool.DeleteAnchor:
                tools.DeleteAnchorAt(raw.X, raw.Y, GrabTolerance);
                _pathGesture = PathGesture.None;
                break;
            case CanvasTool.ConvertPoint:
                _pathGesture = tools.ConvertPress(raw.X, raw.Y, GrabTolerance) ? PathGesture.Select : PathGesture.None;
                break;
            default: // Path Selection, Direct Selection
                bool direct = Tool == CanvasTool.DirectSelect;
                if (tools.SelectPress(raw.X, raw.Y, shift, alt, direct, GrabTolerance)) _pathGesture = PathGesture.Select;
                else
                {
                    _pathGesture = PathGesture.Marquee;
                    _marqueeFrom = _marqueeTo = raw;
                }
                break;
        }
        InvalidateVisual();
        return true;
    }

    /// <summary>Moves of a path gesture, and the pen's rubber band while no button is down.</summary>
    private bool PathToolMoved(PointerEventArgs e)
    {
        if (!IsPathCanvasTool || PathTools is not { } tools) return false;
        var mods = e.KeyModifiers;
        bool shift = mods.HasFlag(KeyModifiers.Shift), alt = mods.HasFlag(KeyModifiers.Alt);
        switch (_pathGesture)
        {
            case PathGesture.Shape:
            {
                var p = SnappedImagePoint(e);
                // Space held while drawing moves the shape instead of resizing it (Photoshop).
                if (_spaceHeld) _shapeStart += p - _shapeLast;
                _shapeLast = p;
                tools.UpdateShapeDrag(_shapeStart.X, _shapeStart.Y, p.X, p.Y, shift, alt);
                return true;
            }
            case PathGesture.Pen:
            {
                var p = SnappedImagePoint(e);
                tools.PenDrag(p.X, p.Y, shift, alt);
                return true;
            }
            case PathGesture.Select:
            {
                var p = SnappedImagePoint(e);
                tools.PathDragTo(p.X, p.Y, shift);
                return true;
            }
            case PathGesture.Marquee:
                _marqueeTo = ToImage(e.GetPosition(this));
                InvalidateVisual();
                return true;
        }
        if (Tool == CanvasTool.Pen && !_panning)
        {
            var p = SnapHover(ToImage(e.GetPosition(this)), mods);
            tools.PenHover(p.X, p.Y, shift);
        }
        return false;
    }

    /// <summary>The pen's rubber band follows the snapped pointer too.</summary>
    private Point SnapHover(Point image, KeyModifiers modifiers)
    {
        if (SnapSuspended(modifiers)) return image;
        var snapper = BeginSnap();
        if (!snapper.IsActive) return image;
        var (x, y) = snapper.SnapPoint(image.X, image.Y);
        return new Point(x, y);
    }

    private void PathToolReleased(PointerReleasedEventArgs e)
    {
        if (PathTools is not { } tools || _pathGesture == PathGesture.None) return;
        var gesture = _pathGesture;
        _pathGesture = PathGesture.None;
        bool direct = Tool is CanvasTool.DirectSelect or CanvasTool.ConvertPoint;
        switch (gesture)
        {
            case PathGesture.Shape:
                tools.EndShapeDrag();
                break;
            case PathGesture.Pen:
                tools.PenRelease();
                break;
            case PathGesture.Select:
                tools.PathRelease(null, direct);
                break;
            case PathGesture.Marquee:
                var (a, b) = (_marqueeFrom, _marqueeTo);
                tools.PathRelease((Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)), direct);
                break;
        }
        _dragSnapper = Editing.Snapper.Off;
        InvalidateVisual();
    }

    /// <summary>
    /// Keys for the path tools: Enter or Esc ends the pen's path; arrow keys nudge the selected path items (Shift: 10
    /// pixels); Delete removes them. Returns true when handled.
    /// </summary>
    private bool PathKeyDown(KeyEventArgs e)
    {
        if (!IsPathCanvasTool || PathTools is not { } tools) return false;
        bool direct = Tool == CanvasTool.DirectSelect;
        int step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;
        switch (e.Key)
        {
            case Key.Escape when _pathGesture != PathGesture.None:
                _pathGesture = PathGesture.None;
                tools.CancelPathDrag();
                break;
            case Key.Escape or Key.Enter or Key.Return when Tool == CanvasTool.Pen && e.KeyModifiers == KeyModifiers.None:
                tools.FinishPen();
                break;
            case Key.Left when Tool is CanvasTool.PathSelect or CanvasTool.DirectSelect: return tools.NudgePath(-step, 0, direct) && Handled();
            case Key.Right when Tool is CanvasTool.PathSelect or CanvasTool.DirectSelect: return tools.NudgePath(step, 0, direct) && Handled();
            case Key.Up when Tool is CanvasTool.PathSelect or CanvasTool.DirectSelect: return tools.NudgePath(0, -step, direct) && Handled();
            case Key.Down when Tool is CanvasTool.PathSelect or CanvasTool.DirectSelect: return tools.NudgePath(0, step, direct) && Handled();
            case Key.Delete or Key.Back when Tool is CanvasTool.PathSelect or CanvasTool.DirectSelect or CanvasTool.Pen:
                return tools.DeleteSelectedPathItems(direct || Tool == CanvasTool.Pen) && Handled();
            default:
                return false;
        }
        return Handled();

        bool Handled()
        {
            e.Handled = true;
            return true;
        }
    }

    private Cursor PathCursor() => new(Tool is CanvasTool.PathSelect or CanvasTool.DirectSelect ? StandardCursorType.Arrow : StandardCursorType.Cross);

    // ---- Drawing ------------------------------------------------------------------------------------------

    private Point Screen(PathPoint p) => new(p.X * Zoom + _offset.X, p.Y * Zoom + _offset.Y);

    private StreamGeometry Geometry(VectorPath path, bool closeOpen)
    {
        var g = new StreamGeometry();
        using var ctx = g.Open();
        ctx.SetFillRule(Avalonia.Media.FillRule.NonZero);
        foreach (var s in path.Subpaths)
        {
            if (s.Knots.Count == 0) continue;
            ctx.BeginFigure(Screen(s.Knots[0].Anchor), true);
            foreach (var (p0, c0, c1, p1) in s.Segments())
            {
                if (c0 == p0 && c1 == p1) ctx.LineTo(Screen(p1));
                else ctx.CubicBezierTo(Screen(c0), Screen(c1), Screen(p1));
            }
            ctx.EndFigure(s.Closed || closeOpen);
        }
        return g;
    }

    private void RenderPaths(DrawingContext context)
    {
        if (PathOverlay is { } o && IsPathCanvasTool)
        {
            if (o.Preview is { } preview)
            {
                var geometry = Geometry(preview, closeOpen: o.PreviewFill is not null);
                if (o.PreviewFill is { } f)
                    context.DrawGeometry(new SolidColorBrush(Color.FromRgb(To8(f.R), To8(f.G), To8(f.B))), null, geometry);
                if (o.PreviewStrokeWidth > 0 && o.PreviewStrokeColor is { } sc)
                    context.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromRgb(To8(sc.R), To8(sc.G), To8(sc.B))), o.PreviewStrokeWidth * Zoom), geometry);
                context.DrawGeometry(null, PathPen, geometry);
            }
            if (o.Path is { } path)
            {
                context.DrawGeometry(null, PathPen, Geometry(path, closeOpen: false));
                if (o.ShowAnchors) DrawAnchors(context, path, o.Selected);
            }
        }
        if (_pathGesture == PathGesture.Marquee)
        {
            var a = Screen(new PathPoint(_marqueeFrom.X, _marqueeFrom.Y));
            var b = Screen(new PathPoint(_marqueeTo.X, _marqueeTo.Y));
            var rect = new Rect(a, b).Normalize();
            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(24, 0x14, 0x73, 0xE6)), new Pen(PathBrush, 1, DashStyle.Dash), rect);
        }
    }

    private static byte To8(float v) => (byte)Math.Round(Math.Clamp(v, 0f, 1f) * 255);

    /// <summary>Anchors as small squares (selected ones filled) and the direction handles of selected anchors and their neighbours.</summary>
    private void DrawAnchors(DrawingContext context, VectorPath path, IReadOnlySet<KnotRef> selected)
    {
        var handleOwners = new HashSet<KnotRef>();
        foreach (var r in selected)
        {
            if (r.Subpath >= path.Subpaths.Count) continue;
            int n = path.Subpaths[r.Subpath].Knots.Count;
            if (n == 0) continue;
            handleOwners.Add(r);
            handleOwners.Add(new KnotRef(r.Subpath, (r.Knot + 1) % n));
            handleOwners.Add(new KnotRef(r.Subpath, (r.Knot - 1 + n) % n));
        }
        bool pathSelect = Tool == CanvasTool.PathSelect;
        foreach (var r in handleOwners)
        {
            if (pathSelect || r.Knot >= path.Subpaths[r.Subpath].Knots.Count) continue;
            var k = path.Subpaths[r.Subpath].Knots[r.Knot];
            var a = Screen(k.Anchor);
            foreach (var (h, has) in new[] { (k.In, k.HasIn), (k.Out, k.HasOut) })
            {
                if (!has) continue;
                var hp = Screen(h);
                context.DrawLine(PathPen, a, hp);
                context.DrawEllipse(PathBrush, null, hp, 3, 3);
            }
        }
        var white = Brushes.White;
        double half = AnchorSize / 2;
        for (int s = 0; s < path.Subpaths.Count; s++)
        {
            var knots = path.Subpaths[s].Knots;
            bool anySelected = pathSelect && selected.Any(r => r.Subpath == s);
            if (pathSelect && !anySelected) continue;
            for (int k = 0; k < knots.Count; k++)
            {
                var c = Screen(knots[k].Anchor);
                bool on = pathSelect || selected.Contains(new KnotRef(s, k));
                context.DrawRectangle(on ? PathBrush : white, PathPen, new Rect(c.X - half, c.Y - half, AnchorSize, AnchorSize));
            }
        }
    }
}
