using System.Diagnostics;
using Strayta.Core;
using Strayta.Editor.Editing;
using Strayta.Rendering.Transforms;

namespace Strayta.Editor.ViewModels;

// Filter › Liquify: the workspace edits a displacement field at screen resolution (LiquifySession); OK applies it to the
// layer's full-resolution pixels (bicubic, area-filtered where the field shrinks) as one undo step "Liquify".
public sealed partial class DocumentViewModel
{
    /// <summary>Time from OK to the liquified pixels being in the document, for the self-test.</summary>
    public double LastLiquifyApplyMs { get; private set; }

    /// <summary>The rasterize prompt before Liquify on type, shapes, fills and smart objects (as for filters); null when none.</summary>
    public string? LiquifyRasterizePrompt() => SelectedLayer?.Node is { } n && RasterizeEdit.CanRasterize(n) ? FilterRasterizePrompt() : null;

    /// <summary>Opens Liquify on the selected layer; null (with a notice) when it has no pixels to liquify.</summary>
    public LiquifySession? BeginLiquify()
    {
        if (IsTransforming || IsPuppetWarping)
        {
            Notice = "Apply or cancel the transform first.";
            return null;
        }
        CommitType();
        string? problem = SelectedLayer?.Node switch
        {
            null => "Select a layer to liquify.",
            { Visible: false } n => $"Could not complete the Liquify command because the target layer \"{n.Name}\" is hidden.",
            not PixelLayer => "Liquify works on a pixel layer.",
            PixelLayer { Pixels: null } n => $"Could not complete the Liquify command because \"{n.Name}\" is empty.",
            _ => null,
        };
        if (problem is not null)
        {
            Notice = problem;
            return null;
        }
        return new LiquifySession((PixelLayer)SelectedLayer!.Node, Model.Bounds);
    }

    /// <summary>OK: the field applied to the layer's pixels as one undo step.</summary>
    public async Task ApplyLiquifyAsync(LiquifySession session)
    {
        if (session.Field.IsIdentity || _baking) return;
        var layer = session.Layer;
        if (layer.Pixels is not { } raster) return;
        _baking = true;
        var clock = Stopwatch.StartNew();
        try
        {
            var bounds = layer.Bounds;
            var field = session.Field;
            var (pixels, newBounds) = await Task.Run(() =>
            {
                var (result, b) = MeshResampler.TransformRaster(ResampleSource.FromRaster(raster), field.ToTriangles(bounds), ResampleFilter.Bicubic);
                if (result is null) return (result, b);
                // The field covers the canvas; keep only what holds pixels.
                var trimmed = Resampler.ContentBounds(new PixelLayer { Pixels = result, Bounds = b });
                return trimmed.IsEmpty ? (null, PixelRect.Empty) : Resampler.Crop(result, b, trimmed);
            });
            var state = TransformEdit.Read(layer);
            Apply(new TransformEdit([(layer, state with { Pixels = pixels, Bounds = pixels is null ? PixelRect.Empty : newBounds })], "Liquify"));
            LastLiquifyApplyMs = clock.Elapsed.TotalMilliseconds;
        }
        catch (Exception ex)
        {
            Notice = $"Could not apply Liquify: {ex.Message}";
        }
        finally
        {
            _baking = false;
        }
    }
}
