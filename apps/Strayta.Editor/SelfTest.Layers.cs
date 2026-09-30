using Strayta.Core;
using Strayta.Editor.Controls;
using Strayta.Editor.Editing;
using Strayta.Editor.ViewModels;
using Strayta.Psd;
using Strayta.Rendering;

namespace Strayta.Editor;

internal static partial class SelfTest
{
    private static PixelLayer SolidLayer(string name, PixelRect bounds, byte r, byte g, byte b, byte a = 255)
    {
        int n = bounds.Width * bounds.Height;
        Plane P(byte v) => new(bounds.Width, bounds.Height, 8, Enumerable.Repeat(v, n).ToArray());
        return new PixelLayer { Name = name, Bounds = bounds, Pixels = new Raster(ColorMode.Rgb, [P(r), P(g), P(b)], P(a)) };
    }

    private static byte[] FullComposite(Document doc) => Compositor.Render(doc).ToRgba8();

    /// <summary>
    /// The layer workflow (STRAYTA_SELFTEST_ONLY=layers): several selected layers (⌘/⇧ clicks, All Layers), moving and
    /// nudging them, Align and Distribute, locks and their messages, groups, links, the merge commands (composite
    /// unchanged), Delete Hidden Layers, the filter bar, thumbnail sizes, Auto-Select, Free Transform on several layers and
    /// a saved file (the Photoshop check file with STRAYTA_LAYER_SAMPLES=dir).
    /// </summary>
    private static async Task RunLayerStepsAsync(EditorViewModel editor, Action<bool, string> check)
    {
        var model = LayerFactory.NewDocument(200, 150, whiteBackground: true);
        var a = SolidLayer("A", new PixelRect(10, 10, 40, 30), 230, 40, 40);
        var b = SolidLayer("B", new PixelRect(60, 50, 80, 90), 40, 160, 60, 200);
        var c = SolidLayer("C", new PixelRect(120, 20, 170, 40), 40, 70, 220);
        c.BlendMode = BlendMode.Multiply;
        foreach (var l in new[] { a, b, c }) model.Root.Add(l);
        var bg = (PixelLayer)model.Root.Children[0];
        var doc = new DocumentViewModel(model, null, editor);
        editor.Factory.AddDocument(doc);
        editor.ActiveDocument = doc;
        await doc.RenderAsync();
        editor.Tool = CanvasTool.Move;
        LayerItemViewModel Item(LayerNode n) => doc.ItemFor(n)!;

        // ---- Selecting several layers ----
        doc.ClickLayer(Item(a), command: false, shift: false);
        doc.ClickLayer(Item(c), command: true, shift: false);
        check(doc.SelectedCount == 2 && doc.SelectedLayer?.Node == c && Item(a).IsSelected && !Item(b).IsSelected,
            $"⌘-click adds a layer; the clicked one is primary ({doc.SelectedCount} selected)");
        var exported = doc.SelectedNodesForExport();
        check(exported.Count == 2 && exported.Contains(a) && exported.Contains(c)
              && model.Root.Descendants().ToList().IndexOf(exported[0]) > model.Root.Descendants().ToList().IndexOf(exported[1]),
            "export of the selection takes every selected layer, top first");
        doc.ClickLayer(Item(c), command: true, shift: false);
        check(doc.SelectedCount == 1 && doc.SelectedLayer?.Node == a, "⌘-click on a selected layer removes it");
        doc.ClickLayer(Item(c), command: false, shift: true);
        check(doc.SelectedCount == 3 && doc.IsNodeSelected(b), "⇧-click selects the rows between");
        doc.SelectAllLayers();
        check(doc.SelectedCount == 4, "Select › All Layers selects every layer");
        doc.SelectedLayer = Item(b);
        check(doc.SelectedCount == 1, "choosing one layer (the panel, code) selects just it");

        // ---- Moving and nudging several (and one undo step each) ----
        doc.SetLayerSelection([a, c]);
        var (aBounds, cBounds) = (a.Bounds, c.Bounds);
        doc.MoveSelected(5, 3);
        doc.MoveSelected(2, 1);
        check(a.Bounds.Left == aBounds.Left + 7 && c.Bounds.Top == cBounds.Top + 4 && b.Bounds.Left == 60 && doc.UndoText == "Undo Move",
            "the Move tool moves every selected layer, as one step");
        doc.Undo();
        check(a.Bounds == aBounds && c.Bounds == cBounds, "undo puts them back");
        doc.Nudge(10, 0);
        check(a.Bounds.Left == aBounds.Left + 10 && c.Bounds.Left == cBounds.Left + 10 && doc.UndoText == "Undo Nudge", "arrow keys nudge them");
        doc.Undo();

        // ---- Align and distribute ----
        doc.SetLayerSelection([a, b, c]);
        doc.AlignLayers(AlignEdge.Left);
        check(a.Bounds.Left == 10 && b.Bounds.Left == 10 && c.Bounds.Left == 10 && doc.UndoText == "Undo Align", "Align Left Edges lines up the selected layers");
        doc.Undo();
        doc.AlignLayers(AlignEdge.Bottom);
        check(a.Bounds.Bottom == 90 && b.Bounds.Bottom == 90 && c.Bounds.Bottom == 90, "Align Bottom Edges uses their combined box");
        doc.Undo();
        doc.SetLayerSelection([b]);
        doc.AlignLayers(AlignEdge.HorizontalCenter);
        check(b.Bounds.Left == 90, $"one layer aligns to the canvas ({b.Bounds})");
        doc.Undo();
        doc.SetLayerSelection([a, b, c]);
        doc.DistributeLayers(DistributeMode.HorizontalCenter);
        double Center(PixelLayer l) => (l.Bounds.Left + l.Bounds.Right) / 2.0;
        check(Math.Abs(Center(b) - (Center(a) + Center(c)) / 2) <= 0.5 && doc.UndoText == "Undo Distribute", "Distribute Horizontal Centers spaces them evenly");
        doc.Undo();
        check(doc.Properties is LayersAlignPanel, "the Properties panel shows Align and Distribute for several layers");

        // ---- Opacity, blend mode and visibility of several ----
        doc.SetLayerSelection([a, b]);
        Item(b).Opacity = 50;
        check(a.Opacity == 0.5f && b.Opacity == 0.5f && doc.UndoText == "Undo Master Opacity Change", "the opacity field sets every selected layer");
        doc.Undo();
        Item(b).BlendMode = BlendMode.Screen;
        check(a.BlendMode == BlendMode.Screen && b.BlendMode == BlendMode.Screen && doc.UndoText == "Undo Blending Change", "so does the blend mode");
        doc.Undo();
        doc.ToggleSelectedVisibility();
        check(!a.Visible && !b.Visible && doc.UndoText == "Undo Hide Layers", "Hide Layers hides the selected layers");
        doc.Undo();

        // ---- Locks ----
        doc.SetLayerSelection([a]);
        Item(a).LockPosition = true;
        check(a.Locks == LayerLocks.Position && Item(a).IsLocked && doc.UndoText == "Undo Lock Layer", "Lock Position is set and shown");
        doc.MoveSelected(4, 4);
        check(a.Bounds == aBounds && doc.Notice.Contains("locked"), $"a position-locked layer does not move ({doc.Notice})");
        check(!doc.BeginFreeTransform() && doc.Notice.Contains("Free Transform") && doc.Notice.Contains("locked"), "Free Transform is refused with Photoshop's message");
        doc.AlignLayers(AlignEdge.Left);
        check(doc.Notice.Contains("Align"), "so is Align");
        doc.Undo();
        Item(a).LockPixels = true;
        editor.Tool = CanvasTool.Brush;
        check(!doc.BeginStroke(20, 20, new Core.Painting.BrushSettings(10, 1, 1), new RgbColor(0, 0, 0), false) && doc.Notice.Contains("locked"),
            $"painting a pixel-locked layer is refused ({doc.Notice})");
        editor.Tool = CanvasTool.Move;
        doc.Undo();
        Item(a).LockAll = true;
        check(Item(a).IsFullyLocked && !Item(a).CanChangeBlending && a.IsLocked(LayerLocks.Position), "Lock All locks everything, blending too");
        doc.Undo();
        check(a.Locks == LayerLocks.None, "undo unlocks");

        // ---- Groups ----
        doc.SetLayerSelection([a, b]);
        doc.GroupSelectedLayers();
        var group = doc.SelectedLayer?.Node as LayerGroup;
        check(group is not null && group.Children.SequenceEqual(new LayerNode[] { a, b }) && model.Root.Children.Contains(group) && doc.UndoText == "Undo Group Layers",
            "Group Layers (⌘G) puts the selected layers in a new group");
        doc.UngroupSelectedLayers();
        check(a.Parent == model.Root && b.Parent == model.Root && !model.Root.Children.Contains(group!) && doc.SelectedCount == 2, "Ungroup Layers (⇧⌘G) releases them");
        doc.Undo();
        doc.Undo();
        check(model.Root.Children.SequenceEqual(new LayerNode[] { bg, a, b, c }), "undo restores the order");

        // ---- Duplicate and delete several ----
        doc.SetLayerSelection([a, c]);
        doc.DuplicateSelected();
        check(model.Root.Children.Count == 6 && doc.SelectedCount == 2 && doc.UndoText == "Undo Duplicate Layers", "Duplicate duplicates every selected layer");
        doc.DeleteSelected();
        check(model.Root.Children.Count == 4 && doc.UndoText == "Undo Delete Layers", "Delete removes every selected layer in one step");
        doc.Undo();
        doc.Undo();

        // ---- Reordering several ----
        doc.SetLayerSelection([a, b]);
        doc.MoveLayers(doc.SelectedTopLevel(), model.Root, model.Root.Children.Count);
        check(model.Root.Children.SequenceEqual(new LayerNode[] { bg, c, a, b }) && doc.UndoText == "Undo Layer Order", "dragging several rows moves them together");
        doc.Undo();

        // ---- Links ----
        doc.SetLayerSelection([a, b]);
        doc.LinkSelectedLayers();
        check(a.LinkGroup != 0 && a.LinkGroup == b.LinkGroup && Item(a).IsLinked, "Link Layers links them");
        doc.SetLayerSelection([a]);
        var bBounds = b.Bounds;
        doc.MoveSelected(3, 0);
        check(b.Bounds.Left == bBounds.Left + 3, "moving a linked layer moves the layers linked to it");
        doc.Undo();

        // ---- Colors ----
        doc.SetLayerSelection([c]);
        doc.SetColorLabel(LayerColor.Violet);
        check(c.Color == LayerColor.Violet && Item(c).ColorBrush is not null && doc.UndoText == "Undo Layer Properties", "a color label is set and shown");

        // ---- Save: locks, links and colors round trip ----
        a.Locks = LayerLocks.Pixels;
        string path = Path.Combine(Path.GetTempPath(), $"layers2-selftest-{Guid.NewGuid():N}.psd");
        await doc.SaveAsync(path);
        var reopened = PsdFile.OpenForEditing(path);
        var ra = reopened.Root.Children.First(n => n.Name == "A");
        var rb = reopened.Root.Children.First(n => n.Name == "B");
        var rc = reopened.Root.Children.First(n => n.Name == "C");
        check(ra.Locks == LayerLocks.Pixels && rc.Color == LayerColor.Violet && ra.LinkGroup != 0 && ra.LinkGroup == rb.LinkGroup && rc.LinkGroup == 0,
            "locks, color labels and links are saved and read back");
        File.Delete(path);
        a.Locks = LayerLocks.None;

        // ---- Merging keeps the image ----
        var before = FullComposite(model);
        doc.SetLayerSelection([c]);
        await doc.MergeDownAsync();
        check(model.Root.Children.Count == 3 && doc.UndoText == "Undo Merge Down" && FullComposite(model).SequenceEqual(before),
            "Merge Down (⌘E) keeps the image exactly");
        doc.Undo();
        check(model.Root.Children.Count == 4 && FullComposite(model).SequenceEqual(before), "undo brings the layers back");
        doc.SetLayerSelection([bg, a, b, c]);
        await doc.MergeDownAsync();
        check(model.Root.Children.Count == 1 && doc.UndoText == "Undo Merge Layers" && FullComposite(model).SequenceEqual(before), "Merge Layers keeps the image exactly");
        doc.Undo();
        b.Visible = false;
        var beforeHidden = FullComposite(model);
        await doc.MergeVisibleAsync();
        check(model.Root.Children.Count == 2 && model.Root.Children.Contains(b) && FullComposite(model).SequenceEqual(beforeHidden) && doc.UndoText == "Undo Merge Visible",
            "Merge Visible (⇧⌘E) keeps hidden layers and the image");
        doc.Undo();
        doc.SetLayerSelection([c]);
        await doc.StampVisibleAsync();
        check(model.Root.Children.Count == 5 && FullComposite(model).SequenceEqual(beforeHidden) && doc.UndoText == "Undo Stamp Visible",
            "Stamp Visible (⌥⇧⌘E) adds the visible image on top");
        doc.Undo();
        await doc.FlattenImageAsync();
        check(model.Root.Children.Count == 1 && model.Root.Children[0].Name == "Background" && FullComposite(model).SequenceEqual(beforeHidden)
              && doc.UndoText == "Undo Flatten Image", "Flatten Image discards hidden layers and keeps the image");
        doc.Undo();
        doc.DeleteHiddenLayers();
        check(!model.Root.Children.Contains(b) && doc.UndoText == "Undo Delete Hidden Layers", "Delete Hidden Layers");
        doc.Undo();
        b.Visible = true;

        // ---- Filter bar and panel options ----
        editor.LayerFilter.FilterTypeIndex = 1;
        editor.LayerFilter.Name = "b";
        check(doc.Rows.Count == 2 && doc.Rows.All(r => r.Name.Contains('B', StringComparison.OrdinalIgnoreCase)), $"filtering by name lists the matching layers ({doc.Rows.Count})");
        editor.LayerFilter.Enabled = false;
        check(doc.Rows.Count == 4, "the switch turns the filter off");
        editor.LayerFilter.Enabled = true;
        editor.LayerFilter.Name = "";
        editor.LayerFilter.FilterTypeIndex = 3;
        editor.LayerFilter.Mode = BlendMode.Multiply;
        check(doc.Rows.Count == 1 && doc.Rows[0].Node == c, "filtering by mode");
        editor.LayerFilter.FilterTypeIndex = 0;
        check(doc.Rows.Count == 4, "no kind chosen shows everything");
        editor.LayerThumbnailSize = LayerThumbnailSize.Large;
        check(Item(a).RowHeight > 40 && Item(a).ThumbWidth > 50, "Large thumbnails make taller rows");
        editor.LayerThumbnailSize = LayerThumbnailSize.None;
        check(!Item(a).ShowThumbnails, "no thumbnails");
        editor.LayerThumbnailSize = LayerThumbnailSize.Medium;
        doc.SetLayerSelection([a]);
        doc.SelectSimilarLayers();
        check(doc.SelectedCount == 4, "Select Similar Layers selects the pixel layers");

        // ---- Auto-Select ----
        editor.MoveAutoSelect = true;
        doc.SetLayerSelection([bg]);
        doc.MoveToolPressed(130, 25, command: false, alt: false, shift: false);
        check(doc.SelectedLayer?.Node == c && doc.SelectedCount == 1, "Auto-Select picks the layer under the pointer");
        doc.MoveToolPressed(15, 15, command: false, alt: false, shift: true);
        check(doc.SelectedCount == 2 && doc.IsNodeSelected(a), "Shift adds it to the selection");
        doc.MoveToolPressed(70, 60, command: true, alt: false, shift: false);
        check(doc.SelectedCount == 2 && !doc.IsNodeSelected(b), "⌘ turns Auto-Select off for the click");
        editor.MoveAutoSelect = false;
        doc.MoveToolPressed(70, 60, command: true, alt: true, shift: false);
        check(doc.SelectedLayer?.Node == b, "⌘⌥-click selects the layer under the pointer");

        // ---- A look at the panel and the canvas (STRAYTA_SELFTEST_SHOTS) ----
        if (Environment.GetEnvironmentVariable("STRAYTA_SELFTEST_SHOTS") is { Length: > 0 } shots
            && Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
        {
            a.Locks = LayerLocks.All;
            b.Locks = LayerLocks.Position;
            a.Color = LayerColor.Red;
            doc.SetLayerSelection([a, c]);
            editor.MoveShowTransformControls = true;
            foreach (var item in doc.Layers) item.Refresh();
            await Task.Delay(800);
            Directory.CreateDirectory(shots);
            using var shot = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize((int)main.Bounds.Width, (int)main.Bounds.Height));
            shot.Render(main);
            shot.Save(Path.Combine(shots, "layers2-window.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            editor.MoveShowTransformControls = false;
            (a.Locks, b.Locks, a.Color) = (LayerLocks.None, LayerLocks.None, LayerColor.None);
        }

        // ---- Free Transform on several layers ----
        doc.SetLayerSelection([a, c]);
        check(doc.BeginFreeTransform(), "Free Transform opens on several layers");
        var ft = doc.FreeTransform!;
        check(ft.Original.Left == 10 && ft.Original.Right == 170, $"its box holds all of them ({ft.Original})");
        ft.X += 7;
        await doc.CommitTransformAsync();
        check(a.Bounds.Left == aBounds.Left + 7 && c.Bounds.Left == cBounds.Left + 7 && b.Bounds.Left == 67,
            "the selected layers move with the transform, and B with A, which it is linked to");
        doc.Undo();

        // ---- The Photoshop check file ----
        if (Environment.GetEnvironmentVariable("STRAYTA_LAYER_SAMPLES") is { Length: > 0 } samples)
            WriteLayerSample(samples, check);

        doc.MarkSavedForTest();
        doc.CloseWithoutAsking();
    }

    /// <summary>A file with linked layers, every lock and every color label, to open in Photoshop.</summary>
    private static void WriteLayerSample(string dir, Action<bool, string> check)
    {
        Directory.CreateDirectory(dir);
        var doc = LayerFactory.NewDocument(400, 300, whiteBackground: true);
        var colors = Enum.GetValues<LayerColor>().Where(c => c != LayerColor.None).ToArray();
        for (int i = 0; i < colors.Length; i++)
        {
            var l = SolidLayer($"{colors[i]} label", new PixelRect(10 + i * 54, 10, 54 + i * 54, 60), (byte)(40 + i * 30), (byte)(200 - i * 20), 120);
            l.Color = colors[i];
            doc.Root.Add(l);
        }
        var locks = new (string Name, LayerLocks Locks)[]
        {
            ("Lock transparent pixels", LayerLocks.Transparency), ("Lock image pixels", LayerLocks.Pixels),
            ("Lock position", LayerLocks.Position), ("Lock all", LayerLocks.All),
            ("Pixels and position", LayerLocks.Pixels | LayerLocks.Position),
        };
        for (int i = 0; i < locks.Length; i++)
        {
            var l = SolidLayer(locks[i].Name, new PixelRect(10 + i * 76, 80, 80 + i * 76, 140), 90, 90, (byte)(120 + i * 25));
            l.Locks = locks[i].Locks;
            doc.Root.Add(l);
        }
        var group = new LayerGroup { Name = "Linked group", LinkGroup = 1, Color = LayerColor.Blue };
        group.Add(SolidLayer("Inside the group", new PixelRect(20, 170, 120, 280), 30, 120, 200));
        doc.Root.Add(group);
        var linkedA = SolidLayer("Linked A", new PixelRect(150, 170, 250, 220), 220, 60, 60);
        var linkedB = SolidLayer("Linked B", new PixelRect(270, 170, 380, 220), 60, 60, 220);
        linkedA.LinkGroup = linkedB.LinkGroup = 1;
        var other = SolidLayer("Linked separately 1", new PixelRect(150, 230, 250, 280), 200, 200, 60);
        var other2 = SolidLayer("Linked separately 2", new PixelRect(270, 230, 380, 280), 60, 200, 200);
        other.LinkGroup = other2.LinkGroup = 2;
        foreach (var l in new[] { linkedA, linkedB, other, other2 }) doc.Root.Add(l);
        string path = Path.Combine(dir, "layers-locks-links-colors.psd");
        using (var renderer = new CpuRenderer())
            PsdWriter.Save(doc, path, new PsdWriteOptions { Composite = renderer.Render(doc).ToRaster(doc.ColorMode, doc.BitDepth) });
        var back = PsdFile.OpenForEditing(path);
        check(back.Root.Descendants().Count(n => n.Color != LayerColor.None) == colors.Length + 1
              && back.Root.Descendants().Count(n => n.LinkGroup == 1) == 3 && back.Root.Descendants().Any(n => n.Locks == LayerLocks.All),
            $"the Photoshop check file reads back ({path})");
        File.WriteAllText(Path.Combine(dir, "README.txt"),
            """
            layers-locks-links-colors.psd (written by Strayta's self-test)

            Open it in Photoshop and check:
            - The first seven layers carry the color labels Red, Orange, Yellow, Green, Blue, Violet and Gray (the eye column).
            - "Lock transparent pixels", "Lock image pixels", "Lock position" and "Lock all" show those locks (Lock All: a
              solid padlock); "Pixels and position" has both.
            - "Linked group", "Linked A" and "Linked B" are linked (chain icons; selecting one highlights the others) and
              move together; the two "Linked separately" layers are linked to each other only.
            - The group is labeled Blue.
            Then save it from Photoshop and send the file back, so its lock, color and link records can be compared.
            """);
    }
}
