using CommunityToolkit.Mvvm.ComponentModel;
using Strayta.Core;
using Strayta.Core.Painting;
using Strayta.Editor.Editing;

namespace Strayta.Editor.ViewModels;

/// <summary>Layer masks: which of the selected layer's thumbnails is targeted, mask commands and mask painting.</summary>
public sealed partial class DocumentViewModel
{
    private string _maskStrokeTool = "Brush";

    /// <summary>
    /// True when painting and the Properties panel act on the selected layer's mask rather than its pixels
    /// (Photoshop's highlighted thumbnail). Selecting an adjustment layer targets its mask, as it has no pixels.
    /// </summary>
    [ObservableProperty] public partial bool EditMask { get; set; }

    /// <summary>What the Properties panel shows for the selection.</summary>
    [ObservableProperty] public partial PropertiesPanel? Properties { get; private set; }

    partial void OnSelectedLayerChanged(LayerItemViewModel? oldValue, LayerItemViewModel? newValue)
    {
        // Rebuilding the rows re-selects the same layer through a new item; keep its target then.
        if (!ReferenceEquals(oldValue?.Node, newValue?.Node))
            EditMask = newValue?.Node is AdjustmentLayer { Mask: not null };
        else if (EditMask && newValue?.HasMask != true)
            EditMask = false;
        oldValue?.RefreshTarget();
        newValue?.RefreshTarget();
        UpdateProperties();
    }

    partial void OnEditMaskChanged(bool value)
    {
        SelectedLayer?.RefreshTarget();
        UpdateProperties();
    }

    /// <summary>Clicking a row's layer or mask thumbnail: selects the layer and targets its pixels or its mask.</summary>
    public void Target(LayerItemViewModel item, bool mask)
    {
        SelectedLayer = item;
        EditMask = mask && item.HasMask;
    }

    private LayerNode? MaskableSelection(string action)
    {
        if (SelectedLayer?.Node is { } node && node.CanHaveMask()) return node;
        Notice = $"Select a layer, group or adjustment layer to {action}.";
        return null;
    }

    /// <summary>Layer › Layer Mask › Reveal All / Hide All (and the Layers panel's mask button).</summary>
    public void AddMask(bool reveal)
    {
        if (MaskableSelection("add a mask to") is not { } node) return;
        if (node.GetMask() is not null)
        {
            Notice = $"\"{node.Name}\" already has a layer mask.";
            return;
        }
        Apply(new MaskEdit(node, LayerMasks.Solid(reveal), reveal ? "Reveal All" : "Hide All"));
        EditMask = true;
    }

    public void DeleteMask()
    {
        if (MaskableSelection("delete a mask from") is not { } node || node.GetMask() is null) return;
        Apply(new MaskEdit(node, null, "Delete Layer Mask"));
        EditMask = false;
    }

    /// <summary>Layer › Layer Mask › Disable / Enable (also Shift-click on the mask thumbnail).</summary>
    public void ToggleMaskEnabled()
    {
        if (MaskableSelection("disable a mask on") is not { } node || node.GetMask() is not { } mask) return;
        Apply(new MaskEdit(node, mask.WithDisabled(!mask.Disabled), mask.Disabled ? "Enable Layer Mask" : "Disable Layer Mask"));
    }

    /// <summary>Layer › Layer Mask › Apply: bakes the mask into a pixel layer's transparency.</summary>
    public async Task ApplyMaskAsync()
    {
        if (SelectedLayer?.Node is not PixelLayer { Mask: { } mask } layer)
        {
            Notice = SelectedLayer?.Node.GetMask() is null
                ? "The selected layer has no layer mask."
                : "Only pixel layers can apply their mask; groups and adjustment layers keep it.";
            return;
        }
        if (layer.Tags.Contains("text") || layer.Tags.Contains("smart-object") || layer.Tags.Contains("fill") || layer.Tags.Contains("shape"))
        {
            Notice = $"Rasterize \"{layer.Name}\" before applying its mask.";
            return;
        }
        var pixels = await Task.Run(() => MaskBaker.ApplyToPixels(layer, mask));
        Apply(new ApplyMaskEdit(layer, pixels));
        EditMask = false;
    }

    /// <summary>
    /// Starts a stroke in <paramref name="owner"/>'s mask. Masks are grayscale: the brush paints the foreground
    /// color's luminance (black hides, white reveals) and the eraser paints the background color's.
    /// </summary>
    private bool BeginMaskStroke(LayerNode owner, float x, float y, BrushSettings brush, RgbColor color, bool erase)
    {
        string? problem = owner switch
        {
            { Visible: false } => $"\"{owner.Name}\" is hidden.",
            _ when Model.ColorMode is not (ColorMode.Rgb or ColorMode.Grayscale) => $"Painting in {Model.ColorMode} documents is not supported yet.",
            _ => null,
        };
        if (problem is not null)
        {
            Notice = problem;
            PaintBlock = new PaintBlock(problem, CanRasterize: false, CanShow: !owner.Visible, CanNewLayer: false);
            return false;
        }

        if (erase)
        {
            var bg = Editor.BackgroundColor;
            color = new RgbColor(bg.R / 255f, bg.G / 255f, bg.B / 255f);
        }
        Notice = "";
        PaintBlock = null;
        _maskStrokeTool = erase ? "Eraser" : "Brush";
        _stroke = PaintStroke.ForMask(owner, brush, 0.299f * color.R + 0.587f * color.G + 0.114f * color.B, Model.Bounds, Selection);
        _stroke.StrokeTo(x, y);
        RequestRender();
        return true;
    }

    /// <summary>The last full-resolution image shown (RGBA), for the self-test to check what reached the screen.</summary>
    internal byte[]? LastFullRender => _lastRender;

    /// <summary>Commits a mask stroke as a new mask plane (one undo step); the live overlay stays until it is ready.</summary>
    private async Task CommitMaskStrokeAsync(PaintStroke stroke)
    {
        var mask = stroke.Owner.GetMask()!;
        int depth = Model.BitDepth;
        var baked = await Task.Run(() => MaskBaker.Bake(mask, stroke, depth));
        _stroke = null;
        Apply(new MaskEdit(stroke.Owner, baked, _maskStrokeTool));
    }
}
