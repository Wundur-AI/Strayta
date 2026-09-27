using System.Numerics;
using Strayta.Core;
using Strayta.Core.Painting;
using Strayta.Core.Selection;
using Strayta.Editor.Controls;
using Strayta.Editor.ViewModels;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Strayta.Psd;

namespace Strayta.Editor;

internal static partial class SelfTest
{
    /// <summary>
    /// Renders the document's canvas offscreen and checks that the outline's pixels are pure black or white where the
    /// selection's left edge is, and untouched a few pixels inside.
    /// </summary>
    private static async Task<bool> AntsVisibleAsync(DocumentViewModel doc, string shotName)
    {
        ImageCanvas? canvas = null;
        for (int i = 0; i < 50 && canvas is not { OutlineReady: true }; i++)
        {
            await Task.Delay(20);
            canvas = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime app
                ? app.Windows.SelectMany(w => w.GetVisualDescendants()).OfType<ImageCanvas>().FirstOrDefault(c => ReferenceEquals(c.DataContext, doc))
                : null;
        }
        if (canvas is not { OutlineReady: true } || doc.Selection is not { } selection) return false;

        var size = new Avalonia.PixelSize((int)canvas.Bounds.Width, (int)canvas.Bounds.Height);
        using var bitmap = new RenderTargetBitmap(size);
        bitmap.Render(canvas);
        var pixels = new byte[size.Width * size.Height * 4];
        unsafe
        {
            fixed (byte* p = pixels) bitmap.CopyPixels(new Avalonia.PixelRect(size), (nint)p, pixels.Length, size.Width * 4);
        }
        (byte, byte, byte) At(Avalonia.Point pt)
        {
            int i = ((int)pt.Y * size.Width + (int)pt.X) * 4;
            return (pixels[i], pixels[i + 1], pixels[i + 2]);
        }
        var b = selection.Bounds;
        int y = (b.Top + b.Bottom) / 2;
        var start = canvas.ImageToControl(b.Left, y);
        var edge = Enumerable.Range(0, 16).Select(k => At(start + new Avalonia.Point(0, k))).ToList();
        if (Environment.GetEnvironmentVariable("STRAYTA_SELFTEST_SHOTS") is { Length: > 0 } dir) bitmap.Save(Path.Combine(dir, shotName + ".png"), new PngBitmapEncoderOptions());
        return edge.Contains((0, 0, 0)) && edge.Contains((255, 255, 255)) && At(canvas.ImageToControl(b.Left + 5, y)) is not (0, 0, 0);
    }

    /// <summary>Marquee, lasso, selection-clipped painting, clear, fill, copy/paste and their undo, in a fresh document.</summary>
    private static async Task RunSelectionStepsAsync(EditorViewModel editor, Action<bool, string> check)
    {
        var model = Editing.LayerFactory.NewDocument(400, 300, whiteBackground: true);
        var doc = new DocumentViewModel(model, null, editor);
        editor.Factory.AddDocument(doc);
        editor.ActiveDocument = doc;
        await doc.RenderAsync();
        doc.SelectedLayer = doc.Layers[0];
        doc.NewLayer();
        var layer = (PixelLayer)doc.SelectedLayer!.Node;

        byte Alpha(PixelLayer l, int x, int y)
        {
            var b = l.Bounds;
            if (l.Pixels is null || x < b.Left || x >= b.Right || y < b.Top || y >= b.Bottom) return 0;
            return l.Pixels.Alpha?.Data[(y - b.Top) * b.Width + (x - b.Left)] ?? 255;
        }

        // Rectangular marquee drag.
        await doc.ApplySelectionGestureAsync(new SelectionGesture(CanvasTool.RectSelect, SelectionMode.Replace, new PixelRect(100, 100, 200, 200), []));
        check(doc.Selection is { IsRectangular: true } s && s.Bounds == new PixelRect(100, 100, 200, 200) && doc.UndoText == "Undo Rectangular Marquee",
            "rectangular marquee selects and is a history step");

        check(await AntsVisibleAsync(doc, "ants-rect"), "marching ants are drawn along the selection edge");

        // Paint across it: only the selected part changes.
        doc.BeginStroke(40, 150, new BrushSettings(30, 1f, 1f), new RgbColor(1, 0, 0), erase: false);
        for (int x = 45; x <= 360; x += 5) doc.ContinueStroke(x, 150);
        await doc.EndStrokeAsync();
        check(Alpha(layer, 150, 150) == 255 && Alpha(layer, 60, 150) == 0 && Alpha(layer, 300, 150) == 0 &&
              layer.Bounds.Left >= 100 && layer.Bounds.Right <= 200, $"brush stroke is clipped to the selection ({layer.Bounds})");

        // Delete clears the selected area; undo restores it.
        await doc.ClearAsync();
        check(Alpha(layer, 150, 150) == 0 && doc.UndoText == "Undo Clear", "Delete clears the selected area");
        doc.Undo();
        check(Alpha(layer, 150, 150) == 255, "undo restores cleared pixels");

        // Copy and paste into a new layer above; paste deselects and undo brings the selection back.
        check(doc.Copy() && editor.Clipboard is not null, "copy takes the selected pixels");
        var selection = doc.Selection;
        doc.Paste();
        var pasted = doc.SelectedLayer?.Node as PixelLayer;
        check(pasted is not null && pasted != layer && pasted.Parent == model.Root && model.Root.IndexOf(pasted) == model.Root.IndexOf(layer) + 1 &&
              Alpha(pasted, 150, 150) == 255 && pasted.Bounds.Left >= 100 && doc.Selection is null,
            $"paste creates a layer above with the copied pixels in place ({pasted?.Bounds})");
        doc.Undo();
        check(pasted!.Parent is null && ReferenceEquals(doc.Selection, selection), "undo paste removes the layer and restores the selection");
        doc.SelectedLayer = doc.Layers.SelectMany(l => l.SelfAndDescendants()).First(i => i.Node == layer);

        // Cut: copies and clears.
        await doc.CutAsync();
        check(Alpha(layer, 150, 150) == 0 && editor.Clipboard!.Bounds.Left >= 100, "cut copies and clears the selection");
        doc.Undo();

        // Layer via Copy (⌘J) and Layer via Cut (⇧⌘J): the selection onto a new layer above, in place, deselected.
        selection = doc.Selection;
        layer.BlendMode = BlendMode.Multiply;
        await editor.LayerViaCommand.ExecuteAsync("copy");
        var viaCopy = doc.SelectedLayer?.Node as PixelLayer;
        check(viaCopy is not null && model.Root.IndexOf(viaCopy) == model.Root.IndexOf(layer) + 1 && Alpha(viaCopy, 150, 150) == 255
              && viaCopy.Bounds.Left >= 100 && viaCopy.Bounds.Right <= 200 && Alpha(layer, 150, 150) == 255
              && viaCopy.BlendMode == BlendMode.Multiply && doc.Selection is null && doc.UndoText == "Undo Layer via Copy",
            $"Layer via Copy puts the selected pixels on a new layer above, keeps the source, deselects ({viaCopy?.Bounds})");
        doc.Undo();
        check(viaCopy!.Parent is null && ReferenceEquals(doc.Selection, selection), "one undo removes it and restores the selection");
        doc.SelectedLayer = doc.Layers.SelectMany(l => l.SelfAndDescendants()).First(i => i.Node == layer);
        await editor.LayerViaCommand.ExecuteAsync("cut");
        var viaCut = doc.SelectedLayer?.Node as PixelLayer;
        check(viaCut is not null && Alpha(viaCut, 150, 150) == 255 && Alpha(layer, 150, 150) == 0 && doc.UndoText == "Undo Layer via Cut",
            "Layer via Cut moves the selected pixels to a new layer");
        doc.Undo();
        check(viaCut!.Parent is null && Alpha(layer, 150, 150) == 255 && ReferenceEquals(doc.Selection, selection),
            "one undo puts the cut pixels back");
        doc.SelectedLayer = doc.Layers.SelectMany(l => l.SelfAndDescendants()).First(i => i.Node == layer);
        doc.SetSelection(null, "Deselect");
        int layerCount = model.Root.Children.Count;
        await editor.LayerViaCommand.ExecuteAsync("copy");
        check(model.Root.Children.Count == layerCount + 1 && doc.SelectedLayer?.Node is PixelLayer { Name: var dupName } && dupName.EndsWith(" copy"),
            "with nothing selected, ⌘J duplicates the layer");
        doc.Undo();
        doc.Undo();
        doc.SelectedLayer = doc.Layers.SelectMany(l => l.SelfAndDescendants()).First(i => i.Node == layer);
        layer.BlendMode = BlendMode.Normal;
        check(ReferenceEquals(doc.Selection, selection), "undo restores the selection for the next steps");

        // Invert, then fill with the foreground color: only outside the old rectangle.
        doc.InvertSelection();
        check(doc.Selection is { } inv && inv.CoverageAt(150, 150) == 0 && inv.CoverageAt(10, 10) == 255 && doc.UndoText == "Undo Select Inverse",
            "Select Inverse selects everything else");
        editor.ForegroundColor = Avalonia.Media.Colors.Blue;
        await doc.FillAsync();
        var fill = layer.Pixels!;
        int At(int x, int y) => (y - layer.Bounds.Top) * layer.Bounds.Width + (x - layer.Bounds.Left);
        check(layer.Bounds == model.Bounds && fill.ColorPlanes[2].Data[At(10, 10)] == 255 && fill.ColorPlanes[0].Data[At(150, 150)] == 255,
            "fill colors the selection and leaves the rest");
        doc.Undo();
        doc.Undo();
        check(doc.Selection?.Bounds == new PixelRect(100, 100, 200, 200) && Alpha(layer, 10, 10) == 0, "undo reverts the fill and the inverse");

        // Modifiers: subtract an ellipse, add a lasso, intersect a rectangle.
        await doc.ApplySelectionGestureAsync(new SelectionGesture(CanvasTool.EllipseSelect, SelectionMode.Subtract, new PixelRect(120, 120, 180, 180), []));
        check(doc.Selection!.CoverageAt(150, 150) == 0 && doc.Selection.CoverageAt(101, 101) == 255, "Option-drag subtracts an ellipse");
        await doc.ApplySelectionGestureAsync(new SelectionGesture(CanvasTool.Lasso, SelectionMode.Add, PixelRect.Empty,
            [new Vector2(250, 50), new Vector2(350, 50), new Vector2(300, 120)]));
        check(doc.Selection!.CoverageAt(300, 70) == 255 && doc.Selection.Bounds.Right >= 340 && doc.UndoText == "Undo Lasso", "Shift-lasso adds an area");
        await doc.ApplySelectionGestureAsync(new SelectionGesture(CanvasTool.RectSelect, SelectionMode.Intersect, new PixelRect(0, 0, 150, 300), []));
        check(doc.Selection!.Bounds.Right <= 150 && doc.Selection.CoverageAt(101, 101) == 255, "Shift+Option intersects");
        check(await AntsVisibleAsync(doc, "ants-combined"), "the combined selection's outline is drawn");

        // Erasing is clipped too.
        doc.BeginStroke(110, 110, new BrushSettings(40, 1f, 1f), default, erase: true);
        doc.ContinueStroke(110, 190);
        await doc.EndStrokeAsync();
        check(Alpha(layer, 108, 150) == 0 && Alpha(layer, 125, 150) == 255, "the eraser is clipped to the selection (the ellipse hole keeps its pixels)");
        doc.Undo();

        // A click deselects; undo restores; Select All / Deselect / Reselect.
        var before = doc.Selection;
        await doc.ApplySelectionGestureAsync(new SelectionGesture(CanvasTool.RectSelect, SelectionMode.Replace, PixelRect.Empty, []));
        check(doc.Selection is null && doc.UndoText == "Undo Deselect", "clicking without dragging deselects");
        doc.Reselect();
        check(ReferenceEquals(doc.Selection, before), "Reselect brings it back");
        doc.SelectAll();
        check(doc.Selection is { IsRectangular: true } all && all.Bounds == model.Bounds, "Select All selects the canvas");
        doc.Undo();
        doc.Undo();
        check(doc.Selection is null, "selection changes undo in order");

        // Saving with an active selection works and does not store it.
        doc.SelectAll();
        string path = Path.Combine(Path.GetTempPath(), $"strayta-selftest-sel-{Guid.NewGuid():N}.psd");
        await doc.SaveAsync(path);
        var reopened = PsdFile.OpenForEditing(path);
        check(reopened.Root.Children.Count == model.Root.Children.Count, "saving with an active selection works");
        File.Delete(path);
    }
}
