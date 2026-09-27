namespace Strayta.Core;

/// <summary>Uniform access to the layer mask that pixel layers, groups and adjustment layers can each carry.</summary>
public static class LayerMasks
{
    public static bool CanHaveMask(this LayerNode node) => node is PixelLayer or LayerGroup or AdjustmentLayer;

    public static LayerMask? GetMask(this LayerNode node) => node switch
    {
        PixelLayer p => p.Mask,
        LayerGroup g => g.Mask,
        AdjustmentLayer a => a.Mask,
        _ => null,
    };

    public static void SetMask(this LayerNode node, LayerMask? mask)
    {
        switch (node)
        {
            case PixelLayer p: p.Mask = mask; break;
            case LayerGroup g: g.Mask = mask; break;
            case AdjustmentLayer a: a.Mask = mask; break;
            default: throw new NotSupportedException($"{node.GetType().Name} cannot have a layer mask.");
        }
    }

    /// <summary>
    /// Photoshop's Reveal All (white) or Hide All (black): a mask with no pixels, just a default color.
    /// Painting it later allocates pixels only where the brush went.
    /// </summary>
    public static LayerMask Solid(bool reveal) => new() { Bounds = PixelRect.Empty, DefaultColor = reveal ? (byte)255 : (byte)0 };

    /// <summary>A copy that is disabled (ignored when rendering) or enabled; pixels are shared, masks are immutable.</summary>
    public static LayerMask WithDisabled(this LayerMask m, bool disabled) => new()
    {
        Bounds = m.Bounds,
        Pixels = m.Pixels,
        DefaultColor = m.DefaultColor,
        Disabled = disabled,
        PositionRelativeToLayer = m.PositionRelativeToLayer,
    };
}
