using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Strayta.Editor.Controls;

/// <summary>
/// Draws <see cref="Fill"/> through a bitmap's alpha (brush tip and stroke thumbnails), so the thumbnail takes the
/// theme's text color in light and dark appearance alike. Drawn at the bitmap's own size, centered.
/// </summary>
public sealed class MaskImage : Control
{
    public static readonly StyledProperty<Bitmap?> MaskProperty = AvaloniaProperty.Register<MaskImage, Bitmap?>(nameof(Mask));
    public static readonly StyledProperty<IBrush?> FillProperty = AvaloniaProperty.Register<MaskImage, IBrush?>(nameof(Fill));

    static MaskImage() => AffectsRender<MaskImage>(MaskProperty, FillProperty);

    public Bitmap? Mask { get => GetValue(MaskProperty); set => SetValue(MaskProperty, value); }
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }

    protected override Size MeasureOverride(Size availableSize) =>
        Mask is { } m ? new Size(m.PixelSize.Width, m.PixelSize.Height) : default;

    public override void Render(DrawingContext context)
    {
        if (Mask is not { } mask || Fill is not { } fill) return;
        var size = new Size(mask.PixelSize.Width, mask.PixelSize.Height);
        var rect = new Rect(new Point((Bounds.Width - size.Width) / 2, (Bounds.Height - size.Height) / 2), size);
        using (context.PushOpacityMask(new ImageBrush(mask) { Stretch = Stretch.Fill, DestinationRect = new RelativeRect(rect, RelativeUnit.Absolute) }, rect))
            context.FillRectangle(fill, rect);
    }
}
