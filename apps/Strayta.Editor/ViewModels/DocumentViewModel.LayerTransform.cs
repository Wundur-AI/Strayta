using Strayta.Core;
using Strayta.Rendering;

namespace Strayta.Editor.ViewModels;

// Free Transform and the Properties panel with several layers selected (or linked): one box around all of them, and the
// Align and Distribute section instead of a single layer's properties.
public sealed partial class DocumentViewModel
{
    /// <summary>
    /// The layers Free Transform changes: the primary layer (a group with everything inside), or with several selected
    /// or linked layers, all of them.
    /// </summary>
    private List<LayerNode> TransformTargets(LayerNode? primary)
    {
        if (primary is null) return [];
        var roots = HasMultipleSelected || primary.LinkGroup != 0
            ? LayerMerger.TopLevel(Model, WithLinked(_selectedNodes.Contains(primary) ? _selectedNodes : [primary])).Where(n => n.Visible).ToList()
            : [primary];
        if (!roots.Contains(primary) && primary.Visible) roots.Add(primary);
        return roots.SelectMany(n => n is LayerGroup g ? g.Descendants().Prepend(g) : [n]).Distinct().ToList();
    }

    /// <summary>Free Transform is refused when any target's position or pixels are locked (Photoshop's rule).</summary>
    private static bool TransformLocked(IEnumerable<LayerNode> targets) =>
        targets.Any(n => n.IsLocked(LayerLocks.Position) || n is PixelLayer && n.IsLocked(LayerLocks.Pixels));

    /// <summary>With several layers selected the Properties panel shows Align and Distribute; returns false otherwise.</summary>
    private bool UpdateMultiLayerProperties()
    {
        if (_selectedNodes.Count < 2) return false;
        if (Properties is LayersAlignPanel current)
        {
            current.Refresh();
            return true;
        }
        Properties?.Dispose();
        Properties = new LayersAlignPanel(this);
        return true;
    }
}

/// <summary>The Properties panel for several selected layers: their count, and Align and Distribute buttons.</summary>
public sealed class LayersAlignPanel(DocumentViewModel document) : PropertiesPanel(document, null)
{
    public override string Title => $"{Document.SelectedCount} Layers";

    public override string Icon => "IconMove";

    /// <summary>Distribute needs three layers.</summary>
    public bool CanDistribute => Document.SelectedTopLevel().Count(n => !DocumentViewModel.ContentBounds(n).IsEmpty) >= 3;

    public EditorViewModel Editor => Document.Editor;
}
