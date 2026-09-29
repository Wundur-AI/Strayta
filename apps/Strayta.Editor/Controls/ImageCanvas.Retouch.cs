using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Strayta.Editor.Controls;

/// <summary>
/// What the canvas draws for the Clone Stamp and Healing Brush: the source point under the brush (image pixels), the
/// active clone source's placement (scale, angle in degrees counter-clockwise) and the Clone Source panel's overlay
/// options.
/// </summary>
public readonly record struct CloneOverlay(float SourceX, float SourceY, float ScaleX, float ScaleY, float Angle, bool Show, double Opacity, bool Clipped, bool AutoHide);

// Clone Stamp and Healing Brush on the canvas: Option-click sets the source point, and while the brush hovers the
// canvas shows what it would paint (the image at the source, placed with the clone source's scale and angle, clipped to
// the brush unless the panel says otherwise: Photoshop's clone overlay) with a crosshair at the source; while painting
// the crosshair follows the sampled point, and the overlay hides unless Auto Hide is off. Strokes themselves go
// through the brush's StrokeBegin/StrokeMove/StrokeEnd, as for the Spot Healing Brush.
public sealed partial class ImageCanvas
{
    /// <summary>Option-click with the Clone Stamp or Healing Brush, at an image pixel.</summary>
    public event Action<int, int>? RetouchSourcePicked;

    /// <summary>The clone overlay for the brush at an image point, or null when there is none to show.</summary>
    public Func<float, float, CloneOverlay?>? RetouchSource { get; set; }

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
        if (RetouchSource?.Invoke((float)image.X, (float)image.Y) is not { } o) return;
        var at = new Point(o.SourceX * Zoom + _offset.X, o.SourceY * Zoom + _offset.Y);

        // The overlay: the displayed image placed so the source point lands under the brush, scaled and turned as the
        // clone source will paint it. It is the displayed bitmap redrawn, so it costs nothing extra.
        if (o.Show && o.Opacity > 0 && (!_stroking || !o.AutoHide) && !_altHeld && Source is { } bmp)
        {
            double r = Math.Max(1.5, BrushSize * Zoom / 2);
            var size = ImageSize;
            // Screen = h + M · (image − source) · zoom, with M the clone source's scale and counter-clockwise turn.
            double a = o.Angle * Math.PI / 180, cos = Math.Cos(a), sin = Math.Sin(a);
            var place = Matrix.CreateTranslation(-o.SourceX, -o.SourceY)
                        * Matrix.CreateScale(o.ScaleX, o.ScaleY)
                        * new Matrix(cos, -sin, sin, cos, 0, 0)
                        * Matrix.CreateScale(Zoom, Zoom)
                        * Matrix.CreateTranslation(h.X, h.Y);
            using (o.Clipped ? context.PushGeometryClip(new EllipseGeometry(new Rect(h.X - r, h.Y - r, 2 * r, 2 * r))) : default(DrawingContext.PushedState?))
            using (context.PushOpacity(o.Clipped ? 0.8 * o.Opacity : 0.5 * o.Opacity))
            using (context.PushTransform(place))
            using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
                context.DrawImage(bmp, new Rect(0, 0, bmp.PixelSize.Width, bmp.PixelSize.Height), new Rect(0, 0, size.Width, size.Height));
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
