using Strayta.Core;

namespace Strayta.Editor.Editing;

/// <summary>
/// A committed Free Transform: new pixels, bounds and masks for every layer it touched (one layer, or all
/// layers in a group). Rasters are immutable, so undo just swaps references back.
/// </summary>
public sealed class TransformEdit : IEdit
{
    /// <summary>What a transform changes on one layer. Groups and adjustment layers only have a mask.</summary>
    public sealed record State(Raster? Pixels, PixelRect Bounds, LayerMask? Mask);

    private readonly List<(LayerNode Node, State Before, State After)> _changes;

    public TransformEdit(IEnumerable<(LayerNode Node, State After)> changes) =>
        _changes = changes.Select(c => (c.Node, Read(c.Node), c.After)).ToList();

    public string Description => "Free Transform";
    public bool ChangesStructure => false;

    public void Do()
    {
        foreach (var (node, _, after) in _changes) Write(node, after);
    }

    public void Undo()
    {
        foreach (var (node, before, _) in _changes) Write(node, before);
    }

    public static State Read(LayerNode node) => node switch
    {
        PixelLayer p => new State(p.Pixels, p.Bounds, p.Mask),
        AdjustmentLayer a => new State(null, PixelRect.Empty, a.Mask),
        LayerGroup g => new State(null, PixelRect.Empty, g.Mask),
        _ => new State(null, PixelRect.Empty, null),
    };

    private static void Write(LayerNode node, State s)
    {
        switch (node)
        {
            case PixelLayer p:
                p.Pixels = s.Pixels;
                p.Bounds = s.Bounds;
                p.Mask = s.Mask;
                break;
            case AdjustmentLayer a:
                a.Mask = s.Mask;
                break;
            case LayerGroup g:
                g.Mask = s.Mask;
                break;
        }
    }
}
