using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Strayta.Editor.Editing;
using Strayta.Rendering.Transforms;

namespace Strayta.Editor.Controls;

// The Perspective Crop tool on the canvas: drag to draw the shape, then drag its corners (or sides, or the inside)
// onto the edges of something that should come out rectangular. A grid in perspective shows how it will be
// straightened; the area outside is shielded. Only the overlay redraws while dragging.
public sealed partial class ImageCanvas
{
    private static readonly IPen PerspectiveGridPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(120, 255, 255, 255)), 1);

    private bool PerspectivePressed(PointerPressedEventArgs e)
    {
        if (PerspectiveCrop is not { } box) return false;
        var props = e.GetCurrentPoint(this).Properties;
        if (!props.IsLeftButtonPressed || box.Locked) return true;
        var p = ToImage(e.GetPosition(this));
        int handle = box.HitTest(p.X, p.Y, HandleGrab / Zoom);
        if (e.ClickCount == 2 && handle == PerspectiveCropBox.Move)
        {
            PerspectiveCropCommit?.Invoke();
            return true;
        }
        box.BeginDrag(handle, p.X, p.Y);
        return true;
    }

    private bool PerspectiveMoved(PointerEventArgs e)
    {
        if (PerspectiveCrop is not { } box) return false;
        var p = ToImage(e.GetPosition(this));
        if (box.IsDragging) box.DragTo(p.X, p.Y);
        else if (!_spaceHeld && Tool != CanvasTool.Hand)
        {
            int handle = box.HitTest(p.X, p.Y, HandleGrab / Zoom);
            Cursor = new Cursor(handle switch
            {
                < 8 => StandardCursorType.Cross,
                PerspectiveCropBox.Move => StandardCursorType.SizeAll,
                _ => StandardCursorType.Cross,
            });
        }
        return true;
    }

    private void DrawPerspectiveCrop(DrawingContext context)
    {
        if (PerspectiveCrop is not { HasShape: true } box) return;
        var q = box.Corners.Select(c => ToScreen(c)).ToArray();

        // Shield everything outside the shape.
        var shield = new StreamGeometry();
        using (var g = shield.Open())
        {
            g.SetFillRule(FillRule.EvenOdd);
            g.BeginFigure(new Point(0, 0), true);
            g.LineTo(new Point(Bounds.Width, 0));
            g.LineTo(new Point(Bounds.Width, Bounds.Height));
            g.LineTo(new Point(0, Bounds.Height));
            g.EndFigure(true);
            g.BeginFigure(q[0], true);
            for (int i = 1; i < 4; i++) g.LineTo(q[i]);
            g.EndFigure(true);
        }
        context.DrawGeometry(Shield, null, shield);

        // The grid in perspective: lines of the straightened result, seen through the shape.
        try
        {
            var map = Projective.RectToQuad(1, 1, q.Select(p => (p.X, p.Y)).ToArray());
            Point At(double u, double v)
            {
                var (x, y) = map.Apply(u, v);
                return new Point(x, y);
            }
            const int n = 6;
            for (int i = 1; i < n; i++)
            {
                double t = i / (double)n;
                context.DrawLine(PerspectiveGridPen, At(t, 0), At(t, 1));
                context.DrawLine(PerspectiveGridPen, At(0, t), At(1, t));
            }
        }
        catch (ArgumentException)
        {
            // A degenerate shape has no grid.
        }

        var outline = new StreamGeometry();
        using (var g = outline.Open())
        {
            g.BeginFigure(q[0], false);
            for (int i = 1; i < 4; i++) g.LineTo(q[i]);
            g.EndFigure(true);
        }
        context.DrawGeometry(null, CropEdgeDark, outline);
        context.DrawGeometry(null, CropEdgeLight, outline);

        for (int i = 0; i < 8; i++)
        {
            var h = ToScreen(box.HandlePosition(i));
            var r = new Rect(h.X - HandleSize / 2, h.Y - HandleSize / 2, HandleSize, HandleSize);
            context.FillRectangle(Brushes.White, r);
            context.DrawRectangle(null, CropEdgeDark, r);
        }
    }
}
