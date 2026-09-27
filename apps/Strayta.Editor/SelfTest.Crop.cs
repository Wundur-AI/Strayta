using System.Diagnostics;
using Strayta.Core;
using Strayta.Core.Selection;
using Strayta.Editor.Controls;
using Strayta.Editor.Editing;
using Strayta.Editor.ViewModels;
using Strayta.Psd;
using Strayta.Rendering.Transforms;

namespace Strayta.Editor;

/// <summary>Self-test steps for the Crop tool, Image Size, Canvas Size, Image › Crop and Trim.</summary>
internal static partial class SelfTest
{
    /// <summary>Waits until the view shows the canvas after a crop or resize (or 5 s pass).</summary>
    private static async Task CanvasSettledAsync(DocumentViewModel doc)
    {
        for (int i = 0; i < 250 && doc.IsCanvasChangePending; i++) await Task.Delay(20);
    }

    /// <summary>A layer of solid color covering <paramref name="bounds"/>, with transparency.</summary>
    private static PixelLayer SolidLayer(string name, PixelRect bounds, byte r, byte g, byte b)
    {
        int n = bounds.Width * bounds.Height;
        Plane P(byte v) => new(bounds.Width, bounds.Height, 8, Enumerable.Repeat(v, n).ToArray());
        return new PixelLayer { Name = name, Bounds = bounds, Pixels = new Raster(ColorMode.Rgb, [P(r), P(g), P(b)], P(255)) };
    }

    private static async Task RunCropStepsAsync(EditorViewModel editor, Action<bool, string> check)
    {
        try
        {
            var model = LayerFactory.NewDocument(400, 300, whiteBackground: true);
            var doc = new DocumentViewModel(model, null, editor);
            editor.Factory.AddDocument(doc);
            editor.ActiveDocument = doc;
            await doc.RenderAsync();
            var background = (PixelLayer)model.Root.Children[0];
            var red = SolidLayer("Red", new PixelRect(-40, 100, 200, 200), 255, 0, 0);
            red.Mask = new LayerMask { Bounds = new PixelRect(-40, 100, 200, 200), Pixels = new Plane(240, 100, 8, Enumerable.Repeat((byte)200, 24000).ToArray()) };
            var group = new LayerGroup { Name = "Group", Mask = LayerMasks.Solid(reveal: true) };
            var type = SolidLayer("Type", new PixelRect(250, 20, 350, 60), 0, 0, 255);
            type.Tags.Add("text");
            doc.Apply(new InsertEdit(red, model.Root, 1, "test"));
            doc.Apply(new InsertEdit(group, model.Root, 2, "test"));
            doc.Apply(new InsertEdit(type, group, 0, "test"));
            doc.SetSelection(SelectionMask.Rectangle(new PixelRect(10, 10, 60, 60), model.Bounds), "Rectangular Marquee");

            // ---- The tool and its box -------------------------------------------------------------------
            editor.Tool = CanvasTool.Crop;
            var box = doc.CropBox;
            check(box is { IsModified: false, ResultWidth: 400, ResultHeight: 300 } && editor.IsCropTool,
                "choosing the Crop tool shows a box around the whole canvas");
            check(editor.ToolGroups.Any(g => g.Key == "C" && g.Contains(CanvasTool.Crop)) && editor.HandleToolKey("C", false) && editor.Tool == CanvasTool.Crop,
                "the Crop tool has its own slot on C");

            int renders = 0;
            void OnFrame(bool full) => renders++;
            doc.FrameDisplayed += OnFrame;
            // Let the renders of the setup above finish, then count only what the drag causes.
            for (int last = -1; last != renders;) { last = renders; await Task.Delay(700); }
            renders = 0;
            box!.BeginDrag(box.HitTest(400, 300, 4), 400, 300);
            for (int i = 1; i <= 20; i++) box.DragTo(400 - i * 5, 300 - i * 5, shift: false, alt: false);
            box.EndDrag();
            await Task.Delay(300);
            doc.FrameDisplayed -= OnFrame;
            check(box.ResultWidth == 300 && box.ResultHeight == 200 && model.Width == 400 && renders == 0,
                $"dragging the corner resizes the box without touching or re-rendering the document ({box.SizeText}, {renders} renders)");
            check(box.HitTest(150, 100, 4) == TransformHandle.Move && box.HitTest(360, 280, 4) == TransformHandle.Rotate
                  && box.HitTest(150, 1, 4) == TransformHandle.Top, "inside moves the image, outside turns it, edges resize");

            box.BeginDrag(TransformHandle.BottomRight, 300, 200);
            box.DragTo(340, 210, shift: true, alt: false);
            box.EndDrag();
            check(Math.Abs(box.ResultWidth / (double)box.ResultHeight - 1.5) < 0.02, $"Shift keeps the box's proportions ({box.SizeText})");

            doc.CancelCrop();
            check(!box.IsModified && box.ResultWidth == 400, "Esc puts the box back around the whole image");

            editor.CropRatioIndex = 1;
            check(box.ResultWidth == box.ResultHeight && box.ResultHeight == 300, $"the 1:1 preset makes the largest square box ({box.SizeText})");
            editor.CropRatioIndex = 0;
            doc.CancelCrop();

            // ---- Crop, undo, redo -----------------------------------------------------------------------
            box.BeginDrag(TransformHandle.TopLeft, 0, 0);
            box.DragTo(50, 40, shift: false, alt: false);
            box.EndDrag();
            box.BeginDrag(TransformHandle.BottomRight, 400, 300);
            box.DragTo(250, 190, shift: false, alt: false);
            box.EndDrag();
            var redPixels = red.Pixels;
            await doc.CommitCropAsync();
            await CanvasSettledAsync(doc);
            check(model.Width == 200 && model.Height == 150 && doc.DocumentSize == new Avalonia.PixelSize(200, 150) && doc.UndoText == "Undo Crop",
                $"Enter crops the canvas as one step ({model.Width}×{model.Height}, {doc.UndoText})");
            check(background.Bounds == model.Bounds && background.Pixels!.Alpha is null && red.Bounds == new PixelRect(0, 60, 150, 150)
                  && red.Mask!.Bounds == red.Bounds, $"layers and masks are cut to the new canvas (red {red.Bounds})");
            check(type.Bounds == new PixelRect(200, -20, 300, 20) && type.Tags.Contains("text"), "type is moved, never cut or rasterized, by a straight crop");
            check(doc.Selection is null, "the crop drops the selection");
            check(doc.CropBox is { IsModified: false, ResultWidth: 200, ResultHeight: 150 } && !ReferenceEquals(doc.CropBox, box),
                "a new box frames the cropped canvas");

            doc.Undo();
            await CanvasSettledAsync(doc);
            check(model.Width == 400 && ReferenceEquals(red.Pixels, redPixels) && red.Bounds == new PixelRect(-40, 100, 200, 200) && doc.Selection is not null
                  && doc.DocumentSize == new Avalonia.PixelSize(400, 300), "undo restores the canvas, the layers and the selection");
            doc.Redo();
            await CanvasSettledAsync(doc);
            check(model.Width == 200 && red.Bounds == new PixelRect(0, 60, 150, 150), "redo crops again");
            doc.Undo();
            await CanvasSettledAsync(doc);

            // ---- Delete Cropped Pixels off keeps hidden pixels, and they survive saving -------------------
            editor.CropDeletePixels = false;
            box = doc.CropBox!;
            box.BeginDrag(TransformHandle.Left, 0, 150);
            box.DragTo(100, 150, shift: false, alt: false);
            box.EndDrag();
            await doc.CommitCropAsync();
            await CanvasSettledAsync(doc);
            check(model.Width == 300 && ReferenceEquals(red.Pixels, redPixels) && red.Bounds == new PixelRect(-140, 100, 100, 200)
                  && background.Name == "Layer 0" && background.Bounds.Left == -100,
                $"without Delete Cropped Pixels layers keep what lies outside (red {red.Bounds}, background is \"{background.Name}\")");
            string path = Path.Combine(Path.GetTempPath(), $"strayta-crop-{Guid.NewGuid():N}.psd");
            await doc.SaveAsync(path);
            var reopened = PsdFile.OpenForEditing(path);
            var savedRed = reopened.Root.Descendants().OfType<PixelLayer>().First(l => l.Name == "Red");
            check(reopened.Width == 300 && savedRed.Bounds == red.Bounds && savedRed.Pixels!.Width == 240,
                $"the saved PSD keeps the hidden pixels ({savedRed.Bounds})");
            File.Delete(path);
            doc.Undo();
            await CanvasSettledAsync(doc);
            check(background.Name == "Background" && background.Pixels!.Alpha is null, "undo turns Layer 0 back into the Background");
            editor.CropDeletePixels = true;

            // ---- Turning: the box shrinks to stay inside, type is rasterized ----------------------------
            box = doc.CropBox!;
            var (cx, cy) = box.Center;
            box.BeginDrag(TransformHandle.Rotate, 450, cy);
            var (sin, cos) = Math.SinCos(10 * Math.PI / 180);
            box.DragTo(cx + (450 - cx) * cos, cy + (450 - cx) * sin, shift: false, alt: false);
            box.EndDrag();
            check(Math.Abs(box.Angle - 10) < 0.01 && box.ResultWidth < 400 && box.ResultHeight < 300
                  && Math.Abs(box.ResultWidth / (double)box.ResultHeight - 4 / 3.0) < 0.02 && doc.CropNotice.Contains("rasterizes"),
                $"dragging outside turns the image and the box shrinks to fit ({box.SizeText}); a notice warns about type");
            int turnedW = box.ResultWidth, turnedH = box.ResultHeight;
            var sw = Stopwatch.StartNew();
            await doc.CommitCropAsync();
            await CanvasSettledAsync(doc);
            check(model.Width == turnedW && model.Height == turnedH && background.Pixels!.Alpha is null && background.Bounds == model.Bounds,
                $"a turned crop resamples every layer ({model.Width}×{model.Height}, {sw.ElapsedMilliseconds} ms)");
            var corner = background.Pixels;
            check(corner!.ColorPlanes[0].Data[0] == 255 && corner.ColorPlanes[1].Data[0] == 255, "no empty corners: the Background is still white there");
            check(!type.Tags.Contains("text") && red.Mask is { Pixels: not null }, "turning rasterized the type layer and turned the mask");
            doc.Undo();
            await CanvasSettledAsync(doc);
            check(type.Tags.Contains("text") && model.Width == 400, "undo brings the type layer back");

            // ---- Straighten -----------------------------------------------------------------------------
            box = doc.CropBox!;
            box.Straighten(100, 100, 300, 100 + 200 * Math.Tan(4 * Math.PI / 180));
            check(Math.Abs(box.Angle + 4) < 0.01, $"Straighten levels the drawn line ({box.Angle:F2}°)");
            doc.CancelCrop();
            box.Straighten(100, 100, 100 + 5, 300);
            check(Math.Abs(box.Angle - Math.Atan2(5, 200) * 180 / Math.PI) < 0.01, $"a nearly vertical line is made plumb ({box.Angle:F2}°)");
            doc.CancelCrop();
            check(box.Angle == 0, "Esc also undoes the turn");
            editor.Tool = CanvasTool.Move;
            check(doc.CropBox is null, "switching tools closes an unchanged crop");

            // ---- Image Size -----------------------------------------------------------------------------
            sw.Restart();
            await doc.ResizeImageAsync(200, 150, ResampleMethod.Bicubic, 144);
            await CanvasSettledAsync(doc);
            check(model.Width == 200 && model.Height == 150 && model.Resolution == 144 && background.Pixels!.Width == 200 && background.Pixels.Alpha is null
                  && red.Bounds.Width == 120 && red.Mask!.Bounds == red.Bounds && doc.UndoText == "Undo Image Size" && !type.Tags.Contains("text"),
                $"Image Size resamples layers and masks to exact dimensions ({model.Width}×{model.Height}, red {red.Bounds}, {sw.ElapsedMilliseconds} ms)");
            doc.Undo();
            await CanvasSettledAsync(doc);
            check(model.Width == 400 && model.Resolution == 72 && type.Tags.Contains("text"), "undo restores size and resolution");
            await doc.ResizeImageAsync(400, 300, ResampleMethod.Bicubic, 300);
            check(model.Resolution == 300 && model.Width == 400 && doc.UndoText == "Undo Image Size", "changing only the resolution does not resample");
            doc.Undo();
            await CanvasSettledAsync(doc);

            // ---- Canvas Size ----------------------------------------------------------------------------
            foreach (var (ax, ay) in new[] { (-1, -1), (0, 0), (1, 1) })
            {
                int ox = (ax + 1) * (500 - 400) / 2, oy = (ay + 1) * (400 - 300) / 2;
                await doc.ResizeCanvasAsync(500, 400, ox, oy, Avalonia.Media.Color.FromRgb(0, 255, 0));
                await CanvasSettledAsync(doc);
                var px = background.Pixels!;
                int far = ax < 0 ? 499 : 0, farY = ay < 0 ? 399 : 0;
                bool green = px.ColorPlanes[1].Data[farY * 500 + far] == 255 && px.ColorPlanes[0].Data[farY * 500 + far] == 0;
                check(model.Width == 500 && background.Bounds == model.Bounds && green && red.Bounds == new PixelRect(-40 + ox, 100 + oy, 200 + ox, 200 + oy),
                    $"Canvas Size anchored ({ax},{ay}) extends the Background in green and moves the layers (red {red.Bounds})");
                doc.Undo();
                await CanvasSettledAsync(doc);
            }

            // ---- Image › Crop and Trim ------------------------------------------------------------------
            doc.SetSelection(SelectionMask.Rectangle(new PixelRect(20, 30, 120, 90), model.Bounds), "Rectangular Marquee");
            await doc.CropToSelectionAsync();
            await CanvasSettledAsync(doc);
            check(model.Width == 100 && model.Height == 60 && doc.Selection is null, "Image › Crop crops to the selection");
            doc.Undo();
            await CanvasSettledAsync(doc);

            var trimModel = LayerFactory.NewDocument(300, 200, whiteBackground: false);
            trimModel.Root.Add(SolidLayer("Block", new PixelRect(40, 50, 140, 90), 10, 20, 30));
            var trimDoc = new DocumentViewModel(trimModel, null, editor);
            editor.Factory.AddDocument(trimDoc);
            editor.ActiveDocument = trimDoc;
            await trimDoc.RenderAsync();
            await trimDoc.TrimAsync(TrimBasis.Transparent, true, true, true, true);
            await CanvasSettledAsync(trimDoc);
            check(trimModel.Width == 100 && trimModel.Height == 40, $"Trim removes transparent edges ({trimModel.Width}×{trimModel.Height})");
        }
        catch (Exception ex)
        {
            check(false, $"exception in Crop steps: {ex}");
        }
        finally
        {
            editor.Tool = CanvasTool.Move;
            editor.CropDeletePixels = true;
        }
    }

    /// <summary>
    /// STRAYTA_CROPBENCH=1 times Image Size and crops on the first opened file; =new on a generated 4000×3000
    /// layered document (background, a large photo-like layer with a mask, a group and a type layer).
    /// </summary>
    public static async Task RunCropBenchmarkAsync(EditorViewModel editor, bool synthetic)
    {
        DocumentViewModel? doc;
        if (synthetic)
        {
            const int w = 4000, h = 3000;
            var model = LayerFactory.NewDocument(w, h, whiteBackground: true);
            int lw = 3200, lh = 2400;
            var planes = Enumerable.Range(0, 4).Select(_ => Plane.Create(lw, lh, 8)).ToArray();
            Parallel.For(0, lh, y =>
            {
                for (int x = 0; x < lw; x++)
                {
                    int i = y * lw + x;
                    planes[0].Data[i] = (byte)(x * 255 / lw);
                    planes[1].Data[i] = (byte)(y * 255 / lh);
                    planes[2].Data[i] = (byte)(128 + 100 * Math.Sin(x * 0.01) * Math.Cos(y * 0.013));
                    planes[3].Data[i] = 255;
                }
            });
            var mask = new Plane(lw, lh, 8, Enumerable.Repeat((byte)220, lw * lh).ToArray());
            model.Root.Add(new PixelLayer
            {
                Name = "Photo", Bounds = new PixelRect(400, 300, 400 + lw, 300 + lh), Pixels = new Raster(ColorMode.Rgb, planes[..3], planes[3]),
                Mask = new LayerMask { Bounds = new PixelRect(400, 300, 400 + lw, 300 + lh), Pixels = mask },
            });
            var group = new LayerGroup { Name = "Group" };
            model.Root.Add(group);
            group.Add(SolidLayer("Shape", new PixelRect(1000, 800, 2600, 1800), 30, 90, 200));
            doc = new DocumentViewModel(model, null, editor);
            editor.Factory.AddDocument(doc);
            editor.ActiveDocument = doc;
            await doc.RenderAsync();
        }
        else
        {
            for (int i = 0; i < 300 && editor.ActiveDocument is null; i++) await Task.Delay(100);
            doc = editor.ActiveDocument;
            if (doc is null) return;
        }
        await Task.Delay(1500); // let the view fit the image and the preview caches warm up
        await doc.RunCanvasBenchmarkAsync();
    }
}
