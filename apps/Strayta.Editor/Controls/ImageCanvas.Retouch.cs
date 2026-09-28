using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Strayta.Editor.Controls;

// Clone Stamp and Healing Brush on the canvas: Option-click sets the source point, and while the brush hovers the
// canvas shows what it would paint (the image at the source, clipped to the brush, as Photoshop's clone overlay) with a
// crosshair at the source; while painting the crosshair follows the sampled point. Strokes themselves go through the
// brush's StrokeBegin/StrokeMove/StrokeEnd, as for the Spot Healing Brush.
public sealed partial class ImageCanvas
{
    /// <summary>Option-click with the Clone Stamp or Healing Brush, at an image pixel.</summary>
    public event Action<int, int>? RetouchSourcePicked;

    /// <summary>Where the source is for the brush at an image point, or null when there is none to show.</summary>
    public Func<float, float, (float X, float Y)?>? RetouchSource { get; set; }

    private bool IsRetouchTool => Tool is CanvasTool.CloneStamp or CanvasTool.SpotHealing or CanvasTool.Healing;

    private bool UsesSourcePoint => Tool is CanvasTool.CloneStamp or CanvasTool.Healing;

    /// <summary>Handles Option-click with a tool that has a source point; false for every other press.</summary>
    private bool RetouchPressed(PointerPressedEventArgs e, PointerPointProperties props)
    {
        if (!UsesSourcePoint || !props.IsLeftButtonPressed || !e.KeyModifiers.HasFlag(KeyModifiers.Alt)) return false;
        var p = ToImage(e.GetPosition(this));
        RetouchSourcePicked?.Invoke((int)Math.Floor(p.X), (int)Math.Floor(p.Y));
        InvalidateVisual();
        return true;
    }

    private void RenderRetouch(DrawingContext context)
    {
        if (!UsesSourcePoint || _spaceHeld || FreeTransform is not null || CropBox is not null || _hover is not { } h) return;
        var image = ToImage(h);
        if (RetouchSource?.Invoke((float)image.X, (float)image.Y) is not { } src) return;
        var at = new Point(src.X * Zoom + _offset.X, src.Y * Zoom + _offset.Y);

        // The overlay: the image around the source drawn under the brush, so you see what a click would paint. It is
        // the displayed image moved, so it costs nothing extra; while painting the stroke itself shows instead.
        if (!_stroking && !_altHeld && Source is { } bmp)
        {
            double r = Math.Max(1.5, BrushSize * Zoom / 2);
            var size = ImageSize;
            var shift = h - at;
            var dest = new Rect(_offset.X + shift.X, _offset.Y + shift.Y, size.Width * Zoom, size.Height * Zoom);
            using (context.PushGeometryClip(new EllipseGeometry(new Rect(h.X - r, h.Y - r, 2 * r, 2 * r))))
            using (context.PushOpacity(0.8))
            using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
                context.DrawImage(bmp, new Rect(0, 0, bmp.PixelSize.Width, bmp.PixelSize.Height), dest);
        }

        // Photoshop's source crosshair: dark under light so it reads over any image.
        const double arm = 7;
        var dark = new Pen(new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)), 3);
        var light = new Pen(Brushes.White, 1.2);
        foreach (var pen in new[] { dark, light })
        {
            context.DrawLine(pen, new Point(at.X - arm, at.Y), new Point(at.X + arm, at.Y));
            context.DrawLine(pen, new Point(at.X, at.Y - arm), new Point(at.X, at.Y + arm));
        }
    }
}
