using CommunityToolkit.Mvvm.ComponentModel;
using Strayta.Core;
using Strayta.Core.Painting;
using Strayta.Core.Selection;
using Strayta.Editor.Controls;
using Strayta.Editor.Editing;

namespace Strayta.Editor.ViewModels;

/// <summary>Pixels copied with Edit > Copy or Cut, with where they came from so Paste can put them back in place.</summary>
public sealed record ClipboardImage(Raster Pixels, PixelRect Bounds);

// Selections: marquee and lasso results, Select menu, and the Edit commands that work on the selected area.
public sealed partial class DocumentViewModel
{
    private SelectionMask? _previousSelection;
    private int _selectionRequest;

    /// <summary>
    /// The active selection, or null when nothing is selected (everything is editable). Changing it only redraws the
    /// canvas outline, never the document.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial SelectionMask? Selection { get; private set; }

    public bool HasSelection => Selection is not null;

    partial void OnSelectionChanged(SelectionMask? oldValue, SelectionMask? newValue)
    {
        if (oldValue is not null) _previousSelection = oldValue;
    }

    /// <summary>Makes <paramref name="next"/> the selection as one undoable step named <paramref name="description"/>.</summary>
    public void SetSelection(SelectionMask? next, string description)
    {
        if (ReferenceEquals(next, Selection)) return;
        Apply(new SelectionEdit(Selection, next, s => Selection = s, description));
    }

    /// <summary>
    /// Applies a finished marquee or lasso drag. Masks for large shapes take a few milliseconds, so they are built in
    /// the background; the canvas keeps showing the drawn shape until the new outline arrives.
    /// </summary>
    public async Task ApplySelectionGestureAsync(SelectionGesture gesture)
    {
        if (gesture.Tool == CanvasTool.ObjectSelect)
        {
            await ApplyObjectSelectionAsync(gesture); // DocumentViewModel.ObjectSelection.cs
            return;
        }
        if (gesture.IsClick)
        {
            // A click without dragging deselects (only in the plain mode, as in Photoshop).
            if (gesture.Mode == SelectionMode.Replace) SetSelection(null, "Deselect");
            return;
        }

        var canvas = Model.Bounds;
        var current = Selection;
        int request = ++_selectionRequest;
        var next = await Task.Run(() =>
        {
            var shape = gesture.Tool switch
            {
                CanvasTool.EllipseSelect => SelectionMask.Ellipse(gesture.Box, canvas),
                CanvasTool.Lasso => SelectionMask.Polygon(gesture.Points, canvas),
                _ => SelectionMask.Rectangle(gesture.Box, canvas),
            };
            return SelectionMask.Combine(current, shape, gesture.Mode);
        });
        // Dropped if another drag finished first or the selection changed meanwhile (undo, Select All, ...).
        if (request != _selectionRequest || !ReferenceEquals(current, Selection)) return;
        SetSelection(next, gesture.Tool switch
        {
            CanvasTool.EllipseSelect => "Elliptical Marquee",
            CanvasTool.Lasso => "Lasso",
            _ => "Rectangular Marquee",
        });
    }

    public void SelectAll() => SetSelection(SelectionMask.All(Model.Bounds), "Select All");

    public void Deselect() => SetSelection(null, "Deselect");

    /// <summary>Select > Reselect: brings back the selection that was last replaced or removed.</summary>
    public void Reselect()
    {
        if (_previousSelection is { } previous && Selection is null) SetSelection(previous, "Reselect");
    }

    public void InvertSelection() => SetSelection(SelectionMask.Invert(Selection, Model.Bounds), "Select Inverse");

    // ---- Edit commands on the selected area ------------------------------------------------------------

    /// <summary>
    /// Delete / Backspace: clears the selected area of the selected layer (Background layers get the background
    /// color). With nothing selected it deletes the layer, as Photoshop does.
    /// </summary>
    public async Task ClearAsync()
    {
        if (Selection is null)
        {
            DeleteSelected();
            return;
        }
        if (EditableLayer("clear") is not { } layer) return;
        var selection = Selection;
        var background = Editor.CurrentBackgroundColor;
        await EditPixelsAsync(layer, "Clear", doc => SelectionPainter.Clear(layer, selection, background, doc.ColorMode, doc.BitDepth));
    }

    /// <summary>Edit > Fill: fills the selection (the whole canvas when nothing is selected) with the foreground or background color.</summary>
    public async Task FillAsync(bool background = false)
    {
        if (EditableLayer("fill") is not { } layer) return;
        var selection = Selection ?? SelectionMask.All(Model.Bounds);
        var color = background ? Editor.CurrentBackgroundColor : Editor.CurrentColor;
        await EditPixelsAsync(layer, "Fill", doc => SelectionPainter.Fill(layer, selection, color, doc.ColorMode, doc.BitDepth));
    }

    /// <summary>Edit > Copy: the selected layer's pixels inside the selection (the whole layer when nothing is selected).</summary>
    public bool Copy()
    {
        if (EditableLayer("copy", allowHidden: true) is not { } layer) return false;
        if (SelectionPainter.Extract(layer, Selection) is not { } copied)
        {
            Notice = "The selected area is empty.";
            return false;
        }
        Editor.Clipboard = new ClipboardImage(copied.Pixels, copied.Bounds);
        Notice = "";
        return true;
    }

    /// <summary>Edit > Cut: copies, then clears the selected area.</summary>
    public async Task CutAsync()
    {
        if (!Copy()) return;
        var layer = (PixelLayer)SelectedLayer!.Node;
        var selection = Selection ?? SelectionMask.All(Model.Bounds);
        var background = Editor.CurrentBackgroundColor;
        await EditPixelsAsync(layer, "Cut", doc => SelectionPainter.Clear(layer, selection, background, doc.ColorMode, doc.BitDepth));
    }

    /// <summary>
    /// Edit > Paste: the copied pixels become a new layer above the selected one, in the place they were copied
    /// from (centered if that is off this canvas). The selection is dropped, as in Photoshop.
    /// </summary>
    public void Paste()
    {
        if (Editor.Clipboard is not { } clip)
        {
            Notice = "Nothing to paste. Copy a selection first.";
            return;
        }
        if (clip.Pixels.ColorMode != Model.ColorMode || clip.Pixels.BitDepth != Model.BitDepth)
        {
            Notice = $"Pasting {clip.Pixels.ColorMode} {clip.Pixels.BitDepth}-bit pixels into a {Model.ColorMode} {Model.BitDepth}-bit document is not supported yet.";
            return;
        }

        var bounds = clip.Bounds;
        if (bounds.Intersect(Model.Bounds).IsEmpty)
        {
            int left = (Model.Width - bounds.Width) / 2, top = (Model.Height - bounds.Height) / 2;
            bounds = new PixelRect(left, top, left + bounds.Width, top + bounds.Height);
        }
        var layer = new PixelLayer { Name = LayerFactory.NextName(Model, "Layer"), Bounds = bounds, Pixels = clip.Pixels };
        var (parent, index) = InsertionPoint();
        Apply(new CompositeEdit("Paste",
            new InsertEdit(layer, parent, index, "Paste"),
            new SelectionEdit(Selection, null, s => Selection = s, "Deselect")));
        Select(layer);
        Notice = "";
    }

    /// <summary>
    /// The paint benchmark again with a large elliptical selection: the stroke lies inside it but every dab reads
    /// the anti-aliased mask, which is the cost a selection adds (STRAYTA_PAINTBENCH=1).
    /// </summary>
    public async Task RunPaintBenchmarkWithSelectionAsync()
    {
        int w = Model.Width, h = Model.Height;
        SetSelection(SelectionMask.Ellipse(new PixelRect(-w / 10, -h / 10, w + w / 10, h + h / 10), Model.Bounds), "Elliptical Marquee");
        Console.WriteLine("PAINTBENCH with an elliptical selection:");
        await RunPaintBenchmarkAsync(); // undoes everything afterwards, the selection included
    }

    /// <summary>Computes new pixels for <paramref name="layer"/> in the background and applies them as one undoable edit.</summary>
    private async Task EditPixelsAsync(PixelLayer layer, string description, Func<Core.Document, (Raster? Pixels, PixelRect Bounds)> compute)
    {
        if (_baking) return;
        _baking = true;
        try
        {
            var doc = Model;
            var (pixels, bounds) = await Task.Run(() => compute(doc));
            Apply(new PixelsEdit(layer, pixels, bounds, description));
        }
        finally
        {
            _baking = false;
        }
    }

    /// <summary>The selected layer if its pixels can be edited; otherwise sets <see cref="Notice"/> and returns null.</summary>
    private PixelLayer? EditableLayer(string action, bool allowHidden = false)
    {
        var node = SelectedLayer?.Node;
        string? problem = node switch
        {
            null => $"Select a layer to {action}.",
            not PixelLayer => $"\"{node.Name}\" has no pixels to {action}. Select a pixel layer.",
            { Visible: false } when !allowHidden => $"\"{node.Name}\" is hidden.",
            _ when !allowHidden && (node.Tags.Contains("text") || node.Tags.Contains("smart-object") || node.Tags.Contains("fill") || node.Tags.Contains("shape"))
                => $"\"{node.Name}\" is drawn from its own data (text, shape, fill or smart object). Rasterize it to {action} its pixels.",
            _ when Model.ColorMode is not (ColorMode.Rgb or ColorMode.Grayscale) => $"Editing pixels in {Model.ColorMode} documents is not supported yet.",
            _ => null,
        };
        Notice = problem ?? "";
        return problem is null ? (PixelLayer)node! : null;
    }
}
