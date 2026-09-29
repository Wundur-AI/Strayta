using Strayta.Core;
using Strayta.Core.Painting;
using Strayta.Core.Selection;
using Strayta.Editor.Editing;
using Strayta.Rendering;

namespace Strayta.Editor.ViewModels;

// Select › Select and Mask: starting a session (on the selection, or on the targeted layer mask to refine it) and
// applying its result to the selection, a layer mask, or a new layer (with Decontaminate Colors' cleaned-up pixels).
public sealed partial class DocumentViewModel
{
    /// <summary>
    /// Starts Select › Select and Mask on this document: the image it looks at (the flattened document, straight and
    /// premultiplied), and what it refines: the targeted layer mask when the mask thumbnail is selected (Photoshop's
    /// mask refinement), otherwise the selection. Null (with a notice) when the document cannot be sampled.
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
        var node = SelectedLayer?.Node;
        var mask = EditMask ? node?.GetMask() : null;
        var selection = mask is not null ? await Task.Run(() => SelectionLayerMask.ToSelection(mask, Model.Bounds)) : Selection;
        var session = new SelectAndMaskViewModel(this, render, sample, selection, mask is not null ? node : null,
            () => RenderBelowAsync(node), Editor.SelectAndMaskMemory);
        Notice = "";
        return session;
    }

    /// <summary>The layers below <paramref name="current"/>, flattened (the On Layers view's backdrop); null when there is no layer.</summary>
    private async Task<byte[]?> RenderBelowAsync(LayerNode? current)
    {
        if (current is null) return null;
        var snapshot = new PreviewDocument(Model, 1);
        var proxy = snapshot.Sync();
        var hidden = new HashSet<LayerNode>(ReferenceEqualityComparer.Instance);
        // The layer itself and everything above it, up through its groups.
        for (var node = current; node.Parent is { } parent; node = parent)
        {
            int from = parent.IndexOf(node) + (ReferenceEquals(node, current) ? 0 : 1);
            for (int i = from; i < parent.Children.Count; i++)
                if (snapshot.ProxyOf(parent.Children[i]) is { } p) hidden.Add(p);
        }
        try
        {
            return await Task.Run(() =>
            {
                using var renderer = new CpuRenderer();
                return renderer.Render(proxy, new RenderOptions { Hidden = hidden }).ToRgba8();
            });
        }
        catch (Exception)
        {
            return null; // the On Layers view then shows the checkerboard behind
        }
    }

    /// <summary>
    /// Applies a finished session's result as one history step named "Select and Mask": to the selection, the layer
    /// mask (the refined one, or a new one on the selected layer), or a copy of the layer, with or without a mask.
    /// </summary>
    public async Task ApplySelectAndMaskAsync(SelectAndMaskViewModel session)
    {
        const string Name = "Select and Mask";
        var refined = await session.ResultAsync();
        var output = session.EffectiveOutput;
        var owner = session.RefinesMaskOf;
        float decontaminate = session.Decontaminate ? (float)(session.DecontaminateAmount / 100) : 0f;
        // Refining a mask leaves the selection alone; otherwise the selection became the result and goes, as in Photoshop.
        IEdit? deselect = owner is null && Selection is not null ? new SelectionEdit(Selection, null, s => Selection = s, "Deselect") : null;
        IEdit[] With(params IEdit[] edits) => deselect is null ? edits : [.. edits, deselect];
        LayerMask MaskOf(SelectionMask? s) => s is null ? LayerMasks.Solid(false) : SelectionLayerMask.Create(s, true, Model.BitDepth);

        switch (output)
        {
            case RefineOutput.Selection:
                SetSelection(refined, Name);
                Notice = refined is null ? "Nothing is selected after Select and Mask." : "";
                return;

            case RefineOutput.LayerMask:
            {
                if ((owner ?? MaskableSelection("add a mask to")) is not { } node) return;
                Apply(new CompositeEdit(Name, With(new MaskEdit(node, MaskOf(refined), Name))));
                EditMask = true;
                Notice = "";
                return;
            }

            default:
            {
                if ((owner ?? MaskableSelection("copy with a mask")) is not { Parent: { } parent } node) return;
                var copy = LayerFactory.Duplicate(node);
                copy.Visible = true;
                var mask = MaskOf(refined);
                if (copy is PixelLayer layer && decontaminate > 0)
                {
                    if (Model.ColorMode != ColorMode.Rgb) Notice = "Decontaminate Colors works on RGB documents; the colors were kept.";
                    else if (layer.Pixels is { } pixels)
                    {
                        var (bounds, palette) = (layer.Bounds, Model.Palette);
                        layer.Pixels = await Task.Run(() => Decontaminated(pixels, bounds, refined, decontaminate, palette));
                    }
                }
                if (output == RefineOutput.NewLayer && copy is PixelLayer plain)
                {
                    // New Layer: the refined selection becomes the copy's transparency.
                    plain.Pixels = await Task.Run(() => MaskBaker.ApplyToPixels(plain, mask));
                    plain.Mask = null;
                }
                else copy.SetMask(mask);
                // Photoshop hides the original so the masked copy shows on its own.
                Apply(new CompositeEdit(Name, With(
                    new InsertEdit(copy, parent, parent.IndexOf(node) + 1, Name),
                    new PropertyEdit<bool>(node, "Visibility", node.Visible, false, (n, v) => n.Visible = v))));
                Select(copy);
                EditMask = output == RefineOutput.NewLayerWithLayerMask;
                if (!Notice.StartsWith("Decontaminate", StringComparison.Ordinal)) Notice = "";
                return;
            }
        }
    }

    /// <summary>
    /// The layer's pixels with Decontaminate Colors applied under <paramref name="refined"/>: only the fringe (pixels less
    /// than fully selected) gets new colors; the rest, and the alpha, are the original's untouched.
    /// </summary>
    private static Raster Decontaminated(Raster pixels, PixelRect bounds, SelectionMask? refined, float amount, byte[]? palette)
    {
        int w = bounds.Width, h = bounds.Height;
        var rgba = RgbaConverter.ToRgba8(pixels, palette);
        var coverage = SelectionLayerMask.Coverage(refined, bounds);
        var clean = ColorDecontamination.Apply(rgba, w, h, coverage, amount);
        var planes = pixels.ColorPlanes.Select(p => new Plane(p.Width, p.Height, p.BitDepth, (byte[])p.Data.Clone())).ToArray();
        Parallel.For(0, h, y =>
        {
            for (int i = y * w, end = i + w; i < end; i++)
            {
                if (coverage[i] >= 250) continue;
                for (int c = 0; c < 3; c++)
                {
                    var plane = planes[c];
                    byte v = clean[i * 4 + c];
                    switch (plane.BitDepth)
                    {
                        case 8: plane.Data[i] = v; break;
                        case 16: plane.AsUInt16()[i] = (ushort)(v * 257); break;
                        default: plane.AsSingle()[i] = v / 255f; break;
                    }
                }
            }
        });
        return new Raster(pixels.ColorMode, planes, pixels.Alpha);
    }
}
