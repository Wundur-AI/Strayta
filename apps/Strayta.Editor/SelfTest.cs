using Strayta.Core;
using Strayta.Core.Painting;
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
        }
        catch (Exception ex)
        {
            Check(false, $"exception: {ex}");
        }

        await RunMaskAndAdjustmentStepsAsync(editor, Check); // SelfTest.Masks.cs

        Console.WriteLine(failures.Count == 0 ? "SELFTEST PASSED" : $"SELFTEST FAILED ({failures.Count})");
    }
}
