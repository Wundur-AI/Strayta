using Strayta.Core;

namespace Strayta.Editor.Editing;

/// <summary>
/// A committed Free Transform: new pixels, bounds, masks and file data for every layer it touched (one layer, or
/// all layers in a group). The file data carries live content (type transform, smart object corners, vector
/// outlines) moved with the transform, so those layers stay editable. Rasters are immutable, so undo just swaps
/// references back.
/// </summary>
public sealed class TransformEdit : IEdit
{
    /// <summary>What a transform changes on one layer. Groups and adjustment layers only have a mask.</summary>
    public sealed record State(Raster? Pixels, PixelRect Bounds, LayerMask? Mask, object? Source);

    private readonly List<(LayerNode Node, State Before, State After)> _changes;

    public TransformEdit(IEnumerable<(LayerNode Node, State After)> changes, string description = "Free Transform")
    {
        _changes = changes.Select(c => (c.Node, Read(c.Node), c.After)).ToList();
        Description = description;
    }

    /// <summary>"Free Transform", "Warp", "Distort", "Rotate 180°", "Puppet Warp", "Liquify", ...</summary>
    public string Description { get; }
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
        PixelLayer p => new State(p.Pixels, p.Bounds, p.Mask, p.SourceData),
        AdjustmentLayer a => new State(null, PixelRect.Empty, a.Mask, a.SourceData),
        LayerGroup g => new State(null, PixelRect.Empty, g.Mask, g.SourceData),
        _ => new State(null, PixelRect.Empty, null, node.SourceData),
    };

    private static void Write(LayerNode node, State s)
    {
        node.SourceData = s.Source;
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
