using Vector2 = System.Numerics.Vector2;
using Strayta.Core;
using Strayta.Core.Selection;
using Strayta.Editor.Controls;
using Strayta.Editor.ViewModels;

namespace Strayta.Editor;

internal static partial class SelfTest
{
    private static bool InDisc(int x, int y) => (x + 0.5 - 120) * (x + 0.5 - 120) + (y + 0.5 - 150) * (y + 0.5 - 150) < 60 * 60;
    private static bool InSquare(int x, int y) => x is >= 250 and < 350 && y is >= 100 and < 200;
    private static bool InSmall(int x, int y) => x is >= 300 and < 340 && y is >= 20 and < 60;

    /// <summary>A layer with a red disc, a blue square and a small red square on transparency.</summary>
    private static PixelLayer ShapesLayer(int w, int h)
    {
        var planes = Enumerable.Range(0, 4).Select(_ => Plane.Create(w, h, 8)).ToArray();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                (byte r, byte g, byte b, byte a) = InDisc(x, y) || InSmall(x, y) ? ((byte)220, (byte)30, (byte)30, (byte)255)
                    : InSquare(x, y) ? ((byte)30, (byte)60, (byte)220, (byte)255)
                    : ((byte)0, (byte)0, (byte)0, (byte)0);
                (planes[0].Data[i], planes[1].Data[i], planes[2].Data[i], planes[3].Data[i]) = (r, g, b, a);
            }
        return new PixelLayer { Name = "Shapes", Bounds = PixelRect.FromSize(w, h), Pixels = new Raster(ColorMode.Rgb, planes[..3], planes[3]) };
    }

    private static int Selected(SelectionMask? s, PixelRect area, Func<int, int, bool>? where = null)
    {
        int n = 0;
        for (int y = area.Top; y < area.Bottom; y++)
            for (int x = area.Left; x < area.Right; x++)
                if (s is not null && s.CoverageAt(x, y) >= 128 && (where is null || where(x, y))) n++;
        return n;
    }

    /// <summary>Magic Wand and Quick Selection through the real view models: tools, options, modifiers, undo.</summary>
    private static async Task RunMagicWandStepsAsync(EditorViewModel editor, Action<bool, string> check)
    {
        try
        {
            // The W group: W picks the last used tool, Shift+W switches.
            editor.SelectWandToolCommand.Execute(null);
            check(editor.Tool == CanvasTool.MagicWand && editor.ToolName == "Magic Wand", "W selects the Magic Wand");
            editor.SelectWandToolCommand.Execute("cycle");
            check(editor.IsQuickSelectTool && editor.ToolName == "Quick Selection", "Shift+W switches to Quick Selection");
            editor.SetToolCommand.Execute("Move");
            editor.SelectWandToolCommand.Execute(null);
            check(editor.IsQuickSelectTool, "W comes back to the tool used last");
            double brush = editor.BrushSize;
            editor.ResizeBrushCommand.Execute("up");
            check(editor.QuickSelectSize > 30 && editor.BrushSize == brush && editor.ToolBrushSize == editor.QuickSelectSize,
                "] resizes the Quick Selection brush, not the paint brush");
            editor.QuickSelectSize = 30;
            editor.SelectWandToolCommand.Execute("cycle");

            const int W = 400, H = 300;
            var model = Editing.LayerFactory.NewDocument(W, H, whiteBackground: true);
            var doc = new DocumentViewModel(model, null, editor);
            editor.Factory.AddDocument(doc);
            editor.ActiveDocument = doc;
            await doc.RenderAsync();
            var shapes = ShapesLayer(W, H);
            doc.Apply(new Editing.InsertEdit(shapes, model.Root, 1, "test"));
            LayerItemViewModel Item(LayerNode n) => doc.Layers.SelectMany(l => l.SelfAndDescendants()).First(i => i.Node == n);
            doc.SelectedLayer = Item(shapes);
            var canvas = model.Bounds;

            // Current layer, contiguous, anti-aliased (the defaults).
            await doc.MagicWandAsync(120, 150, SelectionMode.Replace);
            var disc = doc.Selection;
            check(disc is not null && Selected(disc, canvas) == Selected(disc, canvas, InDisc) && Math.Abs(Selected(disc, canvas) - Math.PI * 3600) < 150 &&
                  doc.UndoText == "Undo Magic Wand", $"a click selects the disc and is a history step ({disc?.Bounds})");
            check(Enumerable.Range(55, 20).Any(x => disc!.CoverageAt(x, 150) is > 0 and < 255), "anti-aliased: the edge is soft");

            // Modifiers (hard edges from here on: subtracting a soft edge from itself leaves faint coverage, as the
            // combine math does for every tool).
            editor.WandAntiAlias = false;
            await doc.MagicWandAsync(120, 150, SelectionMode.Replace);
            check(await AntsVisibleAsync(doc, "wand"), "marching ants follow the wand selection");
            await doc.MagicWandAsync(300, 150, SelectionMode.Add);
            check(doc.Selection!.CoverageAt(300, 150) == 255 && doc.Selection.CoverageAt(120, 150) == 255, "Shift-click adds the square");
            await doc.MagicWandAsync(120, 150, SelectionMode.Subtract);
            check(doc.Selection!.CoverageAt(120, 150) == 0 && doc.Selection.Bounds == new PixelRect(250, 100, 350, 200), $"Option-click subtracts the disc ({doc.Selection.Bounds})");
            await doc.MagicWandAsync(5, 5, SelectionMode.Intersect);
            check(doc.Selection is null, "Shift+Option-click on transparency intersects to nothing");
            doc.Undo();
            check(doc.Selection?.Bounds == new PixelRect(250, 100, 350, 200), "undo brings the square back");

            // Contiguous off: every red pixel, the small square too.
            await doc.MagicWandAsync(120, 150, SelectionMode.Replace);
            int contiguous = Selected(doc.Selection, canvas);
            editor.WandContiguous = false;
            await doc.MagicWandAsync(120, 150, SelectionMode.Replace);
            check(Selected(doc.Selection, canvas, InSmall) == 1600 && Selected(doc.Selection, canvas) == contiguous + 1600,
                "without Contiguous every matching pixel is selected");
            editor.WandContiguous = true;
            check(Enumerable.Range(55, 20).All(x => doc.Selection!.CoverageAt(x, 150) is 0 or 255), "without Anti-alias the edge is hard");

            editor.WandTolerance = 300;
            check(editor.WandTolerance == 255, "tolerance is clamped to 255");
            editor.WandTolerance = 32;

            // Current layer vs Sample All Layers: the white background layer is one flat region; the composite is not.
            await doc.MagicWandAsync(5, 5, SelectionMode.Replace);
            check(doc.Selection!.CoverageAt(5, 5) == 255 && doc.Selection.CoverageAt(120, 150) == 0 && doc.Selection.CoverageAt(200, 50) == 255,
                "on the shapes layer, a click on transparency selects all its transparent pixels");
            doc.SelectedLayer = Item(model.Root.Children[0]);
            await doc.MagicWandAsync(120, 150, SelectionMode.Replace);
            check(doc.Selection is { IsRectangular: true } all && all.Bounds == canvas, "on the white Background layer the click selects everything");
            editor.WandSampleAllLayers = true;
            await doc.MagicWandAsync(120, 150, SelectionMode.Replace);
            check(Selected(doc.Selection, canvas) == Selected(doc.Selection, canvas, InDisc) && Selected(doc.Selection, canvas) > 11000,
                "with Sample All Layers it selects the disc seen in the composite");
            editor.WandSampleAllLayers = false;
            editor.WandAntiAlias = true;
            doc.SelectedLayer = Item(shapes);

            // Quick Selection: a drag inside the disc selects the disc, as one history step, with a live outline.
            doc.Deselect();
            editor.QuickSelectSize = 20;
            check(doc.BeginQuickSelection(100, 150, SelectionMode.Replace), "a Quick Selection drag starts");
            foreach (var (x, y) in new[] { (110, 150), (120, 150), (130, 150), (140, 150), (120, 130), (120, 170) })
            {
                doc.ContinueQuickSelection(x, y);
                await Task.Delay(16);
            }
            await Task.Delay(100);
            check(doc.QuickSelectionOutline is { Count: > 0 } && doc.Selection is null, "the outline grows live while dragging; the selection waits for release");
            int steps = CountUndo(doc);
            await doc.EndQuickSelectionAsync();
            var qs = doc.Selection;
            double iou = Iou(qs, canvas, InDisc);
            check(iou > 0.97 && doc.UndoText == "Undo Quick Selection" && CountUndo(doc) == steps + 1 && doc.QuickSelectionOutline is null,
                $"Quick Selection selects the disc (IoU {iou:F3}) as one history step");
            check(await AntsVisibleAsync(doc, "quick-selection"), "marching ants follow the Quick Selection result");

            // A second drag on the square adds to it (the default once something is selected); Option-drag subtracts.
            doc.BeginQuickSelection(280, 150, SelectionMode.Add);
            doc.ContinueQuickSelection(320, 150);
            await doc.EndQuickSelectionAsync();
            check(doc.Selection!.CoverageAt(120, 150) == 255 && doc.Selection.CoverageAt(300, 150) == 255 && doc.Selection.CoverageAt(200, 150) == 0,
                "a second drag adds the square");
            doc.BeginQuickSelection(110, 150, SelectionMode.Subtract);
            doc.ContinueQuickSelection(130, 150);
            await doc.EndQuickSelectionAsync();
            check(doc.Selection!.CoverageAt(120, 150) == 0 && Iou(doc.Selection, canvas, InSquare) > 0.97, "Option-drag subtracts the disc again");
            doc.Undo();
            doc.Undo();
            check(ReferenceEquals(doc.Selection, qs), "undo steps back one drag at a time");

            // Auto-Enhance: soft edges.
            doc.Deselect();
            editor.QuickSelectAutoEnhance = true;
            doc.BeginQuickSelection(100, 150, SelectionMode.Replace);
            doc.ContinueQuickSelection(140, 150);
            await doc.EndQuickSelectionAsync();
            check(Enumerable.Range(50, 25).Any(x => doc.Selection?.CoverageAt(x, 150) is > 0 and < 255) && Iou(doc.Selection, canvas, InDisc) > 0.97,
                "Auto-Enhance gives the edge soft coverage");
            editor.QuickSelectAutoEnhance = false;
            editor.QuickSelectSize = 30;
        }
        catch (Exception ex)
        {
            check(false, $"exception in magic wand steps: {ex}");
        }

        static double Iou(SelectionMask? s, PixelRect area, Func<int, int, bool> truth)
        {
            int both = Selected(s, area, truth), selected = Selected(s, area), expected = 0;
            for (int y = area.Top; y < area.Bottom; y++)
                for (int x = area.Left; x < area.Right; x++)
                    if (truth(x, y)) expected++;
            return (double)both / (selected + expected - both);
        }

        static int CountUndo(DocumentViewModel d)
        {
            // Number of steps in the history (undo all, count, redo all).
            int n = 0;
            while (d.CanUndo) { d.Undo(); n++; }
            for (int i = 0; i < n; i++) d.Redo();
            return n;
        }
    }

    /// <summary>
    /// STRAYTA_WANDBENCH=new: a 4000×3000 document with a noisy photo-like layer holding large discs, then the Magic
    /// Wand and Quick Selection benchmark on it (the drag brushes inside the middle disc).
    /// </summary>
    public static async Task RunSyntheticWandBenchmarkAsync(EditorViewModel editor)
    {
        const int w = 4000, h = 3000;
        var model = Editing.LayerFactory.NewDocument(w, h, whiteBackground: true);
        var planes = Enumerable.Range(0, 4).Select(_ => Plane.Create(w, h, 8)).ToArray();
        (float X, float Y, float R, int Red, int Green, int Blue)[] discs = [(2000, 1500, 800, 210, 80, 60), (700, 700, 450, 60, 170, 90), (3400, 2400, 500, 230, 200, 60)];
        Parallel.For(0, h, y =>
        {
            uint seed = (uint)y * 2654435761u;
            for (int x = 0; x < w; x++)
            {
                seed = seed * 1664525u + 1013904223u;
                int noise = (int)(seed >> 27) - 16, i = y * w + x;
                int r = 60 + x * 60 / w, g = 90 + y * 60 / h, b = 170;
                foreach (var d in discs)
                    if ((x - d.X) * (x - d.X) + (y - d.Y) * (y - d.Y) < d.R * d.R) (r, g, b) = (d.Red, d.Green, d.Blue);
                planes[0].Data[i] = (byte)Math.Clamp(r + noise, 0, 255);
                planes[1].Data[i] = (byte)Math.Clamp(g + noise, 0, 255);
                planes[2].Data[i] = (byte)Math.Clamp(b + noise, 0, 255);
                planes[3].Data[i] = 255;
            }
        });
        model.Root.Add(new PixelLayer { Name = "Photo", Bounds = model.Bounds, Pixels = new Raster(ColorMode.Rgb, planes[..3], planes[3]) });
        var doc = new DocumentViewModel(model, null, editor);
        editor.Factory.AddDocument(doc);
        editor.ActiveDocument = doc;
        await doc.RenderAsync();
        await Task.Delay(1500); // let the view fit the image
        await doc.RunWandBenchmarkAsync(new Vector2(1600, 1400), new Vector2(2400, 1600));
    }
}
