using System.Diagnostics;
using Strayta.Core;
using Strayta.Core.Painting;
using Strayta.Editor.Controls;
using Strayta.Editor.Editing;

namespace Strayta.Editor.ViewModels;

// Dodge, Burn, Sponge (toning) and Blur, Sharpen, Smudge (focus) strokes.
//
// All six paint through the brush engine's PaintStroke, so they share its live overlay (the render lanes draw the stroke
// over its layer every frame), selection clipping, mask targeting, pressure, smoothing and `[`/`]` sizing. A toning
// stroke carries Core's ToneSettings: coverage builds up per dab to the Exposure and each pixel is changed by its tone
// curve (Toning) wherever the stroke has been. A focus stroke carries a LocalStroke, a working copy of the pixels that
// every dab changes at once, reading what earlier dabs left (so a blur deepens as you go over it again and smudged
// paint travels). Both are committed with the same functions the overlay uses, as one undo step.
public sealed partial class DocumentViewModel
{
    /// <summary>The tool of the toning or focus stroke in progress (for its history name), or null.</summary>
    private CanvasTool? _toolStroke;

    /// <summary>The flattened image a Sample All Layers focus stroke reads, while it is being prepared.</summary>
    private Task<PixelSource>? _toolSample;

    /// <summary>Time from release to the edit being in the document, for the self-test and benchmark.</summary>
    public double LastToolCommitMs { get; private set; }

    /// <summary>
    /// Starts a Dodge, Burn, Sponge, Blur, Sharpen or Smudge stroke on the selected layer, or on its mask when the mask is
    /// targeted; false, with a notice, when it cannot paint there.
    /// </summary>
    public bool BeginToolStroke(float x, float y)
    {
        if (_baking || IsTransforming || _stroke is not null) return false;
        var tool = Editor.Tool;
        LayerNode owner;
        bool mask = EditMask && SelectedLayer?.Node is { } n && n.GetMask() is not null;
        if (mask)
        {
            owner = SelectedLayer!.Node;
            string? problem = owner.Visible ? null : $"\"{owner.Name}\" is hidden.";
            if (Model.ColorMode is not (ColorMode.Rgb or ColorMode.Grayscale)) problem = $"Painting in {Model.ColorMode} documents is not supported yet.";
            if (problem is not null)
            {
                Notice = problem;
                PaintBlock = new PaintBlock(problem, CanRasterize: false, CanShow: !owner.Visible, CanNewLayer: false);
                return false;
            }
            Notice = tool == CanvasTool.Sponge ? "The Sponge changes saturation; a mask has none, so it stays as it is." : "";
            PaintBlock = null;
        }
        else if (PaintableLayer() is { } layer) owner = layer;
        else return false;

        PaintStroke stroke;
        _toolSample = null;
        if (Editor.IsToningTool)
            stroke = PaintStroke.Toning(owner, mask, Editor.CurrentToneBrush, Editor.CurrentToneSettings, Model.Bounds, Selection);
        else
        {
            var brush = Editor.CurrentBrush;
            var target = mask ? PixelSource.FromMask(owner.GetMask()!) : PixelSource.FromRaster(((PixelLayer)owner).Pixels, ((PixelLayer)owner).Bounds);
            // A mask samples only itself; Sample All Layers reads the flattened image (DocumentViewModel.Retouch.cs).
            bool all = !mask && Editor.FocusSampleAllLayers;
            var local = new LocalStroke(Editor.CurrentLocalSettings, target, Model.Bounds, brush.Size, sampleSeparately: all);
            stroke = PaintStroke.Retouching(owner, mask, brush, local, Model.Bounds, Selection);
            if (all)
            {
                var image = CompositeSampleAsync(null);
                if (image.IsCompletedSuccessfully) local.SetSample(image.Result);
                else
                {
                    _toolSample = image;
                    _ = SampleWhenReadyAsync(local, image);
                }
            }
        }

        _stroke = stroke;
        _toolStroke = tool;
        StartStrokeInput(stroke, x, y); // pressure, smoothing, airbrush (DocumentViewModel.Brush.cs)
        RequestRender();
        return true;
    }

    private async Task SampleWhenReadyAsync(LocalStroke local, Task<PixelSource> image)
    {
        try
        {
            local.SetSample(await image);
            RequestRender();
        }
        catch (Exception ex)
        {
            Notice = $"Could not prepare the image to sample: {ex.Message}";
        }
    }

    /// <summary>
    /// Release: bakes the stroke at full resolution in the background and records one undoable edit; the live overlay
    /// keeps showing it until the new pixels are in. Called from <see cref="EndStrokeAsync"/>.
    /// </summary>
    private async Task EndToolStrokeAsync()
    {
        if (_toolStroke is not { } tool || _stroke is not { } stroke) return;
        if (stroke.Bounds.IsEmpty)
        {
            (_stroke, _toolStroke) = (null, null);
            RequestRender();
            return;
        }
        var clock = Stopwatch.StartNew();
        _baking = true;
        try
        {
            if (stroke.Local is { IsReady: false } local && _toolSample is { } sample) local.SetSample(await sample);
            var (mode, depth) = (Model.ColorMode, Model.BitDepth);
            string name = tool switch
            {
                CanvasTool.Dodge => "Dodge Tool",
                CanvasTool.Burn => "Burn Tool",
                CanvasTool.Sponge => "Sponge Tool",
                CanvasTool.Blur => "Blur Tool",
                CanvasTool.Sharpen => "Sharpen Tool",
                _ => "Smudge Tool",
            };
            if (stroke.TargetsMask)
            {
                var mask = stroke.Owner.GetMask()!;
                var baked = await Task.Run(() => MaskBaker.Bake(mask, stroke, depth));
                (_stroke, _toolStroke) = (null, null);
                Apply(new MaskEdit(stroke.Owner, baked, name));
            }
            else
            {
                var (pixels, bounds) = await Task.Run(() => StrokeBaker.Bake(stroke.Target, stroke, mode, depth));
                (_stroke, _toolStroke) = (null, null);
                Apply(new PixelsEdit(stroke.Target, pixels, bounds, name));
            }
            LastToolCommitMs = clock.Elapsed.TotalMilliseconds;
        }
        catch (Exception ex)
        {
            (_stroke, _toolStroke) = (null, null);
            Notice = $"Could not finish the {Editor.ToolName}: {ex.Message}";
            RequestRender();
        }
        finally
        {
            _baking = false;
            _toolSample = null;
        }
    }
}
