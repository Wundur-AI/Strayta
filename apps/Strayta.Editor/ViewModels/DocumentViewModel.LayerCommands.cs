using Strayta.Core;
using Strayta.Editor.Editing;
using Strayta.Rendering;

namespace Strayta.Editor.ViewModels;

// Layer commands on the selected layers (DocumentViewModel.LayerSelection.cs), each one undoable step with Photoshop's
// history name: delete, duplicate, group and ungroup, hide and show, link, locks, color labels, reorder, move and nudge,
// align and distribute, the merge commands, Delete Hidden Layers and the New Layer dialog's options.
public sealed partial class DocumentViewModel
{
    /// <summary>Photoshop's message for a command refused by a lock.</summary>
    public static string LockedMessage(string command) => $"Could not complete the {command} command because the layer is locked.";

    /// <summary>Why the layer's pixels cannot be changed (Lock Image Pixels or Lock All, its own or its group's), or null.</summary>
    private string? PixelLockProblem(LayerNode node)
    {
        if (!node.IsLocked(LayerLocks.Pixels)) return null;
        return Editor.IsPaintTool || Editor.Tool is Controls.CanvasTool.PaintBucket or Controls.CanvasTool.Gradient or Controls.CanvasTool.CloneStamp
            or Controls.CanvasTool.Healing or Controls.CanvasTool.SpotHealing or Controls.CanvasTool.HistoryBrush
            ? $"Could not use the {Editor.ToolName.ToLowerInvariant()} tool because the layer is locked."
            : "Could not complete your request because the layer is locked.";
    }

    private void Commit(IEdit edit)
    {
        switch (edit)
        {
            case TreeEdit { IsEmpty: true }:
            case OffsetsEdit { IsEmpty: true }:
            case MultiPropertyEdit<int> { IsEmpty: true }:
            case MultiPropertyEdit<bool> { IsEmpty: true }:
                return;
            default:
                Apply(edit);
                break;
        }
    }

    private static IEnumerable<LayerGroup> ParentsOf(IEnumerable<LayerNode> nodes) => nodes.Select(n => n.Parent).OfType<LayerGroup>();

    // ---- Delete, duplicate, hide ------------------------------------------------------------------------

    /// <summary>Deletes the selected layers (one step), then selects the layer below the lowest one, as Photoshop does.</summary>
    public void DeleteSelectedLayers()
    {
        var nodes = SelectedTopLevel();
        if (nodes.Count == 0) return;
        var lowest = nodes[0];
        var parent = lowest.Parent!;
        int at = parent.IndexOf(lowest);
        Commit(new TreeEdit(nodes.Count == 1 ? "Delete Layer" : "Delete Layers", ParentsOf(nodes), _ =>
        {
            foreach (var n in nodes) n.Parent?.Remove(n);
        }));
        LayerNode? next = parent.Children.Count == 0 ? (ReferenceEquals(parent, Model.Root) ? null : parent)
            : parent.Children[Math.Clamp(at - 1, 0, parent.Children.Count - 1)];
        SetLayerSelection(next is null ? [] : [next]);
    }

    /// <summary>Duplicates each selected layer just above itself and selects the copies.</summary>
    public void DuplicateSelectedLayers()
    {
        var nodes = SelectedTopLevel();
        if (nodes.Count == 0) return;
        var copies = new List<LayerNode>();
        Commit(new TreeEdit(nodes.Count == 1 ? "Duplicate Layer" : "Duplicate Layers", ParentsOf(nodes), _ =>
        {
            foreach (var n in nodes)
            {
                var copy = LayerFactory.Duplicate(n);
                copy.LinkGroup = 0;
                n.Parent!.Insert(n.Parent.IndexOf(n) + 1, copy);
                copies.Add(copy);
            }
        }));
        SetLayerSelection(copies);
    }

    /// <summary>Layer › Hide Layers (⌘,): hides the selected layers, or shows them when all are hidden.</summary>
    public void ToggleSelectedVisibility()
    {
        var nodes = SelectedTopLevel();
        if (nodes.Count == 0) return;
        bool show = nodes.All(n => !n.Visible);
        SetVisibility(nodes, show);
    }

    public void SetVisibility(IReadOnlyList<LayerNode> nodes, bool visible) =>
        Commit(new MultiPropertyEdit<bool>(nodes, "Visibility", n => n.Visible, _ => visible, (n, v) => n.Visible = v,
            visible ? (nodes.Count == 1 ? "Show Layer" : "Show Layers") : (nodes.Count == 1 ? "Hide Layer" : "Hide Layers")));

    /// <summary>Layer › Delete › Hidden Layers.</summary>
    public void DeleteHiddenLayers()
    {
        var hidden = LayerMerger.TopLevel(Model, Model.Root.Descendants().Where(n => !n.Visible));
        if (hidden.Count == 0)
        {
            Notice = "There are no hidden layers.";
            return;
        }
        Commit(new TreeEdit("Delete Hidden Layers", ParentsOf(hidden), _ =>
        {
            foreach (var n in hidden) n.Parent?.Remove(n);
        }));
        SetLayerSelection(_selectedNodes.Where(InDocument));
    }

    // ---- Groups ---------------------------------------------------------------------------------------

    /// <summary>Layer › Group Layers (⌘G): puts the selected layers in a new group where the top-most one was.</summary>
    public void GroupSelectedLayers()
    {
        var nodes = SelectedTopLevel();
        if (nodes.Count == 0)
        {
            NewGroup();
            return;
        }
        var top = nodes[^1];
        var parent = top.Parent!;
        var group = new LayerGroup { Name = LayerFactory.NextName(Model, "Group") };
        Commit(new TreeEdit("Group Layers", ParentsOf(nodes).Append(parent), t =>
        {
            t.Created(group);
            int index = parent.IndexOf(top) + 1 - nodes.Count(n => n.Parent == parent);
            foreach (var n in nodes) n.Parent!.Remove(n);
            parent.Insert(Math.Clamp(index, 0, parent.Children.Count), group);
            foreach (var n in nodes) group.Add(n);
        }));
        SetLayerSelection([group]);
    }

    /// <summary>Layer › Ungroup Layers (⇧⌘G): the selected groups' layers take their place.</summary>
    public void UngroupSelectedLayers()
    {
        var groups = SelectedTopLevel().OfType<LayerGroup>().ToList();
        if (groups.Count == 0)
        {
            Notice = "Select a group to ungroup.";
            return;
        }
        var released = groups.SelectMany(g => g.Children).ToList();
        Commit(new TreeEdit("Ungroup Layers", groups.Concat(ParentsOf(groups)), _ =>
        {
            foreach (var g in groups)
            {
                var parent = g.Parent!;
                int index = parent.IndexOf(g);
                parent.Remove(g);
                foreach (var child in g.Children.ToList())
                {
                    g.Remove(child);
                    parent.Insert(index++, child);
                }
            }
        }));
        SetLayerSelection(released);
    }

    /// <summary>Collapse All Groups (the panel menu).</summary>
    public void CollapseAllGroups()
    {
        foreach (var item in Layers.SelectMany(l => l.SelfAndDescendants()).Where(i => i.IsGroup)) item.IsExpanded = false;
    }

    // ---- Reorder ------------------------------------------------------------------------------------

    /// <summary>
    /// Drag and drop of several layers in the panel: they keep their order and land at <paramref name="index"/> of
    /// <paramref name="parent"/> (counted before they are taken out).
    /// </summary>
    public void MoveLayers(IReadOnlyList<LayerNode> nodes, LayerGroup parent, int index)
    {
        nodes = LayerMerger.TopLevel(Model, nodes);
        if (nodes.Count == 0) return;
        for (var g = parent; g is not null; g = g.Parent)
            if (nodes.Contains(g)) return; // cannot drop a group into itself
        if (nodes.Count == 1)
        {
            MoveLayer(nodes[0], parent, index);
            return;
        }
        var keep = _selectedNodes.ToList();
        var primary = SelectedLayer?.Node;
        Commit(new TreeEdit("Layer Order", ParentsOf(nodes).Append(parent), _ =>
        {
            int at = index - nodes.Count(n => n.Parent == parent && parent.IndexOf(n) < index);
            foreach (var n in nodes) n.Parent!.Remove(n);
            foreach (var n in nodes) parent.Insert(Math.Clamp(at++, 0, parent.Children.Count), n);
        }));
        SetLayerSelection(keep, primary);
    }

    // ---- Move and nudge ------------------------------------------------------------------------------

    /// <summary>The Move tool's drag (and a single layer's move): every selected and linked layer moves together.</summary>
    public void MoveSelectedLayers(int dx, int dy, string description = "Move")
    {
        var nodes = MoveTargets();
        if (nodes.Count == 0) return;
        if (nodes.FirstOrDefault(n => n.IsPositionLockedWithin()) is not null)
        {
            Notice = "Could not use the move tool because the layer is locked.";
            return;
        }
        if (nodes.Count == 1 && description == "Move") Apply(new MoveEdit(nodes[0], dx, dy, Model.Width, Model.Height));
        else Apply(new MultiMoveEdit(nodes, dx, dy, Model.Width, Model.Height, description));
    }

    /// <summary>Arrow keys with the Move tool: one pixel, ten with Shift.</summary>
    public void Nudge(int dx, int dy) => MoveSelectedLayers(dx, dy, "Nudge");

    // ---- Align and distribute ---------------------------------------------------------------------------

    /// <summary>
    /// Layer › Align: several layers line up with their combined box; a single layer with the canvas; with a selection
    /// active, with the selection's box (Photoshop's Align Layers to Selection).
    /// </summary>
    public void AlignLayers(AlignEdge edge)
    {
        var nodes = SelectedTopLevel().Where(n => !ContentBounds(n).IsEmpty).ToList();
        if (nodes.Count == 0) return;
        if (nodes.Any(n => n.IsPositionLockedWithin()))
        {
            Notice = LockedMessage("Align");
            return;
        }
        var boxes = nodes.Select(ContentBounds).ToList();
        var reference = Selection is { } sel ? sel.Bounds : nodes.Count == 1 ? Model.Bounds : LayerAlignment.Union(boxes);
        var moves = LayerAlignment.Align(boxes, edge, reference);
        Commit(new OffsetsEdit(nodes.Select((n, i) => (n, moves[i].Dx, moves[i].Dy)).ToList(), Model.Width, Model.Height, "Align"));
    }

    /// <summary>Layer › Distribute (three or more layers).</summary>
    public void DistributeLayers(DistributeMode mode)
    {
        var nodes = SelectedTopLevel().Where(n => !ContentBounds(n).IsEmpty).ToList();
        if (nodes.Count < 3)
        {
            Notice = "Distribute needs three or more layers.";
            return;
        }
        if (nodes.Any(n => n.IsPositionLockedWithin()))
        {
            Notice = LockedMessage("Distribute");
            return;
        }
        var moves = LayerAlignment.Distribute(nodes.Select(ContentBounds).ToList(), mode);
        Commit(new OffsetsEdit(nodes.Select((n, i) => (n, moves[i].Dx, moves[i].Dy)).ToList(), Model.Width, Model.Height, "Distribute"));
    }

    // ---- Links, locks, colors, blending ------------------------------------------------------------------

    /// <summary>
    /// Layer › Link Layers: links the selected layers (and those already linked to them); when they are all linked
    /// together already, unlinks them, as the panel's link button does.
    /// </summary>
    public void LinkSelectedLayers()
    {
        var nodes = SelectedTopLevel();
        if (nodes.Count == 0) return;
        if (nodes.Count > 1 && nodes.Select(n => n.LinkGroup).Distinct().Count() == 1 && nodes[0].LinkGroup != 0 || nodes.Count == 1)
        {
            UnlinkSelectedLayers();
            return;
        }
        int id = Model.Root.Descendants().Select(n => n.LinkGroup).DefaultIfEmpty(0).Max() + 1;
        var old = nodes.Select(n => n.LinkGroup).Where(l => l != 0).ToHashSet();
        var all = nodes.Concat(Model.Root.Descendants().Where(n => old.Contains(n.LinkGroup))).Distinct().ToList();
        Commit(new MultiPropertyEdit<int>(all, "Link", n => n.LinkGroup, _ => id, (n, v) => n.LinkGroup = v, "Link Layers"));
    }

    public void UnlinkSelectedLayers()
    {
        var nodes = SelectedTopLevel().Where(n => n.LinkGroup != 0).ToList();
        if (nodes.Count == 0) return;
        // A link left with a single layer is no link at all: that layer is unlinked too.
        nodes.AddRange(Model.Root.Descendants().Where(n => n.LinkGroup != 0 && !nodes.Contains(n))
            .GroupBy(n => n.LinkGroup).Where(g => g.Count() == 1).SelectMany(g => g));
        Commit(new MultiPropertyEdit<int>(nodes, "Link", n => n.LinkGroup, _ => 0, (n, v) => n.LinkGroup = v, "Unlink Layers"));
    }

    /// <summary>Turns a lock on or off for every selected layer (the panel's lock buttons, Layer › Lock Layers).</summary>
    public void SetLock(LayerLocks kind, bool on)
    {
        var nodes = _selectedNodes.ToList();
        if (nodes.Count == 0) return;
        var edit = new MultiPropertyEdit<LayerLocks>(nodes, "Locks", n => n.Locks, n => on ? n.Locks | kind : n.Locks & ~kind,
            (n, v) => n.Locks = v, on ? "Lock Layer" : "Unlock Layer");
        if (!edit.IsEmpty) Apply(edit);
    }

    /// <summary>The color label for every selected layer (the rows' context menu).</summary>
    public void SetColorLabel(LayerColor color)
    {
        var nodes = _selectedNodes.ToList();
        var edit = new MultiPropertyEdit<LayerColor>(nodes, "Color", n => n.Color, _ => color, (n, v) => n.Color = v, "Layer Properties");
        if (!edit.IsEmpty) Apply(edit);
    }

    /// <summary>
    /// Opacity, fill or blend mode set from the panel header: with several layers selected every one of them changes
    /// (layers with Lock All keep theirs), as one step that merges with the next change of the same kind.
    /// </summary>
    internal bool ChangeSelectedLayers<T>(LayerNode primary, string property, string description, Func<LayerNode, T> get, Action<LayerNode, T> set, T value, Func<LayerNode, bool>? accepts = null)
    {
        if (_selectedNodes.Count < 2 || !_selectedNodes.Contains(primary)) return false;
        var nodes = _selectedNodes.Where(n => !n.IsLocked(LayerLocks.All) && (accepts?.Invoke(n) ?? true)).ToList();
        var edit = new MultiPropertyEdit<T>(nodes, property, get, _ => value, set, description);
        if (!edit.IsEmpty) Apply(edit);
        return true;
    }

    // ---- New Layer with options -----------------------------------------------------------------------------

    /// <summary>What the New Layer dialog (⇧⌘N) sets up.</summary>
    public sealed record NewLayerOptions(string Name, bool Clip, LayerColor Color, BlendMode Mode, double OpacityPercent);

    public void NewLayer(NewLayerOptions options)
    {
        var (parent, index) = SelectedLayer?.Node is { Parent: { } p } node ? (p, p.IndexOf(node) + 1) : (Model.Root, Model.Root.Children.Count);
        var layer = new PixelLayer
        {
            Name = string.IsNullOrWhiteSpace(options.Name) ? LayerFactory.NextName(Model, "Layer") : options.Name.Trim(),
            Clipped = options.Clip && index > 0,
            Color = options.Color,
            BlendMode = options.Mode,
            Opacity = (float)Math.Clamp(options.OpacityPercent / 100, 0, 1),
        };
        Apply(new InsertEdit(layer, parent, index, "New Layer"));
        SetLayerSelection([layer]);
    }

    // ---- Merging --------------------------------------------------------------------------------------------

    /// <summary>
    /// Layer › Merge Down (⌘E): on one layer, merges it into the one below (on a clipping base, the clipped layers into
    /// it; on a group, Merge Group); with several selected, Merge Layers.
    /// </summary>
    public Task MergeDownAsync()
    {
        var nodes = SelectedTopLevel();
        if (nodes.Count == 0) return Task.CompletedTask;
        return nodes.Count > 1
            ? MergeAsync(doc => LayerMerger.MergeLayers(doc, nodes))
            : MergeAsync(doc => LayerMerger.MergeDown(doc, nodes[0]));
    }

    /// <summary>Layer › Merge Visible (⇧⌘E).</summary>
    public Task MergeVisibleAsync()
    {
        var named = SelectedLayer?.Node;
        return MergeAsync(doc => LayerMerger.MergeVisible(doc, named));
    }

    /// <summary>Layer › Flatten Image: hidden layers are discarded, transparency becomes white.</summary>
    public Task FlattenImageAsync() => MergeAsync(LayerMerger.Flatten);

    /// <summary>Stamp Visible (⌥⇧⌘E): everything visible on a new layer above the selected one.</summary>
    public Task StampVisibleAsync()
    {
        var above = SelectedLayer?.Node;
        string name = LayerFactory.NextName(Model, "Layer");
        return MergeAsync(doc => LayerMerger.StampVisible(doc, above, name));
    }

    /// <summary>Works out a merge off the UI thread (it renders at full resolution), then applies it as one step.</summary>
    private async Task MergeAsync(Func<Document, MergePlan> plan)
    {
        if (IsTransforming) await CommitTransformAsync();
        CommitType();
        MergePlan merge;
        try
        {
            var doc = Model;
            merge = await Task.Run(() => plan(doc));
        }
        catch (MergeRefusedException ex)
        {
            Notice = ex.Message;
            return;
        }
        var groups = ParentsOf(merge.Removed).Append(merge.Parent).ToList();
        Commit(new TreeEdit(merge.Command, groups, _ => merge.Apply()));
        Notice = "";
        SetLayerSelection(merge.Result is null ? [] : [merge.Result]);
    }
}
