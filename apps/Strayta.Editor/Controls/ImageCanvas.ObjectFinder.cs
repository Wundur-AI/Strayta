using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using PixelRect = Strayta.Core.PixelRect;
using Vector2 = System.Numerics.Vector2;

namespace Strayta.Editor.Controls;

// Object Selection on the canvas: its Lasso mode (draw around the object instead of dragging a box) and the Object
// Finder's hover highlight, Photoshop's blue overlay on the object under the pointer.
public sealed partial class ImageCanvas
{
    /// <summary>Object Selection draws a lasso instead of a rectangle.</summary>
    public static readonly StyledProperty<bool> ObjectLassoModeProperty =
        AvaloniaProperty.Register<ImageCanvas, bool>(nameof(ObjectLassoMode));

    public bool ObjectLassoMode { get => GetValue(ObjectLassoModeProperty); set => SetValue(ObjectLassoModeProperty, value); }

    /// <summary>The outline of the object under the pointer (image coordinates), highlighted while hovering; null for none.</summary>
    public static readonly StyledProperty<IReadOnlyList<Vector2[]>?> HoverOutlineProperty =
        AvaloniaProperty.Register<ImageCanvas, IReadOnlyList<Vector2[]>?>(nameof(HoverOutline));

    public IReadOnlyList<Vector2[]>? HoverOutline { get => GetValue(HoverOutlineProperty); set => SetValue(HoverOutlineProperty, value); }

    /// <summary>The pointer over the image with Object Selection (image coordinates), or null when it left or a drag started.</summary>
    public event Action<Vector2?>? ObjectHover;

    private bool _objectLasso;

    private void OnObjectFinderPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == HoverOutlineProperty) InvalidateVisual();
    }
    private Geometry? _hoverGeometry;
    private (IReadOnlyList<Vector2[]>? Loops, double Zoom, Vector Offset) _hoverKey;

    private static readonly IBrush HoverFill = new ImmutableSolidColorBrush(Color.FromArgb(90, 40, 120, 255));
    private static readonly IPen HoverPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(230, 60, 140, 255)), 1.5);

    /// <summary>True when the shape being drawn is a lasso path (the Lasso, or Object Selection in Lasso mode).</summary>
    private bool LassoShape => _shapeTool == CanvasTool.Lasso || _objectLasso;

    /// <summary>The lasso path's bounding box in whole pixels: Object Selection's box prompt in Lasso mode.</summary>
    private PixelRect LassoBounds()
    {
        float l = _lasso.Min(p => p.X), t = _lasso.Min(p => p.Y), r = _lasso.Max(p => p.X), b = _lasso.Max(p => p.Y);
        return new PixelRect((int)MathF.Floor(l), (int)MathF.Floor(t), (int)MathF.Ceiling(r), (int)MathF.Ceiling(b));
    }

    /// <summary>Reports hover positions for Object Selection's Object Finder (called on every pointer move).</summary>
    private void ObjectFinderMoved(PointerEventArgs e)
    {
        if (Tool != CanvasTool.ObjectSelect || ObjectHover is null) return;
        if (_selecting || _spaceHeld || _dragStart is not null)
        {
            ObjectHover(null);
            return;
        }
        var p = ToImage(e.GetPosition(this));
        ObjectHover(new Vector2((float)p.X, (float)p.Y));
    }

    private void ObjectFinderExited() => ObjectHover?.Invoke(null);

    private void RenderObjectHover(DrawingContext context)
    {
        if (Tool != CanvasTool.ObjectSelect || HoverOutline is not { Count: > 0 } loops || _selecting) return;
        if (_hoverGeometry is null || !ReferenceEquals(loops, _hoverKey.Loops) || Zoom != _hoverKey.Zoom || _offset != _hoverKey.Offset)
        {
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.SetFillRule(FillRule.EvenOdd);
                foreach (var loop in loops)
                {
                    if (loop.Length < 3) continue;
                    ctx.BeginFigure(new Point(_offset.X + loop[0].X * Zoom, _offset.Y + loop[0].Y * Zoom), true);
                    for (int i = 1; i < loop.Length; i++) ctx.LineTo(new Point(_offset.X + loop[i].X * Zoom, _offset.Y + loop[i].Y * Zoom));
                    ctx.EndFigure(true);
                }
            }
            _hoverGeometry = geometry;
            _hoverKey = (loops, Zoom, _offset);
        }
        context.DrawGeometry(HoverFill, HoverPen, _hoverGeometry);
    }
}
