using Strayta.Core;

namespace Strayta.Editor.Editing;

/// <summary>
/// Any change to the layer tree's shape made by code that edits the model directly (grouping, merging, deleting or
/// reordering several layers): the children of every group involved are remembered before and after, and undo and redo
/// swap them. Property changes the same code makes to layers are recorded too, through <see cref="Track"/>.
/// </summary>
public sealed class TreeEdit : IEdit
{
    private readonly List<(LayerGroup Group, LayerNode[] Before, LayerNode[] After)> _lists = [];
    private readonly List<(Action Undo, Action Redo)> _properties;
    private bool _applied;

    /// <summary>
    /// Runs <paramref name="change"/> (which may add, remove or move nodes among <paramref name="groups"/>, and set
    /// properties through the tracker it gets), records the difference and puts the tree back, so pushing the edit
    /// applies it as one undoable step.
    /// </summary>
    public TreeEdit(string description, IEnumerable<LayerGroup> groups, Action<Tracker> change)
    {
        Description = description;
        var involved = groups.Distinct().ToList();
        var before = involved.Select(g => g.Children.ToArray()).ToList();
        var tracker = new Tracker();
        change(tracker);
        // Groups created by the change (a new group, say) are involved too.
        foreach (var g in tracker.NewGroups.Except(involved)) { involved.Add(g); before.Add([]); }
        for (int i = 0; i < involved.Count; i++) _lists.Add((involved[i], before[i], involved[i].Children.ToArray()));
        _properties = tracker.Changes;
        _applied = true;
        Undo();
    }

    public string Description { get; }
    public bool ChangesStructure => true;

    /// <summary>True when the change did nothing (no lists or properties differ).</summary>
    public bool IsEmpty => _properties.Count == 0 && _lists.All(l => l.Before.SequenceEqual(l.After));

    public void Do()
    {
        if (_applied) return;
        SetLists(after: true);
        foreach (var (_, redo) in _properties) redo();
        _applied = true;
    }

    public void Undo()
    {
        if (!_applied) return;
        for (int i = _properties.Count - 1; i >= 0; i--) _properties[i].Undo();
        SetLists(after: false);
        _applied = false;
    }

    private void SetLists(bool after)
    {
        // Empty every group first, so a node moving between two groups is never in both.
        foreach (var (group, _, _) in _lists)
            foreach (var child in group.Children.ToArray()) group.Remove(child);
        foreach (var (group, before, afterList) in _lists)
            foreach (var child in after ? afterList : before)
            {
                child.Parent?.Remove(child);
                group.Add(child);
            }
    }

    /// <summary>Records property changes made during the change, so undo can put them back.</summary>
    public sealed class Tracker
    {
        internal List<(Action Undo, Action Redo)> Changes { get; } = [];
        internal List<LayerGroup> NewGroups { get; } = [];

        /// <summary>Sets a value, remembering the old one.</summary>
        public void Set<T>(LayerNode node, Func<LayerNode, T> get, Action<LayerNode, T> set, T value)
        {
            var old = get(node);
            if (EqualityComparer<T>.Default.Equals(old, value)) return;
            set(node, value);
            Changes.Add((() => set(node, old), () => set(node, value)));
        }

        /// <summary>A group made by the change, whose children the edit must remember.</summary>
        public void Created(LayerGroup group) => NewGroups.Add(group);
    }
}

/// <summary>
/// Moves several layers by the same offset (the Move tool and arrow keys with several layers selected or linked);
/// consecutive moves of the same layers merge into one step, as with a single layer.
/// </summary>
public sealed class MultiMoveEdit(IReadOnlyList<LayerNode> nodes, int dx, int dy, int canvasWidth, int canvasHeight, string description = "Move") : IEdit
{
    private readonly List<MoveEdit> _moves = nodes.Select(n => new MoveEdit(n, dx, dy, canvasWidth, canvasHeight)).ToList();

    public IReadOnlyList<LayerNode> Nodes { get; } = nodes;
    public string Description { get; } = description;
    public bool ChangesStructure => false;

    public void Do()
    {
        foreach (var m in _moves) m.Do();
    }

    public void Undo()
    {
        foreach (var m in _moves) m.Undo();
    }

    public bool TryMerge(IEdit next)
    {
        if (next is not MultiMoveEdit m || m.Description != Description || !m.Nodes.SequenceEqual(Nodes)) return false;
        for (int i = 0; i < _moves.Count; i++) _moves[i].TryMerge(m._moves[i]);
        return true;
    }
}

/// <summary>
/// Moves layers by different offsets in one step (Align, Distribute).
/// </summary>
public sealed class OffsetsEdit(IReadOnlyList<(LayerNode Node, int Dx, int Dy)> moves, int canvasWidth, int canvasHeight, string description) : IEdit
{
    private readonly List<MoveEdit> _moves = moves.Where(m => m.Dx != 0 || m.Dy != 0).Select(m => new MoveEdit(m.Node, m.Dx, m.Dy, canvasWidth, canvasHeight)).ToList();

    public string Description { get; } = description;
    public bool ChangesStructure => false;
    public bool IsEmpty => _moves.Count == 0;

    public void Do()
    {
        foreach (var m in _moves) m.Do();
    }

    public void Undo()
    {
        foreach (var m in _moves) m.Undo();
    }
}

/// <summary>
/// Changes one property of several layers at once (opacity, fill or blend mode with several layers selected, locks,
/// color labels, links, visibility); consecutive changes of the same property on the same layers merge.
/// </summary>
public sealed class MultiPropertyEdit<T> : IEdit
{
    private readonly Action<LayerNode, T> _set;
    private readonly T[] _before;
    private T[] _after;

    /// <summary>Gives each layer the value <paramref name="after"/> computes for it.</summary>
    public MultiPropertyEdit(IReadOnlyList<LayerNode> nodes, string property, Func<LayerNode, T> get, Func<LayerNode, T> after, Action<LayerNode, T> set, string description)
    {
        Nodes = nodes;
        Property = property;
        Description = description;
        _set = set;
        _before = nodes.Select(get).ToArray();
        _after = nodes.Select(after).ToArray();
    }

    public IReadOnlyList<LayerNode> Nodes { get; }
    public string Property { get; }
    public string Description { get; }
    public bool ChangesStructure => false;

    /// <summary>True when no layer's value changes.</summary>
    public bool IsEmpty => _before.SequenceEqual(_after);

    public void Do()
    {
        for (int i = 0; i < Nodes.Count; i++) _set(Nodes[i], _after[i]);
    }

    public void Undo()
    {
        for (int i = 0; i < Nodes.Count; i++) _set(Nodes[i], _before[i]);
    }

    public bool TryMerge(IEdit next)
    {
        if (next is not MultiPropertyEdit<T> p || p.Property != Property || !p.Nodes.SequenceEqual(Nodes)) return false;
        _after = p._after;
        return true;
    }
}
