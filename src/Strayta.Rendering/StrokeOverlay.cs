using Strayta.Core;
using Strayta.Core.Painting;

namespace Strayta.Rendering;

/// <summary>
/// A stroke in progress, drawn live over its target layer. For previews it maps a full-resolution stroke
/// onto a downscaled proxy layer by sampling it at the center of each preview pixel. A mask stroke
/// (<see cref="TargetsMask"/>) is drawn into the target's layer mask instead of its pixels.
/// </summary>
public sealed class StrokeOverlay
{
    private readonly PaintStroke _stroke;
    private readonly int _factor;

    public StrokeOverlay(PaintStroke stroke, LayerNode target, int factor = 1)
    {
        _stroke = stroke;
        Target = target;
        _factor = factor;
        var b = stroke.Bounds;
        Bounds = factor == 1 ? b : new PixelRect(
            FloorDiv(b.Left, factor), FloorDiv(b.Top, factor), -FloorDiv(-b.Right, factor), -FloorDiv(-b.Bottom, factor));
    }

    public LayerNode Target { get; }
    public bool TargetsMask => _stroke.TargetsMask;
    public PixelRect Bounds { get; }
    public int Version => _stroke.Version;
    public bool Erase => _stroke.Erase;
    public float Opacity => _stroke.Brush.Opacity;
    public RgbColor Color => _stroke.Color;

    public float CoverageAt(int x, int y) =>
        _factor == 1 ? _stroke.CoverageAt(x, y) : _stroke.CoverageAt(x * _factor + _factor / 2, y * _factor + _factor / 2);

    /// <summary>True for cloning strokes, whose color comes from <see cref="ReadSource"/> pixel by pixel.</summary>
    public bool HasSource => _stroke.Source is not null;

    /// <summary>How the stroke combines with the layer (<see cref="PaintBlender"/>).</summary>
    public PaintMode Mode => _stroke.Mode;

    /// <summary>
    /// A cloning stroke's color at a (preview) pixel into <paramref name="color"/> (<paramref name="channels"/> entries);
    /// returns the source's alpha (0 while the source is not ready, so nothing shows yet). On a downscaled preview the
    /// source is averaged over the full-resolution pixels the preview pixel stands for (a box-filtered reduction sampled
    /// bilinearly), so cloned detail does not alias.
    /// </summary>
    public float ReadSource(int x, int y, Span<float> color, int channels) =>
        _stroke.Source is not { } source ? 0f
        : _factor == 1 ? source.Read(x, y, color, channels)
        : source.ReadArea(x * _factor, y * _factor, _factor, color, channels);

    /// <summary>
    /// The mask stroke painting <paramref name="node"/>'s mask, if any. Pixel strokes return null, so callers can
    /// pass the render's active stroke through unconditionally.
    /// </summary>
    internal static StrokeOverlay? ForMaskOf(StrokeOverlay? stroke, LayerNode node) =>
        stroke is { TargetsMask: true } s && ReferenceEquals(s.Target, node) ? s : null;

    private static int FloorDiv(int a, int b) => a >= 0 ? a / b : (a - b + 1) / b;
}
