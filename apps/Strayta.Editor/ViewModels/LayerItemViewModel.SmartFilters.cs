using Strayta.Core;
using Strayta.Editor.Editing;

namespace Strayta.Editor.ViewModels;

/// <summary>A row under a smart object: "Smart Filters" (the whole stack) or one smart filter, with its eye.</summary>
public sealed class SmartFilterRowViewModel(LayerItemViewModel owner, string name, int index, bool visible, bool dimmed, bool unknown)
{
    public LayerItemViewModel Owner { get; } = owner;
    public string Name { get; } = name;

    /// <summary>The filter's position in the stack (bottom first), or −1 for the "Smart Filters" row.</summary>
    public int Index { get; } = index;

    public bool IsMaster => Index < 0;
    public bool IsVisible { get; } = visible;

    /// <summary>A filter Strayta does not have: kept, and the layer shows Photoshop's pixels (warning icon).</summary>
    public bool IsUnknown { get; } = unknown;

    public double EyeOpacity => !IsVisible ? 0 : dimmed ? 0.3 : 1;
    public Avalonia.Thickness Indent => new(Owner.Depth * 18 + (IsMaster ? 44 : 60), 0, 0, 0);
    public string Tip => IsUnknown ? "Strayta does not have this filter; the layer keeps the pixels Photoshop drew" : "Double-click to edit the filter's settings";
}

// The Layers panel's list of a smart object's smart filters, as Photoshop shows them under the layer.
public sealed partial class LayerItemViewModel
{
    /// <summary>"Smart Filters" and then each filter, top of the stack first.</summary>
    public IReadOnlyList<SmartFilterRowViewModel> SmartFilterRows
    {
        get
        {
            if (DocumentViewModel.SmartFiltersOf(Node) is not { Filters.Count: > 0 } stack) return [];
            var rows = new List<SmartFilterRowViewModel> { new(this, "Smart Filters", -1, stack.Enabled, false, false) };
            for (int i = stack.Filters.Count - 1; i >= 0; i--)
            {
                var f = stack.Filters[i];
                rows.Add(new(this, f.Name, i, f.Enabled, !stack.Enabled, SmartObjects.ToFilter(f) is null));
            }
            return rows;
        }
    }

    public bool HasSmartFilters => SmartFilterRows.Count > 0;

    /// <summary>The eye of a smart filter row (one undo step).</summary>
    public Task ToggleSmartFilter(SmartFilterRowViewModel row) =>
        Node is PixelLayer layer ? _document.ToggleSmartFilterAsync(layer, row.Index) : Task.CompletedTask;

    /// <summary>Double-click on a smart filter row: its dialog.</summary>
    public Task EditSmartFilter(SmartFilterRowViewModel row) =>
        Node is PixelLayer layer && !row.IsMaster ? _document.Editor.EditSmartFilterAsync(_document, layer, row.Index) : Task.CompletedTask;
}
