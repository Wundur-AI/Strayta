using Strayta.Core;
using Strayta.Core.Painting;
using Strayta.Core.Selection;
using Strayta.Editor.Controls;
using Strayta.Editor.ViewModels;

namespace Strayta.Editor;

internal static partial class SelfTest
{
    /// <summary>
    /// A textured RGB image with a gently varying light, R/G/B planes plus opaque alpha (or none, for a Background),
    /// at 8 or 16 bits, with optional dark blemishes (x, y, radius).
    /// </summary>
    private static Raster RetouchTexture(int w, int h, int bitDepth, bool background, params (int X, int Y, int R)[] blemishes)
    {
        var planes = Enumerable.Range(0, 4).Select(_ => Plane.Create(w, h, bitDepth)).ToArray();
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                float t = 0.25f * MathF.Sin(2 * MathF.PI * x / 16) * MathF.Sin(2 * MathF.PI * y / 24) + 0.1f * MathF.Sin(2 * MathF.PI * (x + y) / 12);
                float light = 0.06f * MathF.Sin(x / 90f) + 0.0004f * y;
                float dark = 1f;
                foreach (var (bx, by, br) in blemishes)
                    dark = MathF.Min(dark, 1 - 0.8f * Math.Clamp(br + 1 - MathF.Sqrt((x - bx) * (x - bx) + (y - by) * (y - by)), 0, 1));
                int i = y * w + x;
                for (int k = 0; k < 3; k++) SetSample(planes[k], i, (0.45f + t * (1 - 0.2f * k) + light) * dark);
                SetSample(planes[3], i, 1f);
            }
        });
        return new Raster(ColorMode.Rgb, planes[..3], background ? null : planes[3]);
    }

    private static void SetSample(Plane p, int i, float v)
    {
        v = Math.Clamp(v, 0f, 1f);
        if (p.BitDepth == 16) p.AsUInt16()[i] = (ushort)MathF.Round(v * 65535f);
        else p.Data[i] = (byte)MathF.Round(v * 255f);
    }

    private static float Sample(PixelLayer layer, int channel, int x, int y) =>
        layer.Pixels!.ColorPlanes[channel].GetNormalized((y - layer.Bounds.Top) * layer.Bounds.Width + (x - layer.Bounds.Left));

    /// <summary>RMSE (0..1) of a layer against a ground-truth raster over a disc.</summary>
    private static double DiscError(PixelLayer layer, Raster truth, int cx, int cy, int r)
    {
        double sum = 0;
        int n = 0;
        for (int y = cy - r; y <= cy + r; y++)
            for (int x = cx - r; x <= cx + r; x++)
            {
                if ((x - cx) * (x - cx) + (y - cy) * (y - cy) > r * r) continue;
                for (int k = 0; k < 3; k++)
                {
                    double d = Sample(layer, k, x, y) - truth.ColorPlanes[k].GetNormalized(y * truth.Width + x);
                    sum += d * d;
                    n++;
                }
            }
        return Math.Sqrt(sum / n);
    }

    /// <summary>Clone Stamp, Healing Brush and Spot Healing Brush through the real view models.</summary>
    private static async Task RunRetouchStepsAsync(EditorViewModel editor, Action<bool, string> check)
    {
        try
        {
            // ---- Tool keys ------------------------------------------------------------------------------
            editor.HandleToolKey("J", shift: false);
            check(editor.IsSpotHealingTool && editor.ToolName == "Spot Healing Brush", "J selects the Spot Healing Brush first, as in Photoshop");
            editor.HandleToolKey("J", shift: true);
            check(editor.IsHealingTool && editor.ToolName == "Healing Brush", "Shift+J switches to the Healing Brush");
            editor.HandleToolKey("S", shift: false);
            check(editor.IsCloneStampTool && editor.ToolName == "Clone Stamp", "S selects the Clone Stamp");
            int b = editor.ToolGroups.ToList().FindIndex(g => g.Key == "B");
            check(editor.ToolGroups[b - 1].Key == "J" && editor.ToolGroups[b + 1].Key == "S", "the strip has J before the Brush and S after it");
            double size = editor.BrushSize;
            editor.ResizeBrushCommand.Execute("up");
            check(editor.BrushSize > size && editor.ToolBrushSize == editor.BrushSize, "] enlarges the Clone Stamp's brush");
            editor.ResizeBrushCommand.Execute("down");

            // ---- Clone Stamp on a Background -----------------------------------------------------------
            // R encodes x and G encodes y, so every cloned pixel says where it came from.
            const int w = 240, h = 160;
            var model = Editing.LayerFactory.NewDocument(w, h, whiteBackground: true);
            var bg = (PixelLayer)model.Root.Children[0];
            var planes = Enumerable.Range(0, 3).Select(_ => Plane.Create(w, h, 8)).ToArray();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    (planes[0].Data[y * w + x], planes[1].Data[y * w + x], planes[2].Data[y * w + x]) = ((byte)x, (byte)y, 128);
            bg.Pixels = new Raster(ColorMode.Rgb, planes, null);
            var doc = new DocumentViewModel(model, null, editor);
            editor.Factory.AddDocument(doc);
            editor.ActiveDocument = doc;
            await doc.RenderAsync();
            doc.SelectedLayer = doc.Layers[0];
            editor.Tool = CanvasTool.CloneStamp;
            editor.CloneAligned = true;
            editor.CloneSampleIndex = 0;
            editor.BrushSize = 10;
            editor.BrushHardness = 100;
            editor.BrushOpacity = 100;

            check(!doc.BeginRetouchStroke(100, 60) && doc.Notice.Contains("Option-click"), "without a source point the Clone Stamp asks for an Option-click");
            doc.SetCloneSource(40, 60);
            check(doc.RetouchSourceFor(130, 70) == (40.5f, 60.5f), "before painting, the source crosshair sits on the source point");
            var before = bg.Pixels;
            check(doc.BeginRetouchStroke(120, 60), "the Clone Stamp paints once a source is set");
            for (int x = 122; x <= 140; x += 2) doc.ContinueStroke(x, 60);
            check(doc.RetouchSourceFor(140, 60) == (60, 60), "while painting, the crosshair follows the sampled point");
            await Until(() => doc.RenderInfo.Length > 0, 500);
            check(ReferenceEquals(bg.Pixels, before), "the layer is untouched while the stroke is previewed");
            await doc.EndStrokeAsync();
            check(Sample(bg, 0, 130, 60) == 50 / 255f && Sample(bg, 1, 130, 60) == 60 / 255f && Sample(bg, 0, 150, 60) == 150 / 255f,
                $"cloned pixels come from 80 px to the left; others are untouched (R {Sample(bg, 0, 130, 60) * 255:F0})");
            check(bg.Pixels!.Alpha is null && doc.UndoText == "Undo Clone Stamp", "the Background stays opaque and the stroke is one undo step");

            // Aligned: a second stroke keeps the offset; off: every stroke starts at the source again.
            doc.BeginRetouchStroke(200, 30);
            await doc.EndStrokeAsync();
            check(Sample(bg, 0, 200, 30) == 120 / 255f && Sample(bg, 1, 200, 30) == 30 / 255f, "Aligned keeps the offset for the next stroke");
            editor.CloneAligned = false;
            doc.BeginRetouchStroke(100, 120);
            await doc.EndStrokeAsync();
            check(Sample(bg, 0, 100, 120) == 40 / 255f && Sample(bg, 1, 100, 120) == 60 / 255f, "with Aligned off the stroke starts at the source point");
            editor.CloneAligned = true;

            // A stroke never samples what it painted itself: cloning 4 px along the stroke's own path.
            doc.SetCloneSource(20, 140);
            doc.BeginRetouchStroke(24, 140);
            for (int x = 26; x <= 90; x += 2) doc.ContinueStroke(x, 140);
            await doc.EndStrokeAsync();
            check(Sample(bg, 0, 70, 140) == 66 / 255f, $"the source is the layer as it was when the stroke started (R {Sample(bg, 0, 70, 140) * 255:F0}, not smeared)");

            // The selection confines it.
            doc.SetSelection(SelectionMask.Rectangle(new PixelRect(0, 0, 150, h), model.Bounds), "test");
            doc.SetCloneSource(60, 100);
            doc.BeginRetouchStroke(150, 100);
            await doc.EndStrokeAsync();
            check(Sample(bg, 0, 148, 100) == 58 / 255f && Sample(bg, 0, 152, 100) == 152 / 255f, "the selection confines the Clone Stamp");
            doc.Deselect();

            // Sample All Layers onto an empty layer.
            doc.NewLayer();
            var top = (PixelLayer)doc.SelectedLayer!.Node;
            editor.CloneSampleIndex = 2;
            doc.SetCloneSource(10, 10);
            check(doc.BeginRetouchStroke(60, 30), "the Clone Stamp paints an empty layer sampling all layers");
            doc.ContinueStroke(64, 30);
            await doc.EndStrokeAsync();
            check(top.Pixels is not null && Sample(top, 0, 62, 30) == 12 / 255f && Sample(top, 1, 62, 30) == 10 / 255f,
                "All Layers copies the image below onto the new layer");
            editor.CloneSampleIndex = 1; // Current & Below from the Background ignores the layer above it
            doc.SelectedLayer = doc.Layers.SelectMany(l => l.SelfAndDescendants()).First(i => i.Node == bg);
            doc.SetCloneSource(60, 30);
            doc.BeginRetouchStroke(60, 90);
            await doc.EndStrokeAsync();
            check(Sample(bg, 1, 60, 90) == 30 / 255f, "Current & Below samples the layers below the selected one");
            editor.CloneSampleIndex = 0;

            // A layer mask: the Clone Stamp copies mask values.
            doc.SelectedLayer = doc.Layers.SelectMany(l => l.SelfAndDescendants()).First(i => i.Node == top);
            doc.AddMask(reveal: true);
            editor.ForegroundColor = Avalonia.Media.Colors.Black;
            doc.BeginStroke(30, 120, new BrushSettings(20, 1f, 1f), new RgbColor(0, 0, 0), erase: false);
            await doc.EndStrokeAsync();
            doc.SetCloneSource(30, 120);
            check(doc.EditMask && doc.BeginRetouchStroke(180, 120), "the Clone Stamp paints the targeted mask");
            await doc.EndStrokeAsync();
            check(MaskBaker.Sample(top.Mask!, 180, 120) == 0f && MaskBaker.Sample(top.Mask!, 200, 120) == 1f, "it copies the mask's black to the new spot");

            int steps = 0;
            while (doc.CanUndo) { doc.Undo(); steps++; }
            check(ReferenceEquals(bg.Pixels, before) || bg.Pixels == before, $"undo takes every retouch step back ({steps} steps)");

            // ---- Healing Brush --------------------------------------------------------------------------
            const int hw = 320, hh = 200;
            var truth = RetouchTexture(hw, hh, 8, background: false);
            var healModel = Editing.LayerFactory.NewDocument(hw, hh, whiteBackground: true);
            var photo = new PixelLayer { Name = "Photo", Bounds = healModel.Bounds, Pixels = RetouchTexture(hw, hh, 8, false, (200, 100, 8)) };
            healModel.Root.Add(photo);
            var healDoc = new DocumentViewModel(healModel, null, editor);
            editor.Factory.AddDocument(healDoc);
            editor.ActiveDocument = healDoc;
            await healDoc.RenderAsync();
            healDoc.SelectedLayer = healDoc.Layers[0];
            editor.Tool = CanvasTool.Healing;
            editor.HealAligned = false;
            editor.HealSampleIndex = 0;
            editor.BrushSize = 26;
            editor.BrushHardness = 80;
            double blemished = DiscError(photo, truth, 200, 100, 9);
            healDoc.SetCloneSource(150, 100); // 48 px left of the stroke's start: the texture repeats every 48 px across (light differs)
            healDoc.BeginRetouchStroke(198, 100);
            healDoc.ContinueStroke(202, 100);
            await healDoc.EndStrokeAsync();
            double healed = DiscError(photo, truth, 200, 100, 9);
            check(healed < blemished * 0.1 && healed < 0.02 && healDoc.UndoText == "Undo Healing Brush",
                $"the Healing Brush removes the blemish (RMSE {blemished:F3} -> {healed:F4}, {healDoc.LastRetouchTimings.TotalMs:F0} ms)");

            // ---- Spot Healing Brush ---------------------------------------------------------------------
            photo.Pixels = RetouchTexture(hw, hh, 8, false, (100, 60, 8));
            editor.Tool = CanvasTool.SpotHealing;
            editor.SpotSampleAllLayers = false;
            blemished = DiscError(photo, truth, 100, 60, 9);
            healDoc.BeginRetouchStroke(98, 59);
            healDoc.ContinueStroke(102, 61);
            await healDoc.EndStrokeAsync();
            healed = DiscError(photo, truth, 100, 60, 9);
            check(healed < blemished * 0.1 && healed < 0.02 && healDoc.UndoText == "Undo Spot Healing Brush",
                $"the Spot Healing Brush removes a blemish with no source set (RMSE {blemished:F3} -> {healed:F4}, offset {healDoc.LastSpotOffset}, " +
                $"{healDoc.LastRetouchTimings.TotalMs:F0} ms)");

            // 16-bit, sampling all layers onto an empty layer.
            var deepTruth = RetouchTexture(hw, hh, 16, false);
            var deepModel = new Document(hw, hh, ColorMode.Rgb, 16);
            deepModel.Root.Add(new PixelLayer { Name = "Photo", Bounds = deepModel.Bounds, Pixels = RetouchTexture(hw, hh, 16, false, (160, 120, 7)) });
            var deepDoc = new DocumentViewModel(deepModel, null, editor);
            editor.Factory.AddDocument(deepDoc);
            editor.ActiveDocument = deepDoc;
            await deepDoc.RenderAsync();
            deepDoc.SelectedLayer = deepDoc.Layers[0];
            deepDoc.NewLayer();
            var fix = (PixelLayer)deepDoc.SelectedLayer!.Node;
            editor.SpotSampleAllLayers = true;
            deepDoc.BeginRetouchStroke(159, 120);
            deepDoc.ContinueStroke(161, 120);
            await deepDoc.EndStrokeAsync();
            check(fix.Pixels is { BitDepth: 16 } && fix.Bounds.Width < 40,
                $"in a 16-bit document Spot Healing with Sample All Layers paints its fix on the empty layer ({fix.Bounds})");
            float centre = Sample(fix, 0, 160, 120), truthCentre = deepTruth.ColorPlanes[0].GetNormalized(120 * hw + 160);
            check(Math.Abs(centre - truthCentre) < 0.02, $"the fix matches the unblemished image ({centre:F4} vs {truthCentre:F4})");
            editor.SpotSampleAllLayers = false;
        }
        catch (Exception ex)
        {
            check(false, $"exception in retouch steps: {ex}");
        }
    }

    /// <summary>STRAYTA_RETOUCHBENCH=new: the retouch benchmark on a generated 4000×3000 textured photo.</summary>
    public static async Task RunSyntheticRetouchBenchmarkAsync(EditorViewModel editor)
    {
        const int w = 4000, h = 3000;
        var model = Editing.LayerFactory.NewDocument(w, h, whiteBackground: true);
        model.Root.Add(new PixelLayer { Name = "Photo", Bounds = model.Bounds, Pixels = RetouchTexture(w, h, 8, false) });
        var doc = new DocumentViewModel(model, null, editor);
        editor.Factory.AddDocument(doc);
        editor.ActiveDocument = doc;
        await doc.RenderAsync();
        doc.SelectedLayer = doc.Layers[0];
        await Task.Delay(1500); // let the view fit the image and the preview caches warm up
        await doc.RunRetouchBenchmarkAsync();
    }
}
