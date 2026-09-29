using System.Numerics;
using Strayta.Core;
using Strayta.Core.Selection;
using Strayta.Editor.Controls;
using Strayta.Editor.ViewModels;
using Strayta.Segmentation;

namespace Strayta.Editor;

internal static partial class SelfTest
{
    // A shaded blue cap (dome and brim, a white logo on it, its shadow side close to the background's colors) and a
    // shaded orange ball, on a blue-gray to olive gradient with grain: what plain Quick Selection needs many strokes
    // for and leaks on.
    private const float CapX = 520, CapY = 560, CapRx = 300, CapRy = 240;
    private static bool CapDome(float x, float y) => y <= CapY && (x - CapX) * (x - CapX) / (CapRx * CapRx) + (y - CapY) * (y - CapY) / (CapRy * CapRy) < 1;
    private static bool CapBrim(float x, float y) => y > CapY - 1 && (x - CapX - 135) * (x - CapX - 135) / (420f * 420) + (y - CapY) * (y - CapY) / (52f * 52) < 1;
    private static bool InCap(int x, int y) => CapDome(x + 0.5f, y + 0.5f) || CapBrim(x + 0.5f, y + 0.5f);
    private static bool InBall(int x, int y) => (x + 0.5f - 1000) * (x + 0.5f - 1000) + (y + 0.5f - 250) * (y + 0.5f - 250) < 110 * 110;

    private static (byte, byte, byte, byte) CapScene(int x, int y, int grain)
    {
        float px = x + 0.5f, py = y + 0.5f;
        float r, g, b;
        if (InBall(x, y))
        {
            float dx = (px - 1000) / 110, dy = (py - 250) / 110;
            var n = new Vector3(dx, dy, MathF.Sqrt(Math.Max(0, 1 - dx * dx - dy * dy)));
            float k = 0.3f + 0.7f * Math.Max(0, Vector3.Dot(n, Vector3.Normalize(new Vector3(-0.5f, -0.6f, 0.62f))));
            (r, g, b) = (230 * k, 130 * k, 40 * k);
        }
        else if (!InCap(x, y)) (r, g, b) = (60 + 40 * py / 900, 70 + 30 * py / 900, 110 - 40 * py / 900);
        else if (CapDome(px, py) && Math.Abs(px - CapX + 22) < 68 && Math.Abs(py - CapY + 128) < 38) (r, g, b) = (235, 235, 230);
        else
        {
            float k = Math.Clamp(1.1f - 0.55f * (px - CapX) / CapRx - 0.7f * (py - CapY + CapRy) / (CapRy * 2), 0.15f, 1.2f);
            if (!CapDome(px, py)) k *= 0.55f;
            (r, g, b) = (60 * k + 20, 110 * k + 10, 150 * k + 10);
        }
        return (Grain(r, 0), Grain(g, 1), Grain(b, 2), 255);

        byte Grain(float v, int c)
        {
            uint h = (uint)(x * 73856093) ^ (uint)(y * 19349663) ^ (uint)(c * 83492791) ^ (uint)(grain * 2654435761u);
            h ^= h >> 13;
            h *= 0x5bd1e995;
            h ^= h >> 15;
            return (byte)Math.Clamp(v + (int)(h % 13u) - 6, 0, 255);
        }
    }

    /// <summary>
    /// Quick Selection's Object-Aware option through the real view models: compared with plain Quick Selection on a
    /// shaded object, adding and subtracting with the session's prompts, one history step per drag, falling back to
    /// plain growth until the image is analyzed, and without the models.
    /// </summary>
    private static async Task RunQuickSelectObjectsStepsAsync(EditorViewModel editor, Action<bool, string> check)
    {
        try
        {
            await QuickSelectObjectsStepsAsync(editor, check);
        }
        catch (Exception ex)
        {
            check(false, $"exception in Object-Aware Quick Selection steps: {ex}");
        }
        editor.QuickSelectObjectAware = false;
        editor.Tool = CanvasTool.Move;
    }

    private static async Task QuickSelectObjectsStepsAsync(EditorViewModel editor, Action<bool, string> check)
    {
        const int w = 1200, h = 900;
        var model = Editing.LayerFactory.NewDocument(w, h, whiteBackground: true);
        var scene = ObjectTestLayer("Scene", PixelRect.FromSize(w, h), (x, y) => CapScene(x, y, 0));
        model.Root.Add(scene);
        var doc = new DocumentViewModel(model, null, editor);
        editor.Factory.AddDocument(doc);
        editor.ActiveDocument = doc;
        await doc.RenderAsync();
        doc.SelectedLayer = doc.Layers.First(l => l.Node == scene);
        editor.Tool = CanvasTool.QuickSelect;
        editor.QuickSelectSize = 30;
        var canvas = model.Bounds;
        double CapIou(SelectionMask? m) => ObjectIou(m, w, h, InCap);

        // Brushing across the dome, as a user would: a short stroke over the lit side into the shade.
        Vector2[] dome = [new(370, 485), new(470, 470), new(560, 480)];
        Vector2[] ball = [new(975, 235), new(1020, 260)];

        // Plain Quick Selection first, for comparison.
        editor.QuickSelectObjectAware = false;
        await QuickDragAsync(doc, SelectionMode.Replace, dome);
        double classical = CapIou(doc.Selection);
        doc.Deselect();

        bool models = SegmentationEngine.Shared.CanSelectObjects;
        check(editor.HasObjectModel == models && (models || editor.QuickSelectObjectAwareTip == SegmentationModels.FetchHint),
            models ? "the Object-Aware option is available with the models installed" : "without the models Object-Aware is disabled and its tooltip says how to get them");
        if (!models)
        {
            // Turned on anyway (e.g. models removed while running): brushing is plain Quick Selection, no error.
            editor.QuickSelectObjectAware = true;
            await QuickDragAsync(doc, SelectionMode.Replace, dome);
            check(Math.Abs(CapIou(doc.Selection) - classical) < 1e-9 && doc.Notice == "" && doc.UndoText == "Undo Quick Selection",
                $"without the models Object-Aware brushing falls back to plain Quick Selection (IoU {classical:F3})");
            Console.WriteLine("SELFTEST skip Object-Aware Quick Selection model steps: models not installed (dotnet build tools/FetchModels.proj)");
            editor.QuickSelectObjectAware = false;
            editor.Tool = CanvasTool.Move;
            return;
        }

        // Object-Aware, the image analyzed beforehand (as when the tool is picked with the option on).
        editor.QuickSelectObjectAware = true;
        await doc.PrepareQuickSelectObjects();
        int steps = CountSteps(doc);
        await QuickDragAsync(doc, SelectionMode.Replace, dome);
        var cap = doc.Selection;
        double aware = CapIou(cap);
        var stats = doc.LastObjectAwareStats;
        check(aware > 0.95 && aware > classical + 0.1 && stats.UsedPrior,
            $"Object-Aware selects the whole shaded cap from one stroke: IoU {aware:F3} vs {classical:F3} plain ({stats.Decodes} SAM decodes, median {Median(stats.DecodeMs):F0} ms)");
        check(doc.UndoText == "Undo Quick Selection" && CountSteps(doc) == steps + 1, "the drag is one history step");
        check(Selected(cap, canvas, (x, y) => !InCap(x, y)) < 0.01 * Selected(cap, canvas), "nothing leaks into the background");

        // Shift-drag on the ball adds it; Option-drag on it takes it away again without touching the cap.
        await QuickDragAsync(doc, SelectionMode.Add, ball);
        double union = ObjectIou(doc.Selection, w, h, (x, y) => InCap(x, y) || InBall(x, y));
        check(doc.Selection is { } both && both.CoverageAt(1000, 250) == 255 && both.CoverageAt(470, 470) == 255 && union > 0.95 && doc.LastObjectAwareStats.UsedPrior,
            $"a Shift-stroke adds the shaded ball (cap and ball IoU {union:F3})");
        await QuickDragAsync(doc, SelectionMode.Subtract, ball);
        double after = CapIou(doc.Selection);
        check(doc.Selection is { } left && left.CoverageAt(1000, 250) == 0 && after > 0.95 && doc.LastObjectAwareStats.UsedPrior,
            $"an Option-stroke on the ball subtracts it and leaves the cap (cap IoU {after:F3})");
        doc.Undo();
        doc.Undo();
        check(ReferenceEquals(doc.Selection, cap), "undo steps back one drag at a time");

        // The session remembers subtracted strokes as negative prompts: Option-brush the brim away, then Shift-brush
        // the dome again. Without the negatives SAM would hand back the whole cap, brim included.
        bool InDome(int x, int y) => CapDome(x + 0.5f, y + 0.5f);
        await QuickDragAsync(doc, SelectionMode.Subtract, [new(800, 582), new(950, 582)]);
        double dome1 = ObjectIou(doc.Selection, w, h, InDome);
        check(doc.Selection is { } noBrim && noBrim.CoverageAt(900, 585) == 0 && noBrim.CoverageAt(470, 470) == 255 && dome1 > 0.9,
            $"an Option-stroke along the brim takes just the brim away (dome IoU {dome1:F3})");
        await QuickDragAsync(doc, SelectionMode.Add, [new(600, 420), new(680, 500)]);
        double dome2 = ObjectIou(doc.Selection, w, h, InDome);
        check(doc.Selection is { } still && still.CoverageAt(900, 585) == 0 && dome2 > 0.9 && doc.LastObjectAwareStats.UsedPrior,
            $"a later Shift-stroke on the dome keeps the brim out: the Option-stroke's points are negative prompts (dome IoU {dome2:F3})");
        doc.Undo();
        doc.Undo();

        // A fresh image is not analyzed yet: the drag grows as plain Quick Selection at once, and becomes object-aware
        // when the analysis arrives, still during the drag.
        var freshModel = Editing.LayerFactory.NewDocument(w, h, whiteBackground: true);
        var freshScene = ObjectTestLayer("Scene", PixelRect.FromSize(w, h), (x, y) => CapScene(x, y, 1));
        freshModel.Root.Add(freshScene);
        var fresh = new DocumentViewModel(freshModel, null, editor);
        editor.Factory.AddDocument(fresh);
        editor.ActiveDocument = fresh;
        await fresh.RenderAsync();
        fresh.SelectedLayer = fresh.Layers.First(l => l.Node == freshScene);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        fresh.BeginQuickSelection(dome[0].X, dome[0].Y, SelectionMode.Replace);
        bool liveBeforeAnalysis = false;
        // Analysis takes ~1.5 s on a quiet machine and several times that under load; the drag keeps going until it
        // arrives (up to 30 s), so the check is that the upgrade happens mid-drag, not how soon.
        for (int i = 0; clock.ElapsedMilliseconds < 30000; i++)
        {
            var p = Vector2.Lerp(dome[0], dome[^1], (i % 60) / 60f);
            fresh.ContinueQuickSelection(p.X, p.Y);
            await Task.Delay(16);
            if (clock.ElapsedMilliseconds is > 150 and < 300 && fresh.QuickSelectionOutline is not null && fresh.IsAnalyzing) liveBeforeAnalysis = true;
            if (clock.ElapsedMilliseconds > 400 && !fresh.IsAnalyzing) break;
        }
        await Task.Delay(100);
        await fresh.EndQuickSelectionAsync();
        var fs = fresh.LastObjectAwareStats;
        double freshIou = ObjectIou(fresh.Selection, w, h, InCap);
        check(liveBeforeAnalysis && fs.UsedPrior && fs.FirstPriorMs > 300 && freshIou > 0.95,
            $"before the image is analyzed the drag grows as plain Quick Selection, then upgrades mid-drag (object prior after {fs.FirstPriorMs:F0} ms; IoU {freshIou:F3})");

        editor.QuickSelectObjectAware = false;
        editor.Tool = CanvasTool.Move;

        static double Median(IReadOnlyList<double> v) => v.Count == 0 ? double.NaN : v.OrderBy(t => t).ElementAt(v.Count / 2);

        static int CountSteps(DocumentViewModel d)
        {
            int n = 0;
            while (d.CanUndo) { d.Undo(); n++; }
            for (int i = 0; i < n; i++) d.Redo();
            return n;
        }
    }

    /// <summary>A Quick Selection drag along <paramref name="path"/> with pointer events every few pixels, like a mouse.</summary>
    private static async Task QuickDragAsync(DocumentViewModel doc, SelectionMode mode, Vector2[] path)
    {
        doc.BeginQuickSelection(path[0].X, path[0].Y, mode);
        for (int i = 1; i < path.Length; i++)
        {
            int n = Math.Max(1, (int)(Vector2.Distance(path[i - 1], path[i]) / 6));
            for (int s = 1; s <= n; s++)
            {
                var p = Vector2.Lerp(path[i - 1], path[i], (float)s / n);
                doc.ContinueQuickSelection(p.X, p.Y);
                await Task.Delay(8);
            }
        }
        await doc.EndQuickSelectionAsync();
    }
}
