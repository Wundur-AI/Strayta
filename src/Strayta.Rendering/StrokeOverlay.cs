using Strayta.Core;
using Strayta.Core.Painting;

namespace Strayta.Rendering;

/// <summary>
/// A stroke in progress, drawn live over its target layer. For previews it maps a full-resolution stroke
/// onto a downscaled proxy layer by sampling it at the center of each preview pixel.
/// </summary>
public sealed class StrokeOverlay
{
    private readonly PaintStroke _stroke;
    private readonly int _factor;

    public StrokeOverlay(PaintStroke stroke, PixelLayer target, int factor = 1)
    {
        _stroke = stroke;
        Target = target;
        _factor = factor;
        var b = stroke.Bounds;
        Bounds = factor == 1 ? b : new PixelRect(
            FloorDiv(b.Left, factor), FloorDiv(b.Top, factor), -FloorDiv(-b.Right, factor), -FloorDiv(-b.Bottom, factor));
    }

    public PixelLayer Target { get; }
    public PixelRect Bounds { get; }
    public int Version => _stroke.Version;
    public bool Erase => _stroke.Erase;
    public float Opacity => _stroke.Brush.Opacity;
    public RgbColor Color => _stroke.Color;

    public float CoverageAt(int x, int y) =>
        _factor == 1 ? _stroke.CoverageAt(x, y) : _stroke.CoverageAt(x * _factor + _factor / 2, y * _factor + _factor / 2);

    private static int FloorDiv(int a, int b) => a >= 0 ? a / b : (a - b + 1) / b;
}
