using System.Diagnostics;
using Strayta.Core;
using Strayta.Core.Text;
using Strayta.Text;

namespace Strayta.Editor.Editing;

/// <summary>
/// One Type tool edit on the canvas: the layer being typed into, its text as a <see cref="TextEditor"/> (caret,
/// selection, undo inside the edit), and the live pixels. Every change is drawn at once by the text engine straight
/// into the layer, without history; <see cref="ViewModels.DocumentViewModel"/> turns the whole edit into one history
/// step when it is committed, or puts the layer back when it is cancelled (Photoshop's ✓ and ⊘).
/// </summary>
public sealed class TypeSession
{
    private readonly Document _doc;
    private readonly object? _originalSource;
    private readonly Raster? _originalPixels;
    private readonly PixelRect _originalBounds;
    private TextLayerData? _rendered;

    internal TypeSession(Document doc, PixelLayer layer, TextLayerData data, bool isNew, LayerGroup parent, int index)
    {
        _doc = doc;
        Layer = layer;
        IsNew = isNew;
        Parent = parent;
        Index = index;
        _originalSource = layer.SourceData;
        _originalPixels = layer.Pixels;
        _originalBounds = layer.Bounds;
        Editor = new TextEditor(data);
        Editor.Changed += OnEditorChanged;
    }

    /// <summary>The type layer being edited (for new text, a layer added for the edit and removed again if it stays empty).</summary>
    public PixelLayer Layer { get; }

    /// <summary>True when the edit made the layer (a click or drag on empty canvas), false when it edits an existing one.</summary>
    public bool IsNew { get; }

    /// <summary>Where a new layer sits.</summary>
    internal LayerGroup Parent { get; }
    internal int Index { get; }

    public TextEditor Editor { get; }

    /// <summary>The text laid out as drawn.</summary>
    public TextLayout Layout => Editor.Layout;

    /// <summary>Text being composed by an input method (e.g. a Japanese reading or a dead key), shown at the caret; null when none.</summary>
    public string? Preedit { get; set { field = string.IsNullOrEmpty(value) ? null : value; Changed?.Invoke(); } }

    /// <summary>Milliseconds the text engine took for the last redraw (layout and pixels).</summary>
    public double LastRenderMs { get; private set; }

    /// <summary>Raised after every change the canvas shows (text, caret, selection, box), after the pixels are updated.</summary>
    public event Action? Changed;

    /// <summary>Raised when the layer's pixels changed and the document needs rendering.</summary>
    internal event Action? PixelsChanged;

    private void OnEditorChanged()
    {
        if (!ReferenceEquals(_rendered, Editor.Data)) Render();
        Changed?.Invoke();
    }

    /// <summary>Draws the current text into the layer.</summary>
    internal void Render()
    {
        var sw = Stopwatch.StartNew();
        var result = TextRenderer.Render(Editor.Data, new TextRenderOptions { ColorMode = _doc.ColorMode, BitDepth = _doc.BitDepth });
        Editor.UseLayout(result.Layout);
        Layer.Pixels = result.Pixels;
        Layer.Bounds = result.Pixels is null ? PixelRect.Empty : result.Bounds;
        _rendered = Editor.Data;
        LastRenderMs = sw.Elapsed.TotalMilliseconds;
        PixelsChanged?.Invoke();
    }

    /// <summary>Puts the layer's pixels and data back as they were before the edit.</summary>
    internal void RestoreOriginal()
    {
        Layer.SourceData = _originalSource;
        Layer.Pixels = _originalPixels;
        Layer.Bounds = _originalBounds;
    }

    // ---- Geometry in document coordinates, for the canvas ------------------------------------------------

    public TextTransform Transform => Editor.Data.Transform;

    /// <summary>A point in text space for a document point.</summary>
    public (double X, double Y) ToText(double x, double y) => Transform.Invert().Apply(x, y);

    /// <summary>The caret as a document-space segment (top to bottom).</summary>
    public ((double X, double Y) Top, (double X, double Y) Bottom) CaretSegment() => Layout.GetCaretDocument(Editor.Caret);

    /// <summary>The selection highlight: one quadrilateral per line, in document coordinates.</summary>
    public IEnumerable<(double X, double Y)[]> SelectionQuads()
    {
        foreach (var r in Layout.GetSelectionRects(Editor.SelectionStart, Editor.SelectionLength))
            yield return Quad(r);
    }

    /// <summary>The paragraph box's corners (top-left, top-right, bottom-right, bottom-left), or null for point text.</summary>
    public (double X, double Y)[]? BoxCorners => Editor.Data.Kind == TextKind.Paragraph ? Quad(Editor.Data.Box) : null;

    /// <summary>The outline Photoshop draws around point text while editing: the laid-out lines' extent.</summary>
    public (double X, double Y)[] LineBoundsCorners => Quad(Layout.Bounds);

    // ---- Resizing the box and moving the text ------------------------------------------------------------

    private TextLayerData? _dragStart;
    private BoxHandle _dragHandle;
    private (double X, double Y) _dragFrom;
    private bool _dragMoved;

    /// <summary>The paragraph box's handle within <paramref name="grab"/> document pixels of a point, or <see cref="BoxHandle.None"/>.</summary>
    public BoxHandle HitHandle(double x, double y, double grab)
    {
        if (BoxCorners is null) return BoxHandle.None;
        foreach (var h in BoxHandles)
        {
            var (hx, hy) = HandlePosition(h);
            if (Math.Abs(hx - x) <= grab && Math.Abs(hy - y) <= grab) return h;
        }
        return BoxHandle.None;
    }

    /// <summary>The eight resize handles, corners first.</summary>
    public static readonly BoxHandle[] BoxHandles =
        [BoxHandle.TopLeft, BoxHandle.TopRight, BoxHandle.BottomRight, BoxHandle.BottomLeft, BoxHandle.Top, BoxHandle.Right, BoxHandle.Bottom, BoxHandle.Left];

    /// <summary>Where a handle of the paragraph box is, in document coordinates.</summary>
    public (double X, double Y) HandlePosition(BoxHandle h)
    {
        var b = Editor.Data.Box;
        double cx = (b.Left + b.Right) / 2, cy = (b.Top + b.Bottom) / 2;
        var (x, y) = h switch
        {
            BoxHandle.TopLeft => (b.Left, b.Top),
            BoxHandle.TopRight => (b.Right, b.Top),
            BoxHandle.BottomRight => (b.Right, b.Bottom),
            BoxHandle.BottomLeft => (b.Left, b.Bottom),
            BoxHandle.Top => (cx, b.Top),
            BoxHandle.Right => (b.Right, cy),
            BoxHandle.Bottom => (cx, b.Bottom),
            BoxHandle.Left => (b.Left, cy),
            _ => (cx, cy),
        };
        return Transform.Apply(x, y);
    }

    /// <summary>Starts dragging a box handle, or moving the text (<see cref="BoxHandle.Move"/>, ⌘-drag), from a document point.</summary>
    public void BeginDrag(BoxHandle handle, double x, double y)
    {
        _dragStart = Editor.Data;
        _dragHandle = handle;
        _dragFrom = (x, y);
        _dragMoved = false;
    }

    public bool IsDragging => _dragStart is not null;

    /// <summary>
    /// Continues the drag: a handle resizes the box (the text reflows at once; Shift keeps a corner's proportions),
    /// Move shifts the text (Shift keeps it horizontal, vertical or diagonal). The whole drag is one undo step.
    /// </summary>
    public void DragTo(double x, double y, bool shift)
    {
        if (_dragStart is not { } start) return;
        TextLayerData next;
        if (_dragHandle == BoxHandle.Move)
        {
            double dx = x - _dragFrom.X, dy = y - _dragFrom.Y;
            if (shift) (dx, dy) = Constrain(dx, dy);
            var t = start.Transform;
            next = start.WithTransform(t with { TX = t.TX + dx, TY = t.TY + dy });
        }
        else
        {
            var inverse = start.Transform.Invert();
            var (px, py) = inverse.Apply(x, y);
            var (fx, fy) = inverse.Apply(_dragFrom.X, _dragFrom.Y);
            double dx = px - fx, dy = py - fy;
            var b = start.Box;
            double l = b.Left, t = b.Top, r = b.Right, bo = b.Bottom;
            if (_dragHandle is BoxHandle.Left or BoxHandle.TopLeft or BoxHandle.BottomLeft) l += dx;
            if (_dragHandle is BoxHandle.Right or BoxHandle.TopRight or BoxHandle.BottomRight) r += dx;
            if (_dragHandle is BoxHandle.Top or BoxHandle.TopLeft or BoxHandle.TopRight) t += dy;
            if (_dragHandle is BoxHandle.Bottom or BoxHandle.BottomLeft or BoxHandle.BottomRight) bo += dy;
            if (shift && _dragHandle is BoxHandle.TopLeft or BoxHandle.TopRight or BoxHandle.BottomRight or BoxHandle.BottomLeft && b.Width > 0 && b.Height > 0)
            {
                // Keep the proportions: follow the larger change, anchored at the opposite corner.
                double sx = (r - l) / b.Width, sy = (bo - t) / b.Height, s = Math.Abs(sx - 1) > Math.Abs(sy - 1) ? sx : sy;
                double w = b.Width * s, h = b.Height * s;
                if (_dragHandle is BoxHandle.TopLeft or BoxHandle.BottomLeft) l = r - w; else r = l + w;
                if (_dragHandle is BoxHandle.TopLeft or BoxHandle.TopRight) t = bo - h; else bo = t + h;
            }
            const double min = 1;
            if (r - l < min) { if (l != b.Left) l = r - min; else r = l + min; }
            if (bo - t < min) { if (t != b.Top) t = bo - min; else bo = t + min; }
            next = start.WithLayout(box: new TextRect(l, t, r, bo));
        }
        Editor.Replace(next, newStep: !_dragMoved);
        _dragMoved = true;
    }

    public void EndDrag() => _dragStart = null;

    private static (double, double) Constrain(double dx, double dy)
    {
        double angle = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4);
        // Project onto the nearest 45° direction.
        double along = dx * Math.Cos(angle) + dy * Math.Sin(angle);
        return (along * Math.Cos(angle), along * Math.Sin(angle));
    }

    private (double X, double Y)[] Quad(TextRect r)
    {
        var t = Transform;
        return [t.Apply(r.Left, r.Top), t.Apply(r.Right, r.Top), t.Apply(r.Right, r.Bottom), t.Apply(r.Left, r.Bottom)];
    }
}

/// <summary>A handle of a paragraph text box while typing, or Move for dragging the text with ⌘.</summary>
public enum BoxHandle
{
    None,
    Move,
    TopLeft,
    TopRight,
    BottomRight,
    BottomLeft,
    Top,
    Right,
    Bottom,
    Left,
}
