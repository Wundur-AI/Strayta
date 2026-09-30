using Strayta.Core;
using Strayta.Rendering;
using Strayta.Rendering.Transforms;

namespace Strayta.Editor.ViewModels;

// Several selected layers, as in Photoshop's Layers panel: ⌘-click toggles a layer, ⇧-click selects a range of rows,
// Select › All Layers (⌥⌘A). SelectedLayer stays the primary layer (the one clicked last), which everything written
// for one layer keeps using; the commands in DocumentViewModel.LayerCommands.cs act on the whole set.
public sealed partial class DocumentViewModel
{
    private readonly List<LayerNode> _selectedNodes = [];
    private LayerNode? _selectionAnchor;
    private bool _settingSelection;

    /// <summary>The selected layers in the order they were selected; the primary (<see cref="SelectedLayer"/>) is among them.</summary>
    public IReadOnlyList<LayerNode> SelectedNodes => _selectedNodes;

    public int SelectedCount => _selectedNodes.Count;

    public bool HasMultipleSelected => _selectedNodes.Count > 1;

    public bool IsNodeSelected(LayerNode node) => _selectedNodes.Contains(node);

    /// <summary>Raised when the set of selected layers changes.</summary>
    public event Action? LayerSelectionChanged;

    /// <summary>The selected layers without those inside a selected group, bottom first.</summary>
    public List<LayerNode> SelectedTopLevel() => LayerMerger.TopLevel(Model, _selectedNodes);

    /// <summary>Called whenever <see cref="SelectedLayer"/> changes (DocumentViewModel.Masks.cs).</summary>
    private void OnPrimaryLayerChanged(LayerItemViewModel? oldValue, LayerItemViewModel? newValue)
    {
        if (_settingSelection) return;
        var node = newValue?.Node;
        if (node is null) _selectedNodes.Clear();
        else if (ReferenceEquals(oldValue?.Node, node) && _selectedNodes.Contains(node))
            _selectedNodes.RemoveAll(n => !InDocument(n)); // the rows were rebuilt: keep the set
        else
        {
            _selectedNodes.Clear();
            _selectedNodes.Add(node);
            _selectionAnchor = node;
        }
        NotifyLayerSelection();
    }

    private bool InDocument(LayerNode node)
    {
        for (var g = node.Parent; g is not null; g = g.Parent)
            if (ReferenceEquals(g, Model.Root)) return true;
        return false;
    }

    /// <summary>Selects <paramref name="nodes"/>; <paramref name="primary"/> (default: the last one) becomes <see cref="SelectedLayer"/>.</summary>
    public void SetLayerSelection(IEnumerable<LayerNode> nodes, LayerNode? primary = null)
    {
        var list = nodes.Where(InDocument).Distinct().ToList();
        primary = primary is not null && list.Contains(primary) ? primary : list.LastOrDefault();
        _selectedNodes.Clear();
        _selectedNodes.AddRange(list);
        _settingSelection = true;
        try
        {
            SelectedLayer = primary is null ? null : ItemFor(primary);
        }
        finally
        {
            _settingSelection = false;
        }
        NotifyLayerSelection();
    }

    private void NotifyLayerSelection()
    {
        foreach (var item in Layers.SelectMany(l => l.SelfAndDescendants())) item.RefreshSelection();
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasMultipleSelected));
        UpdateProperties();
        LayerSelectionChanged?.Invoke();
    }

    /// <summary>The panel's item for <paramref name="node"/>, or null.</summary>
    public LayerItemViewModel? ItemFor(LayerNode node) => Layers.SelectMany(l => l.SelfAndDescendants()).FirstOrDefault(i => i.Node == node);

    /// <summary>
    /// A click on a row: alone it selects just that layer; ⌘ adds or removes it; ⇧ selects the rows from the last clicked
    /// one to this one (⌘⇧ adds them), as in Photoshop.
    /// </summary>
    public void ClickLayer(LayerItemViewModel item, bool command, bool shift)
    {
        var node = item.Node;
        if (shift && _selectionAnchor is { } anchor && InDocument(anchor))
        {
            int a = Rows.ToList().FindIndex(r => r.Node == anchor), b = Rows.IndexOf(item);
            if (a >= 0 && b >= 0)
            {
                var range = Rows.Skip(Math.Min(a, b)).Take(Math.Abs(a - b) + 1).Select(r => r.Node);
                SetLayerSelection(command ? _selectedNodes.Concat(range) : range, node);
                return;
            }
        }
        _selectionAnchor = node;
        if (command)
        {
            if (_selectedNodes.Contains(node))
            {
                var rest = _selectedNodes.Where(n => n != node).ToList();
                SetLayerSelection(rest, ReferenceEquals(SelectedLayer?.Node, node) ? rest.LastOrDefault() : SelectedLayer?.Node);
            }
            else SetLayerSelection(_selectedNodes.Append(node), node);
            return;
        }
        SetLayerSelection([node], node);
    }

    /// <summary>Select › All Layers (⌥⌘A): every layer, inside groups too.</summary>
    public void SelectAllLayers()
    {
        var all = Model.Root.Descendants().Where(n => n is not LayerGroup).ToList();
        if (all.Count == 0) all = Model.Root.Descendants().ToList();
        SetLayerSelection(all, SelectedLayer?.Node is { } p && all.Contains(p) ? p : all.LastOrDefault());
    }

    /// <summary>Select › Deselect Layers.</summary>
    public void DeselectLayers() => SetLayerSelection([]);

    /// <summary>The kind Select Similar Layers compares (Photoshop's filter kinds).</summary>
    public static string KindOf(LayerNode node) => node switch
    {
        LayerGroup => "group",
        AdjustmentLayer => "adjustment",
        _ when node.Tags.Contains("text") => "type",
        _ when node.Tags.Contains("smart-object") => "smart",
        _ when node.Tags.Contains("shape") || node.Tags.Contains("fill") => "shape",
        _ => "pixel",
    };

    /// <summary>Select › Similar Layers: every layer of the primary layer's kind.</summary>
    public void SelectSimilarLayers()
    {
        if (SelectedLayer?.Node is not { } primary) return;
        var kind = KindOf(primary);
        SetLayerSelection(Model.Root.Descendants().Where(n => KindOf(n) == kind), primary);
    }

    /// <summary>Layer › Select Linked Layers: the selected layers and every layer linked to them.</summary>
    public void SelectLinkedLayers() => SetLayerSelection(WithLinked(_selectedNodes), SelectedLayer?.Node);

    /// <summary><paramref name="nodes"/> plus every layer sharing a link with one of them.</summary>
    private List<LayerNode> WithLinked(IEnumerable<LayerNode> nodes)
    {
        var list = nodes.ToList();
        var links = list.Select(n => n.LinkGroup).Where(l => l != 0).ToHashSet();
        if (links.Count > 0) list.AddRange(Model.Root.Descendants().Where(n => links.Contains(n.LinkGroup) && !list.Contains(n)));
        return list;
    }

    // ---- Bounds ----------------------------------------------------------------------------------------

    /// <summary>A layer's visible pixels (a group's: all its layers'), in document pixels; empty for none.</summary>
    public static PixelRect ContentBounds(LayerNode node) => node switch
    {
        PixelLayer p => Resampler.ContentBounds(p),
        LayerGroup g => LayerAlignment.Union(g.Descendants().OfType<PixelLayer>().Select(Resampler.ContentBounds)),
        _ => PixelRect.Empty,
    };

    /// <summary>What the Move tool moves: the selected layers and those linked to them, visible, without nesting.</summary>
    private List<LayerNode> MoveTargets() =>
        LayerMerger.TopLevel(Model, WithLinked(_selectedNodes)).Where(n => n.Visible).ToList();

    /// <summary>The box the Move tool drags (the snapping reference), for the whole selection.</summary>
    private PixelRect? SelectionMovingBounds()
    {
        var box = LayerAlignment.Union(MoveTargets().Select(ContentBounds));
        return box.IsEmpty ? null : box;
    }

    // ---- Filtering (the panel's filter bar) ----------------------------------------------------------

    /// <summary>With the filter on, the rows are every matching layer (at any depth) in stacking order; otherwise null.</summary>
    private List<LayerItemViewModel>? FilteredRows()
    {
        if (!Editor.LayerFilter.IsActive) return null;
        return Layers.SelectMany(l => l.SelfAndDescendants()).Where(i => Editor.LayerFilter.Matches(i.Node, _selectedNodes)).ToList();
    }
}
