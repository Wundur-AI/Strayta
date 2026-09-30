using Strayta.Core;
using Strayta.Core.Painting;

namespace Strayta.Editor.ViewModels;

// The History Brush (Y): paints the selected layer back to how it was in the History Brush source (a state or snapshot
// chosen in the History panel's left column). It paints through the brush engine's PaintStroke with a cloning source
// at no offset, so the live overlay, selection clipping and brush settings are the Brush's; on release the pixels are
// replaced toward the source's (transparency included) as one undo step.
public sealed partial class DocumentViewModel
{
    private (PaintStroke Stroke, Raster? Pixels, PixelRect Bounds)? _historyStroke;

    /// <summary>Starts a History Brush stroke; false, with a notice, when the layer or source cannot be used.</summary>
    public bool BeginHistoryBrushStroke(float x, float y)
    {
        if (_baking || IsTransforming || _stroke is not null) return false;
        if (BlockedByChannelTarget("History Brush")) return false; // DocumentViewModel.Channels.cs
        if (EditMask && SelectedLayer?.Node?.GetMask() is not null)
        {
            Notice = "The History Brush paints a layer's pixels. Click the layer thumbnail to paint on the layer instead of its mask.";
            return false;
        }
        if (PaintableLayer() is not { } layer) return false;
        var (source, problem) = HistoryBrushPixels(layer);
        if (source is not { } s)
        {
            Notice = problem ?? "";
            return false;
        }
        var clone = new CloneSource(0, 0, PixelSource.FromRaster(s.Pixels, s.Bounds));
        var stroke = PaintStroke.Cloning(layer, targetsMask: false, Editor.CurrentBrush, clone, Model.Bounds, Selection);
        _stroke = stroke;
        _historyStroke = (stroke, s.Pixels, s.Bounds);
        stroke.StrokeTo(x, y);
        RequestRender();
        return true;
    }

    /// <summary>Release: bakes the stroke in the background and applies it as one "History Brush" step.</summary>
    private async Task EndHistoryBrushStrokeAsync()
    {
        if (_historyStroke is not { } h) return;
        _historyStroke = null;
        if (h.Stroke.Bounds.IsEmpty)
        {
            _stroke = null;
            return;
        }
        _baking = true;
        try
        {
            var layer = h.Stroke.Target;
            var (mode, depth) = (Model.ColorMode, Model.BitDepth);
            var (pixels, bounds) = await Task.Run(() => HistoryBrushBaker.Bake(layer, h.Stroke, h.Pixels, h.Bounds, mode, depth));
            _stroke = null;
            Apply(new Editing.PixelsEdit(layer, pixels, bounds, "History Brush"));
        }
        finally
        {
            _baking = false;
        }
    }
}
