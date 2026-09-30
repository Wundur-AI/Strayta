using Strayta.Core;

namespace Strayta.Editor.ViewModels;

// The Move tool's press: Auto-Select picks the layer (or its outermost group) under the pointer, ⌘ turns Auto-Select on
// or off for that click, Shift adds to the selection, and ⌘⌥-click selects the layer under the pointer whatever the
// setting. Dragging then moves every selected layer (DocumentViewModel.LayerCommands.cs).
public sealed partial class DocumentViewModel
{
    /// <summary>The top-most visible layer with a visible pixel at (<paramref name="x"/>, <paramref name="y"/>), or null.</summary>
    public PixelLayer? LayerAt(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Model.Width || y >= Model.Height) return null;
        foreach (var node in Model.Root.Descendants().Reverse())
        {
            if (node is not PixelLayer { Pixels: { } px } layer || !IsShown(layer) || !Contains(layer.Bounds, x, y)) continue;
            int i = (y - layer.Bounds.Top) * layer.Bounds.Width + (x - layer.Bounds.Left);
            float alpha = px.Alpha is { } a ? a.GetNormalized(i) : 1f;
            if (layer.Mask is { Disabled: false } m) alpha *= MaskValue(m, x, y);
            if (alpha > 0f && layer.Opacity > 0f) return layer;
        }
        return null;
    }

    private static bool Contains(PixelRect r, int x, int y) => x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;

    private static float MaskValue(LayerMask mask, int x, int y)
    {
        if (mask.Pixels is { } p && Contains(mask.Bounds, x, y))
            return p.GetNormalized((y - mask.Bounds.Top) * mask.Bounds.Width + (x - mask.Bounds.Left));
        return mask.DefaultColor / 255f;
    }

    /// <summary>The Move tool pressed at a document point; may change the selected layers before the drag.</summary>
    public void MoveToolPressed(double x, double y, bool command, bool alt, bool shift)
    {
        bool autoSelect = (command && alt) || Editor.MoveAutoSelect != command;
        if (!autoSelect || FreeTransform is not null) return;
        if (LayerAt((int)Math.Floor(x), (int)Math.Floor(y)) is not { } hit) return;
        LayerNode target = hit;
        if (Editor.MoveAutoSelectGroups && !(command && alt))
            while (target.Parent is { Parent: not null } g) target = g; // the outermost group
        if (shift)
        {
            if (ItemFor(target) is { } item) ClickLayer(item, command: true, shift: false);
        }
        else if (!_selectedNodes.Contains(target)) SetLayerSelection([target]);
    }
}
