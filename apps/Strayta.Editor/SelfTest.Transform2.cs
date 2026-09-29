using System.Buffers.Binary;
using System.Text;
using Strayta.Core;
using Strayta.Core.Selection;
using Strayta.Editor.Controls;
using Strayta.Editor.Editing;
using Strayta.Editor.ViewModels;
using Strayta.Psd;
using Strayta.Psd.Descriptors;
using Strayta.Rendering.Transforms;

namespace Strayta.Editor;

/// <summary>
/// Self-test steps for live layers through crops, Image Size and Free Transform, Scale Styles, selections following
/// the canvas, the crop options (W × H × Resolution, overlays, "Crop the image?", Content-Aware) and Perspective Crop.
/// With STRAYTA_TRANSFORM_SAMPLES set to a folder, the results are also saved there for checking in Photoshop.
/// </summary>
internal static partial class SelfTest
{
    private const int ContentW = 80, ContentH = 40; // the smart object's own pixels: 1-pixel black and white stripes
    private static readonly (double X, double Y)[] PlacedCorners = [(100, 60), (140, 60), (140, 80), (100, 80)]; // shown at half size

    private static Raster Stripes(int w, int h, int period)
    {
        var planes = Enumerable.Range(0, 3).Select(_ => Plane.Create(w, h, 8)).ToArray();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                foreach (var p in planes) p.Data[y * w + x] = (byte)(x / period % 2 == 0 ? 0 : 255);
        return new Raster(ColorMode.Rgb, planes, null);
    }

    private static byte[] Versioned(Descriptor d) => DescriptorWriter.WriteVersioned(d);

    private static ListValue DoubleList(IEnumerable<double> values) => new(values.Select(v => (DescriptorValue)new DoubleValue(v)).ToList());

    private static TaggedBlock Tagged(string key, byte[] data) => new("8BIM", key, 0, data.Length, data);

    /// <summary>A placed (smart object) layer's 'SoLd' and 'PlLd' blocks for the embedded file <paramref name="id"/>.</summary>
    private static (byte[] SoLd, byte[] PlLd) PlacedBlocks(string id)
    {
        var corners = PlacedCorners.SelectMany(c => new[] { c.X, c.Y }).ToArray();
        var warp = new Descriptor
        {
            ClassId = "warp",
            Items =
            [
                new("warpStyle", new EnumValue("warpStyle", "warpNone")), new("warpValue", new DoubleValue(0)),
                new("warpPerspective", new DoubleValue(0)), new("warpPerspectiveOther", new DoubleValue(0)),
                new("warpRotate", new EnumValue("Ornt", "Hrzn")),
                new("bounds", new ObjectValue(new Descriptor
                {
                    ClassId = "classFloatRect",
                    Items = [new("Top ", new DoubleValue(0)), new("Left", new DoubleValue(0)), new("Btom", new DoubleValue(ContentH)), new("Rght", new DoubleValue(ContentW))],
                })),
                new("uOrder", new IntegerValue(4)), new("vOrder", new IntegerValue(4)),
            ],
        };
        var soLd = new Descriptor
        {
            ClassId = "null",
            Items =
            [
                new("Idnt", new TextValue(id)), new("placed", new TextValue(id)), new("PgNm", new IntegerValue(1)),
                new("totalPages", new IntegerValue(1)), new("Crop", new IntegerValue(1)), new("frameCount", new IntegerValue(1)),
                new("Annt", new IntegerValue(16)), new("Type", new IntegerValue(2)),
                new("Trnf", DoubleList(corners)), new("nonAffineTransform", DoubleList(corners)), new("warp", new ObjectValue(warp)),
                new("Sz  ", new ObjectValue(new Descriptor { ClassId = "Pnt ", Items = [new("Wdth", new DoubleValue(ContentW)), new("Hght", new DoubleValue(ContentH))] })),
                new("Rslt", new UnitFloatValue("#Rsl", 72)),
            ],
        };
        var pl = new MemoryStream();
        pl.Write("plcL"u8);
        pl.Write([0, 0, 0, 3]);
        pl.WriteByte((byte)id.Length);
        pl.Write(Encoding.ASCII.GetBytes(id));
        pl.Write([0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 16, 0, 0, 0, 2]);
        Span<byte> d = stackalloc byte[8];
        foreach (var v in corners)
        {
            BinaryPrimitives.WriteDoubleBigEndian(d, v);
            pl.Write(d);
        }
        pl.Write([0, 0, 0, 0, 0, 0, 0, 16]);
        pl.Write(DescriptorWriter.Write(warp));
        return ([.. "soLD"u8, 0, 0, 0, 4, .. Versioned(soLd)], pl.ToArray());
    }

    /// <summary>A 'lnk2' block embedding <paramref name="file"/> (a PSD) as entry <paramref name="id"/> (version 7 layout).</summary>
    private static byte[] LinkedFiles(string id, byte[] file)
    {
        var e = new MemoryStream();
        e.Write("liFD"u8);
        e.Write([0, 0, 0, 7]);
        e.WriteByte((byte)id.Length);
        e.Write(Encoding.ASCII.GetBytes(id));
        var name = Encoding.BigEndianUnicode.GetBytes("Stripes.psd\0");
        Span<byte> n = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(n, name.Length / 2);
        e.Write(n[..4]);
        e.Write(name);
        e.Write("8BPS8BIM"u8);
        BinaryPrimitives.WriteInt64BigEndian(n, file.Length);
        e.Write(n);
        e.WriteByte(0); // no open-file descriptor
        e.Write(file);
        e.Write([0, 0, 0, 0]); // child document ID: empty Unicode string
        e.Write(new byte[8]); // asset modification time
        e.WriteByte(0); // not locked
        var body = e.ToArray();
        var block = new byte[8 + ((body.Length + 3) & ~3)];
        BinaryPrimitives.WriteInt64BigEndian(block, body.Length);
        body.CopyTo(block, 8);
        return block;
    }

    /// <summary>
    /// A 400×300 document with a smart object (80×40 stripes shown at half size, so its layer pixels are gray), a
    /// solid color fill at 30%, a layer with a drop shadow and, for the self-test only, a type layer with a minimal
    /// 'TySh' (no text engine data, so not for Photoshop).
    /// </summary>
    private static string WriteLiveSample(string path, bool withType)
    {
        const string id = "5f1e2d3c-0000-4000-8000-00000000c0de";
        var content = new Document(ContentW, ContentH, ColorMode.Rgb, 8) { Composite = Stripes(ContentW, ContentH, 1) };
        content.Root.Add(new PixelLayer { Name = "Stripes", Bounds = content.Bounds, Pixels = content.Composite });
        var embedded = new MemoryStream();
        PsdWriter.Write(content, embedded);

        var doc = LayerFactory.NewDocument(400, 300, whiteBackground: true);
        doc.SourceData = new PsdFile
        {
            Header = new PsdHeader(1, 3, 400, 300, 8, ColorMode.Rgb),
            GlobalBlocks = [Tagged("lnk2", LinkedFiles(id, embedded.ToArray()))],
        };
        var fillColor = new Descriptor { ClassId = "null", Items = [new("Clr ", new ObjectValue(new Descriptor { ClassId = "RGBC", Items = [new("Rd  ", new DoubleValue(0)), new("Grn ", new DoubleValue(160)), new("Bl  ", new DoubleValue(90))] }))] };
        var fill = SolidLayer("Color Fill 1", doc.Bounds, 0, 160, 90);
        fill.Pixels = new Raster(ColorMode.Rgb, fill.Pixels!.ColorPlanes, null);
        fill.Opacity = 0.3f;
        fill.SourceData = new PsdLayerRecord { Blocks = [Tagged("SoCo", Versioned(fillColor))] };
        doc.Root.Add(fill);

        var (soLd, plLd) = PlacedBlocks(id);
        var half = Stripes(40, 20, 1);
        foreach (var p in half.ColorPlanes) Array.Fill(p.Data, (byte)128); // what Photoshop shows: the stripes averaged
        doc.Root.Add(new PixelLayer
        {
            Name = "Stripes (smart object)", Bounds = new PixelRect(100, 60, 140, 80), Pixels = half,
            SourceData = new PsdLayerRecord { Blocks = [Tagged("PlLd", plLd), Tagged("SoLd", soLd)] },
        });

        var shadowed = SolidLayer("Shadowed", new PixelRect(220, 150, 320, 220), 200, 40, 40);
        shadowed.Effects = new LayerEffects([new DropShadowEffect { Distance = 10, Size = 6, Opacity = 0.75f, Angle = 120 }]);
        doc.Root.Add(shadowed);

        if (withType)
        {
            var t = new MemoryStream();
            t.Write([0, 1]);
            Span<byte> d = stackalloc byte[8];
            foreach (double v in new double[] { 1, 0, 0, 1, 50, 200 })
            {
                BinaryPrimitives.WriteDoubleBigEndian(d, v);
                t.Write(d);
            }
            t.Write([0, 50]);
            t.Write(Versioned(new Descriptor { ClassId = "TxLr", Items = [new("Txt ", new TextValue("Hi"))] }));
            t.Write([0, 1]);
            t.Write(Versioned(new Descriptor { ClassId = "warp", Items = [new("warpStyle", new EnumValue("warpStyle", "warpNone"))] }));
            t.Write(new byte[16]);
            var type = SolidLayer("Hi", new PixelRect(50, 180, 90, 205), 0, 0, 0);
            type.SourceData = new PsdLayerRecord { Blocks = [Tagged("TySh", t.ToArray())] };
            doc.Root.Add(type);
        }
        PsdWriter.Save(doc, path);
        return path;
    }

    /// <summary>Saves a sample for Photoshop without the self-test's type layer, whose minimal 'TySh' only Strayta reads.</summary>
    private static async Task SaveSampleAsync(DocumentViewModel doc, LayerNode type, string path)
    {
        var parent = type.Parent!;
        int index = parent.IndexOf(type);
        parent.Remove(type);
        try
        {
            await doc.SaveAsync(path);
        }
        finally
        {
            parent.Insert(index, type);
        }
    }

    private static double TypeX(LayerNode node) =>
        BinaryPrimitives.ReadDoubleBigEndian(((PsdLayerRecord)node.SourceData!).FindBlock("TySh")!.Data!.AsSpan(34));

    /// <summary>Contrast of the smart object's pixels: stripes redrawn from its own content, or the gray of resampled pixels.</summary>
    private static int StripeContrast(PixelLayer layer)
    {
        var px = layer.Pixels!;
        int w = layer.Bounds.Width, y = layer.Bounds.Height / 2, min = 255, max = 0;
        for (int x = w / 4; x < w * 3 / 4; x++)
        {
            int v = px.ColorPlanes[0].Data[y * w + x];
            min = Math.Min(min, v);
            max = Math.Max(max, v);
        }
        return max - min;
    }

    private static async Task RunTransformGapStepsAsync(EditorViewModel editor, Action<bool, string> check)
    {
        string path = Path.Combine(Path.GetTempPath(), $"transform2-live-{Guid.NewGuid():N}.psd");
        string? samples = Environment.GetEnvironmentVariable("STRAYTA_TRANSFORM_SAMPLES");
        var savedHook = DocumentViewModel.ContentAwareCropFill;
        try
        {
            WriteLiveSample(path, withType: true);
            var model = PsdFile.OpenForEditing(path);
            var doc = new DocumentViewModel(model, path, editor);
            editor.Factory.AddDocument(doc);
            editor.ActiveDocument = doc;
            await doc.RenderAsync();
            var so = model.Root.Descendants().OfType<PixelLayer>().Single(l => l.Tags.Contains("smart-object"));
            var fill = model.Root.Descendants().OfType<PixelLayer>().Single(l => l.Tags.Contains("fill"));
            var type = model.Root.Descendants().OfType<PixelLayer>().Single(l => l.Tags.Contains("text"));
            var shadowed = model.Root.Descendants().OfType<PixelLayer>().Single(l => l.Name == "Shadowed");
            check(PsdLiveContent.ReadSmartObject((PsdLayerRecord)so.SourceData!) is { Warped: false } && StripeContrast(so) < 10,
                "a smart object with embedded content opens (its layer pixels are the stripes averaged to gray)");

            // ---- Free Transform keeps it live and redraws it sharply from its content ---------------------
            doc.SelectedLayer = doc.Layers.SelectMany(l => l.SelfAndDescendants()).First(i => i.Node == so);
            check(doc.BeginFreeTransform(), "Free Transform opens on a smart object");
            doc.FreeTransform!.WidthPercent = 200;
            doc.FreeTransform.HeightPercent = 200;
            await doc.CommitTransformAsync();
            var placed = PsdLiveContent.ReadSmartObject((PsdLayerRecord)so.SourceData!)!;
            check(so.Tags.Contains("smart-object") && Math.Abs(placed.Corners[0].X - 80) < 1e-6 && Math.Abs(placed.Corners[2].Y - 90) < 1e-6
                  && so.Bounds == new PixelRect(80, 50, 160, 90),
                $"scaling 200% moves the placed corners ({placed.Corners[0]}–{placed.Corners[2]}) and keeps the layer a smart object");
            int contrast = StripeContrast(so);
            check(contrast > 200, $"the enlarged smart object is redrawn from its 80×40 content: 1-pixel stripes, not blurred gray (contrast {contrast})");
            doc.Undo();
            check(so.Bounds == new PixelRect(100, 60, 140, 80) && PsdLiveContent.ReadSmartObject((PsdLayerRecord)so.SourceData!)!.Corners[0] == (100, 60),
                "undo restores the smart object's pixels and corners");

            doc.SelectedLayer = doc.Layers.SelectMany(l => l.SelfAndDescendants()).First(i => i.Node == type);
            double typeX = TypeX(type);
            check(doc.BeginFreeTransform(), "Free Transform opens on a type layer");
            doc.FreeTransform!.Angle = 30;
            await doc.CommitTransformAsync();
            check(type.Tags.Contains("text") && Math.Abs(TypeX(type) - typeX) > 1, $"turning type keeps it text and turns its transform (x {typeX:F1} → {TypeX(type):F1})");
            doc.Undo();

            // ---- A turned crop keeps them live ---------------------------------------------------------
            editor.Tool = CanvasTool.Crop;
            var box = doc.CropBox!;
            var (cx, cy) = box.Center;
            box.BeginDrag(TransformHandle.Rotate, 450, cy);
            var (sin, cos) = Math.SinCos(8 * Math.PI / 180);
            box.DragTo(cx + (450 - cx) * cos, cy + (450 - cx) * sin, shift: false, alt: false);
            box.EndDrag();
            var map = box.ResultMap;
            check(doc.CropNotice == "", "turning the crop no longer warns about rasterizing");
            await doc.CommitCropAsync();
            await CanvasSettledAsync(doc);
            placed = PsdLiveContent.ReadSmartObject((PsdLayerRecord)so.SourceData!)!;
            var expected = map.Apply(100, 60);
            check(so.Tags.Contains("smart-object") && type.Tags.Contains("text") && fill.Tags.Contains("fill")
                  && Math.Abs(placed.Corners[0].X - expected.X) < 1e-6 && Math.Abs(placed.Corners[0].Y - expected.Y) < 1e-6,
                $"a turned crop keeps type, smart object and fill live, and turns the smart object's corners ({placed.Corners[0].X:F2}, {placed.Corners[0].Y:F2})");
            check(fill.Bounds == model.Bounds && fill.Pixels!.Alpha is null && fill.Pixels.ColorPlanes[1].Data[0] == 160,
                $"the solid color fill is filled again over the whole new canvas ({fill.Bounds})");
            check(Math.Abs(TypeX(type) - map.Apply(50, 200).X) < 1e-6, "the type's transform follows the crop");
            if (samples is not null) await SaveSampleAsync(doc, type, Path.Combine(samples, "transform2-synthetic-turned-crop.psd"));
            doc.Undo();
            await CanvasSettledAsync(doc);
            editor.Tool = CanvasTool.Move;

            // ---- Image Size: Scale Styles ------------------------------------------------------------------
            await doc.ResizeImageAsync(200, 150, ResampleMethod.Bicubic, 72, scaleStyles: true);
            await CanvasSettledAsync(doc);
            var shadow = shadowed.Effects!.Items.OfType<DropShadowEffect>().Single();
            placed = PsdLiveContent.ReadSmartObject((PsdLayerRecord)so.SourceData!)!;
            check(shadow is { Distance: 5, Size: 3 } && Math.Abs(placed.Corners[2].X - 70) < 1e-6 && so.Tags.Contains("smart-object"),
                $"Image Size 50% with Scale Styles halves the drop shadow ({shadow.Distance}/{shadow.Size} px) and moves the smart object's corners");
            if (samples is not null) await SaveSampleAsync(doc, type, Path.Combine(samples, "transform2-synthetic-image-size-50.psd"));
            doc.Undo();
            await CanvasSettledAsync(doc);
            await doc.ResizeImageAsync(200, 150, ResampleMethod.Bicubic, 72, scaleStyles: false);
            await CanvasSettledAsync(doc);
            check(shadowed.Effects!.Items.OfType<DropShadowEffect>().Single().Distance == 10, "without Scale Styles the effects keep their size");
            doc.Undo();
            await CanvasSettledAsync(doc);

            // ---- Selections follow Canvas Size, Image Size and Crop -----------------------------------------
            doc.SetSelection(SelectionMask.Rectangle(new PixelRect(40, 40, 100, 80), model.Bounds), "Rectangular Marquee");
            await doc.ResizeCanvasAsync(500, 400, 50, 50, Avalonia.Media.Colors.White);
            await CanvasSettledAsync(doc);
            check(doc.Selection?.Bounds == new PixelRect(90, 90, 150, 130), $"Canvas Size moves the selection with the image ({doc.Selection?.Bounds})");
            doc.Undo();
            await CanvasSettledAsync(doc);
            await doc.ResizeImageAsync(200, 150, ResampleMethod.Bicubic, 72);
            await CanvasSettledAsync(doc);
            check(doc.Selection is { IsRectangular: true } s1 && s1.Bounds == new PixelRect(20, 20, 50, 40), $"Image Size scales the selection ({doc.Selection?.Bounds})");
            doc.Undo();
            await CanvasSettledAsync(doc);
            check(doc.Selection?.Bounds == new PixelRect(40, 40, 100, 80), "undo puts the selection back");
            doc.SetSelection(null, "Deselect");

            // ---- Crop options --------------------------------------------------------------------------------
            editor.Tool = CanvasTool.Crop;
            editor.CropRatioIndex = EditorViewModel.CropSizePreset;
            editor.CropTargetWidth = 300;
            editor.CropTargetHeight = 200;
            editor.CropTargetResolution = 144;
            box = doc.CropBox!;
            check(Math.Abs(box.ResultWidth / (double)box.ResultHeight - 1.5) < 0.01, $"W x H x Resolution keeps the box at 3:2 ({box.SizeText})");
            await doc.CommitCropAsync();
            await CanvasSettledAsync(doc);
            check(model.Width == 300 && model.Height == 200 && model.Resolution == 144,
                $"W x H x Resolution crops to exactly 300×200 px at 144 ppi ({model.Width}×{model.Height}, {model.Resolution} ppi)");
            doc.Undo();
            await CanvasSettledAsync(doc);
            editor.ClearCropSizeCommand.Execute(null);
            editor.CropRatioIndex = 0;

            editor.CropOverlay = CropOverlay.RuleOfThirds;
            editor.CycleCropOverlay(orientation: false);
            check(editor.CropOverlay == CropOverlay.Grid, "O steps to the next overlay (Grid)");
            for (int i = 0; i < 5; i++) editor.CycleCropOverlay(orientation: false);
            editor.CycleCropOverlay(orientation: true);
            check(editor.CropOverlay == CropOverlay.RuleOfThirds && editor.CropOverlayOrientation == 1, "six steps come back round; Shift+O turns the overlay");
            editor.CropOverlayOrientation = 0;

            // "Crop the image?" when switching tools with a changed box.
            box = doc.CropBox!;
            box.BeginDrag(TransformHandle.Left, 0, 150);
            box.DragTo(60, 150, shift: false, alt: false);
            box.EndDrag();
            var answer = CropPromptChoice.Cancel;
            int asked = 0;
            editor.CropPromptOverride = () => { asked++; return Task.FromResult(answer); };
            editor.Tool = CanvasTool.Move;
            await Task.Delay(50);
            check(asked == 1 && editor.Tool == CanvasTool.Crop && ReferenceEquals(doc.CropBox, box) && box.IsModified && model.Width == 400,
                "leaving the Crop tool with a changed box asks; Cancel returns to the crop as it was");
            answer = CropPromptChoice.DontCrop;
            editor.Tool = CanvasTool.Move;
            await Task.Delay(50);
            check(asked == 2 && doc.CropBox is null && model.Width == 400, "Don't Crop closes the box and leaves the image");
            editor.Tool = CanvasTool.Crop;
            box = doc.CropBox!;
            box.BeginDrag(TransformHandle.Left, 0, 150);
            box.DragTo(60, 150, shift: false, alt: false);
            box.EndDrag();
            answer = CropPromptChoice.Crop;
            editor.Tool = CanvasTool.Move;
            for (int i = 0; i < 100 && model.Width == 400; i++) await Task.Delay(20);
            await CanvasSettledAsync(doc);
            check(asked == 3 && model.Width == 340, $"Crop applies it ({model.Width}×{model.Height})");
            doc.Undo();
            await CanvasSettledAsync(doc);
            editor.CropPromptOverride = null;

            // Content-Aware is a seam for the content-aware synthesis: disabled until it is provided.
            DocumentViewModel.ContentAwareCropFill = null;
            check(!editor.CanCropContentAware, "Content-Aware is unavailable until content-aware filling is provided");
            DocumentViewModel.ContentAwareCropFill = (pixels, region, _) =>
            {
                var planes = pixels.ColorPlanes.Select(p => new Plane(p.Width, p.Height, 8, (byte[])p.Data.Clone())).ToArray();
                for (int y = 0; y < pixels.Height; y++)
                    for (int x = 0; x < pixels.Width; x++)
                        if (region.CoverageAt(x, y) > 128)
                            (planes[0].Data[y * pixels.Width + x], planes[1].Data[y * pixels.Width + x], planes[2].Data[y * pixels.Width + x]) = (255, 0, 255);
                return new Raster(pixels.ColorMode, planes, pixels.Alpha);
            };
            editor.CropContentAware = true;
            editor.Tool = CanvasTool.Crop;
            box = doc.CropBox!;
            box.BeginDrag(TransformHandle.Right, 400, 150);
            box.DragTo(450, 150, shift: false, alt: false);
            box.EndDrag();
            await doc.CommitCropAsync();
            await CanvasSettledAsync(doc);
            var background = (PixelLayer)model.Root.Children[0];
            check(model.Width == 450 && background.Pixels!.ColorPlanes[1].Data[449] == 0 && background.Pixels.ColorPlanes[0].Data[449] == 255
                  && background.Pixels.ColorPlanes[1].Data[300] == 255,
                "with Content-Aware on, the area a crop adds beyond the image goes to the content-aware fill");
            doc.Undo();
            await CanvasSettledAsync(doc);
            editor.CropContentAware = false;
            DocumentViewModel.ContentAwareCropFill = savedHook;

            // ---- Perspective Crop ------------------------------------------------------------------------------
            editor.HandleToolKey("C", shift: true);
            check(editor.Tool == CanvasTool.PerspectiveCrop && doc.PerspectiveCrop is { HasShape: false } && doc.CropBox is null,
                "Shift+C steps to the Perspective Crop tool, which starts without a shape");
            var pc = doc.PerspectiveCrop!;
            pc.BeginDrag(pc.HitTest(50, 40, 4), 50, 40);
            pc.DragTo(350, 260);
            pc.EndDrag();
            check(pc.HasShape && pc.ResultSize == (300, 220), $"dragging draws the shape ({pc.SizeText})");
            pc.BeginDrag(0, 50, 40);
            pc.DragTo(80, 50);
            pc.EndDrag();
            pc.BeginDrag(1, 350, 40);
            pc.DragTo(330, 60);
            pc.EndDrag();
            check(pc.Corners[0] == (80, 50) && pc.Corners[1] == (330, 60) && doc.PerspectiveCropNotice.Contains("rasterizes"),
                "corners drag on their own; a notice says live layers will be rasterized");
            pc.BeginDrag(2, 350, 260);
            pc.DragTo(40, 20); // would fold the shape
            pc.EndDrag();
            check(pc.Corners[2] == (350, 260), "a drag that would fold the shape is refused");
            var (pw, ph) = pc.ResultSize;
            await doc.CommitPerspectiveCropAsync();
            await CanvasSettledAsync(doc);
            check(model.Width == pw && model.Height == ph && doc.UndoText == "Undo Perspective Crop" && !so.Tags.Contains("smart-object")
                  && ((PixelLayer)model.Root.Children[0]).Pixels!.Alpha is null,
                $"Perspective Crop straightens the shape onto a {model.Width}×{model.Height} canvas and rasterizes live layers");
            if (samples is not null) await SaveSampleAsync(doc, type, Path.Combine(samples, "transform2-synthetic-perspective-crop.psd"));
            doc.Undo();
            await CanvasSettledAsync(doc);
            check(model.Width == 400 && so.Tags.Contains("smart-object"), "undo brings the canvas and the smart object back");
            editor.Tool = CanvasTool.Move;

            if (samples is not null) await WritePhotoshopSamplesAsync(editor, samples, check);
        }
        catch (Exception ex)
        {
            check(false, $"exception in transform steps: {ex}");
        }
        finally
        {
            editor.CropPromptOverride = null;
            editor.CropContentAware = false;
            editor.CropRatioIndex = 0;
            DocumentViewModel.ContentAwareCropFill = savedHook;
            editor.Tool = CanvasTool.Move;
            File.Delete(path);
        }
    }

    /// <summary>
    /// Saves files for checking in Photoshop (STRAYTA_TRANSFORM_SAMPLES): the synthetic smart object sample itself, and,
    /// with STRAYTA_CORPUS set, real files with type, shapes and smart objects after a turned crop, Image Size and
    /// Free Transform, all through the editor's own commands.
    /// </summary>
    private static async Task WritePhotoshopSamplesAsync(EditorViewModel editor, string dir, Action<bool, string> check)
    {
        Directory.CreateDirectory(dir);
        WriteLiveSample(Path.Combine(dir, "transform2-synthetic-original.psd"), withType: false);
        var corpus = Environment.GetEnvironmentVariable("STRAYTA_CORPUS");
        if (corpus is null) return;

        async Task<DocumentViewModel> Open(string name)
        {
            var model = PsdFile.OpenForEditing(Path.Combine(corpus, name));
            var doc = new DocumentViewModel(model, null, editor);
            editor.Factory.AddDocument(doc);
            editor.ActiveDocument = doc;
            await doc.RenderAsync();
            return doc;
        }
        async Task TurnedCrop(DocumentViewModel doc, double degrees)
        {
            editor.Tool = CanvasTool.Crop;
            var box = doc.CropBox!;
            var (cx, cy) = box.Center;
            double far = box.Right + 50;
            box.BeginDrag(TransformHandle.Rotate, far, cy);
            var (sin, cos) = Math.SinCos(degrees * Math.PI / 180);
            box.DragTo(cx + (far - cx) * cos, cy + (far - cx) * sin, shift: false, alt: false);
            box.EndDrag();
            await doc.CommitCropAsync();
            await CanvasSettledAsync(doc);
            editor.Tool = CanvasTool.Move;
        }

        foreach (var (file, output) in new[]
                 {
                     ("51-TMP - Font Asset Icon.psd", "transform2-type-and-shapes-turned-crop.psd"),
                     ("12-PageGradient.psd", "transform2-fill-shape-turned-crop.psd"),
                     ("01. Cosmetic Product Branding.psd", "transform2-smart-objects-turned-crop.psd"),
                 })
        {
            if (!File.Exists(Path.Combine(corpus, file))) continue;
            var doc = await Open(file);
            await TurnedCrop(doc, 12);
            await doc.SaveAsync(Path.Combine(dir, output));
            check(File.Exists(Path.Combine(dir, output)), $"sample {output}");
        }
        if (File.Exists(Path.Combine(corpus, "51-TMP - Font Asset Icon.psd")))
        {
            var doc = await Open("51-TMP - Font Asset Icon.psd");
            await doc.ResizeImageAsync(320, 320, ResampleMethod.Bicubic, doc.Model.Resolution * 2.5);
            await CanvasSettledAsync(doc);
            await doc.SaveAsync(Path.Combine(dir, "transform2-type-and-shapes-image-size-250.psd"));
            var text = doc.Layers.SelectMany(l => l.SelfAndDescendants()).First(i => i.Node.Tags.Contains("text"));
            doc.SelectedLayer = text;
            if (doc.BeginFreeTransform())
            {
                doc.FreeTransform!.Angle = 20;
                doc.FreeTransform.WidthPercent = 80;
                doc.FreeTransform.HeightPercent = 80;
                await doc.CommitTransformAsync();
            }
            await doc.SaveAsync(Path.Combine(dir, "transform2-type-free-transform.psd"));
        }
        if (File.Exists(Path.Combine(corpus, "01. Cosmetic Product Branding.psd")))
        {
            var doc = await Open("01. Cosmetic Product Branding.psd");
            await doc.ResizeImageAsync(2000, 1500, ResampleMethod.Bicubic, doc.Model.Resolution);
            await CanvasSettledAsync(doc);
            await doc.SaveAsync(Path.Combine(dir, "transform2-smart-objects-image-size-50.psd"));
        }
    }
}
