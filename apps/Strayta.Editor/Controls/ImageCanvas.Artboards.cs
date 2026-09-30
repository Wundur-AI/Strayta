using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Strayta.Editor.Controls;

/// <summary>An artboard as the canvas shows it: bounds in image pixels, its name, whether it is selected.</summary>
public sealed record CanvasArtboard(object Key, string Name, int Left, int Top, int Right, int Bottom, bool IsSelected)
{
    public Rect Bounds => new(Left, Top, Right - Left, Bottom - Top);
}

// Artboards on the canvas: every artboard's name above its top-left corner (clicking the name selects the artboard,
// and with the Move tool dragging it moves the artboard), the checkerboard only inside artboards (outside is
// pasteboard), and the Artboard tool: drag on the pasteboard to draw a new artboard, drag inside one to move it, its
// eight handles to resize it, and the "+" beside each edge to add another of the same size there.
public sealed partial class ImageCanvas
{
    /// <summary>The document's artboards, asked for on every frame (null or empty: an ordinary document).</summary>
    public Func<IReadOnlyList<CanvasArtboard>>? ArtboardsSource { get; set; }

    public event Action<int, int, int, int>? ArtboardDrawn; // left, top, right, bottom in image pixels
    public event Action<object>? ArtboardSelected;
    public event Action<object, int, int>? ArtboardMoved;
    public event Action<object, int, int, int, int>? ArtboardResized;
    public event Action<object, int>? ArtboardAddPressed; // side: 0 left, 1 top, 2 right, 3 bottom
    public event Action? ArtboardGestureEnded;

    private enum ArtboardDrag { None, Create, Move, Resize }

    private ArtboardDrag _artboardDrag;
    private CanvasArtboard? _artboardTarget;
    private Point _artboardStart; // image coordinates
    private Point _artboardNow;
    private int _artboardHandle; // 0..7: TL, T, TR, R, BR, B, BL, L
    private Vector _artboardMoved; // whole pixels already reported

    private const double PlusDistance = 22, PlusRadius = 9, ArtboardHandleSize = 7;

    private IReadOnlyList<CanvasArtboard> CurrentArtboards => ArtboardsSource?.Invoke() ?? [];

    private Point ToScreen(double x, double y) => new(_offset.X + x * Zoom, _offset.Y + y * Zoom);

    private Rect ScreenRect(CanvasArtboard a) => new(ToScreen(a.Left, a.Top), ToScreen(a.Right, a.Bottom));

    private IBrush Accent => this.TryFindResource("St.Accent", ActualThemeVariant, out var b) && b is IBrush brush ? brush : Brushes.DodgerBlue;

    /// <summary>With artboards, the checkerboard shows only inside them; returns false when there are none (draw it everywhere).</summary>
    private bool RenderArtboardChecker(DrawingContext context, Rect dest)
    {
        var artboards = CurrentArtboards;
        if (artboards.Count == 0) return false;
        foreach (var a in artboards)
        {
            var r = ScreenRect(a).Intersect(dest);
            if (r.Width > 0 && r.Height > 0) context.FillRectangle(_checker, r);
        }
        return true;
    }

    private FormattedText ArtboardLabel(string text, IBrush brush) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 11, brush);

    private Rect LabelRect(CanvasArtboard a)
    {
        var at = ToScreen(a.Left, a.Top);
        var label = ArtboardLabel(a.Name, Brushes.White);
        return new Rect(at.X, at.Y - label.Height - 4, Math.Max(20, label.Width), label.Height + 2);
    }

    private void RenderArtboards(DrawingContext context)
    {
        var artboards = CurrentArtboards;
        if (artboards.Count == 0 && _artboardDrag != ArtboardDrag.Create) return;
        var muted = this.TryFindResource("St.TextMuted", ActualThemeVariant, out var m) && m is IBrush mb ? mb : Brushes.Gray;
        var accent = Accent;
        bool tool = Tool == CanvasTool.Artboard && FreeTransform is null && CropBox is null;
        foreach (var a in artboards)
        {
            var label = ArtboardLabel(a.Name, a.IsSelected ? accent : muted);
            var at = ToScreen(a.Left, a.Top);
            context.DrawText(label, new Point(at.X, at.Y - label.Height - 3));
            if (!a.IsSelected || !tool) continue;

            var r = ScreenRect(a);
            context.DrawRectangle(null, new Pen(accent, 1.5), r);
            var fill = new SolidColorBrush(Colors.White);
            foreach (var h in Handles(r))
                context.DrawRectangle(fill, new Pen(accent, 1), new Rect(h.X - ArtboardHandleSize / 2, h.Y - ArtboardHandleSize / 2, ArtboardHandleSize, ArtboardHandleSize));
            foreach (var (p, _) in Pluses(r))
            {
                context.DrawEllipse(this.TryFindResource("St.PanelRaised", ActualThemeVariant, out var pr) && pr is IBrush prb ? prb : Brushes.DimGray,
                    new Pen(accent, 1), p, PlusRadius, PlusRadius);
                context.DrawLine(new Pen(accent, 1.5), new Point(p.X - 4, p.Y), new Point(p.X + 4, p.Y));
                context.DrawLine(new Pen(accent, 1.5), new Point(p.X, p.Y - 4), new Point(p.X, p.Y + 4));
            }
        }

        if (_artboardDrag == ArtboardDrag.Create)
        {
            var r = new Rect(ToScreen(_artboardStart.X, _artboardStart.Y), ToScreen(_artboardNow.X, _artboardNow.Y)).Normalize();
            context.DrawRectangle(null, new Pen(Brushes.Black, 1), r);
            context.DrawRectangle(null, new Pen(Brushes.White, 1, new DashStyle([4, 4], 0)), r);
        }
    }

    private static Point[] Handles(Rect r) =>
    [
        r.TopLeft, new(r.Center.X, r.Top), r.TopRight, new(r.Right, r.Center.Y),
        r.BottomRight, new(r.Center.X, r.Bottom), r.BottomLeft, new(r.Left, r.Center.Y),
    ];

    private static (Point At, int Side)[] Pluses(Rect r) =>
    [
        (new Point(r.Left - PlusDistance, r.Center.Y), 0),
        (new Point(r.Center.X, r.Top - PlusDistance - 14), 1), // above the name
        (new Point(r.Right + PlusDistance, r.Center.Y), 2),
        (new Point(r.Center.X, r.Bottom + PlusDistance), 3),
    ];

    /// <summary>Artboard names (Move and Artboard tools) and the Artboard tool itself.</summary>
    private bool ArtboardPressed(PointerPressedEventArgs e, PointerPointProperties props)
    {
        if (!props.IsLeftButtonPressed || FreeTransform is not null || CropBox is not null) return false;
        if (Tool is not (CanvasTool.Artboard or CanvasTool.Move)) return false;
        var screen = e.GetPosition(this);
        var image = ToImage(screen);
        var artboards = CurrentArtboards;

        if (Tool == CanvasTool.Artboard && artboards.FirstOrDefault(a => a.IsSelected) is { } selected)
        {
            var r = ScreenRect(selected);
            foreach (var (p, side) in Pluses(r))
                if (Distance(p, screen) <= PlusRadius + 2)
                {
                    ArtboardAddPressed?.Invoke(selected.Key, side);
                    ArtboardGestureEnded?.Invoke();
                    return true;
                }
            var handles = Handles(r);
            for (int i = 0; i < handles.Length; i++)
                if (Math.Abs(handles[i].X - screen.X) <= ArtboardHandleSize && Math.Abs(handles[i].Y - screen.Y) <= ArtboardHandleSize)
                {
                    StartArtboardDrag(ArtboardDrag.Resize, selected, image);
                    _artboardHandle = i;
                    return true;
                }
        }

        // The name: selects the artboard, and a drag moves it.
        if (artboards.LastOrDefault(a => LabelRect(a).Contains(screen)) is { } named)
        {
            ArtboardSelected?.Invoke(named.Key);
            StartArtboardDrag(ArtboardDrag.Move, named, image);
            return true;
        }
        if (Tool != CanvasTool.Artboard) return false;

        if (artboards.LastOrDefault(a => a.Bounds.Contains(image)) is { } inside)
        {
            ArtboardSelected?.Invoke(inside.Key);
            StartArtboardDrag(ArtboardDrag.Move, inside, image);
            return true;
        }
        StartArtboardDrag(ArtboardDrag.Create, null, image);
        return true;
    }

    private void StartArtboardDrag(ArtboardDrag kind, CanvasArtboard? target, Point image)
    {
        _artboardDrag = kind;
        _artboardTarget = target;
        _artboardStart = _artboardNow = image;
        _artboardMoved = default;
    }

    private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private bool ArtboardMovedPointer(PointerEventArgs e)
    {
        if (_artboardDrag == ArtboardDrag.None) return false;
        _artboardNow = ToImage(e.GetPosition(this));
        var d = _artboardNow - _artboardStart;
        switch (_artboardDrag)
        {
            case ArtboardDrag.Move when _artboardTarget is { } t:
            {
                int tx = (int)Math.Round(d.X), ty = (int)Math.Round(d.Y);
                int dx = tx - (int)_artboardMoved.X, dy = ty - (int)_artboardMoved.Y;
                if (dx != 0 || dy != 0)
                {
                    _artboardMoved = new Vector(tx, ty);
                    ArtboardMoved?.Invoke(t.Key, dx, dy);
                }
                break;
            }
            case ArtboardDrag.Resize when _artboardTarget is { } t:
            {
                int dx = (int)Math.Round(d.X), dy = (int)Math.Round(d.Y);
                int l = t.Left, top = t.Top, r = t.Right, b = t.Bottom;
                if (_artboardHandle is 0 or 6 or 7) l = Math.Min(r - 1, t.Left + dx);
                if (_artboardHandle is 2 or 3 or 4) r = Math.Max(l + 1, t.Right + dx);
                if (_artboardHandle is 0 or 1 or 2) top = Math.Min(b - 1, t.Top + dy);
                if (_artboardHandle is 4 or 5 or 6) b = Math.Max(top + 1, t.Bottom + dy);
                ArtboardResized?.Invoke(t.Key, l, top, r, b);
                break;
            }
        }
        InvalidateVisual();
        return true;
    }

    private void ArtboardReleased()
    {
        if (_artboardDrag == ArtboardDrag.None) return;
        if (_artboardDrag == ArtboardDrag.Create)
        {
            int l = (int)Math.Round(Math.Min(_artboardStart.X, _artboardNow.X)), r = (int)Math.Round(Math.Max(_artboardStart.X, _artboardNow.X));
            int t = (int)Math.Round(Math.Min(_artboardStart.Y, _artboardNow.Y)), b = (int)Math.Round(Math.Max(_artboardStart.Y, _artboardNow.Y));
            // A click is not a drag: it only deselects nothing; tiny boxes are ignored.
            if (r - l >= 2 && b - t >= 2 && (r - l) * Zoom >= 4 && (b - t) * Zoom >= 4) ArtboardDrawn?.Invoke(l, t, r, b);
        }
        _artboardDrag = ArtboardDrag.None;
        _artboardTarget = null;
        ArtboardGestureEnded?.Invoke();
        InvalidateVisual();
    }
}
