using System.Diagnostics;
using Strayta.Core;
using Strayta.Core.Painting;
using Strayta.Editor.Editing;

namespace Strayta.Editor.ViewModels;

/// <summary>Edit › Content-Aware Fill settings.</summary>
/// <param name="SampleAllLayers">Sample the whole image (flattened) instead of the selected layer.</param>
/// <param name="OutputToNewLayer">Put the fill on a new layer above the selected one instead of into it.</param>
/// <param name="ColorAdaptation">Heal the synthesized fill into its surroundings (Poisson), removing brightness and color seams.</param>
public sealed record ContentAwareFillSettings(bool SampleAllLayers = false, bool OutputToNewLayer = false, bool ColorAdaptation = true);

// Edit › Content-Aware Fill: fills the selection with patches synthesized from everything outside it (Core
// PatchSynthesis, the same engine as the Spot Healing Brush's Content-Aware type), then heals the result in. Soft
// selection edges blend the fill with what was there. One undo step.
public sealed partial class DocumentViewModel
{
    /// <summary>Time of the last Content-Aware Fill (synthesis and heal, commit), for the self-test and benchmark.</summary>
    public (double TotalMs, double SynthesisMs) LastContentAwareFillTimings { get; private set; }

    /// <summary>
    /// Fills the selection as described by <paramref name="settings"/>; false (with a notice) when there is no selection
    /// or nothing can be filled.
    /// </summary>
    public async Task<bool> ContentAwareFillAsync(ContentAwareFillSettings settings)
    {
        if (Selection is not { } selection)
        {
            Notice = "Content-Aware Fill needs a selection: select the area to fill.";
            return false;
        }
        if (_baking || _stroke is not null) return false;
        PixelLayer? layer = settings.OutputToNewLayer ? null : EditableLayer("fill");
        if (!settings.OutputToNewLayer && layer is null) return false;
        if (settings.OutputToNewLayer && !settings.SampleAllLayers && SelectedLayer?.Node is not PixelLayer)
        {
            Notice = "Select a pixel layer to sample, or turn on Sample All Layers.";
            return false;
        }
        var clock = Stopwatch.StartNew();
        _baking = true;
        try
        {
            var sampled = settings.SampleAllLayers
                ? await CompositeSampleAsync(null)
                : PixelSource.FromRaster(((PixelLayer)SelectedLayer!.Node).Pixels, ((PixelLayer)SelectedLayer.Node).Bounds);
            var doc = Model;
            var (canvas, mode, depth) = (doc.Bounds, doc.ColorMode, doc.BitDepth);
            var area = selection.Bounds.Intersect(canvas);
            var target = layer ?? new PixelLayer { Name = LayerFactory.NextName(Model, "Layer") };
            double synthesisMs = 0;
            var baked = await Task.Run(() =>
            {
                var synth = Stopwatch.StartNew();
                var coverage = new float[area.Width * area.Height];
                Parallel.For(area.Top, area.Bottom, y =>
                {
                    for (int x = area.Left; x < area.Right; x++)
                        coverage[(y - area.Top) * area.Width + (x - area.Left)] = selection.CoverageAt(x, y) / 255f;
                });
                // Everything outside the selection may be sampled (no sample margin).
                if (PatchSynthesis.Synthesize(sampled, coverage, area, canvas, new SynthesisOptions()) is not { } texture) return null;
                var fill = settings.ColorAdaptation ? Healing.Heal(sampled, texture, 0, 0, coverage, area, canvas) : texture;
                synthesisMs = synth.Elapsed.TotalMilliseconds;
                var stroke = PaintStroke.FromCoverage(target, false, coverage, area, new CloneSource(0, 0, fill), canvas);
                return ((Raster?, PixelRect)?)StrokeBaker.Bake(target, stroke, mode, depth);
            });
            if (baked is not { } px)
            {
                Notice = "Content-Aware Fill found nothing to sample outside the selection.";
                return false;
            }
            const string name = "Content-Aware Fill";
            if (layer is not null) Apply(new PixelsEdit(layer, px.Item1, px.Item2, name));
            else
            {
                target.Pixels = px.Item1;
                target.Bounds = px.Item2;
                var (parent, index) = InsertionPoint();
                Apply(new InsertEdit(target, parent, index, name));
                Select(target);
            }
            Notice = "";
            LastContentAwareFillTimings = (clock.Elapsed.TotalMilliseconds, synthesisMs);
            return true;
        }
        catch (Exception ex)
        {
            Notice = $"Could not fill: {ex.Message}";
            return false;
        }
        finally
        {
            _baking = false;
        }
    }
}
