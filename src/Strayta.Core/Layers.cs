namespace Strayta.Core;

/// <summary>Base for anything in the layer tree.</summary>
public abstract class LayerNode
{
    public string Name { get; set; } = "";
    public bool Visible { get; set; } = true;

    /// <summary>Overall opacity, 0..1. Applies to the layer and its effects.</summary>
    public float Opacity { get; set; } = 1f;

    /// <summary>Fill opacity, 0..1. Applies to the layer's own pixels but not its effects.</summary>
    public float FillOpacity { get; set; } = 1f;

    public BlendMode BlendMode { get; set; } = BlendMode.Normal;

    /// <summary>True when this layer is clipped to the nearest unclipped layer beneath it.</summary>
    public bool Clipped { get; set; }

    public LayerGroup? Parent { get; internal set; }

    /// <summary>
    /// Opaque data a format reader attaches so its writer can preserve content this model does not
    /// represent (text, smart objects, unknown settings). Keep it when moving or editing the node;
    /// new layers leave it null.
    /// </summary>
    public object? SourceData { get; set; }

    /// <summary>Layer styles (drop shadow, stroke, overlays, ...), or null if the layer has none.</summary>
    public LayerEffects? Effects { get; set; }

    /// <summary>
    /// Descriptive tags set by format readers, e.g. "text", "smart-object", "effects", "adjustment".
    /// Renderers use them to report content they cannot reproduce yet.
    /// </summary>
    public ISet<string> Tags { get; } = new HashSet<string>(StringComparer.Ordinal);
}

/// <summary>A layer that carries its own pixels.</summary>
public sealed class PixelLayer : LayerNode
{
    /// <summary>Position of <see cref="Pixels"/> within the document.</summary>
    public PixelRect Bounds { get; set; }

    /// <summary>Layer pixels covering <see cref="Bounds"/>; null for empty layers.</summary>
    public Raster? Pixels { get; set; }

    public LayerMask? Mask { get; set; }

    /// <summary>Transparent pixels are locked against painting.</summary>
    public bool TransparencyLocked { get; set; }
}

/// <summary>A folder of layers, composited together before blending with what is below.</summary>
public sealed class LayerGroup : LayerNode
{
    private readonly List<LayerNode> _children = [];

    public LayerGroup() => BlendMode = BlendMode.PassThrough;

    /// <summary>Children in compositing order: index 0 is the bottom-most.</summary>
    public IReadOnlyList<LayerNode> Children => _children;

    public bool Expanded { get; set; } = true;

    public LayerMask? Mask { get; set; }

    public void Add(LayerNode child) => Insert(_children.Count, child);

    /// <summary>Inserts <paramref name="child"/> at <paramref name="index"/> (0 = bottom-most).</summary>
    public void Insert(int index, LayerNode child)
    {
        if (child.Parent is not null)
            throw new InvalidOperationException($"Layer '{child.Name}' already belongs to group '{child.Parent.Name}'.");
        for (var g = this; g is not null; g = g.Parent)
            if (ReferenceEquals(g, child))
                throw new InvalidOperationException("A group cannot be placed inside itself.");
        child.Parent = this;
        _children.Insert(index, child);
    }

    public int IndexOf(LayerNode child) => _children.IndexOf(child);

    public bool Remove(LayerNode child)
    {
        if (!_children.Remove(child)) return false;
        child.Parent = null;
        return true;
    }

    /// <summary>Every descendant, depth-first, bottom to top.</summary>
    public IEnumerable<LayerNode> Descendants()
    {
        foreach (var child in _children)
        {
            yield return child;
            if (child is LayerGroup g)
                foreach (var d in g.Descendants())
                    yield return d;
        }
    }
}
