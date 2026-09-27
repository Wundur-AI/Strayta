using Strayta.Core;
using Strayta.Core.Painting;
using Strayta.Editor.Editing;
using Strayta.Editor.ViewModels;
using Strayta.Psd;

namespace Strayta.Editor;

/// <summary>
/// Drives the real editor view models through a full editing session and reports each step
/// (STRAYTA_SELFTEST=1). It exercises the same code paths as the UI, with a real dispatcher.
/// </summary>
internal static partial class SelfTest
{
    public static async Task RunAsync(EditorViewModel editor, Func<Task<(int, int, bool)?>> _)
    {
        var failures = new List<string>();
        void Check(bool ok, string what)
        {
            Console.WriteLine($"SELFTEST {(ok ? "ok  " : "FAIL")} {what}");
            if (!ok) failures.Add(what);
        }

        try
        {
            // New document with a white background.
            var model = Editing.LayerFactory.NewDocument(400, 300, whiteBackground: true);
            var doc = new DocumentViewModel(model, null, editor);
            editor.Factory.AddDocument(doc);
            editor.ActiveDocument = doc;
            await doc.RenderAsync();
            Check(model.Root.Children.Count == 1 && model.Root.Children[0].Name == "Background", "new document has a white Background layer");

            // New layer above it, selected.
            doc.SelectedLayer = doc.Layers[0];
            doc.NewLayer();
            var layer = doc.SelectedLayer?.Node as PixelLayer;
            Check(layer is { Name: "Layer 1", Pixels: null } && model.Root.Children.Count == 2 && model.Root.Children[1] == layer, "New Layer adds an empty layer on top and selects it");

            // Paint a red stroke.
            var brush = new BrushSettings(20, 1f, 1f);
            Check(doc.BeginStroke(50, 150, brush, new RgbColor(1, 0, 0), erase: false), "brush stroke starts on the new layer");
            for (int x = 55; x <= 350; x += 5) doc.ContinueStroke(x, 150);
            await doc.EndStrokeAsync();
            Check(layer!.Pixels is not null && layer.Bounds.Width > 300, $"stroke is committed into layer pixels ({layer.Bounds})");
            int at = (150 - layer.Bounds.Top) * layer.Bounds.Width + (200 - layer.Bounds.Left);
            Check(layer.Pixels!.ColorPlanes[0].Data[at] == 255 && layer.Pixels.ColorPlanes[1].Data[at] == 0 && layer.Pixels.Alpha!.Data[at] == 255,
                "painted pixel is opaque red");

            // Erase part of it.
            doc.BeginStroke(200, 150, new BrushSettings(30, 1f, 1f), default, erase: true);
            await doc.EndStrokeAsync();
            int at2 = (150 - layer.Bounds.Top) * layer.Bounds.Width + (200 - layer.Bounds.Left);
            Check(layer.Pixels!.Alpha!.Data[at2] == 0, "eraser clears alpha");

            // Undo both, redo one.
            doc.Undo();
            Check(layer.Pixels!.Alpha!.Data[at2] == 255, "undo restores erased pixels");
            doc.Undo();
            Check(layer.Pixels is null, "undo removes the stroke");
            doc.Redo();
            Check(layer.Pixels is not null, "redo brings the stroke back");

            // Painting is refused on groups, with a reason.
            doc.NewGroup();
            Check(!doc.BeginStroke(10, 10, brush, new RgbColor(0, 0, 1), false) && doc.Notice.Contains("Group"), "painting on a group is refused with a notice");

            // Duplicate the painted layer, then drag it into the group.
            doc.SelectedLayer = doc.Layers.SelectMany(l => l.SelfAndDescendants()).First(i => i.Node == layer);
            doc.DuplicateSelected();
            var copy = doc.SelectedLayer!.Node;
            Check(copy.Name == "Layer 1 copy" && ((PixelLayer)copy).Pixels == layer.Pixels, "duplicate shares pixels and gets a copy name");
            var group = model.Root.Children.OfType<LayerGroup>().Single();
            doc.MoveLayer(copy, group, 0);
            Check(copy.Parent == group, "drag-and-drop moves a layer into a group");
            doc.MoveLayer(copy, model.Root, 0);
            Check(copy.Parent == model.Root && model.Root.Children[0] == copy, "drag-and-drop moves it to the bottom of the document");
            doc.MoveLayer(group, group, 0);
            Check(group.Parent == model.Root, "a group cannot be dropped into itself");

            // Switching the selection between groups and layers (with the live Layers panel bound to it) must not
            // change anyone's blend mode, opacity or fill.
            group.BlendMode = BlendMode.PassThrough;
            layer.BlendMode = BlendMode.Multiply;
            layer.Opacity = 0.6f;
            var before = model.Root.Descendants().Select(n => (n.Name, n.BlendMode, n.Opacity, n.FillOpacity)).ToList();
            for (int round = 0; round < 20; round++)
                foreach (var item in doc.Layers.SelectMany(l => l.SelfAndDescendants()).ToList())
                {
                    doc.SelectedLayer = item;
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
                }
            var after = model.Root.Descendants().Select(n => (n.Name, n.BlendMode, n.Opacity, n.FillOpacity)).ToList();
            Check(before.SequenceEqual(after), "changing the selection never changes blend modes, opacity or fill" +
                (before.SequenceEqual(after) ? "" : $" ({string.Join("; ", before.Zip(after).Where(p => p.First != p.Second).Select(p => $"{p.First} -> {p.Second}"))})"));
            Check(!doc.UndoText.Contains("Blend") && !doc.UndoText.Contains("Opacity"), $"no edits were recorded by selecting (last: {doc.UndoText})");

            // Hiding a group hides (and dims) what is inside it; expanding and collapsing changes the rows.
            var groupItem = doc.Layers.SelectMany(l => l.SelfAndDescendants()).First(i => i.Node == group);
            doc.MoveLayer(copy, group, 0);
            var copyItem = doc.Layers.SelectMany(l => l.SelfAndDescendants()).First(i => i.Node == copy);
            groupItem = doc.Layers.SelectMany(l => l.SelfAndDescendants()).First(i => i.Node == group);
            groupItem.IsExpanded = true;
            int expandedRows = doc.Rows.Count;
            Check(doc.Rows.Contains(copyItem), "expanding a group shows its layers");
            groupItem.IsExpanded = false;
            Check(doc.Rows.Count == expandedRows - 1 && !doc.Rows.Contains(copyItem), "collapsing a group hides its rows");
            groupItem.IsVisible = false;
            Check(!group.Visible && copyItem.AncestorHidden && copyItem.EyeOpacity is > 0 and < 1,
                "hiding a group greys the eyes of the layers inside it");
            groupItem.IsVisible = true;
            Check(!copyItem.AncestorHidden && copyItem.EyeOpacity == 1, "showing the group again restores them");

            // Painting on a fill layer is refused with fixes offered; rasterizing makes it paintable, and undo reverts.
            var fill = (PixelLayer)Editing.LayerFactory.Duplicate(model.Root.Children.OfType<PixelLayer>().First(l => l.Name == "Background"));
            fill.Name = "Fill";
            fill.Tags.Add("fill");
            doc.Apply(new Editing.InsertEdit(fill, model.Root, model.Root.Children.Count, "test"));
            doc.SelectedLayer = doc.Layers.SelectMany(l => l.SelfAndDescendants()).First(i => i.Node == fill);
            Check(!doc.BeginStroke(20, 20, brush, new RgbColor(0, 1, 0), false) && doc.PaintBlock is { CanRasterize: true, CanNewLayer: true },
                "painting a fill layer shows Rasterize and New Layer");
            doc.RasterizeSelected();
            Check(!fill.Tags.Contains("fill") && doc.PaintBlock is null && doc.BeginStroke(20, 20, brush, new RgbColor(0, 1, 0), false),
                "after Rasterize the layer can be painted");
            await doc.EndStrokeAsync();
            doc.Undo();
            doc.Undo();
            Check(fill.Tags.Contains("fill"), "undo brings back the fill layer");
            doc.Undo(); // remove the test layer again

            // Save and reopen.
            string path = Path.Combine(Path.GetTempPath(), $"strayta-selftest-{Guid.NewGuid():N}.psd");
            await doc.SaveAsync(path);
            var reopened = PsdFile.OpenForEditing(path);
            var names = reopened.Root.Descendants().Select(n => n.Name).ToList();
            Check(names.SequenceEqual(model.Root.Descendants().Select(n => n.Name)), $"saved file has the same layers ({string.Join(", ", names)})");
            var savedLayer = reopened.Root.Descendants().OfType<PixelLayer>().First(l => l.Name == "Layer 1");
            Check(savedLayer.Pixels!.ColorPlanes[0].Data.SequenceEqual(layer.Pixels!.ColorPlanes[0].Data), "saved painted pixels match");
            Check(!doc.IsModified && doc.Title == Path.GetFileName(path), "document is clean after saving");
            File.Delete(path);

            await FreeTransformSteps(doc, editor, layer, group, Check);
            await ExportSteps(doc, Check);
            await RunSelectionStepsAsync(editor, Check);
        }
        catch (Exception ex)
        {
            Check(false, $"exception: {ex}");
        }

        await RunMaskAndAdjustmentStepsAsync(editor, Check); // SelfTest.Masks.cs
        await RunImageStepsAsync(editor, Check); // SelfTest.Images.cs

        Console.WriteLine(failures.Count == 0 ? "SELFTEST PASSED" : $"SELFTEST FAILED ({failures.Count})");
    }

    // ---- Free Transform and Export -----------------------------------------------------------------

    private static async Task FreeTransformSteps(DocumentViewModel doc, EditorViewModel editor, PixelLayer layer, LayerGroup group, Action<bool, string> check)
    {
        LayerItemViewModel Item(LayerNode n) => doc.Layers.SelectMany(l => l.SelfAndDescendants()).First(i => i.Node == n);

        doc.SelectedLayer = Item(layer);
        var (pixels, bounds) = (layer.Pixels, layer.Bounds);
        check(doc.BeginFreeTransform() && editor.IsTransforming, "Free Transform opens on the painted layer");
        var ft = doc.FreeTransform!;
        var corner = ft.HandlePosition(TransformHandle.BottomRight);
        check(ft.HitTest(corner.X + 1, corner.Y - 1, 4) == TransformHandle.BottomRight && ft.HitTest(ft.Center.X, ft.Center.Y, 4) == TransformHandle.Move
              && ft.HitTest(corner.X + 60, corner.Y + 60, 4) == TransformHandle.Rotate, "handles, inside and outside hit-test as in Photoshop");
        ft.BeginDrag(TransformHandle.BottomRight, corner.X, corner.Y);
        ft.DragTo(corner.X + 60, corner.Y + 10, shift: false, alt: false);
        ft.EndDrag();
        check(ft.WidthPercent > 110 && Math.Abs(ft.WidthPercent - ft.HeightPercent) < 1e-6
              && Math.Abs(ft.Corners[0].X - ft.Original.Left) < 1e-6 && Math.Abs(ft.Corners[0].Y - ft.Original.Top) < 1e-6, $"corner drag scales proportionally from the opposite corner (W {ft.WidthPercent:F1}%)");
        ft.Angle = 30;
        await Task.Delay(300); // let the preview lane draw the transformed layer
        check(ReferenceEquals(layer.Pixels, pixels) && layer.Bounds == bounds, "the document is untouched while transforming");
        await doc.CommitTransformAsync();
        check(!doc.IsTransforming && !ReferenceEquals(layer.Pixels, pixels) && layer.Bounds.Height > bounds.Height * 3 && doc.UndoText == "Undo Free Transform",
            $"commit scales and rotates the pixels as one edit ({bounds} -> {layer.Bounds})");
        int centerAt = (layer.Bounds.Height / 2) * layer.Bounds.Width + layer.Bounds.Width / 2;
        check(layer.Pixels!.Alpha!.Data[centerAt] == 255 && layer.Pixels.ColorPlanes[0].Data[centerAt] == 255, "the stroke is still opaque red after resampling");
        doc.Undo();
        check(ReferenceEquals(layer.Pixels, pixels) && layer.Bounds == bounds, "undo restores the original pixels and bounds");

        check(doc.BeginFreeTransform(), "Free Transform reopens");
        doc.FreeTransform!.WidthPercent = 50;
        doc.Undo(); // inside a transform, undo backs out of it
        check(!doc.IsTransforming && ReferenceEquals(layer.Pixels, pixels) && doc.CanRedo, "undo during a transform cancels it without touching history");

        // Whole groups move with every layer inside; a pure move keeps pixels exactly (no resampling).
        doc.SelectedLayer = Item(group);
        var inside = group.Descendants().OfType<PixelLayer>().First(p => p.Pixels is not null);
        var (insidePixels, insideBounds) = (inside.Pixels, inside.Bounds);
        check(doc.BeginFreeTransform(), "Free Transform opens on a group");
        ft = doc.FreeTransform!;
        ft.BeginDrag(TransformHandle.Move, ft.Center.X, ft.Center.Y);
        ft.DragTo(ft.Center.X + 12.4, ft.Center.Y - 5, shift: false, alt: false);
        ft.EndDrag();
        await doc.CommitTransformAsync();
        check(ReferenceEquals(inside.Pixels, insidePixels) && inside.Bounds == insideBounds with
        {
            Left = insideBounds.Left + 12, Right = insideBounds.Right + 12, Top = insideBounds.Top - 5, Bottom = insideBounds.Bottom - 5,
        }, $"moving a group moves its layers by whole pixels ({insideBounds} -> {inside.Bounds})");
        doc.Undo();

        doc.NewLayer();
        check(!doc.BeginFreeTransform() && doc.Notice.Contains("no pixels"), "an empty layer is refused with a notice");
        doc.Undo();
    }

    private static async Task ExportSteps(DocumentViewModel doc, Action<bool, string> check)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"strayta-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string png = Path.Combine(dir, "export.png"), jpg = Path.Combine(dir, "export.jpg");
            await doc.ExportAsync(png, new Rendering.Export.ExportOptions { Format = Rendering.Export.ExportFormat.Png });
            await doc.ExportAsync(jpg, new Rendering.Export.ExportOptions { Format = Rendering.Export.ExportFormat.Jpeg, Quality = 85 });
            using var a = new Avalonia.Media.Imaging.Bitmap(png);
            using var b = new Avalonia.Media.Imaging.Bitmap(jpg);
            var size = new Avalonia.PixelSize(doc.Model.Width, doc.Model.Height);
            check(a.PixelSize == size, $"exported PNG decodes at the document size ({a.PixelSize}, {new FileInfo(png).Length / 1024} KB)");
            check(b.PixelSize == size, $"exported JPEG decodes at the document size ({b.PixelSize}, {new FileInfo(jpg).Length / 1024} KB)");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// STRAYTA_TRANSFORMBENCH=new: builds a 4000×3000 document with a large photo-like layer and runs the drag and
    /// Free Transform benchmarks on it, for machines without a large PSD at hand.
    /// </summary>
    public static async Task RunSyntheticBenchmarksAsync(EditorViewModel editor)
    {
        const int w = 4000, h = 3000;
        var model = Editing.LayerFactory.NewDocument(w, h, whiteBackground: true);
        int lw = 3200, lh = 2400;
        var planes = Enumerable.Range(0, 4).Select(_ => Plane.Create(lw, lh, 8)).ToArray();
        Parallel.For(0, lh, y =>
        {
            uint seed = (uint)y * 2654435761u;
            for (int x = 0; x < lw; x++)
            {
                seed = seed * 1664525u + 1013904223u;
                int noise = (int)(seed >> 28) - 8, i = y * lw + x;
                planes[0].Data[i] = (byte)Math.Clamp(x * 255 / lw + noise, 0, 255);
                planes[1].Data[i] = (byte)Math.Clamp(y * 255 / lh + noise, 0, 255);
                planes[2].Data[i] = (byte)Math.Clamp(128 + 100 * Math.Sin(x * 0.01) * Math.Cos(y * 0.013) + noise, 0, 255);
                planes[3].Data[i] = 255;
            }
        });
        model.Root.Add(new PixelLayer { Name = "Photo", Bounds = new PixelRect(400, 300, 400 + lw, 300 + lh), Pixels = new Raster(ColorMode.Rgb, planes[..3], planes[3]) });
        var doc = new DocumentViewModel(model, null, editor);
        editor.Factory.AddDocument(doc);
        editor.ActiveDocument = doc;
        await doc.RenderAsync();
        await Task.Delay(1500); // let the view fit the image and the preview caches warm up
        await doc.RunDragBenchmarkAsync();
        await Task.Delay(500);
        await doc.RunTransformBenchmarkAsync();
    }
}
