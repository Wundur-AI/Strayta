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

    /// <summary>Dodge / Burn / Sponge settings of a toning stroke (each pixel is changed by coverage × <see cref="Opacity"/>), or null.</summary>
    public ToneSettings? Tone => _stroke.Tone;

    /// <summary>True for Blur / Sharpen / Smudge strokes, whose pixels come from <see cref="ReadLocal"/> wherever the stroke has been.</summary>
    public bool IsLocal => _stroke.Local is not null;

    /// <summary>
    /// A Blur / Sharpen / Smudge stroke's working pixel (straight color into <paramref name="color"/>, gray replicated to
    /// <paramref name="channels"/>) at a (preview) pixel; returns its alpha. A downscaled preview averages the
    /// full-resolution pixels the preview pixel stands for.
    /// </summary>
    public float ReadLocal(int x, int y, Span<float> color, int channels)
    {
        var local = _stroke.Local!;
        float a = _factor == 1 ? local.Read(x, y, color) : local.ReadArea(x * _factor, y * _factor, _factor, color);
        for (int k = local.Channels; k < channels; k++) color[k] = color[0];
        return a;
    }

    /// <summary>
    /// A toning or focus stroke's new value for a mask's gray <paramref name="value"/> at a (preview) pixel with coverage
    /// × opacity <paramref name="cov"/>.
    /// </summary>
    public float ToolGray(int x, int y, float value, float cov)
    {
        if (_stroke.Local is not null)
        {
            Span<float> g = stackalloc float[3];
            return ReadLocal(x, y, g, 1) >= 0f ? g[0] : value;
        }
        return StrokeBaker.ToolGray(_stroke, x, y, value, cov);
    }

    /// <summary>
    /// The mask stroke painting <paramref name="node"/>'s mask, if any. Pixel strokes return null, so callers can
    /// pass the render's active stroke through unconditionally.
    /// </summary>
    internal static StrokeOverlay? ForMaskOf(StrokeOverlay? stroke, LayerNode node) =>
        stroke is { TargetsMask: true } s && ReferenceEquals(s.Target, node) ? s : null;

    private static int FloorDiv(int a, int b) => a >= 0 ? a / b : (a - b + 1) / b;
}
