using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Strayta.Core.Selection;
using PixelRect = Strayta.Core.PixelRect;
using Vector2 = System.Numerics.Vector2;

namespace Strayta.Editor.Controls;

/// <summary>
/// A finished marquee or lasso drag in image coordinates: <see cref="Box"/> is the marquee rectangle (whole
/// pixels), <see cref="Points"/> the lasso path. A click without a drag has neither.
/// </summary>
public sealed record SelectionGesture(CanvasTool Tool, SelectionMode Mode, PixelRect Box, IReadOnlyList<Vector2> Points)
{
    public bool IsClick => Tool == CanvasTool.Lasso ? Points.Count < 3 : Box.IsEmpty;
}

// Marquee and lasso input, and the selection outline ("marching ants"). The outline is traced once per selection
// (and zoom level) in the background and drawn as vector strokes over the image, so animating it never touches
// the document render.
public sealed partial class ImageCanvas
{
    public static readonly StyledProperty<SelectionMask?> SelectionProperty =
        AvaloniaProperty.Register<ImageCanvas, SelectionMask?>(nameof(Selection));

    public SelectionMask? Selection { get => GetValue(SelectionProperty); set => SetValue(SelectionProperty, value); }

    /// <summary>Raised when a marquee or lasso drag (or a click that deselects) finishes.</summary>
    public event Action<SelectionGesture>? SelectionGestureCompleted;

    private bool IsSelectTool => Tool is CanvasTool.RectSelect or CanvasTool.EllipseSelect or CanvasTool.Lasso;

    // Traced outline of Selection, in document coordinates, at _outlineFactor detail.
    private IReadOnlyList<Vector2[]> _outline = [];
    private SelectionMask? _outlineSource;
    private int _outlineFactor, _traceVersion;
    private Geometry? _outlineGeometry;
    private (IReadOnlyList<Vector2[]> Loops, double Zoom, Vector Offset) _outlineGeometryKey;

    // The shape being dragged. After release it stays on screen until the new selection's outline is ready.
    private bool _selecting, _showShape, _hideOutline;
    private DateTime _shapeUntil;
    private CanvasTool _shapeTool;
    private Point _selectStart, _selectEnd, _selectStartScreen;
    private PixelRect _shapeBox;
    private SelectionMode _selectMode;
    private bool _shiftForMode, _altForMode, _shiftReleased, _altReleased, _dragged;
    private readonly List<Vector2> _lasso = [];

    private DispatcherTimer? _antsTimer;
    private int _antsPhase;

    private static readonly IPen AntsLight = new ImmutablePen(Brushes.White, 1);

    private void OnSelectionPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == SelectionProperty || change.Property == ZoomProperty) Retrace();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _antsTimer?.Stop();
        _antsTimer = null;
    }

    /// <summary>Photoshop-style ants: a step every 80 ms reads as marching without redrawing every frame.</summary>
    private void StartAnts()
    {
        if (_antsTimer is not null) return;
        _antsTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(80), DispatcherPriority.Render, (_, _) =>
        {
            if (_showShape && !_selecting && DateTime.UtcNow > _shapeUntil) _showShape = _hideOutline = false;
            if (Selection is null && !_showShape) return;
            _antsPhase = (_antsPhase + 1) % 8;
            InvalidateVisual();
        });
        _antsTimer.Start();
    }

    /// <summary>One outline cell per screen pixel or so: zoomed out, tracing every image pixel would only add vertices.</summary>
    private static int OutlineFactor(double zoom)
    {
        int f = 1;
        while (f < 16 && zoom * f * 2 <= 1) f *= 2;
        return f;
    }

    private async void Retrace()
    {
        var selection = Selection;
        int factor = OutlineFactor(Zoom);
        if (ReferenceEquals(selection, _outlineSource) && (selection is null || factor == _outlineFactor)) return;
        int version = ++_traceVersion;

        IReadOnlyList<Vector2[]> loops;
        if (selection is null || selection.IsRectangular) loops = SelectionOutline.Trace(selection, factor);
        else loops = await Task.Run(() => SelectionOutline.Trace(selection, factor));
        if (version != _traceVersion) return;

        if (!ReferenceEquals(selection, _outlineSource)) _showShape = _hideOutline = false;
        _outline = loops;
        _outlineSource = selection;
        _outlineFactor = factor;
        InvalidateVisual();
    }

    // ---- Input ------------------------------------------------------------------------------------

    private void BeginSelection(PointerPressedEventArgs e)
    {
        var mods = e.KeyModifiers;
        bool shift = mods.HasFlag(KeyModifiers.Shift), alt = mods.HasFlag(KeyModifiers.Alt);
        bool has = Selection is not null;
        // With nothing selected there is nothing to add to or subtract from, so the modifiers constrain and
        // center the marquee instead.
        _selectMode = !has ? SelectionMode.Replace
            : shift && alt ? SelectionMode.Intersect
            : shift ? SelectionMode.Add
            : alt ? SelectionMode.Subtract
            : SelectionMode.Replace;
        _shiftForMode = has && shift;
        _altForMode = has && alt;
        _shiftReleased = _altReleased = _dragged = false;
        _selectStartScreen = e.GetPosition(this);
        _selectStart = _selectEnd = ToImage(_selectStartScreen);
        _shapeTool = Tool;
        _lasso.Clear();
        _lasso.Add(new Vector2((float)_selectStart.X, (float)_selectStart.Y));
        _shapeBox = PixelRect.Empty;
        _selecting = true;
    }

    private void MoveSelection(PointerEventArgs e)
    {
        var mods = e.KeyModifiers;
        if (!mods.HasFlag(KeyModifiers.Shift)) _shiftReleased = true;
        if (!mods.HasFlag(KeyModifiers.Alt)) _altReleased = true;
        var pos = e.GetPosition(this);
        if (!_dragged && Math.Abs(pos.X - _selectStartScreen.X) + Math.Abs(pos.Y - _selectStartScreen.Y) < 3) return;
        if (!_dragged)
        {
            _dragged = true;
            _showShape = true;
            // A new plain selection replaces the old one, which disappears as soon as the drag starts.
            _hideOutline = _selectMode == SelectionMode.Replace;
        }

        if (_shapeTool == CanvasTool.Lasso)
        {
            foreach (var point in e.GetIntermediatePoints(this))
            {
                var p = ToImage(point.Position);
                var last = _lasso[^1];
                // Skip points closer than a screen pixel; they add vertices without changing the shape.
                if (Math.Abs(p.X - last.X) * Zoom + Math.Abs(p.Y - last.Y) * Zoom < 1) continue;
                _lasso.Add(new Vector2((float)p.X, (float)p.Y));
            }
        }
        _selectEnd = ToImage(pos);
        _shapeBox = MarqueeBox(mods);
        InvalidateVisual();
    }

    private void EndSelection(PointerReleasedEventArgs e)
    {
        _selecting = false;
        if (_dragged)
        {
            _selectEnd = ToImage(e.GetPosition(this));
            _shapeBox = MarqueeBox(e.KeyModifiers);
        }
        var gesture = _shapeTool == CanvasTool.Lasso
            ? new SelectionGesture(_shapeTool, _selectMode, PixelRect.Empty, _dragged ? [.. _lasso] : [])
            : new SelectionGesture(_shapeTool, _selectMode, _dragged ? _shapeBox : PixelRect.Empty, []);
        if (gesture.IsClick) _showShape = _hideOutline = false;
        else _shapeUntil = DateTime.UtcNow.AddSeconds(1); // in case the result is unchanged and no new outline comes
        InvalidateVisual();
        SelectionGestureCompleted?.Invoke(gesture);
    }

    /// <summary>
    /// The marquee rectangle in whole image pixels. Shift makes it square and Alt draws it from the center, unless
    /// those keys were pressed to pick the add/subtract mode and have not been released since.
    /// </summary>
    private PixelRect MarqueeBox(KeyModifiers mods)
    {
        bool constrain = mods.HasFlag(KeyModifiers.Shift) && (!_shiftForMode || _shiftReleased);
        bool centered = mods.HasFlag(KeyModifiers.Alt) && (!_altForMode || _altReleased);
        double dx = _selectEnd.X - _selectStart.X, dy = _selectEnd.Y - _selectStart.Y;
        if (constrain)
        {
            double m = Math.Max(Math.Abs(dx), Math.Abs(dy));
            dx = dx < 0 ? -m : m;
            dy = dy < 0 ? -m : m;
        }
        double x0 = centered ? _selectStart.X - dx : _selectStart.X, y0 = centered ? _selectStart.Y - dy : _selectStart.Y;
        double x1 = _selectStart.X + dx, y1 = _selectStart.Y + dy;
        int l = (int)Math.Round(Math.Min(x0, x1)), t = (int)Math.Round(Math.Min(y0, y1));
        int r = (int)Math.Round(Math.Max(x0, x1)), b = (int)Math.Round(Math.Max(y0, y1));
        return r > l && b > t ? new PixelRect(l, t, r, b) : PixelRect.Empty;
    }

    // ---- Drawing ----------------------------------------------------------------------------------

    private void RenderSelection(DrawingContext context)
    {
        if (!_hideOutline && _outline.Count > 0 && Selection is not null)
        {
            var key = (_outline, Zoom, _offset);
            if (_outlineGeometry is null || !ReferenceEquals(key._outline, _outlineGeometryKey.Loops) ||
                key.Zoom != _outlineGeometryKey.Zoom || key._offset != _outlineGeometryKey.Offset)
            {
                _outlineGeometry = LoopsGeometry(_outline, closed: true);
                _outlineGeometryKey = key;
            }
            DrawAnts(context, _outlineGeometry);
        }

        if (_showShape)
        {
            if (_shapeTool == CanvasTool.Lasso)
            {
                if (_lasso.Count > 1) DrawAnts(context, LoopsGeometry([[.. _lasso]], closed: !_selecting));
            }
            else if (_shapeBox is { IsEmpty: false } box)
            {
                var r = new Rect(SnapX(box.Left), SnapY(box.Top), SnapX(box.Right) - SnapX(box.Left), SnapY(box.Bottom) - SnapY(box.Top));
                DrawAnts(context, _shapeTool == CanvasTool.EllipseSelect ? new EllipseGeometry(r) : new RectangleGeometry(r));
            }
        }
    }

    private void DrawAnts(DrawingContext context, Geometry geometry)
    {
        context.DrawGeometry(null, AntsLight, geometry);
        context.DrawGeometry(null, new Pen(Brushes.Black, 1, new DashStyle([4, 4], -_antsPhase)), geometry);
    }

    /// <summary>Where an image position appears in this control (for tests).</summary>
    internal Point ImageToControl(double x, double y) => new(SnapX(x), SnapY(y));

    /// <summary>True once the outline of the current selection has been traced (for tests).</summary>
    internal bool OutlineReady => ReferenceEquals(_outlineSource, Selection) && !_hideOutline;

    // Image edges map to the middle of a screen pixel, so one-pixel lines stay crisp at any zoom.
    private double SnapX(double x) => Math.Round(_offset.X + x * Zoom) + 0.5;
    private double SnapY(double y) => Math.Round(_offset.Y + y * Zoom) + 0.5;

    private StreamGeometry LoopsGeometry(IReadOnlyList<Vector2[]> loops, bool closed)
    {
        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        foreach (var loop in loops)
        {
            if (loop.Length < 2) continue;
            ctx.BeginFigure(new Point(SnapX(loop[0].X), SnapY(loop[0].Y)), false);
            for (int i = 1; i < loop.Length; i++) ctx.LineTo(new Point(SnapX(loop[i].X), SnapY(loop[i].Y)));
            ctx.EndFigure(closed);
        }
        return geometry;
    }
}
