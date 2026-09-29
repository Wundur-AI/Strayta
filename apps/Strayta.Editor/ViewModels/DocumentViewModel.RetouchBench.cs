using System.Diagnostics;
using Strayta.Editor.Controls;
using Strayta.Rendering;

namespace Strayta.Editor.ViewModels;

// STRAYTA_RETOUCHBENCH: frame rates and release times of the retouching tools (DocumentViewModel.Retouch.cs).
public sealed partial class DocumentViewModel
{
    /// <summary>
    /// Two-second Clone Stamp strokes with 120 Hz input (frames reaching the screen, commit time), sampling the layer and
    /// all layers, then Healing Brush and Spot Healing strokes of typical sizes, timing release to edit (and the heal's
    /// share). Runs on the selected pixel layer.
    /// </summary>
    public async Task RunRetouchBenchmarkAsync()
    {
        static double Median(List<double> v) => v.Count == 0 ? double.NaN : v.OrderBy(t => t).ElementAt(v.Count / 2);
        static double P90(List<double> v) => v.Count == 0 ? double.NaN : v.OrderBy(t => t).ElementAt((int)(v.Count * 0.9));
        int w = Model.Width, h = Model.Height;
        var photo = SelectedLayer;
        Editor.BrushHardness = 50;
        Editor.BrushOpacity = 100;

        foreach (var (sample, scaled) in new[] { (0, false), (2, false), (0, true) })
        {
            SelectedLayer = Layers.SelectMany(l => l.SelfAndDescendants()).FirstOrDefault(i => i.Node == photo?.Node);
            if (sample == 2) NewLayer(); // the usual way: clone onto an empty layer, sampling all layers
            Editor.Tool = CanvasTool.CloneStamp;
            Editor.CloneSampleIndex = sample;
            Editor.BrushSize = 80;
            SetCloneSource(w / 4, h / 3);
            // The Clone Source panel's transform: resampled (bilinear, reduced when shrunk) instead of copied.
            if (scaled) (ActiveCloneSource.WidthPercent, ActiveCloneSource.Angle) = (70, 30);
            var prep = Stopwatch.StartNew();
            if (sample != 0) await CompositeSampleAsync(null);
            double prepMs = prep.Elapsed.TotalMilliseconds;

            int frames = 0;
            var times = new List<double>();
            var clock = Stopwatch.StartNew();
            double last = 0;
            void OnFrame(bool full)
            {
                if (full) return;
                frames++;
                times.Add(clock.Elapsed.TotalMilliseconds - last);
                last = clock.Elapsed.TotalMilliseconds;
            }
            FrameDisplayed += OnFrame;
            BeginRetouchStroke(w * 0.45f, h * 0.5f);
            for (int i = 1; i <= 240; i++)
            {
                float t = i / 240f;
                ContinueStroke(w * (0.45f + 0.4f * t), h * (0.5f + 0.25f * MathF.Sin(t * 12)));
                await Task.Delay(8);
            }
            double inputMs = clock.Elapsed.TotalMilliseconds;
            FrameDisplayed -= OnFrame;
            await EndStrokeAsync();
            if (scaled) ActiveCloneSource.ResetTransformCommand.Execute(null);
            Console.WriteLine($"RETOUCHBENCH clone stamp, sample {Editor.RetouchSampleNames[sample]}{(scaled ? ", 70% turned 30°" : "")}: frames={frames} fps={frames / (inputMs / 1000):F1} " +
                              $"median-frame={Median(times):F0}ms p90={P90(times):F0}ms commit={LastRetouchTimings.TotalMs:F0}ms " +
                              $"(source prepared ahead in {prepMs:F0} ms) factor={PreviewDocument.FactorForZoom(_viewZoom)} on {w}x{h}");
        }

        SelectedLayer = Layers.SelectMany(l => l.SelfAndDescendants()).FirstOrDefault(i => i.Node == photo?.Node);
        foreach (var (tool, type) in new[] { (CanvasTool.Healing, SpotHealType.ProximityMatch), (CanvasTool.SpotHealing, SpotHealType.ProximityMatch),
                     (CanvasTool.SpotHealing, SpotHealType.ContentAware), (CanvasTool.SpotHealing, SpotHealType.CreateTexture) })
        {
            Editor.Tool = tool;
            Editor.HealSampleIndex = 0;
            Editor.SpotTypeIndex = (int)type;
            foreach (var (size, length) in new[] { (30, 50), (60, 100), (60, 150), (150, 0) })
            {
                Editor.BrushSize = size;
                SetCloneSource(w / 3, h / 4);
                var totals = new List<double>();
                var heals = new List<double>();
                for (int run = 0; run < 3; run++)
                {
                    float x0 = w * (0.3f + 0.1f * run), y0 = h * 0.6f;
                    if (!BeginRetouchStroke(x0, y0))
                    {
                        Console.WriteLine($"RETOUCHBENCH could not start a {tool} stroke: {Notice}");
                        return;
                    }
                    for (int i = 1; i <= 10; i++) ContinueStroke(x0 + length * i / 10f, y0 + length * 0.3f * i / 10f);
                    await EndStrokeAsync();
                    totals.Add(LastRetouchTimings.TotalMs);
                    heals.Add(LastRetouchTimings.HealMs);
                }
                string name = tool == CanvasTool.Healing ? "healing brush" : $"spot healing {Editor.SpotTypeNames[(int)type]}";
                Console.WriteLine($"RETOUCHBENCH {name} brush {size} px, stroke {length} px: release to edit median {Median(totals):F0} ms " +
                                  $"(max {totals.Max():F0}), heal {Median(heals):F0} ms{(tool == CanvasTool.SpotHealing ? " incl. search" : "")}");
            }
        }
        Editor.SpotTypeIndex = 0;

        // Edit > Content-Aware Fill of elliptical selections, into the layer (sampling everything outside).
        foreach (int size in new[] { 100, 300, 600 })
        {
            var box = new Strayta.Core.PixelRect(w / 2 - size / 2, h / 2 - size / 2, w / 2 + size / 2, h / 2 + size / 2);
            SetSelection(Strayta.Core.Selection.SelectionMask.Ellipse(box, Model.Bounds), "Elliptical Marquee");
            await ContentAwareFillAsync(new ContentAwareFillSettings());
            Console.WriteLine($"RETOUCHBENCH content-aware fill {size}x{size} ellipse: {LastContentAwareFillTimings.TotalMs:F0} ms " +
                              $"(synthesis and heal {LastContentAwareFillTimings.SynthesisMs:F0} ms)");
            Deselect();
        }
        while (CanUndo) Undo();
    }
}
