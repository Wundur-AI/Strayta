using Strayta.Core;
using Strayta.Core.Painting;
using Strayta.Editor.Editing;
using Strayta.Editor.ViewModels;
using Strayta.Psd;
using Strayta.Psd.Descriptors;
using Strayta.Rendering.Export;
using Strayta.Rendering.Filters;
using Strayta.Rendering.Transforms;

namespace Strayta.Editor;

/// <summary>
/// Self-test steps for smart objects (STRAYTA_SELFTEST_ONLY=smart): Convert to Smart Object, Edit Contents and saving
/// back, smart filters, New Smart Object via Copy, Replace and Export Contents, linked files, and saving. With
/// STRAYTA_SMART_SAMPLES set to a folder, files for checking in Photoshop are written there (and, with STRAYTA_CORPUS, a
/// corpus mock-up with edited and replaced contents).
/// </summary>
internal static partial class SelfTest
{
    private static IEnumerable<LayerItemViewModel> Items(DocumentViewModel doc) => doc.Layers.SelectMany(l => l.SelfAndDescendants());

    private static byte At(PixelLayer layer, int x, int y, int plane)
    {
        var p = plane < 3 ? layer.Pixels!.ColorPlanes[plane] : layer.Pixels!.Alpha!;
        return p.Data[(y - layer.Bounds.Top) * layer.Bounds.Width + x - layer.Bounds.Left];
    }

    private static byte[] Png(int w, int h, Func<int, int, (byte, byte, byte, byte)> f)
    {
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var (r, g, b, a) = f(x, y);
                int i = (y * w + x) * 4;
                (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = (r, g, b, a);
            }
        var o = new MemoryStream();
        PngEncoder.Encode(o, rgba, w, h);
        return o.ToArray();
    }

    private static async Task RunSmartObjectStepsAsync(EditorViewModel editor, Action<bool, string> check)
    {
        string temp = Path.Combine(Path.GetTempPath(), $"smart-selftest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        string? samples = Environment.GetEnvironmentVariable("STRAYTA_SMART_SAMPLES");
        if (samples is not null) Directory.CreateDirectory(samples);
        var savedDialogs = editor.FilterDialogs;
        var dialogs = new ScriptedFilterDialogs();
        editor.FilterDialogs = dialogs;
        try
        {
            // ---- Convert to Smart Object -----------------------------------------------------------------------
            var model = LayerFactory.NewDocument(400, 300, whiteBackground: true);
            var doc = new DocumentViewModel(model, null, editor);
            editor.Factory.AddDocument(doc);
            editor.ActiveDocument = doc;
            await doc.RenderAsync();
            var square = SolidLayer("Square", new PixelRect(100, 80, 220, 180), 220, 40, 30);
            square.Opacity = 0.8f;
            doc.Apply(new InsertEdit(square, model.Root, 1, "test"));
            doc.SelectedLayer = Items(doc).First(i => i.Node == square);
            check(MenuItem("Layer", "Smart Objects", "Convert to Smart Object") is not null, "the Layer menu has Smart Objects › Convert to Smart Object");
            await Click(MenuItem("Layer", "Smart Objects", "Convert to Smart Object"));
            var so = doc.SelectedLayer?.Node as PixelLayer;
            var file = model.SourceData as PsdFile;
            var entry = so is null ? null : SmartObjects.Entry(model, so);
            check(so is { Name: "Square", Opacity: 0.8f } && so.Tags.Contains("smart-object") && so.Bounds == new PixelRect(100, 80, 220, 180)
                  && entry is { Kind: "liFD", FileType: "8BPB", Name: "Square.psb" } && doc.UndoText == "Undo Convert to Smart Object",
                $"Convert to Smart Object puts the layer in an embedded PSB (\"{entry?.Name}\") and keeps its opacity outside ({so?.Bounds})");
            var inner = entry?.Data is { } psb ? PsdFile.Read(new MemoryStream(psb)) : null;
            check(inner is { Header.IsPsb: true, Header.Width: 120, Header.Height: 100 } && inner.Layers.Count == 1 && inner.Layers[0].Opacity == 255,
                "the embedded file is a 120×100 PSB holding the layer at 100%");
            check(so is not null && At(so, 150, 120, 0) == 220 && At(so, 150, 120, 3) == 255, "the smart object shows the same pixels");
            doc.Undo();
            check(square.Parent == model.Root && so?.Parent is null && (model.SourceData as PsdFile)?.GlobalBlocks.Any(b => b.Key == "lnk2") != true,
                "undo puts the plain layer back and drops the embedded file");
            doc.Redo();
            so = (PixelLayer)model.Root.Children[1];

            // ---- Edit Contents, then Save writes back --------------------------------------------------------
            doc.SelectedLayer = Items(doc).First(i => i.Node == so);
            await editor.EditContentsCommand.ExecuteAsync(null);
            var child = editor.ActiveDocument;
            check(child is { SmartContent: not null, Title: "Square.psb" } && !ReferenceEquals(child, doc), $"Edit Contents opens the content in its own tab ({child?.Title})");
            if (child is { SmartContent: not null })
            {
                child.SelectedLayer = child.Layers.FirstOrDefault();
                child.NewLayer();
                check(child.BeginStroke(0, 50, new BrushSettings(30, 1f, 1f), new RgbColor(0, 0, 1), erase: false), "painting in the contents");
                for (int x = 5; x <= 120; x += 5) child.ContinueStroke(x, 50);
                await child.EndStrokeAsync();
                check(child.IsModified, "the contents tab is modified");
                await editor.SaveCommand.ExecuteAsync(null);
                check(!child.IsModified && doc.UndoText == "Undo Update Smart Object" && At(so, 160, 130, 2) > 200 && At(so, 160, 130, 0) < 40,
                    $"Save writes the contents back: the smart object shows the blue stroke ({At(so, 160, 130, 0)}, {At(so, 160, 130, 2)}) as one step \"Update Smart Object\"");
                var saved = SmartObjects.Entry(model, so);
                check(saved is { Kind: "liFD" } && saved.UniqueId == entry!.UniqueId && saved.Data!.Length != entry.Data!.Length,
                    "the embedded file is replaced under the same ID, with its new size");
                doc.Undo();
                check(At(so, 160, 130, 2) < 60 && SmartObjects.Entry(model, so)?.Data?.Length == entry?.Data?.Length, "undo restores the old content and pixels");
                doc.Redo();
                editor.ActiveDocument = child;
                child.CloseWithoutAsking();
                editor.ActiveDocument = doc;
            }

            // ---- Smart filters -----------------------------------------------------------------------------------
            doc.SelectedLayer = Items(doc).First(i => i.Node == so);
            var sharp = so.Pixels;
            dialogs.Script = session =>
            {
                session.Radius = 6;
                return Task.FromResult(true);
            };
            await editor.FilterCommand.ExecuteAsync(nameof(FilterKind.GaussianBlur));
            var stack = DocumentViewModel.SmartFiltersOf(so);
            check(so.Tags.Contains("smart-object") && stack is { Filters: [{ FilterClass: "GsnB", Enabled: true }] } && doc.UndoText == "Undo Gaussian Blur"
                  && stack.Filters[0].Settings!.Number("Rds ") == 6,
                $"Gaussian Blur on a smart object adds a smart filter instead of rasterizing ({doc.Notice})");
            check(so.Bounds.Width > 120 && At(so, 100, 130, 3) is > 60 and < 200, $"the smart object is drawn blurred ({so.Bounds}, edge alpha {At(so, 100, 130, 3)})");
            var item = Items(doc).First(i => i.Node == so);
            check(item.SmartFilterRows is [{ IsMaster: true }, { Name: "Gaussian Blur", IsUnknown: false }], "the Layers panel lists Smart Filters › Gaussian Blur under the layer");
            await item.ToggleSmartFilter(item.SmartFilterRows[1]);
            check(so.Bounds == new PixelRect(100, 80, 220, 180) && DocumentViewModel.SmartFiltersOf(so)!.Filters[0].Enabled == false,
                "hiding the filter's eye redraws the smart object without it");
            doc.Undo();
            check(so.Bounds.Width > 120, "undo shows the filter again");
            dialogs.Script = session =>
            {
                session.Radius = 1;
                return Task.FromResult(true);
            };
            await editor.EditSmartFilterAsync(doc, so, 0);
            check(DocumentViewModel.SmartFiltersOf(so)!.Filters[0].Settings!.Number("Rds ") == 1 && so.Bounds.Width < 150,
                $"double-clicking the filter edits its settings and redraws ({so.Bounds})");
            await item.ToggleSmartFilter(Items(doc).First(i => i.Node == so).SmartFilterRows[0]);
            check(DocumentViewModel.SmartFiltersOf(so)!.Enabled == false && so.Bounds == new PixelRect(100, 80, 220, 180), "the Smart Filters eye hides them all");
            doc.Undo();
            if (samples is not null) await doc.SaveAsync(Path.Combine(samples, "smart-filter-gaussian-blur.psd"));

            // ---- New Smart Object via Copy -------------------------------------------------------------------
            doc.SelectedLayer = Items(doc).First(i => i.Node == so);
            await Click(MenuItem("Layer", "Smart Objects", "New Smart Object via Copy"));
            var copy = doc.SelectedLayer?.Node as PixelLayer;
            var ids = model.Root.Descendants().OfType<PixelLayer>().Select(l => SmartObjects.Read(l)?.UniqueId).OfType<string>().ToList();
            check(copy is not null && copy != so && ids.Distinct().Count() == 2 && SmartObjects.Entry(model, copy) is { Data: not null },
                "New Smart Object via Copy makes an independent copy with its own embedded file");

            // ---- Replace Contents, Export Contents ------------------------------------------------------------
            string png = Path.Combine(temp, "smart-replacement.png");
            File.WriteAllBytes(png, Png(60, 50, (x, y) => ((byte)(x * 4), 200, (byte)(y * 5), 255)));
            editor.PickSmartObjectFile = () => Task.FromResult<string?>(png);
            doc.SelectedLayer = Items(doc).First(i => i.Node == copy);
            await Click(MenuItem("Layer", "Smart Objects", "Replace Contents…"));
            var replaced = copy is null ? null : SmartObjects.Entry(model, copy);
            check(replaced is { FileType: "png ", Name: "smart-replacement.png" } && copy!.Bounds.Width is >= 60 and <= 80 && At(copy, copy.Bounds.Left + 30, copy.Bounds.Top + 25, 1) == 200,
                $"Replace Contents embeds the chosen PNG, keeping the scale ({copy?.Bounds})");
            check(SmartObjects.Read(so)!.UniqueId != SmartObjects.Read(copy!)!.UniqueId && At(so, 160, 130, 2) > 150, "the original keeps its own content");
            string exported = Path.Combine(temp, "smart-exported.png");
            editor.PickSmartExportFile = _ => Task.FromResult<string?>(exported);
            await Click(MenuItem("Layer", "Smart Objects", "Export Contents…"));
            check(File.Exists(exported) && File.ReadAllBytes(exported).SequenceEqual(File.ReadAllBytes(png)), "Export Contents writes the embedded file as it is");

            // ---- Saving: edited smart objects keep their data; unedited ones stay byte for byte ------------------
            string path = Path.Combine(temp, "smart-saved.psd");
            await doc.SaveAsync(path);
            var reopened = PsdFile.Open(path, new PsdReadOptions { MaxRawBlockBytes = long.MaxValue });
            var sos = reopened.Layers.Where(r => PsdLiveContent.ReadSmartObject(r) is not null).ToList();
            check(sos.Count == 2 && sos.All(r => PsdLiveContent.FindEmbeddedFile(reopened, PsdLiveContent.ReadSmartObject(r)!.UniqueId) is not null)
                  && PsdLiveContent.ReadSmartObject(sos[0])!.HasFilters,
                "saved: both smart objects, their embedded files and the smart filter are in the file");
            var again = PsdFile.OpenForEditing(path);
            var againPath = Path.Combine(temp, "smart-saved-again.psd");
            PsdWriter.Save(again, againPath);
            var twice = PsdFile.Open(againPath, new PsdReadOptions { MaxRawBlockBytes = long.MaxValue });
            bool same = twice.GlobalBlocks.First(b => b.Key == "lnk2").Data!.SequenceEqual(reopened.GlobalBlocks.First(b => b.Key == "lnk2").Data!)
                        && twice.Layers.Zip(reopened.Layers).All(p => p.First.FindBlock("SoLd")?.Data is not { } d || d.SequenceEqual(p.Second.FindBlock("SoLd")!.Data!));
            check(same, "opening and saving again keeps the smart objects' blocks byte for byte");
            if (samples is not null)
            {
                File.Copy(path, Path.Combine(samples, "smart-convert-edit-replace-copy.psd"), overwrite: true);
                WriteWarpStyleSample(Path.Combine(samples, "smart-warp-styles.psd"), File.ReadAllBytes(png), check);
            }
            doc.CloseWithoutAsking();

            // ---- Linked smart objects --------------------------------------------------------------------------
            string linkedPng = Path.Combine(temp, "smart-linked.png");
            File.WriteAllBytes(linkedPng, Png(40, 40, (_, _) => (255, 0, 0, 255)));
            string linkedPsd = WriteLinkedSample(Path.Combine(temp, "smart-linked-parent.psd"), linkedPng);
            await editor.OpenAsync(linkedPsd);
            var ldoc = editor.ActiveDocument!;
            var linked = ldoc.Model.Root.Descendants().OfType<PixelLayer>().Single(l => l.Tags.Contains("smart-object"));
            check(SmartObjects.Entry(ldoc.Model, linked) is { Kind: "liFE" } && ldoc.LinkedFilePaths().Contains(linkedPng), "a linked smart object finds its file");
            File.WriteAllBytes(linkedPng, Png(40, 40, (_, _) => (0, 255, 0, 255)));
            int updated = await ldoc.UpdateLinkedAsync(linkedPng);
            check(updated == 1 && At(linked, 20, 20, 1) == 255 && At(linked, 20, 20, 0) == 0 && ldoc.UndoText == "Undo Update Modified Content",
                "when the linked file changes the smart object is redrawn from it");
            File.WriteAllBytes(linkedPng, Png(40, 40, (_, _) => (0, 0, 255, 255)));
            for (int i = 0; i < 100 && At(linked, 20, 20, 2) != 255; i++) await Task.Delay(50);
            check(At(linked, 20, 20, 2) == 255, "the linked file is watched: saving it elsewhere redraws the smart object");
            ldoc.SelectedLayer = Items(ldoc).First(i => i.Node == linked);
            int before = editor.Factory.OpenDocuments().Count();
            await editor.EditContentsCommand.ExecuteAsync(null);
            check(editor.ActiveDocument?.FilePath == linkedPng && editor.Factory.OpenDocuments().Count() == before + 1, "Edit Contents on a linked smart object opens the linked file");
            editor.ActiveDocument?.CloseWithoutAsking();
            ldoc.CloseWithoutAsking();

            // ---- Photoshop check files from the corpus mock-up -------------------------------------------------
            if (samples is not null && Environment.GetEnvironmentVariable("STRAYTA_CORPUS") is { } corpus
                && Directory.EnumerateFiles(corpus, "01. Cosmetic*.psd").FirstOrDefault() is { } mockup)
                await WriteMockupSamplesAsync(editor, mockup, samples, temp, check);
        }
        finally
        {
            editor.FilterDialogs = savedDialogs;
            editor.PickSmartObjectFile = null;
            editor.PickSmartExportFile = null;
            try { Directory.Delete(temp, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Six smart objects sharing one embedded PNG, warped with Arc, Arc (vertical), Flag, Bulge, Twist and a custom mesh,
    /// drawn by Strayta: opening one's Warp in Photoshop shows how its own drawing compares.
    /// </summary>
    private static void WriteWarpStyleSample(string path, byte[] png, Action<bool, string> check)
    {
        var doc = LayerFactory.NewDocument(900, 420, whiteBackground: true);
        const string id = "3a9d0c11-2222-4b33-8c44-00000000beef";
        var file = PsdSmartObjects.WithLinkedFile(new PsdFile { Header = new PsdHeader(1, 3, 900, 420, 8, ColorMode.Rgb) },
            PsdSmartObjects.NewEmbedded(id, "art.png", "png ", png));
        doc.SourceData = file;
        var content = SmartObjects.Decode(png, doc)!;
        var mesh = WarpMesh.Identity((0, 0, content.Width, content.Height)).Select((p, i) => i is 1 or 2 ? (p.X, p.Y - 25) : i is 13 ? (p.X + 10, p.Y + 15) : p).ToArray();
        (string Name, WarpSpec Warp)[] styles =
        [
            ("Arc 50", new WarpSpec { Style = "warpArc", Value = 50 }), ("Arc 50 vertical", new WarpSpec { Style = "warpArc", Value = 50, Vertical = true }),
            ("Flag 50", new WarpSpec { Style = "warpFlag", Value = 50 }), ("Bulge 50", new WarpSpec { Style = "warpBulge", Value = 50 }),
            ("Twist 50", new WarpSpec { Style = "warpTwist", Value = 50 }), ("Custom", new WarpSpec { Style = "warpCustom", Mesh = mesh }),
        ];
        int drawn = 0;
        for (int i = 0; i < styles.Length; i++)
        {
            double x = 40 + (i % 3) * 290, y = 30 + (i / 3) * 200;
            double w = content.Width * 3, h = content.Height * 3;
            var warp = styles[i].Warp with { Bounds = (0, 0, content.Width, content.Height) };
            var placed = PsdSmartObjects.NewPlaced(id, PsdSmartObjects.NewId(), content.Width, content.Height, [(x, y), (x + w, y), (x + w, y + h), (x, y + h)], 72)
                .With("warp", new ObjectValue(PsdSmartObjects.WarpDescriptor(warp)));
            var record = PsdSmartObjects.WithPlaced(new PsdLayerRecord { Blocks = [new TaggedBlock("8BIM", "PlLd", 0, 0, [])] }, placed);
            if (SmartObjects.Draw(record, file, doc, doc.Bounds) is not { Pixels: { } pixels } result) continue;
            var layer = new PixelLayer { Name = styles[i].Name, Bounds = result.Bounds, Pixels = pixels, SourceData = record };
            layer.Tags.Add("smart-object");
            doc.Root.Add(layer);
            drawn++;
        }
        check(drawn == styles.Length, $"a sample with {drawn} warped smart objects (styles and a custom mesh) is drawn for Photoshop");
        using var renderer = new Rendering.CpuRenderer();
        PsdWriter.Save(doc, path, new PsdWriteOptions { Composite = renderer.Render(doc).ToRaster(doc.ColorMode, doc.BitDepth) });
    }

    /// <summary>A 200×120 document with one smart object linked to <paramref name="png"/> (40×40 at (0,0)).</summary>
    private static string WriteLinkedSample(string path, string png)
    {
        const string id = "7b0c3f2e-1111-4a22-9b33-00000000abcd";
        var entry = new PsdLinkedFile
        {
            Kind = "liFE", Version = 7, UniqueId = id, Name = Path.GetFileName(png), FileType = "png ",
            LinkDescriptor = new Descriptor
            {
                ClassId = "ExternalFileLink",
                Items = [new("descVersion", new IntegerValue(2)), new("Nm  ", new TextValue(Path.GetFileName(png))), new("fullPath", new TextValue(new Uri(png).AbsoluteUri))],
            },
            FileDate = (2026, 1, 1, 0, 0, 0), ExternalSize = new FileInfo(png).Length,
        };
        var doc = LayerFactory.NewDocument(200, 120, whiteBackground: true);
        doc.SourceData = PsdSmartObjects.WithLinkedFile(new PsdFile { Header = new PsdHeader(1, 3, 200, 120, 8, ColorMode.Rgb) }, entry);
        var placed = PsdSmartObjects.NewPlaced(id, PsdSmartObjects.NewId(), 40, 40, [(0, 0), (40, 0), (40, 40), (0, 40)], 72);
        var layer = SolidLayer("Linked", new PixelRect(0, 0, 40, 40), 255, 0, 0);
        layer.SourceData = PsdSmartObjects.WithPlaced(new PsdLayerRecord { Blocks = [new TaggedBlock("8BIM", "PlLd", 0, 0, [])] }, placed);
        doc.Root.Add(layer);
        PsdWriter.Save(doc, path);
        return path;
    }

    /// <summary>
    /// Edits the corpus mock-up's warped (cylinder) smart object: Edit Contents adds a bold band, Save writes it back; and
    /// Replace Contents with a generated image on a second one. Saved for checking in Photoshop.
    /// </summary>
    private static async Task WriteMockupSamplesAsync(EditorViewModel editor, string mockup, string samples, string temp, Action<bool, string> check)
    {
        await editor.OpenAsync(mockup);
        var doc = editor.ActiveDocument!;
        var sos = doc.Model.Root.Descendants().OfType<PixelLayer>().Where(l => SmartObjects.Read(l) is { Warped: true }).ToList();
        if (sos.Count < 2)
        {
            doc.CloseWithoutAsking();
            return;
        }
        var target = sos[0];
        doc.SelectedLayer = Items(doc).First(i => i.Node == target);
        await editor.EditContentsCommand.ExecuteAsync(null);
        var child = editor.ActiveDocument!;
        if (child.SmartContent is not null)
        {
            var band = SolidLayer("Band", new PixelRect(0, child.Model.Height / 3, child.Model.Width, child.Model.Height / 3 + child.Model.Height / 8), 230, 60, 20);
            child.Apply(new InsertEdit(band, child.Model.Root, child.Model.Root.Children.Count, "Band"));
            await editor.SaveCommand.ExecuteAsync(null);
            check(doc.UndoText == "Undo Update Smart Object", $"the corpus mock-up's warped smart object takes edited contents ({doc.Notice})");
            child.CloseWithoutAsking();
        }
        editor.ActiveDocument = doc;
        string png = Path.Combine(temp, "smart-mockup-art.png");
        File.WriteAllBytes(png, Png(1000, 1600, (x, y) => ((byte)(40 + x / 8), (byte)(30 + y / 10), (byte)(((x / 100) + (y / 100)) % 2 == 0 ? 220 : 60), 255)));
        editor.PickSmartObjectFile = () => Task.FromResult<string?>(png);
        var second = sos.First(l => SmartObjects.Read(l)!.UniqueId != SmartObjects.Read(target)!.UniqueId);
        doc.SelectedLayer = Items(doc).First(i => i.Node == second);
        await editor.ReplaceContentsCommand.ExecuteAsync(null);
        check(doc.UndoText == "Undo Replace Contents", $"the mock-up's second smart object takes replaced contents ({doc.Notice})");
        await doc.SaveAsync(Path.Combine(samples, "smart-mockup-edited-and-replaced-contents.psd"));
        doc.CloseWithoutAsking();
    }
}
