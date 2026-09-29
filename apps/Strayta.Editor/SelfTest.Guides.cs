using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Strayta.Core;
using Strayta.Core.Painting;
using Strayta.Core.Selection;
using Strayta.Editor.Controls;
using Strayta.Editor.Editing;
using Strayta.Editor.ViewModels;
using Strayta.Psd;
using Strayta.Rendering.Transforms;

namespace Strayta.Editor;

internal static partial class SelfTest
{
    private static async Task<ImageCanvas?> CanvasOfAsync(DocumentViewModel doc)
    {
        for (int i = 0; i < 100; i++)
        {
            var canvas = Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime app
                ? app.Windows.SelectMany(w => w.GetVisualDescendants()).OfType<ImageCanvas>().FirstOrDefault(c => ReferenceEquals(c.DataContext, doc) && c.Bounds.Width > 0)
                : null;
            if (canvas is not null) return canvas;
            await Task.Delay(20);
        }
        return null;
    }

    private static Task Settle() => Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background).GetTask();

    /// <summary>
    /// Rulers, guides, grid, snapping and the Info panel: guides dragged out of the rulers (Option swaps them), moved with
    /// the Move tool and deleted on a ruler, each one undo step; lock; New Guide Layout; snapping of guides, the Move tool
    /// and the snapper itself; guides following Image Size; the Info readout in ruler units; and a saved file whose guides
    /// read back (the Photoshop check file, with STRAYTA_SELFTEST_SHOTS).
    /// </summary>
    private static async Task RunGuideStepsAsync(EditorViewModel editor, Action<bool, string> check)
    {
        var model = LayerFactory.NewDocument(400, 300, whiteBackground: true);
        var doc = new DocumentViewModel(model, null, editor);
        editor.Factory.AddDocument(doc);
        editor.ActiveDocument = doc;
        await doc.RenderAsync();
        (editor.ShowRulers, editor.ShowGuides, editor.LockGuides, editor.ShowGrid, editor.SnapEnabled, editor.SnapTo, editor.RulerUnit) =
            (true, true, false, false, true, SnapTargets.All, RulerUnit.Pixels);
        editor.Tool = CanvasTool.Move;
        var canvas = await CanvasOfAsync(doc);
        check(canvas is not null, "the document's canvas is on screen");
        if (canvas is null) return;
        await Settle();
        check(canvas.ShowRulers && canvas.ShowGuides, "View › Rulers shows the rulers on the canvas");

        Point Screen(double x, double y) => canvas.ImageToControl(x, y) - new Point(0.5, 0.5);
        double zoom = canvas.Zoom;

        // Drag a guide out of the top ruler to y ≈ 100.
        var top = new Point(Screen(200, 0).X, ImageCanvas.RulerSize / 2);
        check(canvas.GuidePress(top, KeyModifiers.None, 1, left: true, right: false), "a press on the top ruler starts a guide");
        canvas.GuideMove(Screen(200, 100), KeyModifiers.None);
        canvas.GuideRelease(Screen(200, 100));
        check(model.Guides is [{ IsHorizontal: true } g1] && Math.Abs(g1.Position - 100) <= 1 / zoom + 1e-9 && doc.UndoText == "Undo New Guide" && doc.IsModified,
            $"dragging from the top ruler makes a horizontal guide at the pointer, one undoable step ({string.Join(", ", model.Guides)})");

        // Option-drag from the top ruler: a vertical guide; near the document's center it snaps onto it.
        canvas.GuidePress(top, KeyModifiers.Alt, 1, left: true, right: false);
        var nearCenter = Screen(200, 150) + new Point(3, 0);
        canvas.GuideMove(nearCenter, KeyModifiers.Alt);
        canvas.GuideRelease(nearCenter);
        check(model.Guides.Count == 2 && model.Guides[1] == new Guide(GuideOrientation.Vertical, 200),
            $"Option-drag from the top ruler makes a vertical guide, snapped to the document center 3 screen px away ({model.Guides.LastOrDefault()})");

        // A guide dropped back on the ruler it came from is not made.
        canvas.GuidePress(new Point(ImageCanvas.RulerSize / 2, Screen(0, 50).Y), KeyModifiers.None, 1, left: true, right: false);
        canvas.GuideRelease(new Point(ImageCanvas.RulerSize / 2, Screen(0, 60).Y));
        check(model.Guides.Count == 2, "a guide let go over the ruler is not added");

        // Move the vertical guide with the Move tool; hidden guides are not grabbed.
        var onGuide = Screen(200, 40);
        check(canvas.GuideAt(onGuide) == 1, "the vertical guide is under the pointer");
        check(canvas.GuidePress(onGuide, KeyModifiers.None, 1, left: true, right: false), "the Move tool picks up a guide");
        canvas.GuideMove(Screen(260, 40), KeyModifiers.None);
        canvas.GuideRelease(Screen(260, 40));
        check(Math.Abs(model.Guides[1].Position - 260) <= 1 / zoom + 1e-9 && doc.UndoText == "Undo Move Guide", $"dragging moves it ({model.Guides[1]})");
        doc.Undo();
        check(model.Guides[1].Position == 200, "undo puts it back");
        doc.Redo();

        // ⌘ with another tool picks up guides too; Lock Guides stops it.
        editor.Tool = CanvasTool.Brush;
        var cmd = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        var onMoved = Screen(model.Guides[1].Position, 40);
        check(!canvas.GuidePress(onMoved, KeyModifiers.None, 1, true, false), "with the Brush a plain press does not grab a guide");
        check(canvas.GuidePress(onMoved, cmd, 1, true, false), "⌘ with any tool grabs a guide");
        canvas.GuideRelease(onMoved);
        editor.Tool = CanvasTool.Move;
        editor.LockGuides = true;
        await Settle();
        check(!canvas.GuidePress(onMoved, KeyModifiers.None, 1, true, false), "Lock Guides: the Move tool no longer grabs them");
        editor.LockGuides = false;
        await Settle();

        // Drag it onto the left ruler: deleted.
        canvas.GuidePress(onMoved, KeyModifiers.None, 1, true, false);
        canvas.GuideMove(new Point(5, onMoved.Y), KeyModifiers.None);
        canvas.GuideRelease(new Point(5, onMoved.Y));
        check(model.Guides.Count == 1 && doc.UndoText == "Undo Delete Guide", "dragging a guide onto a ruler deletes it");
        doc.Undo();
        check(model.Guides.Count == 2, "undo brings it back");

        // Rendered: the rulers along the top and left, guides in cyan.
        var shots = Environment.GetEnvironmentVariable("STRAYTA_SELFTEST_SHOTS");
        editor.ShowGrid = true;
        editor.GridUnit = RulerUnit.Pixels;
        editor.GridSpacing = 50;
        editor.GridSubdivisions = 5;
        await Settle();
        check(Math.Abs(canvas.GridSpacing - 50) < 1e-9 && editor.GridSpacingPixels(model) == 50, "the grid follows the settings (every 50 px)");
        {
            var size = new PixelSize((int)canvas.Bounds.Width, (int)canvas.Bounds.Height);
            using var bitmap = new RenderTargetBitmap(size);
            bitmap.Render(canvas);
            var pixels = new byte[size.Width * size.Height * 4];
            unsafe
            {
                fixed (byte* p = pixels) bitmap.CopyPixels(new Avalonia.PixelRect(size), (nint)p, pixels.Length, size.Width * 4);
            }
            (byte R, byte G, byte B) At(Point pt)
            {
                int i = ((int)pt.Y * size.Width + (int)pt.X) * 4;
                return (pixels[i], pixels[i + 1], pixels[i + 2]);
            }
            var guideAt = canvas.ImageToControl(model.Guides[1].Position, 77);
            var ruler = At(new Point(Screen(123, 0).X + 3, 3));
            check(At(guideAt) is (0, 255, 255), $"a vertical guide is drawn in cyan ({At(guideAt)})");
            check(ruler.R < 250 && ruler != At(canvas.ImageToControl(123, 3)), $"the top ruler is drawn over the canvas ({ruler})");
            var gridLine = At(canvas.ImageToControl(150, 33));
            check(gridLine.R < 250, $"a gridline is drawn every 50 px ({gridLine})");
            if (shots is { Length: > 0 }) bitmap.Save(Path.Combine(shots, "guides-rulers-grid.png"), new PngBitmapEncoderOptions());
        }
        editor.ShowGrid = false;

        // Ruler steps: labels at least ~56 screen points apart, in 1-2-5 steps.
        var (major, divisions) = ImageCanvas.RulerStep(1, RulerUnit.Pixels);
        var (inchMajor, inchDivisions) = ImageCanvas.RulerStep(72 * 4, RulerUnit.Inches);
        check(major == 100 && divisions == 10 && inchMajor == 0.25 && inchDivisions == 8,
            $"ruler ticks: 100 px labels at 100% zoom, quarter inches at 4× ({major}/{divisions}, {inchMajor}/{inchDivisions})");

        // The ruler origin: dragged from the corner, reset by double-clicking it.
        var corner = new Point(ImageCanvas.RulerSize / 2, ImageCanvas.RulerSize / 2);
        canvas.GuidePress(corner, KeyModifiers.None, 1, true, false);
        canvas.GuideMove(Screen(120, 70), KeyModifiers.None);
        canvas.GuideRelease(Screen(120, 70));
        check(Math.Abs(doc.RulerOrigin.X - 120) <= 1 / zoom + 1e-9 && Math.Abs(doc.RulerOrigin.Y - 70) <= 1 / zoom + 1e-9, $"the ruler origin is dragged from the corner ({doc.RulerOrigin})");

        // Info: position from the origin in ruler units, the color under the pointer, the document size.
        await Task.Delay(300);
        canvas.GuideMove(Screen(220, 170), KeyModifiers.None);
        var info = editor.Info;
        bool Near(string text, double expected, double tolerance) => double.TryParse(text, out double v) && Math.Abs(v - expected) <= tolerance;
        check(Near(info.X, 100, 1.5 / zoom) && Near(info.Y, 100, 1.5 / zoom) && info.R == "255" && info.Depth == "8-bit" && info.DocumentSize == "400 × 300 px",
            $"Info shows the position from the origin and the color under the pointer (X {info.X}, Y {info.Y}, R {info.R}, doc {info.DocumentSize})");
        editor.RulerUnit = RulerUnit.Percent;
        canvas.GuideMove(Screen(220, 170), KeyModifiers.None);
        check(Near(info.X, 25, 0.5) && info.Unit == "%", $"in Percent, x 100 px of a 400 px document reads 25 ({info.X} {info.Unit})");
        editor.RulerUnit = RulerUnit.Pixels;
        canvas.GuidePress(corner, KeyModifiers.None, 2, true, false);
        check(doc.RulerOrigin == default, "double-clicking the corner resets the origin");

        // Snapping: the shared service, and the Move tool through it.
        var snapper = doc.CreateSnapper(1);
        check(snapper.SnapX(203).Value == 200 && snapper.SnapX(209).Value == 209 && snapper.SnapY(0.5 + model.Guides[0].Position).Value == model.Guides[0].Position,
            "within 8 screen px points snap to guides, and not beyond");
        check(snapper.SnapRect(150, 10, 197, 20) == (3, 0), $"a moved box snaps its nearest edge ({snapper.SnapRect(150, 10, 197, 20)})");
        editor.SnapEnabled = false;
        check(!doc.CreateSnapper(1).IsActive, "View › Snap off: nothing snaps");
        editor.SnapEnabled = true;
        editor.ToggleSnapToCommand.Execute("Guides");
        check(!editor.SnapsTo(SnapTargets.Guides) && doc.CreateSnapper(1).SnapX(203).Kind == SnapKind.DocumentBounds
              && doc.CreateSnapper(1).SnapY(model.Guides[0].Position + 1).Kind == SnapKind.None,
            "Snap To › Guides off: guides are no longer targets (the document's center still is)");
        editor.ToggleSnapToCommand.Execute("All");

        doc.NewLayer();
        doc.SelectedLayer = doc.Layers[0];
        await doc.ApplySelectionGestureAsync(new SelectionGesture(CanvasTool.RectSelect, SelectionMode.Replace, new Strayta.Core.PixelRect(20, 20, 80, 60), []));
        await doc.FillWithAsync(new FillOptions(new FillSource.Color(new RgbColor(0.9f, 0.2f, 0.1f))));
        doc.Deselect();
        var red = (PixelLayer)doc.SelectedLayer!.Node;
        var before = red.Bounds;
        // Dragging right by (175 - 3) document pixels would leave its right edge 3 px short of the vertical guide at 200.
        canvas.MoveDragForTest(new Vector((200 - before.Right - 3) * zoom, 0));
        check(red.Bounds.Right == 200, $"the Move tool snaps the layer's edge onto the guide ({before} → {red.Bounds})");
        doc.Undo();
        canvas.MoveDragForTest(new Vector((200 - before.Right - 3) * zoom, 0), KeyModifiers.Control);
        check(!OperatingSystem.IsMacOS() || red.Bounds.Right == 197, $"holding Control moves without snapping ({red.Bounds})");
        while (red.Bounds != before && doc.CanUndo) doc.Undo();

        // New Guide Layout and Clear Guides.
        doc.AddGuideLayout(new GuideLayout { Columns = 4, ColumnGutter = 20, HasMargins = true, MarginLeft = 20, MarginRight = 20, MarginTop = 10, MarginBottom = 10 }, clearExisting: true);
        var vertical = model.Guides.Where(g => !g.IsHorizontal).Select(g => g.Position).Order().ToList();
        check(vertical.SequenceEqual([20.0, 95, 115, 190, 210, 285, 305, 380]) && model.Guides.Count(g => g.IsHorizontal) == 2 && doc.UndoText == "Undo New Guide Layout",
            $"New Guide Layout: four columns with 20 px gutters inside 20 px margins ({string.Join(", ", vertical)})");
        doc.ClearGuides();
        check(model.Guides.Count == 0 && doc.UndoText == "Undo Clear Guides", "Clear Guides removes them all, one step");
        doc.Undo();
        check(model.Guides.Count == 10, "and undo brings them back");

        // Image Size moves the guides with the pixels; undo restores them.
        await doc.ResizeImageAsync(200, 150, ResampleMethod.Bicubic, model.Resolution);
        check(model.Guides.Where(g => !g.IsHorizontal).Select(g => g.Position).Order().First() == 10 && canvas.Guides == model.Guides,
            $"Image Size to half scales the guides ({string.Join(", ", model.Guides.Take(3))})");
        doc.Undo();
        check(model.Width == 400 && model.Guides.Where(g => !g.IsHorizontal).Min(g => g.Position) == 20, "undo restores them");

        // Saved and read back.
        string dir = shots is { Length: > 0 } ? shots : Path.Combine(Path.GetTempPath(), "guides-selftest");
        Directory.CreateDirectory(dir);
        doc.AddGuide(new Guide(GuideOrientation.Horizontal, 100.5));
        string path = Path.Combine(dir, "guides-selftest.psd");
        await doc.SaveAsync(path);
        var reopened = PsdFile.OpenForEditing(path);
        check(reopened.Guides.SequenceEqual(model.Guides), $"guides are saved in the PSD and read back ({reopened.Guides.Count})");

        // The Photoshop check file: a 1200×800 page with a 12-column layout, margins and two loose guides.
        if (shots is { Length: > 0 })
        {
            var page = LayerFactory.NewDocument(1200, 800, whiteBackground: true);
            var pageDoc = new DocumentViewModel(page, null, editor);
            editor.Factory.AddDocument(pageDoc);
            editor.ActiveDocument = pageDoc;
            await pageDoc.RenderAsync();
            pageDoc.AddGuideLayout(new GuideLayout
            {
                Columns = 12, ColumnGutter = 20, Rows = 0, HasMargins = true, MarginTop = 60, MarginBottom = 60, MarginLeft = 60, MarginRight = 60,
            }, clearExisting: true);
            pageDoc.AddGuide(new Guide(GuideOrientation.Horizontal, 400));
            pageDoc.AddGuide(new Guide(GuideOrientation.Vertical, 333.25)); // a quarter pixel: Photoshop keeps 1/32
            pageDoc.NewLayer();
            await pageDoc.ApplySelectionGestureAsync(new SelectionGesture(CanvasTool.RectSelect, SelectionMode.Replace, new Strayta.Core.PixelRect(60, 60, 600, 400), []));
            await pageDoc.FillWithAsync(new FillOptions(new FillSource.Color(new RgbColor(0.2f, 0.45f, 0.9f))));
            pageDoc.Deselect();
            string check7 = Path.Combine(dir, "guides-photoshop-check.psd");
            await pageDoc.SaveAsync(check7);
            check(PsdFile.OpenForEditing(check7).Guides.Count == page.Guides.Count, $"Photoshop check file written ({check7}, {page.Guides.Count} guides)");
            pageDoc.CloseWithoutAsking();
        }
        doc.CloseWithoutAsking();
        editor.ShowRulers = false;
    }
}
