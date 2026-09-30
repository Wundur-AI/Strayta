using Strayta.Core;
using Strayta.Core.Painting;

namespace Strayta.Rendering;

/// <summary>
/// Gray values (0..1) of a channel at the pixels of a rendered view, which may be reduced by a factor (a preview):
/// view pixel (x, y) stands for document pixel (x·factor + factor/2, y·factor + factor/2).
/// </summary>
public interface IChannelSampler
{
    float Sample(int x, int y);
}

/// <summary>A canvas-sized channel plane sampled at a view's scale.</summary>
public sealed class PlaneSampler(Plane plane, int factor) : IChannelSampler
{
    public float Sample(int x, int y)
    {
        int dx = Math.Min(x * factor + factor / 2, plane.Width - 1), dy = Math.Min(y * factor + factor / 2, plane.Height - 1);
        return dx < 0 || dy < 0 ? 0f : plane.GetNormalized(dy * plane.Width + dx);
    }
}

/// <summary>A plane already at the view's scale (a preview computed on the reduced canvas).</summary>
public sealed class ViewPlaneSampler(Plane plane) : IChannelSampler
{
    public float Sample(int x, int y) =>
        x < plane.Width && y < plane.Height ? plane.GetNormalized(y * plane.Width + x) : 0f;
}

/// <summary>A layer mask (its default color outside its pixels) at a view's scale.</summary>
public sealed class MaskSampler(LayerMask mask, int factor) : IChannelSampler
{
    public float Sample(int x, int y) => MaskBaker.Sample(mask, x * factor + factor / 2, y * factor + factor / 2);
}

/// <summary>A channel with a gray stroke being painted into it (the live brush in a channel or mask).</summary>
public sealed class StrokeSampler(IChannelSampler inner, PaintStroke stroke, int factor) : IChannelSampler
{
    private readonly float _gray = stroke.Color.R, _opacity = stroke.Brush.Opacity;

    public float Sample(int x, int y)
    {
        float v = inner.Sample(x, y);
        float cov = stroke.CoverageAt(x * factor + factor / 2, y * factor + factor / 2) * _opacity;
        return cov > 0f ? v + (_gray - v) * cov : v;
    }
}

/// <summary>How a visible channel other than the color channels is drawn.</summary>
public enum ChannelOverlayKind
{
    /// <summary>A saved selection, quick mask or layer mask: its color over the dark (masked) parts, at its opacity.</summary>
    Mask,

    /// <summary>A spot color: its ink printed over the image (multiplied), dark parts full ink, scaled by solidity.</summary>
    Spot,
}

/// <summary>A visible channel drawn over (or, alone, as) the image.</summary>
public sealed record ChannelOverlay(IChannelSampler Sampler, RgbColor Color, float Opacity, ChannelOverlayKind Kind);

/// <summary>
/// What the Channels panel's eyes show, applied to a rendered image (RGBA, straight alpha): all color channels is the
/// image itself; one is that channel in gray; some but not all show in their colors (the others at zero); saved
/// selections and masks tint the parts they mask in their color, and spot channels print their ink. With no color
/// channel visible, the first visible channel shows in gray and the rest over it.
/// </summary>
public sealed class ChannelView
{
    /// <param name="colorVisible">Per color channel (red, green, blue; or gray), whether its eye is on.</param>
    /// <param name="overlays">Visible saved selections, spot channels and masks, drawn in order.</param>
    public ChannelView(IReadOnlyList<bool> colorVisible, IReadOnlyList<ChannelOverlay> overlays)
    {
        ColorVisible = colorVisible;
        Overlays = overlays;
    }

    public IReadOnlyList<bool> ColorVisible { get; }
    public IReadOnlyList<ChannelOverlay> Overlays { get; }

    /// <summary>True when the view is just the rendered image.</summary>
    public bool IsComposite => Overlays.Count == 0 && ColorVisible.All(v => v);

    /// <summary>Applies the view to <paramref name="rgba"/> in place.</summary>
    public void Apply(byte[] rgba, int width, int height, CancellationToken cancel = default)
    {
        if (IsComposite) return;
        int colors = ColorVisible.Count;
        int visible = ColorVisible.Count(v => v);
        bool gray = colors == 1 || visible == 1;
        int single = gray ? Math.Max(0, IndexOf(ColorVisible, true)) : -1;
        bool[] show = colors >= 3 ? [ColorVisible[0], ColorVisible[1], ColorVisible[2]] : [true, true, true];
        var overlays = Overlays;
        int firstOverlay = 0;
        IChannelSampler? baseChannel = null;
        if (visible == 0 && overlays.Count > 0)
        {
            baseChannel = overlays[0].Sampler;
            firstOverlay = 1;
        }

        Parallel.For(0, height, new ParallelOptions { CancellationToken = cancel }, y =>
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                float r, g, b;
                if (baseChannel is not null)
                {
                    r = g = b = baseChannel.Sample(x, y);
                }
                else if (visible == 0)
                {
                    r = g = b = 1f;
                }
                else
                {
                    // Channel views show the image flattened over white.
                    float a = rgba[i + 3] / 255f;
                    r = rgba[i] / 255f * a + 1f - a;
                    g = rgba[i + 1] / 255f * a + 1f - a;
                    b = rgba[i + 2] / 255f * a + 1f - a;
                    if (gray)
                    {
                        float v = colors == 1 ? r : single switch { 0 => r, 1 => g, _ => b };
                        r = g = b = v;
                    }
                    else
                    {
                        if (!show[0]) r = 0f;
                        if (!show[1]) g = 0f;
                        if (!show[2]) b = 0f;
                    }
                }

                for (int k = firstOverlay; k < overlays.Count; k++)
                {
                    var o = overlays[k];
                    float v = o.Sampler.Sample(x, y);
                    float amount = (1f - v) * o.Opacity;
                    if (amount <= 0f) continue;
                    if (o.Kind == ChannelOverlayKind.Spot)
                    {
                        r *= 1f - amount + amount * o.Color.R;
                        g *= 1f - amount + amount * o.Color.G;
                        b *= 1f - amount + amount * o.Color.B;
                    }
                    else
                    {
                        r += (o.Color.R - r) * amount;
                        g += (o.Color.G - g) * amount;
                        b += (o.Color.B - b) * amount;
                    }
                }

                rgba[i] = ToByte(r);
                rgba[i + 1] = ToByte(g);
                rgba[i + 2] = ToByte(b);
                rgba[i + 3] = 255;
            }
        });
    }

    private static int IndexOf(IReadOnlyList<bool> list, bool value)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i] == value) return i;
        return -1;
    }

    private static byte ToByte(float v) => (byte)Math.Clamp(MathF.Round(v * 255f), 0f, 255f);
}
