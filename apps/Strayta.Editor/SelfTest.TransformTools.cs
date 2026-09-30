using System.Diagnostics;
using Strayta.Core;
using Strayta.Core.Text;
using Strayta.Editor.Editing;
using Strayta.Editor.ViewModels;
using Strayta.Psd;
using Strayta.Psd.Text;
using Strayta.Rendering.Transforms;
using Strayta.Text;

namespace Strayta.Editor;

/// <summary>
/// Self-test steps for Edit › Transform (distort, perspective, skew, rotate and flip, warp on pixels, smart objects and
/// type), Puppet Warp and Liquify, with WARPBENCH timings on a 4000×3000 document. With STRAYTA_WARP_SAMPLES set to a
/// folder, files for checking in Photoshop are written there (a warped smart object, a distorted layer, warped text).
/// </summary>
internal static partial class SelfTest
{
    private sealed class ScriptedLiquify(Func<LiquifySession, bool> script) : ILiquifyDialogs
    {
        public Task<bool> RunLiquifyAsync(LiquifySession session) => Task.FromResult(script(session));
    }

    /// <summary>A checkerboard layer (with a solid border) covering <paramref name="bounds"/>.</summary>
    private static PixelLayer CheckerLayer(string name, PixelRect bounds, int cell)
    {
        int w = bounds.Width, h = bounds.Height;
        var planes = Enumerable.Range(0, 4).Select(_ => Plane.Create(w, h, 8)).ToArray();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                bool dark = (x / cell + y / cell) % 2 == 0;
                planes[0].Data[i] = (byte)(dark ? 30 : 240);
                planes[1].Data[i] = (byte)(dark ? 60 : 200);
                planes[2].Data[i] = (byte)(dark ? 160 : 40);
                planes[3].Data[i] = 255;
            }
        return new PixelLayer { Name = name, Bounds = bounds, Pixels = new Raster(ColorMode.Rgb, planes[..3], planes[3]) };
    }

    private static async Task<DocumentViewModel> NewDocAsync(EditorViewModel editor, int w, int h)
    {
        var model = LayerFactory.NewDocument(w, h, whiteBackground: true);
        var doc = new DocumentViewModel(model, null, editor);
        editor.Factory.AddDocument(doc);
        editor.ActiveDocument = doc;
        await doc.RenderAsync();
        return doc;
    }

    private static void AddLayer(DocumentViewModel doc, PixelLayer layer)
    {
        doc.Apply(new InsertEdit(layer, doc.Model.Root, doc.Model.Root.Children.Count, "test"));
        doc.SelectedLayer = ItemOf(doc, layer);
    }

    /// <summary>A smart object showing an embedded PNG of <paramref name="cw"/>×<paramref name="ch"/> placed at <paramref name="corners"/>.</summary>
    private static PixelLayer AddSmartObject(DocumentViewModel doc, string name, int cw, int ch, (double X, double Y)[] corners)
    {
        var model = doc.Model;
        string id = PsdSmartObjects.NewId();
        var png = Png(cw, ch, (x, y) => ((x / 8 + y / 8) % 2 == 0 ? ((byte)220, (byte)40, (byte)40, (byte)255) : ((byte)250, (byte)240, (byte)200, (byte)255)));
        var file = model.SourceData as PsdFile ?? new PsdFile { Header = new PsdHeader(1, 3, model.Width, model.Height, 8, ColorMode.Rgb) };
        model.SourceData = PsdSmartObjects.WithLinkedFile(file, PsdSmartObjects.NewEmbedded(id, name + ".png", "png ", png));
        var placed = PsdSmartObjects.NewPlaced(id, PsdSmartObjects.NewId(), cw, ch, corners, 72);
        var record = PsdSmartObjects.WithPlaced(new PsdLayerRecord { Blocks = [new TaggedBlock("8BIM", "PlLd", 0, 0, [])] }, placed);
        var drawn = SmartObjects.Draw(record, (PsdFile)model.SourceData, model, model.Bounds)!.Value;
        var layer = new PixelLayer { Name = name, Bounds = drawn.Bounds, Pixels = drawn.Pixels, SourceData = record };
        layer.Tags.Add("smart-object");
        AddLayer(doc, layer);
        return layer;
    }

    private static async Task RunTransformToolStepsAsync(EditorViewModel editor, Action<bool, string> check)
    {
        string? samples = Environment.GetEnvironmentVariable("STRAYTA_WARP_SAMPLES");
        if (samples is not null) Directory.CreateDirectory(samples);
        var savedDialogs = editor.FilterDialogs;
        var savedLiquify = editor.LiquifyDialogs;
        var dialogs = new ScriptedFilterDialogs { RasterizeAnswer = true };
        editor.FilterDialogs = dialogs;
        try
        {
            await DistortStepsAsync(editor, check, samples);
            await WarpStepsAsync(editor, check, samples);
            await WarpTextStepsAsync(editor, check, samples);
            await PuppetStepsAsync(editor, check);
            await LiquifyStepsAsync(editor, check);
            await ContentAwareScaleStepsAsync(editor, check);
            await WarpBenchmarksAsync(editor);
        }
        finally
        {
            editor.FilterDialogs = savedDialogs;
            editor.LiquifyDialogs = savedLiquify;
        }
    }

    // ---- Distort, perspective, skew, rotate, flip ------------------------------------------------------------

    private static async Task DistortStepsAsync(EditorViewModel editor, Action<bool, string> check, string? samples)
    {
        var doc = await NewDocAsync(editor, 400, 300);
        var layer = CheckerLayer("Checker", new PixelRect(100, 100, 200, 180), 10);
        AddLayer(doc, layer);

        // ⌘-drag the bottom-right corner: distort (the other corners stay).
        check(doc.BeginFreeTransform(), "Free Transform opens on the checker layer");
        var ft = doc.FreeTransform!;
        var br = ft.HandlePosition(TransformHandle.BottomRight);
        ft.BeginDrag(TransformHandle.BottomRight, br.X, br.Y);
        ft.DragTo(br.X + 60, br.Y + 40, shift: false, alt: false, command: true);
        ft.EndDrag();
        var corners = ft.Corners;
        check(ft.IsDistorted && !ft.IsAffine && Math.Abs(corners[2].X - 260) < 1e-6 && Math.Abs(corners[2].Y - 220) < 1e-6 && Math.Abs(corners[0].X - 100) < 1e-6,
            $"⌘-dragging a corner moves only that corner (distort) ({corners[2]})");
        check(await NextFullFrameAsync(doc) && RenderedAt(doc, 240, 205).R < 250 && RenderedAt(doc, 240, 205) != (255, 255, 255),
            "the canvas previews the distorted layer");
        await doc.CommitTransformAsync();
        check(!doc.IsTransforming && layer.Bounds.Right >= 259 && layer.Bounds.Bottom >= 219 && doc.UndoText == "Undo Free Transform",
            $"committing a distort resamples the layer as one step ({layer.Bounds})");
        check(Alpha(layer, 250, 210) > 0.9f && Alpha(layer, 110, 170) > 0.9f && Alpha(layer, 255, 110) < 0.1f,
            "the distorted layer fills the new quadrilateral only");
        doc.Undo();
        check(layer.Bounds == new PixelRect(100, 100, 200, 180), "undo restores the layer");

        // Perspective mode: dragging the top-left corner right pulls the top-right one left, symmetrically.
        check(doc.SetTransformMode(TransformMode.Perspective) && doc.FreeTransform!.Mode == TransformMode.Perspective, "Edit › Transform › Perspective opens the box in Perspective mode");
        ft = doc.FreeTransform!;
        ft.BeginDrag(TransformHandle.TopLeft, 100, 100);
        ft.DragTo(125, 100, shift: false, alt: false);
        ft.EndDrag();
        corners = ft.Corners;
        check(Math.Abs(corners[0].X - 125) < 1e-6 && Math.Abs(corners[1].X - 175) < 1e-6 && Math.Abs(corners[2].X - 200) < 1e-6,
            $"Perspective mirrors the corner on its side ({corners[0].X:F1}, {corners[1].X:F1})");
        ft.BeginDrag(TransformHandle.TopLeft, 125, 100);
        ft.DragTo(260, 100, shift: false, alt: false); // would cross over: refused
        ft.EndDrag();
        check(FreeTransform.IsConvex(ft.Corners), "a drag that would fold the box is refused");
        doc.CancelTransform();

        // Skew mode: a side slides along itself, a parallelogram (affine).
        doc.SetTransformMode(TransformMode.Skew);
        ft = doc.FreeTransform!;
        var top = ft.HandlePosition(TransformHandle.Top);
        ft.BeginDrag(TransformHandle.Top, top.X, top.Y);
        ft.DragTo(top.X + 30, top.Y + 25, shift: false, alt: false);
        ft.EndDrag();
        corners = ft.Corners;
        check(ft.IsDistorted && ft.IsAffine && Math.Abs(corners[0].X - 130) < 1e-6 && Math.Abs(corners[0].Y - 100) < 1e-6,
            "Skew slides the top side along itself: a parallelogram, still affine");
        await doc.CommitTransformAsync();
        check(doc.UndoText == "Undo Skew", "the history names the skew");
        doc.Undo();

        // ⌘⇧ on a side skews in Free Transform too; the modifier can change mid-drag.
        doc.BeginFreeTransform();
        ft = doc.FreeTransform!;
        var right = ft.HandlePosition(TransformHandle.Right);
        ft.BeginDrag(TransformHandle.Right, right.X, right.Y);
        ft.DragTo(right.X + 20, right.Y + 30, shift: true, alt: false, command: true);
        check(ft.IsAffine && ft.IsDistorted && Math.Abs(ft.Corners[1].Y - 130) < 1e-6 && Math.Abs(ft.Corners[1].X - 200) < 1e-6, $"⌘⇧-dragging a side skews it ({ft.Corners[1]})");
        ft.DragTo(right.X + 20, right.Y + 30, shift: false, alt: false, command: false);
        check(!ft.IsDistorted && Math.Abs(ft.WidthPercent - 120) < 1e-6, $"letting go of ⌘ mid-drag turns it back into a stretch ({ft.WidthPercent:F1}%)");
        ft.EndDrag();
        doc.CancelTransform();

        // Rotate 180° and Flip Horizontal without an open transform: one step each.
        var before = layer.Pixels;
        await doc.TransformActionAsync("rotate180");
        check(!doc.IsTransforming && doc.UndoText == "Undo Rotate 180°" && layer.Bounds == new PixelRect(100, 100, 200, 180) && !ReferenceEquals(before, layer.Pixels),
            "Rotate 180° turns the layer in place as one step");
        doc.Undo();
        await doc.TransformActionAsync("flipH");
        check(doc.UndoText == "Undo Flip Horizontal" && layer.Bounds == new PixelRect(100, 100, 200, 180), "Flip Horizontal as one step");
        doc.Undo();
        doc.BeginFreeTransform();
        await doc.TransformActionAsync("rotate90cw");
        check(doc.IsTransforming && Math.Abs(doc.FreeTransform!.Angle - 90) < 1e-9, "Rotate 90° CW inside an open transform turns the box");
        doc.CancelTransform();

        // Distort a smart object: its corners go into the placed-layer data and it is redrawn from its content.
        var so = AddSmartObject(doc, "Placed", 64, 48, [(20, 20), (84, 20), (84, 68), (20, 68)]);
        doc.SetTransformMode(TransformMode.Distort);
        ft = doc.FreeTransform!;
        var c3 = ft.HandlePosition(TransformHandle.BottomRight);
        ft.BeginDrag(TransformHandle.BottomRight, c3.X, c3.Y);
        ft.DragTo(c3.X + 40, c3.Y + 30, shift: false, alt: false);
        ft.EndDrag();
        await doc.CommitTransformAsync();
        var info = SmartObjects.Read(so);
        check(so.Tags.Contains("smart-object") && info is not null && info.Corners[2].X > 110 && Math.Abs(info.Corners[0].X - 20) < 1.5,
            $"a distorted smart object stays a smart object with its corners moved ({info?.Corners[2]})");

        // Type cannot be distorted live: the menu asks to rasterize (the script says OK).
        var font = FontCatalog.System.FallbackPostScriptName ?? "ArialMT";
        var text = TypeLayers.Create(doc.Model, TextLayerData.CreatePoint("Type", new TextStyle { FontPostScriptName = font, FontSize = 40 }, 40, 260), out _);
        AddLayer(doc, text);
        dialogs(editor).RasterizeAnswer = false;
        await editor.TransformModeCommand.ExecuteAsync("Distort");
        check(!doc.IsTransforming && text.Tags.Contains("text") && dialogs(editor).LastPrompt?.Contains("type layer") == true,
            "Distort on type asks to rasterize first; Cancel leaves it alone");
        doc.BeginFreeTransform();
        ft = doc.FreeTransform!;
        var tc = ft.HandlePosition(TransformHandle.BottomRight);
        ft.BeginDrag(TransformHandle.BottomRight, tc.X, tc.Y);
        ft.DragTo(tc.X + 30, tc.Y + 30, shift: false, alt: false, command: true);
        ft.EndDrag();
        check(!ft.IsDistorted, "⌘-drag on type scales instead of distorting");
        doc.CancelTransform();
        dialogs(editor).RasterizeAnswer = true;

        if (samples is not null)
        {
            // A distorted pixel layer, a perspective one and a distorted smart object, for Photoshop.
            var sample = await NewDocAsync(editor, 600, 400);
            var a = CheckerLayer("Distorted", new PixelRect(40, 40, 240, 200), 16);
            AddLayer(sample, a);
            sample.SetTransformMode(TransformMode.Distort);
            sample.FreeTransform!.SetCorners([(40, 60), (250, 30), (230, 230), (60, 190)]);
            await sample.CommitTransformAsync();
            var b = CheckerLayer("Perspective", new PixelRect(320, 60, 560, 300), 16);
            AddLayer(sample, b);
            sample.SetTransformMode(TransformMode.Perspective);
            sample.FreeTransform!.SetCorners([(380, 60), (500, 60), (560, 300), (320, 300)]);
            await sample.CommitTransformAsync();
            var s2 = AddSmartObject(sample, "Smart object in perspective", 80, 60, [(60, 260), (220, 260), (220, 380), (60, 380)]);
            sample.SetTransformMode(TransformMode.Perspective);
            sample.FreeTransform!.SetCorners([(90, 260), (190, 260), (230, 380), (50, 380)]);
            await sample.CommitTransformAsync();
            string path = Path.Combine(samples, "distorted-layers.psd");
            await sample.SaveAsync(path);
            var reopened = PsdFile.Open(path);
            check(reopened.Layers.Any(r => PsdLiveContent.ReadSmartObject(r) is { } p && Math.Abs(p.Corners[0].X - 90) < 1.5),
                "the perspective smart object's corners are saved");
            sample.CloseWithoutAsking();
            _ = s2;
        }
        doc.CloseWithoutAsking();

        static ScriptedFilterDialogs dialogs(EditorViewModel e) => (ScriptedFilterDialogs)e.FilterDialogs!;
    }

    // ---- Warp -----------------------------------------------------------------------------------------------

    private static async Task WarpStepsAsync(EditorViewModel editor, Action<bool, string> check, string? samples)
    {
        var doc = await NewDocAsync(editor, 400, 300);
        var layer = CheckerLayer("Checker", new PixelRect(100, 100, 300, 200), 10);
        AddLayer(doc, layer);

        // Warp on pixels: a style, then a custom drag.
        check(doc.SetTransformMode(TransformMode.Warp) && doc.FreeTransform!.Warp is { } warp && doc.FreeTransform.IsWarping,
            "Edit › Transform › Warp opens the warp on a pixel layer");
        warp = doc.FreeTransform!.Warp!;
        warp.Style = "warpArc";
        warp.Bend = 50;
        check(doc.FreeTransform.HasWarp && warp.IsStyle, "choosing Arc bends the layer");
        check(await NextFullFrameAsync(doc), "the warp preview renders");
        // Arc framed in the box: it bows up, so the bottom's middle rises while its corners stay on the box.
        var place = doc.WarpPlacement();
        var mesh = warp.DocumentMesh(place);
        var bottomMiddle = mesh.Map(100, 100);
        check(bottomMiddle.Y < 195 && Math.Abs(mesh.Map(0, 100).Y - 200) < 1, $"the Arc bows the layer up ({bottomMiddle.Y:F1})");
        warp.Style = "warpCustom";
        check(warp.IsCustom && warp.CustomPoints.Count == 16, "Custom keeps the Arc as an editable mesh");
        var p5 = place.Apply(warp.CustomPoints[5].X, warp.CustomPoints[5].Y);
        check(warp.BeginDrag(place, p5.X, p5.Y, 4), "a control point can be grabbed");
        warp.DragTo(place, p5.X - 20, p5.Y - 20);
        warp.EndDrag();
        check(Math.Abs(place.Apply(warp.CustomPoints[5].X, warp.CustomPoints[5].Y).X - (p5.X - 20)) < 1e-6, "dragging moves the control point");
        warp.GridSize = 3;
        check(warp.CustomPoints.Count == 100 && warp.GridSize == 3, "Grid 3 × 3 splits the mesh into 9 patches");
        var mid = warp.DocumentMesh(place).Map(100, 50);
        check(warp.BeginDrag(place, mid.X, mid.Y, 4), "the surface itself can be grabbed");
        warp.DragTo(place, mid.X + 10, mid.Y + 15);
        warp.EndDrag();
        var moved = warp.DocumentMesh(place).Map(100, 50);
        check(Math.Abs(moved.X - mid.X - 10) < 0.5 && Math.Abs(moved.Y - mid.Y - 15) < 0.5, "the grabbed point follows the pointer");
        // Toggle back to Free Transform: the warp stays, the box scales it.
        await editor.ToggleWarpCommand.ExecuteAsync(null);
        check(doc.FreeTransform.Mode == TransformMode.Free && doc.FreeTransform.HasWarp, "the options bar's toggle goes back to Free Transform, keeping the warp");
        doc.FreeTransform.WidthPercent = 80;
        var pixels = layer.Pixels;
        await doc.CommitTransformAsync();
        check(!ReferenceEquals(pixels, layer.Pixels) && doc.UndoText == "Undo Warp" && !doc.IsTransforming, "the warp is baked into the pixels as one step");
        doc.Undo();
        check(ReferenceEquals(pixels, layer.Pixels), "undo takes the warp back");
        doc.SetTransformMode(TransformMode.Warp);
        doc.FreeTransform!.Warp!.Style = "warpFlag";
        doc.CancelTransform();
        check(ReferenceEquals(pixels, layer.Pixels) && !doc.IsTransforming, "Esc leaves the layer as it was");

        // Warp on a smart object: the warp goes into its placed-layer data and it stays a smart object.
        var so = AddSmartObject(doc, "Smart", 80, 60, [(20, 20), (100, 20), (100, 80), (20, 80)]);
        doc.SetTransformMode(TransformMode.Warp);
        warp = doc.FreeTransform!.Warp!;
        check(Math.Abs(warp.Width - 80) < 1e-9 && Math.Abs(warp.Height - 60) < 1e-9, "a smart object warps in its placed size");
        warp.Style = "warpFlag";
        warp.Bend = 40;
        check(await NextFullFrameAsync(doc), "the smart object's warp previews from its content");
        await doc.CommitTransformAsync();
        var info = SmartObjects.Read(so);
        check(so.Tags.Contains("smart-object") && info?.Warp is { Style: "warpFlag", Value: 40 }, $"the Flag warp is written into the smart object ({info?.Warp?.Style})");
        doc.SetTransformMode(TransformMode.Warp);
        warp = doc.FreeTransform!.Warp!;
        check(warp.Style == "warpFlag" && Math.Abs(warp.Bend - 40) < 1e-9, "warping it again starts from its warp");
        warp.GridSize = 3;
        var sp = doc.WarpPlacement();
        var q = warp.DocumentMesh(sp).Map(40, 30);
        warp.BeginDrag(sp, q.X, q.Y, 4);
        warp.DragTo(sp, q.X + 8, q.Y - 6);
        warp.EndDrag();
        await doc.CommitTransformAsync();
        info = SmartObjects.Read(so);
        check(info?.Warp is { Style: "warpCustom", Rows: 10, Columns: 10 } && info.Warp.SlicesX?.Count == 4, "a split custom warp is written as a quilt warp");
        string path = Path.Combine(Path.GetTempPath(), $"warp-selftest-{Guid.NewGuid():N}.psd");
        await doc.SaveAsync(path);
        var reopened = PsdFile.Open(path);
        var saved = reopened.Layers.Select(PsdLiveContent.ReadSmartObject).FirstOrDefault(s => s is not null);
        check(saved?.Warp is { Style: "warpCustom", Rows: 10 }, "the quilt warp is saved");
        File.Delete(path);
        doc.CloseWithoutAsking();

        if (samples is not null)
        {
            var sample = await NewDocAsync(editor, 900, 420);
            (string Name, Action<WarpTransform> Set)[] styles =
            [
                ("Arc 50", w => { w.Style = "warpArc"; w.Bend = 50; }),
                ("Flag 50", w => { w.Style = "warpFlag"; w.Bend = 50; }),
                ("Bulge 50 vertical", w => { w.Style = "warpBulge"; w.Bend = 50; w.VerticalOrientation = true; }),
                ("Twist 40", w => { w.Style = "warpTwist"; w.Bend = 40; }),
                ("Custom", w => { w.Style = "warpCustom"; var pts = w.CustomPoints.ToArray(); pts[5] = (pts[5].X - 15, pts[5].Y - 15); pts[10] = (pts[10].X + 10, pts[10].Y + 20); w.SetCustomPoints(pts); }),
                ("Custom 3 x 3", w => { w.GridSize = 3; var pts = w.CustomPoints.ToArray(); pts[44] = (pts[44].X + 12, pts[44].Y - 12); w.SetCustomPoints(pts); }),
            ];
            for (int i = 0; i < styles.Length; i++)
            {
                double x = 40 + (i % 3) * 290, y = 30 + (i / 3) * 200;
                var s = AddSmartObject(sample, styles[i].Name, 96, 64, [(x, y), (x + 192, y), (x + 192, y + 128), (x, y + 128)]);
                sample.SetTransformMode(TransformMode.Warp);
                styles[i].Set(sample.FreeTransform!.Warp!);
                await sample.CommitTransformAsync();
                _ = s;
            }
            var pixelWarp = CheckerLayer("Pixels (baked arc)", new PixelRect(640, 330, 860, 410), 10);
            AddLayer(sample, pixelWarp);
            sample.SetTransformMode(TransformMode.Warp);
            sample.FreeTransform!.Warp!.Style = "warpArc";
            await sample.CommitTransformAsync();
            string file = Path.Combine(samples, "warped-smart-objects.psd");
            await sample.SaveAsync(file);
            check(File.Exists(file), $"a sample with warped smart objects is written ({file})");
            sample.CloseWithoutAsking();
        }
    }

    // ---- Warp Text ------------------------------------------------------------------------------------------

    private static async Task WarpTextStepsAsync(EditorViewModel editor, Action<bool, string> check, string? samples)
    {
        var doc = await NewDocAsync(editor, 500, 300);
        var font = FontCatalog.System.FallbackPostScriptName ?? "ArialMT";
        var text = TypeLayers.Create(doc.Model, TextLayerData.CreatePoint("Warp Text", new TextStyle { FontPostScriptName = font, FontSize = 60 }, 60, 160), out _);
        AddLayer(doc, text);
        var before = text.Bounds;
        check(doc.SetTransformMode(TransformMode.Warp) && doc.FreeTransform!.Warp is { StylesOnly: true } warp && warp.Style == "warpArc",
            "Warp on type is Warp Text: styles only, starting from Arc");
        warp = doc.FreeTransform!.Warp!;
        warp.Bend = 60;
        check(await NextFullFrameAsync(doc), "warped type previews");
        await doc.CommitTransformAsync();
        var record = (PsdLayerRecord)text.SourceData!;
        check(text.Tags.Contains("text") && PsdTypeWarp.Read(record) is { Style: "warpArc", Value: 60 } && TypeLayers.Read(text)?.Text == "Warp Text",
            "the warp is written into the type data and the layer stays type");
        check(text.Bounds.Top < before.Top - 5 || text.Bounds.Height > before.Height + 5, $"the type is drawn bent ({before} → {text.Bounds})");
        doc.Undo();
        check(PsdTypeWarp.Read((PsdLayerRecord)text.SourceData!) is null && text.Bounds == before, "undo takes the warp back");
        // A later Free Transform keeps the warp and redraws it.
        doc.Redo();
        doc.BeginFreeTransform();
        doc.FreeTransform!.Angle = 10;
        await doc.CommitTransformAsync();
        check(PsdTypeWarp.Read((PsdLayerRecord)text.SourceData!) is { Style: "warpArc" } && text.Tags.Contains("text"), "turning warped type keeps its warp");

        if (samples is not null)
        {
            var sample = await NewDocAsync(editor, 700, 420);
            (string Text, string Style, double Bend, bool Vertical, double Y)[] items =
            [
                ("Arc 50", "warpArc", 50, false, 110), ("Flag 40", "warpFlag", 40, false, 230), ("Bulge 50", "warpBulge", 50, false, 350),
            ];
            foreach (var (t, style, bend, vertical, y) in items)
            {
                var layer = TypeLayers.Create(sample.Model, TextLayerData.CreatePoint(t, new TextStyle { FontPostScriptName = font, FontSize = 64 }, 80, y), out _);
                AddLayer(sample, layer);
                sample.SetTransformMode(TransformMode.Warp);
                var w = sample.FreeTransform!.Warp!;
                w.Style = style;
                w.Bend = bend;
                w.VerticalOrientation = vertical;
                await sample.CommitTransformAsync();
            }
            string file = Path.Combine(samples, "warped-text.psd");
            await sample.SaveAsync(file);
            check(File.Exists(file), $"a sample with warped type is written ({file})");
            sample.CloseWithoutAsking();
        }
        doc.CloseWithoutAsking();
    }

    // ---- Puppet Warp -------------------------------------------------------------------------------------

    private static async Task PuppetStepsAsync(EditorViewModel editor, Action<bool, string> check)
    {
        var doc = await NewDocAsync(editor, 400, 300);
        var bar = CheckerLayer("Bar", new PixelRect(50, 130, 350, 170), 10);
        AddLayer(doc, bar);
        await editor.PuppetWarpCommand.ExecuteAsync(null);
        check(doc.PuppetWarp is { } s && s.Mesh.VertexCount > 20, $"Edit › Puppet Warp builds a mesh over the layer ({doc.PuppetWarp?.Mesh.VertexCount} points)");
        var puppet = doc.PuppetWarp!;
        check(puppet.Press(60, 150, 3, alt: false) && puppet.Pins.Count == 1, "clicking the mesh adds a pin");
        puppet.EndDrag();
        check(puppet.Press(200, 150, 3, alt: false) && puppet.Pins.Count == 2, "a second pin");
        puppet.EndDrag();
        check(!puppet.Press(200, 20, 3, alt: false) && puppet.Pins.Count == 2, "clicking off the mesh adds nothing");
        check(puppet.Press(340, 150, 3, alt: false), "a third pin, dragged at once");
        for (int i = 1; i <= 20; i++) puppet.DragTo(puppet.Pins[2].X, 150 + i * 4);
        puppet.EndDrag();
        var end = puppet.Pins[2];
        int v = end.Vertex;
        check(Math.Abs(puppet.Y[v] - end.Y) < 1e-6 && puppet.Y[v] > 225, "the dragged pin's point follows it");
        var pinned = puppet.Pins[0];
        check(Math.Abs(puppet.X[pinned.Vertex] - pinned.X) < 1e-6 && Math.Abs(puppet.Y[pinned.Vertex] - pinned.Y) < 1e-6, "the other pins hold");
        check(await NextFullFrameAsync(doc), "Puppet Warp previews on the canvas");
        check(puppet.Press(puppet.Pins[1].X, puppet.Pins[1].Y, 3, alt: true) && puppet.Pins.Count == 2, "Option-click removes a pin");
        var pixels = bar.Pixels;
        await editor.CommitPuppetWarpCommand.ExecuteAsync(null);
        check(!doc.IsPuppetWarping && !ReferenceEquals(pixels, bar.Pixels) && doc.UndoText == "Undo Puppet Warp" && bar.Bounds.Bottom > 215,
            $"Enter bakes the warp as one step ({bar.Bounds}, {doc.LastPuppetCommitMs:F0} ms)");
        doc.Undo();
        check(ReferenceEquals(pixels, bar.Pixels), "undo restores the layer");
        await doc.BeginPuppetWarpAsync();
        doc.PuppetWarp!.AddPin(60, 150);
        doc.CancelPuppetWarp();
        check(!doc.IsPuppetWarping && ReferenceEquals(pixels, bar.Pixels), "Esc cancels Puppet Warp");
        doc.CloseWithoutAsking();
    }

    // ---- Liquify ---------------------------------------------------------------------------------------------

    private static async Task LiquifyStepsAsync(EditorViewModel editor, Action<bool, string> check)
    {
        var doc = await NewDocAsync(editor, 400, 300);
        var layer = CheckerLayer("Checker", new PixelRect(100, 50, 300, 250), 10);
        AddLayer(doc, layer);
        LiquifySession? seen = null;
        editor.LiquifyDialogs = new ScriptedLiquify(s =>
        {
            seen = s;
            s.Tool = LiquifyTool.ForwardWarp;
            s.BrushSize = 80;
            s.BrushPressure = 100;
            s.BeginStroke(200, 150);
            for (int i = 1; i <= 10; i++) s.StrokeTo(200 + i * 3, 150);
            s.EndStroke();
            s.Tool = LiquifyTool.TwirlClockwise;
            s.BeginStroke(150, 100);
            for (int i = 0; i < 10; i++) s.Tick();
            s.EndStroke();
            return true;
        });
        var pixels = layer.Pixels;
        await editor.LiquifyCommand.ExecuteAsync(null);
        check(seen is { } session && !session.Field.IsIdentity, "Filter › Liquify runs the workspace, and strokes distort the field");
        check(!ReferenceEquals(pixels, layer.Pixels) && doc.UndoText == "Undo Liquify", $"OK applies Liquify as one step ({doc.LastLiquifyApplyMs:F0} ms)");
        doc.Undo();
        check(ReferenceEquals(pixels, layer.Pixels), "undo takes Liquify back");

        // Inside the workspace: undo a stroke, Restore All, the freeze mask.
        var s2 = doc.BeginLiquify()!;
        s2.BeginStroke(200, 150);
        s2.StrokeTo(230, 150);
        s2.EndStroke();
        s2.Undo();
        check(s2.Field.IsIdentity, "⌘Z in Liquify undoes the last stroke");
        s2.Tool = LiquifyTool.FreezeMask;
        s2.BrushSize = 400;
        s2.BrushPressure = 100;
        s2.BrushDensity = 100;
        s2.BeginStroke(200, 150);
        for (int i = 0; i < 5; i++) s2.Tick();
        s2.StrokeTo(201, 150);
        s2.EndStroke();
        s2.Tool = LiquifyTool.ForwardWarp;
        s2.BrushSize = 60;
        s2.BeginStroke(200, 150);
        s2.StrokeTo(230, 150);
        s2.EndStroke();
        check(s2.Field.IsIdentity, "frozen areas do not move");
        s2.ThawAll();
        s2.BeginStroke(200, 150);
        s2.StrokeTo(230, 150);
        s2.EndStroke();
        s2.RestoreAll();
        check(s2.Field.IsIdentity, "Restore All undoes every distortion");
        editor.LiquifyDialogs = new ScriptedLiquify(_ => false);
        await editor.LiquifyCommand.ExecuteAsync(null);
        check(ReferenceEquals(pixels, layer.Pixels), "Cancel leaves the layer as it was");
        doc.CloseWithoutAsking();
    }

    // ---- Content-Aware Scale -----------------------------------------------------------------------------------

    private static async Task ContentAwareScaleStepsAsync(EditorViewModel editor, Action<bool, string> check)
    {
        var doc = await NewDocAsync(editor, 400, 300);
        // A plain field with a checkered block: narrowing carves the field and keeps the block.
        var layer = SolidLayer("Scene", new PixelRect(0, 50, 300, 250), 90, 140, 200);
        var block = CheckerLayer("Block", new PixelRect(120, 100, 180, 160), 6).Pixels!;
        for (int y = 0; y < 60; y++)
            for (int x = 0; x < 60; x++)
            {
                int d = (y + 50) * 300 + x + 120, s = y * 60 + x;
                for (int c = 0; c < 3; c++) layer.Pixels!.ColorPlanes[c].Data[d] = block.ColorPlanes[c].Data[s];
            }
        AddLayer(doc, layer);
        await editor.ContentAwareScaleCommand.ExecuteAsync(null);
        check(doc.IsContentAwareScaling && doc.FreeTransform!.ModeName == "Content-Aware Scale", "Edit › Content-Aware Scale opens its box");
        var ft = doc.FreeTransform!;
        ft.BeginDrag(TransformHandle.Rotate, 350, 20);
        ft.DragTo(380, 150, false, false);
        ft.EndDrag();
        check(ft.Angle == 0, "the box does not turn");
        ft.WidthPercent = 60;
        check(await NextFullFrameAsync(doc), "the carve previews on the canvas");
        await doc.CommitTransformAsync();
        check(!doc.IsTransforming && layer.Bounds.Width == 180 && layer.Bounds.Height == 200 && doc.UndoText == "Undo Content-Aware Scale",
            $"Enter carves the layer to 60% as one step ({layer.Bounds}, {doc.LastContentAwareScaleMs:F0} ms)");
        // The block keeps its 60 columns of checkers: count the color changes along its middle row.
        int changes = 0;
        for (int x = layer.Bounds.Left + 1; x < layer.Bounds.Right; x++)
            if (Math.Abs(Rgba(layer, x, 130).R - Rgba(layer, x - 1, 130).R) > 100) changes++;
        check(changes >= 9, $"the busy block survives the carve ({changes} edges along a row)");
        doc.Undo();
        check(layer.Bounds == new PixelRect(0, 50, 300, 250), "undo restores the layer");

        // Protect the selection: with the block selected, it stays whole even when carving deep.
        doc.SetSelection(Core.Selection.SelectionMask.Rectangle(new PixelRect(110, 90, 190, 170), doc.Model.Bounds), "Rectangular Marquee");
        doc.BeginContentAwareScale();
        check(doc.ProtectNames.Contains("Selection"), "Protect offers the selection");
        doc.ProtectIndex = doc.ProtectNames.ToList().IndexOf("Selection");
        doc.FreeTransform!.WidthPercent = 40;
        await doc.CommitTransformAsync();
        changes = 0;
        for (int x = layer.Bounds.Left + 1; x < layer.Bounds.Right; x++)
            if (Math.Abs(Rgba(layer, x, 130).R - Rgba(layer, x - 1, 130).R) > 100) changes++;
        check(layer.Bounds.Width == 120 && changes >= 9, $"a protected block survives a deep carve ({changes} edges)");
        doc.CloseWithoutAsking();
    }

    // ---- Timings ---------------------------------------------------------------------------------------------

    /// <summary>
    /// WARPBENCH lines on a generated 4000×3000 document: preview frames while dragging a distort corner, a custom warp
    /// point, a smart object's warp and a puppet pin (120 Hz input for a second each), commit times, and Liquify stroke
    /// and apply times. No assertions: timings depend on the machine's load.
    /// </summary>
    private static async Task WarpBenchmarksAsync(EditorViewModel editor)
    {
        var doc = await NewDocAsync(editor, 4000, 3000);
        var layer = CheckerLayer("Big", new PixelRect(400, 300, 3600, 2700), 40);
        AddLayer(doc, layer);
        await Task.Delay(300);

        async Task<(int Frames, double Median, double P90)> Drag(Action<int> step)
        {
            var times = new List<double>();
            var clock = Stopwatch.StartNew();
            double last = 0;
            void OnFrame(bool full)
            {
                if (full) return;
                times.Add(clock.Elapsed.TotalMilliseconds - last);
                last = clock.Elapsed.TotalMilliseconds;
            }
            doc.FrameDisplayed += OnFrame;
            for (int i = 1; i <= 120; i++)
            {
                step(i);
                await Task.Delay(8);
            }
            await Task.Delay(100);
            doc.FrameDisplayed -= OnFrame;
            times.Sort();
            return (times.Count, times.Count > 0 ? times[times.Count / 2] : double.NaN, times.Count > 0 ? times[(int)(times.Count * 0.9)] : double.NaN);
        }
        async Task<double> Commit(Func<Task> commit)
        {
            var c = Stopwatch.StartNew();
            await commit();
            return c.Elapsed.TotalMilliseconds;
        }
        void Report(string what, (int Frames, double Median, double P90) r, double commitMs) =>
            Console.WriteLine($"WARPBENCH {what}: frames={r.Frames} in ~1.1 s, median-frame={r.Median:F0}ms p90={r.P90:F0}ms, commit={commitMs:F0}ms");

        // Distort: ⌘-drag a corner.
        doc.BeginFreeTransform();
        var ft = doc.FreeTransform!;
        var br = ft.HandlePosition(TransformHandle.BottomRight);
        ft.BeginDrag(TransformHandle.BottomRight, br.X, br.Y);
        var r1 = await Drag(i => ft.DragTo(br.X + 3 * i, br.Y - 2 * i, false, false, true));
        ft.EndDrag();
        Report("distort 3200x2400 layer", r1, await Commit(doc.CommitTransformAsync));
        doc.Undo();

        // Custom warp: drag a control point.
        doc.SetTransformMode(TransformMode.Warp);
        var warp = doc.FreeTransform!.Warp!;
        warp.Style = "warpCustom";
        var place = doc.WarpPlacement();
        var p = place.Apply(warp.CustomPoints[5].X, warp.CustomPoints[5].Y);
        warp.BeginDrag(place, p.X, p.Y, 20);
        var r2 = await Drag(i => warp.DragTo(place, p.X - 4 * i, p.Y - 3 * i));
        warp.EndDrag();
        Report("warp 3200x2400 layer (custom point)", r2, await Commit(doc.CommitTransformAsync));
        doc.Undo();

        // Style slider: Bend from 0 to 100 (a Properties-like drag).
        doc.SetTransformMode(TransformMode.Warp);
        warp = doc.FreeTransform!.Warp!;
        warp.Style = "warpArc";
        var r3 = await Drag(i => warp.Bend = i * 100 / 120.0);
        Report("warp style bend drag", r3, 0);
        doc.CancelTransform();

        // Smart object warp (drawn from its 2000×1500 content).
        var so = AddSmartObject(doc, "Big smart object", 2000, 1500, [(500, 500), (3500, 500), (3500, 2750), (500, 2750)]);
        await Task.Delay(200);
        doc.SetTransformMode(TransformMode.Warp);
        warp = doc.FreeTransform!.Warp!;
        warp.Style = "warpCustom";
        place = doc.WarpPlacement();
        p = place.Apply(warp.CustomPoints[6].X, warp.CustomPoints[6].Y);
        warp.BeginDrag(place, p.X, p.Y, 20);
        var r4 = await Drag(i => warp.DragTo(place, p.X + 4 * i, p.Y - 3 * i));
        warp.EndDrag();
        Report("smart object warp 2000x1500 content", r4, await Commit(doc.CommitTransformAsync));
        doc.Undo();
        doc.Undo(); // the smart object
        doc.SelectedLayer = ItemOf(doc, layer);

        // Puppet Warp: drag a pin.
        await doc.BeginPuppetWarpAsync();
        var puppet = doc.PuppetWarp!;
        puppet.AddPin(500, 1500);
        puppet.AddPin(2000, 1500);
        puppet.Press(3500, 1500, 60, false);
        var solve = new List<double>();
        var r5 = await Drag(i =>
        {
            var c = Stopwatch.StartNew();
            puppet.DragTo(3500, 1500 + 8 * i);
            solve.Add(c.Elapsed.TotalMilliseconds);
        });
        puppet.EndDrag();
        solve.Sort();
        Console.WriteLine($"WARPBENCH puppet mesh {puppet.Mesh.VertexCount} points, solve per move median={solve[solve.Count / 2]:F2}ms");
        Report("puppet warp 3200x2400 layer", r5, await Commit(doc.CommitPuppetWarpAsync));
        doc.Undo();

        // Liquify: stroke speed at preview resolution, and the full-resolution apply.
        var session = doc.BeginLiquify()!;
        session.BrushSize = 400;
        var strokes = Stopwatch.StartNew();
        session.BeginStroke(2000, 1500);
        for (int i = 1; i <= 60; i++) session.StrokeTo(2000 + i * 10, 1500 + i * 3);
        session.EndStroke();
        double perMove = strokes.Elapsed.TotalMilliseconds / 60;
        await doc.ApplyLiquifyAsync(session);
        Console.WriteLine($"WARPBENCH liquify preview {session.PreviewWidth}x{session.PreviewHeight}: {perMove:F1} ms per stroke move (field + preview), apply={doc.LastLiquifyApplyMs:F0}ms");
        doc.Undo();

        // Content-Aware Scale: narrow the layer by a quarter with a side drag, then apply.
        doc.SelectedLayer = ItemOf(doc, layer);
        doc.BeginContentAwareScale();
        var cas = doc.FreeTransform!;
        var side = cas.HandlePosition(TransformHandle.Right);
        cas.BeginDrag(TransformHandle.Right, side.X, side.Y);
        var r6 = await Drag(i => cas.DragTo(side.X - 800.0 * i / 120, side.Y, false, false));
        cas.EndDrag();
        Report("content-aware scale 3200x2400 layer to 75% width", r6, await Commit(doc.CommitTransformAsync));
        doc.CloseWithoutAsking();
        _ = so;
    }
}
