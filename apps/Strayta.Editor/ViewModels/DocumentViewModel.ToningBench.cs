using System.Diagnostics;
using Strayta.Editor.Controls;
using Strayta.Rendering;

namespace Strayta.Editor.ViewModels;

// STRAYTA_TONEBENCH: frame rates of Dodge, Burn, Sponge, Blur, Sharpen and Smudge strokes, and of the brush with a
// sampled tip and with Shape Dynamics / Scattering (DocumentViewModel.Toning.cs, Core's LocalStroke and BrushTip).
public sealed partial class DocumentViewModel
{
    /// <summary>
    /// Two-second strokes with 120 Hz input on the selected layer, one per tool and option set, reporting frames reaching
    /// the screen (preview lane), the median / 90th-percentile frame interval, the time spent adding dabs per input
    /// event, and the commit time. Each stroke is undone afterwards.
    /// </summary>
    public async Task RunToningBenchmarkAsync(Action<string>? report = null)
    {
        report ??= Console.WriteLine;
        var saved = (Editor.Tool, Editor.BrushSize, Editor.BrushHardness, Editor.BrushTip, Editor.ShapeDynamics, Editor.Scattering, Editor.Scatter, Editor.ScatterCount,
            Editor.BlurSampleAllLayers, Editor.BrushSpacing);
        var cases = new (string Name, CanvasTool Tool, Action Set)[]
        {
            ("Dodge midtones, soft 150 px", CanvasTool.Dodge, () => (Editor.BrushSize, Editor.BrushHardness) = (150, 0)),
            ("Burn highlights, 300 px", CanvasTool.Burn, () => (Editor.BrushSize, Editor.BurnRangeIndex) = (300, 2)),
            ("Sponge saturate, vibrance", CanvasTool.Sponge, () => (Editor.BrushSize, Editor.SpongeModeIndex) = (150, 1)),
            ("Blur 50%, 100 px", CanvasTool.Blur, () => Editor.BrushSize = 100),
            ("Blur 50%, 300 px", CanvasTool.Blur, () => Editor.BrushSize = 300),
            ("Blur, Sample All Layers", CanvasTool.Blur, () => (Editor.BrushSize, Editor.BlurSampleAllLayers) = (100, true)),
            ("Sharpen, Protect Detail, 100 px", CanvasTool.Sharpen, () => Editor.BrushSize = 100),
            ("Smudge 70%, 80 px", CanvasTool.Smudge, () => (Editor.BrushSize, Editor.SmudgeStrength) = (80, 70)),
            ("Brush, sampled tip 120 px", CanvasTool.Brush, () => (Editor.BrushSize, Editor.BrushTip) = (120, Editing.BrushPresetLibrary.BuiltIns.First(p => p.Tip is not null).Tip)),
            ("Brush, dynamics + scatter x3", CanvasTool.Brush, () => (Editor.BrushSize, Editor.ShapeDynamics, Editor.SizeJitter, Editor.Scattering, Editor.Scatter, Editor.ScatterCount) = (60, true, 80, true, 200, 3)),
        };
        var layer = SelectedLayer;
        // STRAYTA_TONEBENCH_CASES=smudge,blur runs only the cases whose names contain one of the words.
        var only = Environment.GetEnvironmentVariable("STRAYTA_TONEBENCH_CASES")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var (name, tool, set) in cases)
        {
            if (only is { Length: > 0 } && !only.Any(o => name.Contains(o, StringComparison.OrdinalIgnoreCase))) continue;
            (Editor.BrushSize, Editor.BrushHardness, Editor.BrushTip, Editor.ShapeDynamics, Editor.Scattering, Editor.BlurSampleAllLayers, Editor.BrushSpacing) =
                (100, 50, null, false, false, false, 25);
            Editor.Tool = tool;
            set();
            SelectedLayer = layer;
            int frames = 0;
            var times = new List<double>();
            var clock = Stopwatch.StartNew();
            double last = 0, dabMs = 0, worstDab = 0;
            void OnFrame(bool full)
            {
                if (full) return;
                frames++;
                times.Add(clock.Elapsed.TotalMilliseconds - last);
                last = clock.Elapsed.TotalMilliseconds;
            }
            FrameDisplayed += OnFrame;
            bool started = tool == CanvasTool.Brush
                ? BeginStroke(Model.Width * 0.1f, Model.Height * 0.5f, Editor.CurrentBrush, Editor.CurrentColor, erase: false)
                : BeginToolStroke(Model.Width * 0.1f, Model.Height * 0.5f);
            if (!started)
            {
                FrameDisplayed -= OnFrame;
                report($"TONEBENCH {name}: could not start ({Notice})");
                continue;
            }
            for (int i = 1; i <= 240; i++)
            {
                float t = i / 240f;
                var d = Stopwatch.StartNew();
                ContinueStroke(Model.Width * (0.1f + 0.8f * t), Model.Height * (0.5f + 0.3f * MathF.Sin(t * 12)));
                double ms = d.Elapsed.TotalMilliseconds;
                dabMs += ms;
                worstDab = Math.Max(worstDab, ms);
                await Task.Delay(8);
            }
            double input = clock.Elapsed.TotalMilliseconds;
            FrameDisplayed -= OnFrame;
            var commit = Stopwatch.StartNew();
            await EndStrokeAsync();
            long commitMs = commit.ElapsedMilliseconds;
            times.Sort();
            report($"TONEBENCH {name}: frames={frames} fps={frames / (input / 1000):F1} median-frame={(times.Count > 0 ? times[times.Count / 2] : double.NaN):F0}ms " +
                   $"p90={(times.Count > 0 ? times[(int)(times.Count * 0.9)] : double.NaN):F0}ms dabs/event={dabMs / 240:F2}ms (worst {worstDab:F1}ms) " +
                   $"commit={commitMs}ms factor={PreviewDocument.FactorForZoom(_viewZoom)} on {Model.Width}x{Model.Height}");
            if (CanUndo) Undo();
        }
        (Editor.Tool, Editor.BrushSize, Editor.BrushHardness, Editor.BrushTip, Editor.ShapeDynamics, Editor.Scattering, Editor.Scatter, Editor.ScatterCount,
            Editor.BlurSampleAllLayers, Editor.BrushSpacing) = saved;
    }
}
