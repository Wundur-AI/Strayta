using System.Diagnostics;
using Strayta.Core.Painting;
using Strayta.Rendering;

namespace Strayta.Editor.ViewModels;

// STRAYTA_PAINTBENCH: frame rates of brush strokes with the brush options (DocumentViewModel.Brush.cs).
public sealed partial class DocumentViewModel
{
    /// <summary>
    /// Two-second strokes with 120 Hz input on a new layer, one per brush option set (flow, a blend mode, smoothing,
    /// airbrush, pen pressure, an elliptical tip, a large soft brush), reporting frames reaching the screen and commit time.
    /// </summary>
    public async Task RunBrushOptionsBenchmarkAsync()
    {
        var saved = (Editor.BrushFlow, Editor.BrushModeIndex, Editor.BrushSmoothing, Editor.BrushAirbrush, Editor.BrushPressureSize,
            Editor.BrushPressureOpacity, Editor.BrushRoundness, Editor.BrushAngle, Editor.BrushSize, Editor.BrushHardness);
        var cases = new (string Name, Action Set, Func<float, float?>? Pressure)[]
        {
            ("flow 30%, soft 80 px", () => (Editor.BrushFlow, Editor.BrushSize, Editor.BrushHardness) = (30, 80, 0), null),
            ("mode Multiply", () => Editor.BrushModeIndex = (int)PaintMode.Multiply, null),
            ("smoothing 50%", () => Editor.BrushSmoothing = 50, null),
            ("airbrush, flow 20%", () => (Editor.BrushAirbrush, Editor.BrushFlow) = (true, 20), null),
            ("pen pressure size+opacity", () => (Editor.BrushPressureSize, Editor.BrushPressureOpacity) = (true, true), t => 0.3f + 0.7f * MathF.Abs(MathF.Sin(t * 9))),
            ("elliptical tip 30% at 45°", () => (Editor.BrushRoundness, Editor.BrushAngle) = (30, 45), null),
            ("soft 400 px, flow 20%", () => (Editor.BrushSize, Editor.BrushHardness, Editor.BrushFlow) = (400, 0, 20), null),
        };
        Editor.Tool = Controls.CanvasTool.Brush;
        var photo = SelectedLayer;
        foreach (var (name, set, pressure) in cases)
        {
            (Editor.BrushFlow, Editor.BrushModeIndex, Editor.BrushSmoothing, Editor.BrushAirbrush, Editor.BrushPressureSize,
                Editor.BrushPressureOpacity, Editor.BrushRoundness, Editor.BrushAngle, Editor.BrushSize, Editor.BrushHardness) = (100, 0, 0, false, false, false, 100, 0, 80, 50);
            set();
            SelectedLayer = photo;
            NewLayer();
            float t = 0;
            PenPressure = pressure is null ? null : () => pressure(t);
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
            BeginStroke(Model.Width * 0.1f, Model.Height * 0.5f, Editor.CurrentBrush, Editor.CurrentColor, erase: false);
            for (int i = 1; i <= 240; i++)
            {
                t = i / 240f;
                ContinueStroke(Model.Width * (0.1f + 0.8f * t), Model.Height * (0.5f + 0.3f * MathF.Sin(t * 12)));
                await Task.Delay(8);
            }
            double input = clock.Elapsed.TotalMilliseconds;
            FrameDisplayed -= OnFrame;
            var commit = Stopwatch.StartNew();
            await EndStrokeAsync();
            long commitMs = commit.ElapsedMilliseconds;
            PenPressure = null;
            times.Sort();
            Console.WriteLine($"PAINTBENCH {name}: frames={frames} fps={frames / (input / 1000):F1} median-frame={(times.Count > 0 ? times[times.Count / 2] : double.NaN):F0}ms " +
                              $"p90={(times.Count > 0 ? times[(int)(times.Count * 0.9)] : double.NaN):F0}ms commit={commitMs}ms " +
                              $"factor={PreviewDocument.FactorForZoom(_viewZoom)} on {Model.Width}x{Model.Height}");
        }
        (Editor.BrushFlow, Editor.BrushModeIndex, Editor.BrushSmoothing, Editor.BrushAirbrush, Editor.BrushPressureSize,
            Editor.BrushPressureOpacity, Editor.BrushRoundness, Editor.BrushAngle, Editor.BrushSize, Editor.BrushHardness) = saved;
        while (CanUndo) Undo();
    }
}
