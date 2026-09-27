using System.Diagnostics;
using Strayta.Core;
using Strayta.Core.Selection;
using Strayta.Editor.Editing;

namespace Strayta.Editor.ViewModels;

/// <summary>The Select › Modify commands (each asks for a pixel amount, as in Photoshop).</summary>
public enum SelectionModification
{
    Expand,
    Contract,
    Feather,
    Smooth,
    Border,
}

/// <summary>Where Select and Mask puts its result (Photoshop's Output To, the options Strayta supports).</summary>
public enum RefineOutput
{
    Selection,
    LayerMask,
    NewLayerWithLayerMask,
}

// Select › Modify, masks made from the selection (Layer › Layer Mask › Reveal/Hide Selection), and applying Select
// and Mask's result. The work runs in the background; each command is one history step with Photoshop's name.
public sealed partial class DocumentViewModel
{
    private int _modifyRequest;

    /// <summary>How long the last Select › Modify command took to compute, for the self-test's timings.</summary>
    public double LastModifyMs { get; private set; }

    /// <summary>
    /// Select › Modify › Expand / Contract / Feather / Smooth / Border with <paramref name="amount"/> pixels. Dropped
    /// if the selection changes while it computes (undo, another command), like the selection tools.
    /// </summary>
    public async Task ModifySelectionAsync(SelectionModification kind, double amount, bool applyAtCanvasBounds)
    {
        if (Selection is not { } current)
        {
            Notice = "Nothing is selected.";
            return;
        }
        var canvas = Model.Bounds;
        int request = ++_modifyRequest;
        int whole = (int)Math.Round(amount);
        var clock = Stopwatch.StartNew();
        var next = await Task.Run(() => kind switch
        {
            SelectionModification.Expand => SelectionModify.Expand(current, whole, canvas),
            SelectionModification.Contract => SelectionModify.Contract(current, whole, canvas, applyAtCanvasBounds),
            SelectionModification.Feather => SelectionModify.Feather(current, (float)amount, canvas, applyAtCanvasBounds),
            SelectionModification.Smooth => SelectionModify.Smooth(current, whole, canvas, applyAtCanvasBounds),
            _ => SelectionModify.Border(current, whole, canvas, applyAtCanvasBounds),
        });
        LastModifyMs = clock.Elapsed.TotalMilliseconds;
        if (request != _modifyRequest || !ReferenceEquals(current, Selection)) return;
        // Photoshop's warning when the result would be invisible as marching ants.
        Notice = next is not null && !AnyMostlySelected(next)
            ? "No pixels are more than 50% selected. The selection edges will not be visible."
            : next is null ? "The selection disappeared." : "";
        SetSelection(next, kind.ToString());
    }

    private static bool AnyMostlySelected(SelectionMask s)
    {
        if (s.IsRectangular) return true;
        var row = new byte[s.Bounds.Width];
        for (int y = s.Bounds.Top; y < s.Bounds.Bottom; y++)
        {
            s.CopyRow(y, s.Bounds.Left, row);
            if (row.AsSpan().IndexOfAnyInRange((byte)128, (byte)255) >= 0) return true;
        }
        return false;
    }

    /// <summary>
    /// Layer › Layer Mask › Reveal Selection / Hide Selection: a mask that shows (or hides) the selected area. The
    /// selection is dropped in the same step, as in Photoshop.
    /// </summary>
    public void AddMaskFromSelection(bool reveal)
    {
        if (Selection is not { } selection)
        {
            Notice = "Make a selection first, or use Reveal All / Hide All.";
            return;
        }
        if (MaskableSelection("add a mask to") is not { } node) return;
        if (node.GetMask() is not null)
        {
            Notice = $"\"{node.Name}\" already has a layer mask.";
            return;
        }
        var mask = SelectionLayerMask.Create(selection, reveal, Model.BitDepth);
        Apply(new CompositeEdit("Add Layer Mask",
            new MaskEdit(node, mask, "Add Layer Mask"),
            new SelectionEdit(selection, null, s => Selection = s, "Deselect")));
        EditMask = true;
        Notice = "";
    }

    /// <summary>The Layers panel's mask button: reveals the selection when there is one, otherwise reveals all (Photoshop's "Add layer mask").</summary>
    public void AddMaskButton()
    {
        if (Selection is not null) AddMaskFromSelection(reveal: true);
        else AddMask(reveal: true);
    }

    // ---- Select and Mask ------------------------------------------------------------------------------

    /// <summary>
    /// Starts Select › Select and Mask on this document: the image it looks at (the flattened document, straight and
    /// premultiplied) and the selection to refine. Null (with a notice) when the document cannot be sampled.
    /// </summary>
    public async Task<SelectAndMaskViewModel?> BeginSelectAndMaskAsync()
    {
        if (IsTransforming)
        {
            Notice = "Apply or cancel the Free Transform first.";
            return null;
        }
        if (await CompositeAsync() is not { } render) return null;
        if (await SampleImageAsync(sampleAll: true) is not { } sample) return null;
        return new SelectAndMaskViewModel(this, render, sample, Selection);
    }

    /// <summary>Applies a finished Select and Mask session's result as one history step named "Select and Mask".</summary>
    public void ApplySelectAndMask(SelectionMask? refined, RefineOutput output)
    {
        const string Name = "Select and Mask";
        switch (output)
        {
            case RefineOutput.Selection:
                SetSelection(refined, Name);
                Notice = refined is null ? "Nothing is selected after Select and Mask." : "";
                return;

            case RefineOutput.LayerMask:
            {
                if (MaskableSelection("add a mask to") is not { } node) return;
                var mask = refined is null ? LayerMasks.Solid(false) : SelectionLayerMask.Create(refined, true, Model.BitDepth);
                Apply(new CompositeEdit(Name,
                    new MaskEdit(node, mask, Name),
                    new SelectionEdit(Selection, null, s => Selection = s, "Deselect")));
                EditMask = true;
                Notice = "";
                return;
            }

            case RefineOutput.NewLayerWithLayerMask:
            {
                if (MaskableSelection("copy with a mask") is not { Parent: { } parent } node) return;
                var copy = LayerFactory.Duplicate(node);
                copy.SetMask(refined is null ? LayerMasks.Solid(false) : SelectionLayerMask.Create(refined, true, Model.BitDepth));
                copy.Visible = true;
                // Photoshop hides the original so the masked copy shows on its own.
                Apply(new CompositeEdit(Name,
                    new InsertEdit(copy, parent, parent.IndexOf(node) + 1, Name),
                    new PropertyEdit<bool>(node, "Visibility", node.Visible, false, (n, v) => n.Visible = v),
                    new SelectionEdit(Selection, null, s => Selection = s, "Deselect")));
                Select(copy);
                EditMask = true;
                Notice = "";
                return;
            }
        }
    }
}
