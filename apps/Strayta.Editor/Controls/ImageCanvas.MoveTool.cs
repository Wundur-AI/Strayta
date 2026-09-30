using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Strayta.Editor.Editing;
using PixelRect = Strayta.Core.PixelRect;

namespace Strayta.Editor.Controls;

// The Move tool's press (Auto-Select picks the layer under the pointer before the drag) and Show Transform Controls:
// the selected layers' box with its eight handles; dragging a handle starts Free Transform on them.
public sealed partial class ImageCanvas
{
    public static readonly StyledProperty<bool> ShowTransformControlsProperty =
        AvaloniaProperty.Register<ImageCanvas, bool>(nameof(ShowTransformControls));

    /// <summary>Show Transform Controls (the Move tool's option).</summary>
    public bool ShowTransformControls { get => GetValue(ShowTransformControlsProperty); set => SetValue(ShowTransformControlsProperty, value); }

    /// <summary>A Move tool press, in document pixels, before any drag (Auto-Select changes the selected layers here).</summary>
    public event Action<Point, KeyModifiers>? MovePress;

    /// <summary>Starts Free Transform on the selected layers (a transform-control handle was grabbed); false if refused.</summary>
    public Func<bool>? BeginTransformFromControls { get; set; }

    private static readonly TransformHandle[] ControlHandles =
    [
        TransformHandle.TopLeft, TransformHandle.Top, TransformHandle.TopRight, TransformHandle.Right,
        TransformHandle.BottomRight, TransformHandle.Bottom, TransformHandle.BottomLeft, TransformHandle.Left,
    ];

    private static (double X, double Y) HandlePoint(PixelRect b, TransformHandle h) => h switch
    {
        TransformHandle.TopLeft => (b.Left, b.Top),
        TransformHandle.Top => ((b.Left + b.Right) / 2.0, b.Top),
        TransformHandle.TopRight => (b.Right, b.Top),
        TransformHandle.Right => (b.Right, (b.Top + b.Bottom) / 2.0),
        TransformHandle.BottomRight => (b.Right, b.Bottom),
        TransformHandle.Bottom => ((b.Left + b.Right) / 2.0, b.Bottom),
        TransformHandle.BottomLeft => (b.Left, b.Bottom),
        _ => (b.Left, (b.Top + b.Bottom) / 2.0),
    };

    private PixelRect? TransformControlsBox() =>
        Tool == CanvasTool.Move && ShowTransformControls && FreeTransform is null && CropBox is null ? MoveBounds?.Invoke() : null;

    /// <summary>
    /// A press with the Move tool: a grabbed transform-control handle opens Free Transform and starts dragging it (returns
    /// true: the press is taken); otherwise Auto-Select runs and the press goes on to move the layers (false).
    /// </summary>
    private bool MoveToolPressed(PointerPressedEventArgs e, PointerPointProperties props)
    {
        if (Tool != CanvasTool.Move || _panning || !props.IsLeftButtonPressed || FreeTransform is not null) return false;
        var p = ToImage(e.GetPosition(this));
        if (TransformControlsBox() is { } box)
        {
            double grab = HandleGrab / Zoom;
            bool onHandle = ControlHandles.Any(h => HandlePoint(box, h) is var (hx, hy) && Math.Abs(hx - p.X) <= grab && Math.Abs(hy - p.Y) <= grab);
            if (onHandle && BeginTransformFromControls?.Invoke() == true && FreeTransform is not null)
                return TransformPressed(e);
        }
        MovePress?.Invoke(p, e.KeyModifiers);
        return false;
    }

    private void RenderTransformControls(DrawingContext context)
    {
        if (TransformControlsBox() is not { } box) return;
        var rect = new Rect(ToScreen((box.Left, box.Top)), ToScreen((box.Right, box.Bottom)));
        context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), 2.5), rect);
        context.DrawRectangle(null, new Pen(Brushes.White, 1), rect);
        var edge = new Pen(new SolidColorBrush(Color.FromArgb(220, 0, 0, 0)), 1);
        foreach (var h in ControlHandles)
        {
            var s = ToScreen(HandlePoint(box, h));
            context.DrawRectangle(Brushes.White, edge, new Rect(s.X - HandleSize / 2, s.Y - HandleSize / 2, HandleSize, HandleSize));
        }
    }
}
