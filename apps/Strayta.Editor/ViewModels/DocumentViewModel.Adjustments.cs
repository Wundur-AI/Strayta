using System.Diagnostics;
using Strayta.Core;
using Strayta.Editor.Editing;
using Strayta.Psd;
using Strayta.Rendering;

namespace Strayta.Editor.ViewModels;

/// <summary>Adjustment layers: creating them, editing their settings, and the Properties panel's data.</summary>
public sealed partial class DocumentViewModel
{
    /// <summary>Layer › New Adjustment Layer: adds it above the selected layer with a reveal-all mask, selected.</summary>
    public void NewAdjustmentLayer(string kind)
    {
        var (parent, index) = InsertionPoint();
        var layer = AdjustmentFactory.Create(Model, kind, Editor.ForegroundRgb, Editor.BackgroundRgb);
        Apply(new InsertEdit(layer, parent, index, $"New {kind} Layer"));
        Select(layer);
    }

    /// <summary>
    /// The Properties panel's clip button: clips the layer to the one below (Create Clipping Mask) or releases it.
    /// The step names alternate, so two clicks stay two undo steps.
    /// </summary>
    public void SetClipped(LayerNode layer, bool clipped)
    {
        if (layer.Clipped == clipped) return;
        Apply(new PropertyEdit<bool>(layer, clipped ? "Create Clipping Mask" : "Release Clipping Mask", layer.Clipped, clipped,
            static (n, v) => n.Clipped = v));
        RefreshRows();
    }

    /// <summary>The Properties panel's eye: shows or hides the layer (one undo step per click).</summary>
    public void SetVisible(LayerNode layer, bool visible)
    {
        if (layer.Visible == visible) return;
        Apply(new PropertyEdit<bool>(layer, visible ? "Show Layer" : "Hide Layer", layer.Visible, visible, static (n, v) => n.Visible = v));
        RefreshRows();
    }

    /// <summary>
    /// Records new settings for an adjustment layer. Consecutive changes made with the same control merge, so
    /// a slider drag is one undo step (like other property edits).
    /// </summary>
    public void SetAdjustment(AdjustmentLayer layer, Adjustment adjustment, string control)
    {
        if (PsdAdjustmentWriter.SameSettings(layer.Adjustment, adjustment)) return;
        Apply(new PropertyEdit<Adjustment?>(layer, control, layer.Adjustment, adjustment,
            static (n, v) => ((AdjustmentLayer)n).Adjustment = v));
    }

    /// <summary>Replaces the Properties panel when the selection or target changes to something it cannot show.</summary>
    internal void UpdateProperties()
    {
        if (UpdateMultiLayerProperties()) return; // several layers selected: Align and Distribute (LayersAlignPanel.cs)
        var node = SelectedLayer?.Node;
        if (Properties is { } current && current.Fits(node))
        {
            current.Refresh();
            return;
        }
        Properties?.Dispose();
        Properties = PropertiesPanel.For(this, node);
    }

    /// <summary>
    /// Histograms of the image as it looks without <paramref name="layer"/> (what an adjustment starts from):
    /// luminosity, red, green and blue, 256 bins each. Rendered from a reduced copy, off the UI thread.
    /// </summary>
    public async Task<int[][]> HistogramWithoutAsync(LayerNode layer)
    {
        int factor = 1;
        while (factor < 16 && Math.Max(Model.Width, Model.Height) / factor > 1024) factor *= 2;
        var preview = new PreviewDocument(Model, factor);
        var proxy = preview.Sync();
        var hidden = preview.ProxyOf(layer) is { } p ? new HashSet<LayerNode> { p } : null;
        return await Task.Run(() =>
        {
            var rgba = Compositor.Render(proxy, new RenderOptions { Hidden = hidden }).ToRgba8();
            var bins = new int[4][];
            for (int c = 0; c < 4; c++) bins[c] = new int[256];
            for (int i = 0; i < rgba.Length; i += 4)
            {
                if (rgba[i + 3] == 0) continue;
                byte r = rgba[i], g = rgba[i + 1], b = rgba[i + 2];
                bins[0][(r * 299 + g * 587 + b * 114 + 500) / 1000]++;
                bins[1][r]++;
                bins[2][g]++;
                bins[3][b]++;
            }
            return bins;
        });
    }

    /// <summary>
    /// Adds an adjustment layer on top and drags one of its Properties sliders for about two seconds with 120 Hz
    /// input, through the same panel view model the UI uses; reports frames reaching the screen and the time
    /// until the full-resolution image follows (STRAYTA_ADJUSTBENCH=1).
    /// </summary>
    public async Task RunAdjustmentBenchmarkAsync(IReadOnlyList<string>? kinds = null)
    {
        kinds ??= Environment.GetEnvironmentVariable("STRAYTA_ADJUSTBENCH_KINDS")?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            ?? ["Levels", "Hue/Saturation", "Curves", "Exposure", "Vibrance", "Color Balance", "Black & White", "Photo Filter",
                "Channel Mixer", "Selective Color", "Gradient Map", "Color Lookup"];
        foreach (var kind in kinds)
        {
            SelectedLayer = Layers.FirstOrDefault();
            NewAdjustmentLayer(kind);
            var panel = Properties;
            if (panel is ColorLookupPanel lookup) lookup.LutName = lookup.LutNames[^1]; // a built-in look; the drag moves Dither
            var start = ((AdjustmentLayer)SelectedLayer!.Node).Adjustment;
            Action<int> drag = panel switch
            {
                LevelsPanel l => i => l.InputWhite = 255 - i % 120,
                HueSaturationPanel h => i => h.Hue = i % 120 - 60,
                CurvesPanel c => i => c.SetPoints([new(0, 0), new(128, 128 + i % 100 - 50), new(255, 255)]),
                ExposurePanel e => i => e.Exposure = (i % 120 - 60) / 30.0,
                VibrancePanel v => i => v.Vibrance = i % 120 - 60,
                ColorBalancePanel b => i => b.CyanRed = i % 120 - 60,
                BlackWhitePanel w => i => w.Reds = i % 200,
                PhotoFilterPanel f => i => f.Density = 1 + i % 99,
                ChannelMixerPanel m => i => m.Red = 100 - i % 100,
                SelectiveColorPanel sc => i => sc.Cyan = i % 100,
                GradientMapPanel g => i => g.Gradient = GradientModel.TwoColor("Bench", RgbColor.Black, new RgbColor(i % 120 / 120f, 0.5f, 0.2f)),
                ColorLookupPanel lut => i => lut.Dither = i % 2 == 0,
                _ => throw new InvalidOperationException($"No panel for {kind}."),
            };

            int frames = 0;
            var times = new List<double>();
            var parts = new List<double>();
            var clock = Stopwatch.StartNew();
            double last = 0;
            void OnFrame(bool full)
            {
                if (full) return;
                frames++;
                parts.Add(LastFrameTimings.Render);
                times.Add(clock.Elapsed.TotalMilliseconds - last);
                last = clock.Elapsed.TotalMilliseconds;
            }
            FrameDisplayed += OnFrame;
            for (int i = 1; i <= 240; i++)
            {
                drag(i);
                await Task.Delay(8);
            }
            double inputMs = clock.Elapsed.TotalMilliseconds;
            var fullShown = new TaskCompletionSource<double>();
            void OnFull(bool full) { if (full) fullShown.TrySetResult(clock.Elapsed.TotalMilliseconds - inputMs); }
            FrameDisplayed += OnFull;
            var settle = await Task.WhenAny(fullShown.Task, Task.Delay(5000));
            FrameDisplayed -= OnFull;
            FrameDisplayed -= OnFrame;

            // The whole drag must be one undo step: undoing once restores the starting settings.
            var layer = (AdjustmentLayer)SelectedLayer!.Node;
            Undo();
            bool oneStep = PsdAdjustmentWriter.SameSettings(layer.Adjustment, start);

            times.Sort();
            parts.Sort();
            double Pick(List<double> v, double q) => v.Count == 0 ? double.NaN : v[(int)(v.Count * q)];
            Console.WriteLine($"ADJUSTBENCH {kind} {Model.Width}x{Model.Height} input={inputMs:F0}ms frames={frames} fps={frames / (inputMs / 1000):F1} " +
                              $"median-frame={Pick(times, 0.5):F0}ms p90={Pick(times, 0.9):F0}ms median-render={Pick(parts, 0.5):F1}ms " +
                              $"factor={PreviewDocument.FactorForZoom(_viewZoom)} one-undo-step={oneStep} " +
                              (settle == fullShown.Task ? $"full-res={fullShown.Task.Result:F0}ms after input (incl. 400 ms idle delay)" : "full-res did not appear within 5 s"));
            while (CanUndo) Undo();
        }
    }
}
