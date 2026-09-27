using Strayta.Core;

namespace Strayta.Editor.Editing;

/// <summary>
/// Replaces a layer's mask: add (Reveal All / Hide All), delete, disable or enable, or a committed mask stroke.
/// Masks are immutable, so undo just swaps references.
/// </summary>
public sealed class MaskEdit(LayerNode node, LayerMask? mask, string description) : ILayerPropertyEdit
{
    private readonly LayerMask? _old = node.GetMask();

    public LayerNode Node { get; } = node;
    public string Description => description;
    public bool ChangesStructure => false;

    public void Do() => Node.SetMask(mask);
    public void Undo() => Node.SetMask(_old);
}

/// <summary>Layer › Layer Mask › Apply: bakes the mask into the layer's transparency and removes it.</summary>
public sealed class ApplyMaskEdit(PixelLayer layer, Raster? pixels) : ILayerPropertyEdit
{
    private readonly Raster? _oldPixels = layer.Pixels;
    private readonly LayerMask? _oldMask = layer.Mask;

    public LayerNode Node => layer;
    public string Description => "Apply Layer Mask";
    public bool ChangesStructure => false;

    public void Do()
    {
        layer.Pixels = pixels;
        layer.Mask = null;
    }

    public void Undo()
    {
        layer.Pixels = _oldPixels;
        layer.Mask = _oldMask;
    }
}
