using Vector2 = System.Numerics.Vector2;
using Strayta.Core;
using Strayta.Core.Selection;
using Strayta.Editor.Controls;
using Strayta.Editor.ViewModels;
using Strayta.Segmentation;

namespace Strayta.Editor;

internal static partial class SelfTest
{
    /// <summary>
    /// Object Selection and Select › Subject on a synthetic document (a bright disc and a square on a dark
    /// background). Without the models, checks that the tool explains how to get them instead.
    /// </summary>
    private static async Task RunObjectSelectionStepsAsync(EditorViewModel editor, Action<bool, string> check)
    {
        const int w = 1200, h = 900;
        const float cx = 450, cy = 420, r = 220;
        var square = new PixelRect(850, 250, 1050, 450);
        var model = Editing.LayerFactory.NewDocument(w, h, whiteBackground: true);
        var backdrop = ObjectTestLayer("Backdrop", PixelRect.FromSize(w, h), (x, y) =>
            (x - cx + 0.5f) * (x - cx + 0.5f) + (y - cy + 0.5f) * (y - cy + 0.5f) <= r * r ? ((byte)235, (byte)200, (byte)60, (byte)255) : ((byte)30, (byte)34, (byte)40, (byte)255));
        var box = ObjectTestLayer("Box", square, (_, _) => ((byte)60, (byte)150, (byte)230, (byte)255));
        model.Root.Add(backdrop);
        model.Root.Add(box);
        var doc = new DocumentViewModel(model, null, editor);
        editor.Factory.AddDocument(doc);
        editor.ActiveDocument = doc;
        await doc.RenderAsync();
        doc.SelectedLayer = doc.Layers.First(l => l.Node == backdrop);
        editor.Tool = CanvasTool.ObjectSelect;
        check(editor.ToolName == "Object Selection" && editor.IsObjectSelectTool, "Object Selection is a canvas tool");

        var discBox = new PixelRect((int)(cx - r - 40), (int)(cy - r - 40), (int)(cx + r + 40), (int)(cy + r + 40));
        if (!SegmentationEngine.Shared.CanSelectObjects || !SegmentationEngine.Shared.CanSelectSubject)
        {
            await doc.ApplySelectionGestureAsync(new SelectionGesture(CanvasTool.ObjectSelect, SelectionMode.Replace, discBox, []));
            check(doc.Selection is null && doc.Notice == SegmentationModels.FetchHint && !editor.HasSelectionModels,
                "without the models, Object Selection explains how to fetch them");
            Console.WriteLine("SELFTEST skip Object Selection / Select Subject model steps: models not installed (dotnet build tools/FetchModels.proj)");
            editor.Tool = CanvasTool.Move;
            return;
        }

        double DiscIou(SelectionMask? m) => ObjectIou(m, w, h, (x, y) => (x - cx + 0.5f) * (x - cx + 0.5f) + (y - cy + 0.5f) * (y - cy + 0.5f) <= r * r);
        bool InSquare(int x, int y) => x >= square.Left && x < square.Right && y >= square.Top && y < square.Bottom;

        // Box around the disc; the first prompt waits for the image to be analyzed, later ones are instant.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await doc.ApplySelectionGestureAsync(new SelectionGesture(CanvasTool.ObjectSelect, SelectionMode.Replace, discBox, []));
        long first = sw.ElapsedMilliseconds;
        double iou = DiscIou(doc.Selection);
        check(iou > 0.9 && doc.UndoText == "Undo Object Selection" && !doc.IsAnalyzing,
            $"a box around the disc selects it (IoU {iou:F3}; first prompt {first} ms including model load and analysis)");
        check(await OutlineTracedAsync(doc), "the object's outline is traced for the marching ants");

        // Shift-click adds the square, Option-click on the disc subtracts it; each is instant now.
        await doc.ApplySelectionGestureAsync(new SelectionGesture(CanvasTool.ObjectSelect, SelectionMode.Add, PixelRect.Empty, [new Vector2(950, 350)]));
        double add = doc.LastObjectSelectionMs;
        check(doc.Selection is { } both && both.CoverageAt(950, 350) == 255 && both.CoverageAt((int)cx, (int)cy) == 255 && both.CoverageAt(20, 20) == 0,
            $"Shift-click adds the square ({add:F0} ms from click to selection)");
        await doc.ApplySelectionGestureAsync(new SelectionGesture(CanvasTool.ObjectSelect, SelectionMode.Subtract, PixelRect.Empty, [new Vector2(cx + 30, cy - 20)]));
        double squareIou = ObjectIou(doc.Selection, w, h, InSquare);
        check(doc.Selection is { } left && left.CoverageAt((int)cx, (int)cy) == 0 && squareIou > 0.9,
            $"Option-click subtracts the disc (square IoU {squareIou:F3}, {doc.LastObjectSelectionMs:F0} ms)");
        doc.Undo();
        doc.Undo();
        check(DiscIou(doc.Selection) > 0.9, "undo steps back through the object selections");

        // Current layer only: the square layer is sampled on its own (a clean edge, no disc).
        editor.ObjectSampleAllLayers = false;
        doc.SelectedLayer = doc.Layers.First(l => l.Node == box);
        await doc.ApplySelectionGestureAsync(new SelectionGesture(CanvasTool.ObjectSelect, SelectionMode.Replace, PixelRect.Empty, [new Vector2(950, 350)]));
        double layerIou = ObjectIou(doc.Selection, w, h, InSquare);
        check(layerIou > 0.9, $"with Sample All Layers off, a click samples the selected layer (IoU {layerIou:F3})");
        editor.ObjectSampleAllLayers = true;
        doc.SelectedLayer = doc.Layers.First(l => l.Node == backdrop);

        // Select > Subject: the bright shapes, not the dark background.
        sw.Restart();
        await doc.SelectSubjectAsync();
        long subjectMs = sw.ElapsedMilliseconds;
        var subject = doc.Selection;
        check(subject is not null && subject.CoverageAt((int)cx, (int)cy) == 255 && subject.CoverageAt(10, h - 10) == 0 && subject.CoverageAt(w - 10, 10) == 0
              && doc.UndoText == "Undo Select Subject", $"Select Subject selects the disc (IoU with the disc {DiscIou(subject):F3}, {subjectMs} ms)");
        doc.Undo();
        editor.Tool = CanvasTool.Move;
    }

    /// <summary>
    /// Waits for the document's canvas to finish tracing the current selection. (The pixel check in
    /// <c>AntsVisibleAsync</c> looks exactly at the bounds' left column, which an anti-aliased curve may only
    /// graze.)
    /// </summary>
    private static async Task<bool> OutlineTracedAsync(DocumentViewModel doc)
    {
        for (int i = 0; i < 100; i++)
        {
            var canvas = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime app
                ? app.Windows.SelectMany(w => Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(w)).OfType<ImageCanvas>().FirstOrDefault(c => ReferenceEquals(c.DataContext, doc))
                : null;
            if (canvas is { OutlineReady: true } && doc.Selection is not null) return true;
            await Task.Delay(20);
        }
        return false;
    }

    private static PixelLayer ObjectTestLayer(string name, PixelRect bounds, Func<int, int, (byte R, byte G, byte B, byte A)> color)
    {
        int lw = bounds.Width, lh = bounds.Height;
        var planes = Enumerable.Range(0, 4).Select(_ => Plane.Create(lw, lh, 8)).ToArray();
        for (int y = 0; y < lh; y++)
            for (int x = 0; x < lw; x++)
            {
                var c = color(bounds.Left + x, bounds.Top + y);
                int i = y * lw + x;
                (planes[0].Data[i], planes[1].Data[i], planes[2].Data[i], planes[3].Data[i]) = (c.R, c.G, c.B, c.A);
            }
        return new PixelLayer { Name = name, Bounds = bounds, Pixels = new Raster(ColorMode.Rgb, planes[..3], planes[3]) };
    }

    private static double ObjectIou(SelectionMask? mask, int w, int h, Func<int, int, bool> reference)
    {
        long both = 0, either = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                bool a = mask is not null && mask.CoverageAt(x, y) >= 128, b = reference(x, y);
                if (a && b) both++;
                if (a || b) either++;
            }
        return either == 0 ? 1 : (double)both / either;
    }
}
