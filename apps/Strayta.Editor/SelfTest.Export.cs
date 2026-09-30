using SkiaSharp;
using Strayta.Core;
using Strayta.Editor.Controls;
using Strayta.Editor.Editing;
using Strayta.Editor.ViewModels;
using Strayta.Psd;
using Strayta.Rendering.Export;

namespace Strayta.Editor;

internal static partial class SelfTest
{
    private static Raster SolidRaster(int w, int h, byte r, byte g, byte b)
    {
        var planes = new[] { r, g, b }.Select(v =>
        {
            var p = Plane.Create(w, h, 8);
            Array.Fill(p.Data, v);
            return p;
        }).ToArray();
        return new Raster(ColorMode.Rgb, planes, null);
    }

    private static PixelLayer AddSolid(DocumentViewModel doc, LayerGroup parent, string name, PixelRect rect, byte r, byte g, byte b)
    {
        var layer = new PixelLayer { Name = name, Bounds = rect, Pixels = SolidRaster(rect.Width, rect.Height, r, g, b) };
        doc.Apply(new InsertEdit(layer, parent, parent.Children.Count, "New Layer"));
        return layer;
    }

    private static (int W, int H)? ImageSize(string path)
    {
        using var codec = SKCodec.Create(path);
        return codec is null ? null : (codec.Info.Width, codec.Info.Height);
    }

    /// <summary>
    /// Export and artboards (STRAYTA_SELFTEST_ONLY=export): Export As (formats, preview and size estimate, scales with
    /// suffixes), Quick Export, Layers to Files, Generate Image Assets (and again on save), then artboards: New Artboard,
    /// the "+" beside one (the canvas grows), resize steps merging into one undo step, Artboards from Layers and its undo,
    /// the Layers panel icon, the canvas overlay, export per artboard, and a PSD round trip that saves unchanged files
    /// byte for byte. STRAYTA_ARTBOARD_SAMPLE=dir also writes a two-artboard file to check in Photoshop.
    /// </summary>
    private static async Task RunExportStepsAsync(EditorViewModel editor, Action<bool, string> check)
    {
        string dir = Path.Combine(Path.GetTempPath(), "export2-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        ExportPreferences.UseDirectory(Path.Combine(dir, "settings")); // never touch the person's own export settings
        try
        {
            var model = LayerFactory.NewDocument(400, 300, whiteBackground: true);
            var doc = new DocumentViewModel(model, null, editor);
            editor.Factory.AddDocument(doc);
            editor.ActiveDocument = doc;
            await doc.RenderAsync();
            var icon = AddSolid(doc, model.Root, "icon.png, 200% icons/icon@2x.png", new PixelRect(40, 50, 100, 90), 255, 0, 0);
            AddSolid(doc, model.Root, "Blue bar", new PixelRect(200, 200, 380, 220), 0, 0, 255);

            // Export As: the document, GIF, with a second size.
            var export = editor.CreateExportAs(selection: false)!;
            check(export.Items.Count == 1 && export.Items[0].Kind == "Document", "Export As lists the document");
            export.FormatIndex = (int)ExportFormat.Gif;
            await export.RenderSelectedAsync();
            for (int i = 0; i < 100 && export.Preview is null; i++) await Task.Delay(20);
            check(export.Preview is { } p && p.PixelSize.Width == 400 && export.FileSizeText.Length > 0,
                $"the preview shows the encoded GIF with its size ({export.PreviewInfo})");
            export.AddScale();
            check(export.Scales.Count == 2 && export.Scales[1].Suffix == "@2x", "the + adds a @2x size");
            string exportDir = Path.Combine(dir, "export-as");
            var files = await export.ExportAllAsync(exportDir);
            check(files.Count == 2 && files.Select(f => ImageSize(f)).SequenceEqual(new (int, int)?[] { (400, 300), (800, 600) }),
                $"Export As writes each size, the @2x one twice as large ({string.Join(", ", files.Select(Path.GetFileName))})");

            // WebP and JPEG through the same pipeline, with a scale and canvas size.
            export.FormatIndex = (int)ExportFormat.WebP;
            export.ScalePercent = 50;
            export.CanvasWidth = 300;
            export.CanvasHeight = 300;
            export.RemoveScale(export.Scales[1]);
            var webp = await export.ExportAllAsync(Path.Combine(dir, "webp"));
            check(webp.Count == 1 && ImageSize(webp[0]) == (300, 300), "WebP export at 50% on a 300×300 canvas");

            // Export As for the selected layer: trimmed to it.
            doc.SelectedLayer = doc.Layers.SelectMany(l => l.SelfAndDescendants()).First(i => i.Node == icon);
            var layerExport = editor.CreateExportAs(selection: true)!;
            check(layerExport.Items.Single().Name == "icon", "a layer named as an asset exports under its asset name");
            var layerFiles = await layerExport.ExportAllAsync(Path.Combine(dir, "layer"));
            check(layerFiles.Count == 1 && ImageSize(layerFiles[0]) == (60, 40), "a layer exports trimmed to its pixels");

            // Quick Export as PNG.
            var quick = await editor.QuickExportToAsync(doc, Path.Combine(dir, "quick"), "quick.png", ExportPreferences.Shared.QuickExport.ToOptions());
            check(quick.Count == 1 && Path.GetFileName(quick[0]) == "quick.png" && ImageSize(quick[0]) == (400, 300), "Quick Export as PNG writes the document");

            // Layers to Files: every top-level layer, trimmed, named with a prefix.
            var layersDir = Path.Combine(dir, "layers");
            var layerFilesAll = await doc.ExportLayersAsync(layersDir, new ExportOptions(), ExportLayerScope.TopLevel, true, true, "ui_");
            check(layerFilesAll.Select(Path.GetFileName).SequenceEqual(["ui_Blue bar.png", "ui_icon.png", "ui_Background.png"])
                  && ImageSize(Path.Combine(layersDir, "ui_Blue bar.png")) == (180, 20),
                $"Layers to Files writes one trimmed file per layer ({string.Join(", ", layerFilesAll.Select(Path.GetFileName))})");

            // Generate › Image Assets, then again on save.
            var assets = await doc.GenerateImageAssetsAsync(Path.Combine(dir, "assets"));
            check(assets.Count == 2 && assets.All(a => a.Error is null)
                  && ImageSize(Path.Combine(dir, "assets", "icon.png")) == (60, 40)
                  && ImageSize(Path.Combine(dir, "assets", "icons", "icon@2x.png")) == (120, 80),
                "Generate Image Assets writes icon.png and the 200% icons/icon@2x.png");
            doc.GeneratesImageAssets = true;
            string psd = Path.Combine(dir, "Poster.psd");
            await doc.SaveAsync(psd);
            check(File.Exists(Path.Combine(dir, "Poster-assets", "icon.png")), "saving regenerates the assets into Poster-assets");
            doc.GeneratesImageAssets = false;

            await RunArtboardStepsAsync(editor, check, dir);
        }
        finally
        {
            ExportPreferences.UseDirectory(UserPresets.DefaultDirectory());
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    private static async Task RunArtboardStepsAsync(EditorViewModel editor, Action<bool, string> check, string dir)
    {
        var model = new Document(400, 300, ColorMode.Rgb, 8);
        var doc = new DocumentViewModel(model, null, editor);
        editor.Factory.AddDocument(doc);
        editor.ActiveDocument = doc;
        await doc.RenderAsync();

        var first = doc.NewArtboard(new PixelRect(0, 0, 200, 300), preset: "Custom");
        check(model.Root.Children.Single() == first && first.Artboard is { } a && a.Rect == new PixelRect(0, 0, 200, 300) && first.Name == "Artboard 1",
            "New Artboard adds a top-level artboard");
        var rendered = ExportRenderer.Render(model, ExportTarget.Document);
        check(rendered.Pixels[(100 * 400 + 100) * 4 + 3] == 255 && rendered.Pixels[(100 * 400 + 300) * 4 + 3] == 0,
            "the artboard draws white; the pasteboard beside it stays transparent");
        var row = doc.Layers.Single();
        check(row.PlaceholderIcon is not null && row.PlaceholderIcon != new LayerItemViewModel(new LayerGroup(), doc).PlaceholderIcon,
            "the Layers panel shows the artboard with its own icon");

        editor.Tool = CanvasTool.Artboard;
        check(editor.IsArtboardTool && editor.ToolGroups[0].Tools.Any(t => t.Tool == CanvasTool.Artboard), "the Artboard tool shares the Move tool's slot");
        check(editor.HasSelectedArtboard && editor.ArtboardWidth == 200, "its options bar shows the selected artboard's size");

        // "+" on the right: same size, beside it; the canvas grows to hold it.
        var second = doc.AddAdjacentArtboard(first, ArtboardSide.Right)!;
        check(second.Artboard!.Rect == new PixelRect(300, 0, 500, 300) && model.Width == 500, $"the + adds an artboard beside it and the canvas grows ({model.Width}×{model.Height})");
        doc.Undo();
        doc.Undo();
        check(model.Root.Children.Count == 1 && model.Width == 400, "undo removes it and the canvas growth");
        doc.Redo();
        doc.Redo();
        check(model.Root.Children.Count == 2 && model.Width == 500, "redo brings both back");

        // Resize by the handles: the steps of one drag are one undo step.
        doc.ResizeArtboard(second, new PixelRect(300, 0, 480, 300));
        doc.ResizeArtboard(second, new PixelRect(300, 0, 460, 280));
        check(second.Artboard!.Rect == new PixelRect(300, 0, 460, 280) && doc.UndoText == "Undo Resize Artboard", "resizing sets the new bounds");
        doc.Undo();
        check(second.Artboard!.Rect == new PixelRect(300, 0, 500, 300), "one undo reverts the whole resize");

        // Options bar: the background of the selected artboard.
        doc.SelectArtboard(second);
        editor.ArtboardBackgroundIndex = 1;
        check(second.Artboard!.Background == ArtboardBackground.Black, "the options bar sets the background");
        doc.Undo();

        // Moving an artboard moves its layers.
        var dot = AddSolid(doc, first, "dot", new PixelRect(10, 10, 30, 30), 0, 160, 0);
        doc.SelectArtboard(first);
        doc.MoveArtboard(first, 5, 7);
        check(first.Artboard!.Rect == new PixelRect(5, 7, 205, 307) && dot.Bounds == new PixelRect(15, 17, 35, 37), "moving an artboard moves its layers");
        doc.Undo();

        // Artboards from Layers.
        var loose = AddSolid(doc, model.Root, "loose", new PixelRect(210, 40, 260, 90), 200, 100, 0);
        doc.SelectedLayer = doc.Layers.SelectMany(l => l.SelfAndDescendants()).First(i => i.Node == loose);
        var fromLayers = doc.ArtboardFromLayers();
        check(fromLayers is { Artboard.Rect: var r } && r == new PixelRect(210, 40, 260, 90) && loose.Parent == fromLayers,
            "Artboards from Layers puts the layer on a new artboard around it");
        doc.Undo();
        check(loose.Parent == model.Root && model.Root.Children.Count(c => c is LayerGroup { Artboard: not null }) == 2, "undo puts the layer back");
        doc.DeleteSelected();

        // The canvas shows names and handles.
        doc.SelectArtboard(first);
        var canvas = await CanvasOfAsync(doc);
        check(canvas?.ArtboardsSource?.Invoke() is { Count: 2 } overlays && overlays.Count(o => o.IsSelected) == 1, "the canvas gets both artboards to label");

        // Export per artboard.
        var export = editor.CreateExportAs(selection: false)!;
        check(export.Items.Count == 2 && export.Items.All(i => i.Kind == "Artboard"), "Export As lists the artboards");
        var boards = await export.ExportAllAsync(Path.Combine(dir, "artboards"));
        check(boards.Select(f => ImageSize(f)).SequenceEqual(new (int, int)?[] { (200, 300), (200, 300) })
              && boards.Select(Path.GetFileName).SequenceEqual(["Artboard 2.png", "Artboard 1.png"]),
            $"each artboard exports at its own size ({string.Join(", ", boards.Select(Path.GetFileName))})");

        // Save, reopen, save again: artboards survive and an unchanged file is written byte for byte.
        string path = Path.Combine(dir, "Artboards.psd");
        await doc.SaveAsync(path);
        var reopened = PsdFile.OpenForEditing(path);
        var groups = reopened.Root.Children.OfType<LayerGroup>().ToList();
        check(groups.Count == 2 && groups.Select(g => g.Artboard).SequenceEqual(model.Root.Children.OfType<LayerGroup>().Select(g => g.Artboard)),
            "artboards read back from the saved PSD");
        var again = new DocumentViewModel(reopened, path, editor);
        string path2 = Path.Combine(dir, "Artboards2.psd");
        await again.SaveAsync(path2);
        check(File.ReadAllBytes(path).AsSpan().SequenceEqual(File.ReadAllBytes(path2)), "an unchanged artboard file saves byte for byte");
        again.CloseWithoutAsking();

        if (Environment.GetEnvironmentVariable("STRAYTA_ARTBOARD_SAMPLE") is { Length: > 0 } sampleDir)
            await WriteArtboardSampleAsync(editor, sampleDir, check);
        doc.MarkSavedForTest();
        doc.CloseWithoutAsking();
    }

    /// <summary>A file with two artboards made through the editor (phone and web sizes, one with its own color), for Photoshop.</summary>
    private static async Task WriteArtboardSampleAsync(EditorViewModel editor, string sampleDir, Action<bool, string> check)
    {
        var model = new Document(750, 1334, ColorMode.Rgb, 8);
        var doc = new DocumentViewModel(model, null, editor);
        editor.Factory.AddDocument(doc);
        editor.ActiveDocument = doc;
        var phone = doc.NewArtboard(new PixelRect(0, 0, 750, 1334), "Phone", preset: "iPhone SE");
        AddSolid(doc, phone, "Header", new PixelRect(0, 0, 750, 160), 30, 90, 200);
        AddSolid(doc, phone, "Button", new PixelRect(175, 1100, 575, 1220), 240, 80, 60);
        AddSolid(doc, phone, "Overflow (clipped at the artboard edge)", new PixelRect(600, 600, 900, 700), 20, 170, 90);
        var web = doc.AddAdjacentArtboard(phone, ArtboardSide.Right)!;
        doc.SelectArtboard(web);
        doc.SetSelectedArtboardSize(1280, 800, "Web 1280");
        doc.SetSelectedArtboardBackground(ArtboardBackground.Custom, (236, 240, 245));
        doc.Apply(new PropertyEdit<string>(web, "Name", web.Name, "Web", (n, v) => n.Name = v));
        AddSolid(doc, web, "Hero", new PixelRect(950, 100, 1930, 500), 60, 60, 70);
        AddSolid(doc, web, "Card", new PixelRect(950, 560, 1250, 760), 255, 255, 255);
        Directory.CreateDirectory(sampleDir);
        string path = Path.Combine(sampleDir, "two-artboards.psd");
        await doc.SaveAsync(path);
        var back = PsdFile.OpenForEditing(path);
        check(back.Root.Children.OfType<LayerGroup>().Count(g => g.Artboard is not null) == 2, $"wrote the Photoshop check file {path}");
        doc.CloseWithoutAsking();
    }
}
