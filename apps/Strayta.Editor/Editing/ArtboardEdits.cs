using Strayta.Core;

namespace Strayta.Editor.Editing;

/// <summary>
/// Changes a group's artboard (bounds, background, preset) or makes it one (or an ordinary group again, with null).
/// Steps of one resize drag merge into a single history step.
/// </summary>
public sealed class ArtboardEdit(LayerGroup group, Artboard? after, string description, bool mergeable = false) : ILayerPropertyEdit
{
    private readonly Artboard? _before = group.Artboard;
    private Artboard? _after = after;

    public LayerNode Node => group;
    public string Description => description;

    // The Layers panel shows artboards with their own icon, so becoming one (or not) changes the rows.
    public bool ChangesStructure => (_before is null) != (_after is null);

    public void Do() => group.Artboard = _after;
    public void Undo() => group.Artboard = _before;

    public bool TryMerge(IEdit next)
    {
        if (!mergeable || next is not ArtboardEdit e || !ReferenceEquals(e.Node, group) || e.Description != Description) return false;
        _after = e._after;
        return true;
    }
}

/// <summary>
/// Artboards from Layers: puts a new artboard group into the root and moves the given layers into it, in their
/// stacking order. Undo puts every layer back where it was.
/// </summary>
public sealed class ArtboardFromLayersEdit(Document doc, LayerGroup group, IReadOnlyList<LayerNode> nodes) : IEdit
{
    private List<(LayerNode Node, LayerGroup Parent, int Index)>? _origin;

    public string Description => "Artboard from Layers";
    public bool ChangesStructure => true;

    public void Do()
    {
        var order = doc.Root.Descendants().ToList();
        var moving = nodes.OrderBy(order.IndexOf).ToList();
        _origin = moving.Select(n => (n, n.Parent!, n.Parent!.IndexOf(n))).ToList();

        // Above the top-level layer that holds the topmost of them.
        var anchor = moving[^1];
        while (!ReferenceEquals(anchor.Parent, doc.Root)) anchor = anchor.Parent!;
        int anchorIndex = doc.Root.IndexOf(anchor);
        int index = anchorIndex + 1 - _origin.Count(o => ReferenceEquals(o.Parent, doc.Root) && o.Index <= anchorIndex);

        foreach (var n in moving) n.Parent!.Remove(n);
        doc.Root.Insert(Math.Clamp(index, 0, doc.Root.Children.Count), group);
        foreach (var n in moving) group.Add(n);
    }

    public void Undo()
    {
        foreach (var (node, _, _) in _origin!) group.Remove(node);
        doc.Root.Remove(group);
        foreach (var (node, parent, index) in _origin.OrderBy(o => o.Index)) parent.Insert(Math.Min(index, parent.Children.Count), node);
    }
}
