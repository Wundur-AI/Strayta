using Strayta.Core;
using Strayta.Core.Painting;
using Strayta.Editor.Editing;

namespace Strayta.Editor.ViewModels;

// Layer > New > Layer via Copy (⌘J) and Layer via Cut (⇧⌘J).
public sealed partial class DocumentViewModel
{
    /// <summary>
    /// Puts the selected pixels of the selected layer on a new layer directly above it, in place, and drops the
    /// selection, as Photoshop does; Cut also clears them from the source (a Background gets the background color).
    /// The new layer keeps the source's blend mode, opacity and fill. With nothing selected, Copy duplicates the
    /// layer (any kind) and Cut does nothing. One undo step.
    /// </summary>
    public async Task LayerViaAsync(bool cut)
    {
        string description = cut ? "Layer via Cut" : "Layer via Copy";
        if (Selection is null)
        {
            if (cut) Notice = "Layer via Cut needs a selection.";
            else DuplicateSelected();
            return;
        }
        if (EditableLayer(cut ? "cut" : "copy", allowHidden: !cut) is not { Parent: { } parent } layer || _baking) return;
        _baking = true;
        try
        {
            var selection = Selection;
            var background = Editor.CurrentBackgroundColor;
            var doc = Model;
            var (copied, cleared) = await Task.Run(() => (
                SelectionPainter.Extract(layer, selection),
                cut ? SelectionPainter.Clear(layer, selection, background, doc.ColorMode, doc.BitDepth) : ((Raster?, PixelRect)?)null));
            if (copied is not { } c)
            {
                Notice = "The selected area is empty.";
                return;
            }
            var created = new PixelLayer
            {
                Name = LayerFactory.NextName(Model, "Layer"),
                Bounds = c.Bounds,
                Pixels = c.Pixels,
                BlendMode = layer.BlendMode,
                Opacity = layer.Opacity,
                FillOpacity = layer.FillOpacity,
            };
            var edits = new List<IEdit> { new InsertEdit(created, parent, parent.IndexOf(layer) + 1, description) };
            if (cleared is { } after) edits.Add(new PixelsEdit(layer, after.Item1, after.Item2, description));
            edits.Add(new SelectionEdit(selection, null, s => Selection = s, "Deselect"));
            Apply(new CompositeEdit(description, [.. edits]));
            Select(created);
            Notice = "";
        }
        finally
        {
            _baking = false;
        }
    }
}
