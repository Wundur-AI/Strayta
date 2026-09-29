using System.Diagnostics;
using Avalonia.Media;
using Strayta.Core;
using Strayta.Core.Paths;
using Strayta.Core.Selection;
using Strayta.Editor.Controls;
using Strayta.Editor.Editing;
using Strayta.Editor.ViewModels;
using Strayta.Psd;

namespace Strayta.Editor;

internal static partial class SelfTest
{
    private static (byte R, byte G, byte B, byte A) PixelAt(PixelLayer layer, int x, int y)
    {
        var b = layer.Bounds;
        if (layer.Pixels is not { } px || x < b.Left || y < b.Top || x >= b.Right || y >= b.Bottom) return (0, 0, 0, 0);
        int i = (y - b.Top) * b.Width + (x - b.Left);
        return (px.ColorPlanes[0].Data[i], px.ColorPlanes[1].Data[i], px.ColorPlanes[2].Data[i], px.Alpha?.Data[i] ?? 255);
    }

    private static void Draw(DocumentViewModel doc, double x0, double y0, double x1, double y1, bool shift = false, bool alt = false)
    {
        IPathTools tools = doc;
        tools.BeginShapeDrag(x0, y0);
        tools.UpdateShapeDrag(x0, y0, (x0 + x1) / 2, (y0 + y1) / 2, shift, alt);
        tools.UpdateShapeDrag(x0, y0, x1, y1, shift, alt);
        tools.EndShapeDrag();
    }

    private static void Pen(DocumentViewModel doc, double x, double y, double? dragX = null, double? dragY = null)
    {
        IPathTools tools = doc;
        tools.PenPress(x, y, false, 4);
        if (dragX is { } dx && dragY is { } dy) tools.PenDrag(dx, dy, false, false);
        tools.PenRelease();
    }

    /// <summary>
    /// Shapes and paths: every shape tool with Shift / Option, the options bar's fill, stroke and path operations,
    /// corner radii in Properties, the Pen (corners, smooth points, closing), Direct and Path Selection (drag, nudge,
    /// delete, Option-copy), Add / Delete / Convert Point, the Paths panel (Work Path, Save Path, Fill, Stroke, Make
    /// Selection, Make Work Path), moving and transforming shape layers, saving and reopening, rasterizing, timings on
    /// 4000×3000, and (STRAYTA_SHAPE_SAMPLES) the Photoshop check files.
    /// </summary>
    private static async Task RunShapeStepsAsync(EditorViewModel editor, Action<bool, string> check)
    {
        var model = LayerFactory.NewDocument(800, 600, whiteBackground: true);
        var doc = new DocumentViewModel(model, null, editor);
        editor.Factory.AddDocument(doc);
        editor.ActiveDocument = doc;
        await doc.RenderAsync();
        var o = editor.Shapes;
        o.Mode = ShapeToolMode.Shape;
        o.OperationIndex = 0;
        o.FillKindIndex = (int)ShapePaintKind.Color;
        o.FillColor = Colors.Red;
        o.StrokeKindIndex = (int)ShapePaintKind.None;
        o.CornerRadius = 0;
        doc.SelectedLayer = doc.Layers[0];

        // ---- Rectangle: drag, undo, redo --------------------------------------------------------------
        editor.Tool = CanvasTool.Rectangle;
        check(editor.ToolGroups.Any(g => g.Key == "U" && g.Tools.Count == 6) && editor.ToolGroups.Any(g => g.Key == "P") && editor.ToolGroups.Any(g => g.Key == "A"),
            "the tool strip has the Pen (P), Path Selection (A) and Shape (U) slots");
        int layersBefore = model.Root.Children.Count;
        Draw(doc, 100, 100, 300, 250);
        var rect = doc.SelectedLayer?.Node as PixelLayer;
        var rectData = ShapeLayers.Read(model, rect);
        check(rect is { Name: "Rectangle 1" } && model.Root.Children.Count == layersBefore + 1 && rectData is not null && rect.Tags.Contains("shape"),
            $"a rectangle drag makes a shape layer ({rect?.Name})");
        check(PixelAt(rect!, 200, 175) == (255, 0, 0, 255) && PixelAt(rect!, 99, 175).A == 0 && PixelAt(rect!, 300, 175).A == 0,
            "its pixels are the red fill inside the box, nothing outside");
        check(rectData!.LiveShapes is [{ Kind: LiveShapeKind.Rectangle, Left: 100, Top: 100, Right: 300, Bottom: 250 }], "it keeps a live rectangle");
        check(doc.UndoText == "Undo New Shape Layer", $"one undo step ({doc.UndoText})");
        doc.Undo();
        check(model.Root.Children.Count == layersBefore, "undo removes the shape layer");
        doc.Redo();
        check(model.Root.Children.Count == layersBefore + 1, "redo brings it back");
        doc.SelectedLayer = doc.Layers.SelectMany(l => l.SelfAndDescendants()).First(i => i.Node == rect);

        // ---- Shift and Option ------------------------------------------------------------------------
        editor.Tool = CanvasTool.Ellipse;
        Draw(doc, 400, 100, 500, 160, shift: true);
        var circle = ShapeLayers.Read(model, doc.SelectedLayer!.Node)!;
        check(circle.LiveShapes[0] is { Kind: LiveShapeKind.Ellipse } e && Math.Abs(e.Width - 100) < 1e-9 && Math.Abs(e.Height - 100) < 1e-9,
            "Shift draws a circle (the box stays square)");
        Draw(doc, 600, 300, 650, 330, alt: true);
        var centered = ShapeLayers.Read(model, doc.SelectedLayer!.Node)!.LiveShapes[0];
        check(centered is { Left: 550, Top: 270, Right: 650, Bottom: 330 }, "Option draws from the centre");

        // ---- Corner radius in Properties, restyling from the options bar -------------------------------
        doc.SelectedLayer = doc.Layers.SelectMany(l => l.SelfAndDescendants()).First(i => i.Node == rect);
        editor.Tool = CanvasTool.Rectangle;
        var panel = (doc.Properties as LayerPanel)?.Shape;
        check(panel is { HasCorners: true }, "Properties shows a Live Shape section with corners for the rectangle");
        panel!.Radius = 20;
        var rounded = ShapeLayers.Read(model, rect)!;
        check(rounded.LiveShapes[0] is { Kind: LiveShapeKind.RoundedRectangle, Radii.TopLeft: 20 } && rounded.Path.Subpaths[0].Knots.Count == 8,
            "a corner radius makes a live rounded rectangle");
        check(PixelAt(rect!, 101, 101).A == 0 && PixelAt(rect!, 120, 101).A > 200, "the corner is rounded in the pixels");
        panel.LinkCorners = false;
        panel.TopRight = 0;
        check(ShapeLayers.Read(model, rect)!.LiveShapes[0].Radii is { TopRight: 0, TopLeft: 20 } && PixelAt(rect!, 299, 100).A > 200,
            "corners can have their own radii");
        o.LoadFrom(ShapeLayers.Read(model, rect)!);
        o.StrokeKindIndex = (int)ShapePaintKind.Color;
        o.StrokeColor = Colors.Blue;
        o.StrokeAlignmentIndex = (int)StrokeAlignment.Outside;
        o.StrokeWidth = 6;
        var stroked = ShapeLayers.Read(model, rect)!;
        check(stroked.Stroke is { Enabled: true, Width: 6, Alignment: StrokeAlignment.Outside } && PixelAt(rect!, 97, 175) == (0, 0, 255, 255),
            "the options bar's stroke restyles the selected shape (outside, 6 px, blue)");
        check(doc.UndoText == "Undo Change Shape Stroke", $"a run of stroke changes is one step ({doc.UndoText})");
        o.DashIndex = 1;
        check(ShapeLayers.Read(model, rect)!.Stroke.Dashes.SequenceEqual([4.0, 2.0]), "dash presets apply");
        o.DashIndex = 0;
        o.StrokeKindIndex = (int)ShapePaintKind.None;

        // ---- Path operations -------------------------------------------------------------------------
        o.OperationIndex = 2; // Subtract Front Shape
        editor.Tool = CanvasTool.Ellipse;
        Draw(doc, 150, 125, 250, 225);
        var cut = ShapeLayers.Read(model, rect)!;
        check(cut.Path.Subpaths.Count == 2 && cut.Path.Subpaths[1].Operation == PathOperation.Subtract && PixelAt(rect!, 200, 175).A == 0 && PixelAt(rect!, 130, 175).A == 255,
            "Subtract Front Shape cuts the ellipse out of the selected shape");
        o.OperationIndex = 0;

        // ---- Polygon, star, triangle, line, custom shape -----------------------------------------------
        editor.Tool = CanvasTool.Polygon;
        o.Sides = 6;
        o.StarRatio = 50;
        Draw(doc, 350, 350, 450, 450);
        check(ShapeLayers.Read(model, doc.SelectedLayer!.Node)!.Path.Subpaths[0].Knots.Count == 12, "the polygon tool draws a six-pointed star");
        o.StarRatio = 100;
        editor.Tool = CanvasTool.Triangle;
        Draw(doc, 460, 350, 560, 450);
        check(ShapeLayers.Read(model, doc.SelectedLayer!.Node)!.Path.Subpaths[0].Knots.Count == 3, "the triangle tool draws a triangle");
        editor.Tool = CanvasTool.Line;
        o.LineWeight = 4;
        o.ArrowEnd = true;
        Draw(doc, 100, 500, 300, 500);
        var line = (PixelLayer)doc.SelectedLayer!.Node;
        check(PixelAt(line, 200, 500).A == 255 && PixelAt(line, 200, 505).A == 0 && PixelAt(line, 270, 505).A > 0,
            $"the line tool draws a 4 px line with an arrowhead ({PixelAt(line, 200, 500).A}, {PixelAt(line, 200, 505).A}, {PixelAt(line, 270, 505).A}, {line.Bounds})");
        o.ArrowEnd = false;
        editor.Tool = CanvasTool.CustomShape;
        o.CustomShape = "Ring";
        Draw(doc, 600, 400, 700, 500);
        var ring = (PixelLayer)doc.SelectedLayer!.Node;
        check(PixelAt(ring, 650, 450).A == 0 && PixelAt(ring, 610, 450).A == 255, "a custom shape with a hole (Ring) keeps its hole");

        // ---- Pen: a shape with a curve, closed ---------------------------------------------------------
        editor.Tool = CanvasTool.Pen;
        o.Mode = ShapeToolMode.Shape;
        doc.SelectedLayer = doc.Layers.SelectMany(l => l.SelfAndDescendants()).First(i => i.Node.Name == "Background");
        int before = model.Root.Children.Count;
        Pen(doc, 100, 300);
        Pen(doc, 200, 260, 240, 260);
        Pen(doc, 300, 340);
        check(doc.PathOverlay?.Path is { Subpaths: [{ Knots.Count: 3, Closed: false }] }, "the pen adds anchors to an open path");
        Pen(doc, 100.5, 300.5); // the first anchor: closes
        var penLayer = doc.SelectedLayer!.Node as PixelLayer;
        var penData = ShapeLayers.Read(model, penLayer);
        check(model.Root.Children.Count == before + 1 && penData?.Path.Subpaths is [{ Closed: true, Knots.Count: 3 } s]
              && s.Knots[1] is { Linked: true } k && Math.Abs(k.Out.X - 240) < 1e-3 && Math.Abs(k.In.X - 160) < 1e-3,
            $"clicking the first anchor closes the path; a drag made a smooth point ({model.Root.Children.Count - before} layer, "
            + $"{string.Join(" | ", penData?.Path.Subpaths.Select(x => $"{x.Closed} {string.Join(" ", x.Knots.Select(q => $"{q.In.X:F1}/{q.Anchor.X:F1}/{q.Out.X:F1}{(q.Linked ? "L" : "")}"))}") ?? [])})");
        check(PixelAt(penLayer!, 200, 300).A == 255, "the pen's shape layer is filled");

        // ---- Pen in Path mode: the Work Path, Make Selection -----------------------------------------------
        o.Mode = ShapeToolMode.Path;
        doc.SelectedLayer = doc.Layers.SelectMany(l => l.SelfAndDescendants()).First(i => i.Node.Name == "Background");
        Pen(doc, 500, 200);
        Pen(doc, 700, 200);
        Pen(doc, 700, 300);
        Pen(doc, 500, 300);
        Pen(doc, 500, 200);
        check(doc.DocumentPaths is [{ Kind: DocumentPathKind.Work, Path.Subpaths: [{ Closed: true, Knots.Count: 4 }] }], "the pen in Path mode draws the Work Path");
        check(doc.LoadPathAsSelection() && doc.Selection is { } sel && sel.CoverageAt(600, 250) == 255 && sel.CoverageAt(450, 250) == 0,
            "⌘Return loads the path as a selection");

        // ---- Direct Selection, Add / Delete / Convert Point ---------------------------------------------
        IPathTools tools = doc;
        editor.Tool = CanvasTool.DirectSelect;
        check(tools.SelectPress(700, 200, false, false, true, 4), "Direct Selection picks an anchor");
        tools.PathDragTo(720, 180, false);
        tools.PathRelease(null, true);
        var work = doc.DocumentPaths[0].Path;
        // (Document paths are stored in the PSD's 8.24 fixed-point fractions of the canvas: exact to 1/20000 px here.)
        static bool Near(PathPoint a, double x, double y) => Math.Abs(a.X - x) < 1e-3 && Math.Abs(a.Y - y) < 1e-3;
        check(Near(work.Subpaths[0].Knots[1].Anchor, 720, 180) && doc.UndoText == "Undo Drag Anchor", $"dragging moves the anchor (one step) ({work.Subpaths[0].Knots[1].Anchor}, {doc.UndoText})");
        tools.NudgePath(1, 0, true);
        check(Near(doc.DocumentPaths[0].Path.Subpaths[0].Knots[1].Anchor, 721, 180), "arrow keys nudge the selected anchor");
        tools.SelectPress(0, 0, false, false, true, 4);
        tools.PathRelease((490, 290, 710, 310), true);
        check(doc.SelectedKnots.Count == 2, $"a marquee selects the anchors inside it ({doc.SelectedKnots.Count})");
        editor.Tool = CanvasTool.AddAnchor;
        check(tools.AddAnchorAt(600, 300, 4) && doc.DocumentPaths[0].Path.Subpaths[0].Knots.Count == 5, "Add Anchor Point splits a segment");
        editor.Tool = CanvasTool.ConvertPoint;
        tools.ConvertPress(600, 300, 4);
        tools.PathDragTo(640, 300, false);
        tools.PathRelease(null, true);
        check(doc.DocumentPaths[0].Path.Subpaths[0].Knots.Any(kn => kn.Linked && kn.HasOut), "Convert Point drags out smooth handles");
        editor.Tool = CanvasTool.DeleteAnchor;
        check(tools.DeleteAnchorAt(600, 300, 4) && doc.DocumentPaths[0].Path.Subpaths[0].Knots.Count == 4, "Delete Anchor Point removes it");

        // ---- Path Selection: Option-drag copies, Delete removes -----------------------------------------
        editor.Tool = CanvasTool.PathSelect;
        tools.SelectPress(500, 250, false, alt: true, direct: false, 4);
        tools.PathDragTo(500, 450, false);
        tools.PathRelease(null, false);
        check(doc.DocumentPaths[0].Path.Subpaths.Count == 2 && Math.Abs(doc.DocumentPaths[0].Path.Subpaths[1].Knots[0].Anchor.Y - 400) < 1e-3,
            $"Option-drag with Path Selection moves a copy ({doc.DocumentPaths[0].Path.Subpaths.Count})");
        int selectedBefore = doc.SelectedKnots.Count;
        bool deleted = tools.DeleteSelectedPathItems(false);
        check(doc.DocumentPaths[0].Path.Subpaths.Count == 1, $"Delete removes the selected component ({selectedBefore} anchors selected, {deleted}, {doc.DocumentPaths[0].Path.Subpaths.Count} left)");

        // ---- Paths panel ------------------------------------------------------------------------------
        doc.SelectDocumentPath(0);
        doc.SavePath("Outline");
        check(doc.DocumentPaths is [{ Name: "Outline", Kind: DocumentPathKind.Saved }], "Save Path turns the Work Path into a saved path");
        doc.SelectedLayer = doc.Layers.SelectMany(l => l.SelfAndDescendants()).First(i => i.Node.Name == "Background");
        doc.NewLayer();
        var paint = (PixelLayer)doc.SelectedLayer!.Node;
        doc.SelectDocumentPath(0);
        await doc.FillPathAsync(new Core.Painting.FillOptions(new Core.Painting.FillSource.Color(new RgbColor(0, 1, 0))));
        check(PixelAt(paint, 600, 250) == (0, 255, 0, 255), "Fill Path fills the path's area on the selected layer");
        doc.Undo();
        await doc.StrokePathAsync(new Core.Painting.BrushSettings(9, 1f, 1f), new RgbColor(0, 0, 0));
        check(PixelAt(paint, 500, 250).A > 200 && PixelAt(paint, 600, 250).A == 0 && doc.UndoText == "Undo Stroke Path",
            $"Stroke Path paints along the path with the brush, one step ({PixelAt(paint, 500, 250).A}, {PixelAt(paint, 600, 250).A}, {doc.UndoText})");
        doc.SetSelection(SelectionMask.Ellipse(new PixelRect(100, 100, 300, 260), model.Bounds), "test");
        check(doc.MakeWorkPathFromSelection(2) && doc.DocumentPaths[0] is { Kind: DocumentPathKind.Work } w0
              && PathRasterizer.Rasterize(w0.Path, new PixelRect(0, 0, 800, 600)) is var cov && cov[180 * 800 + 200] == 255 && cov[105 * 800 + 105] == 0,
            "Make Work Path traces the selection");
        doc.Deselect();

        // ---- Moving and transforming shape layers ---------------------------------------------------------
        doc.SelectedLayer = doc.Layers.SelectMany(l => l.SelfAndDescendants()).First(i => i.Node == rect);
        var boxBefore = ShapeLayers.Read(model, rect)!.Path.CurveBounds()!.Value;
        doc.MoveSelected(10, 5);
        var boxAfter = ShapeLayers.Read(model, rect)!.Path.CurveBounds()!.Value;
        check(Math.Abs(boxAfter.Left - boxBefore.Left - 10) < 1e-3 && Math.Abs(boxAfter.Top - boxBefore.Top - 5) < 1e-3, "the Move tool moves the shape's outline with its pixels");
        check(doc.BeginFreeTransform(), "Free Transform opens on the shape layer");
        doc.FreeTransform!.WidthPercent = 150;
        await doc.CommitTransformAsync();
        var scaled = ShapeLayers.Read(model, rect)!;
        var sb = scaled.Path.CurveBounds()!.Value;
        check(Math.Abs(sb.Right - sb.Left - (boxBefore.Right - boxBefore.Left) * 1.5) < 0.5, "Free Transform scales the outline");
        int y = (int)((sb.Top + sb.Bottom) / 2);
        check(PixelAt(rect!, (int)Math.Floor(sb.Left) + 2, y) == (255, 0, 0, 255), "and the shape is drawn again from it (crisp, not resampled)");

        // ---- Saving and reopening -----------------------------------------------------------------------
        string dir = Path.Combine(Path.GetTempPath(), "strayta-shapes-selftest");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "shapes.psd");
        await doc.SaveAsync(file);
        var reopened = PsdFile.Open(file);
        var shapesBack = reopened.Layers.Where(PsdShapeLayer.IsShape).ToList();
        int shapeCount = model.Root.Descendants().Count(ShapeLayers.IsShape);
        check(shapesBack.Count == shapeCount && PsdPathResources.Read(reopened) is [{ Name: "Work Path" }, { Name: "Outline" }],
            $"saved shape layers ({shapesBack.Count}/{shapeCount}) and paths read back");
        var back = PsdShapeLayer.Read(shapesBack.First(r => r.Name == "Rectangle 1"), reopened.Header.Width, reopened.Header.Height)!;
        check(back.Stroke.Enabled == false && back.Path.Subpaths.Count == 2, "the rectangle's outline and settings read back");

        // ---- Rasterize --------------------------------------------------------------------------------
        doc.RasterizeSelected();
        check(!ShapeLayers.IsShape(rect) && rect!.Mask is null && PixelAt(rect, 130, 180).A == 255, "Layer › Rasterize turns the shape into pixels");
        doc.Undo();
        check(ShapeLayers.IsShape(rect), "and undo makes it a shape again");
        doc.CloseWithoutAsking();

        await RunShapeTimingsAsync(editor, check);
        if (Environment.GetEnvironmentVariable("STRAYTA_SHAPE_SAMPLES") is { Length: > 0 } samples)
            await WriteShapeSamplesAsync(editor, check, samples);
    }

    /// <summary>
    /// On a 4000×3000 document: release-to-layer time for a canvas-sized ellipse, and the live redraw of a shape layer
    /// while an anchor is dragged (SHAPEBENCH lines). Bounds are loose; the machine may be busy.
    /// </summary>
    private static async Task RunShapeTimingsAsync(EditorViewModel editor, Action<bool, string> check)
    {
        var model = LayerFactory.NewDocument(4000, 3000, whiteBackground: true);
        var doc = new DocumentViewModel(model, null, editor);
        editor.Factory.AddDocument(doc);
        editor.ActiveDocument = doc;
        await doc.RenderAsync();
        editor.Shapes.Mode = ShapeToolMode.Shape;
        editor.Shapes.OperationIndex = 0;
        editor.Shapes.StrokeKindIndex = (int)ShapePaintKind.Color;
        editor.Shapes.StrokeWidth = 12;
        editor.Tool = CanvasTool.Ellipse;
        IPathTools tools = doc;
        var clock = Stopwatch.StartNew();
        tools.BeginShapeDrag(20, 20);
        for (int i = 1; i <= 30; i++) tools.UpdateShapeDrag(20, 20, 20 + i * 132, 20 + i * 99, false, false);
        double dragMs = clock.Elapsed.TotalMilliseconds / 30;
        clock.Restart();
        tools.EndShapeDrag();
        double releaseMs = clock.Elapsed.TotalMilliseconds;
        Console.WriteLine($"SHAPEBENCH 4000x3000 ellipse with a 12 px stroke: drag update {dragMs:F2} ms, release to layer {releaseMs:F0} ms");
        check(doc.SelectedLayer?.Node is PixelLayer { Pixels: not null }, "a canvas-sized ellipse is drawn on 4000×3000");
        check(releaseMs < 3000, $"release to layer within a few frames under load ({releaseMs:F0} ms)");

        editor.Tool = CanvasTool.DirectSelect;
        var path = ShapeLayers.Read(model, doc.SelectedLayer!.Node)!.Path;
        var anchor = path.Subpaths[0].Knots[1].Anchor;
        tools.SelectPress(anchor.X, anchor.Y, false, false, true, 4);
        var times = new List<double>();
        for (int i = 1; i <= 12; i++)
        {
            tools.PathDragTo(anchor.X + i * 10, anchor.Y, false);
            await Settle(); // the redraw runs at background priority, after input
            times.Add(doc.LastShapePreviewMs);
        }
        tools.PathRelease(null, true);
        times.Sort();
        Console.WriteLine($"SHAPEBENCH anchor drag redraw on 4000x3000: median {times[times.Count / 2]:F1} ms, max {times[^1]:F1} ms");
        check(doc.UndoText == "Undo Drag Anchor", "the anchor drag is one step");
        doc.CloseWithoutAsking();
    }

    /// <summary>Files for checking in Photoshop that the shapes open as live shapes with their settings.</summary>
    private static async Task WriteShapeSamplesAsync(EditorViewModel editor, Action<bool, string> check, string dir)
    {
        Directory.CreateDirectory(dir);
        var o = editor.Shapes;
        async Task<DocumentViewModel> NewDoc()
        {
            var m = LayerFactory.NewDocument(800, 600, whiteBackground: true);
            var d = new DocumentViewModel(m, null, editor);
            editor.Factory.AddDocument(d);
            editor.ActiveDocument = d;
            await d.RenderAsync();
            d.SelectedLayer = d.Layers[0];
            o.Mode = ShapeToolMode.Shape;
            o.OperationIndex = 0;
            o.FillKindIndex = (int)ShapePaintKind.Color;
            o.StrokeKindIndex = (int)ShapePaintKind.None;
            o.CornerRadius = 0;
            o.DashIndex = 0;
            return d;
        }
        // Options bar changes restyle the selected shape layer (as in Photoshop), so each new style starts from the Background.
        void Unselect(DocumentViewModel d) => d.SelectedLayer = d.Layers.SelectMany(l => l.SelfAndDescendants()).First(i => i.Node.Name == "Background");
        async Task Save(DocumentViewModel d, string name)
        {
            string path = Path.Combine(dir, name);
            await d.SaveAsync(path);
            check(File.Exists(path), $"Photoshop check file {path}");
            d.CloseWithoutAsking();
        }

        // 1: rectangles — plain, rounded (per corner), with a dashed outside stroke.
        var doc = await NewDoc();
        editor.Tool = CanvasTool.Rectangle;
        o.FillColor = Color.FromRgb(0xE0, 0x40, 0x30);
        Draw(doc, 50, 50, 250, 200);
        Unselect(doc);
        o.CornerRadius = 24;
        o.FillColor = Color.FromRgb(0x30, 0x80, 0xE0);
        Draw(doc, 300, 50, 550, 200);
        if ((doc.Properties as LayerPanel)?.Shape is { } p)
        {
            p.LinkCorners = false;
            p.BottomRight = 60;
        }
        Unselect(doc);
        o.CornerRadius = 0;
        o.FillColor = Color.FromRgb(0xF0, 0xC0, 0x20);
        o.StrokeKindIndex = (int)ShapePaintKind.Color;
        o.StrokeColor = Colors.Black;
        o.StrokeWidth = 8;
        o.StrokeAlignmentIndex = 2;
        o.DashIndex = 1;
        Draw(doc, 100, 280, 500, 520);
        await Save(doc, "shapes-01-rectangles.psd");

        // 2: ellipse with a gradient fill, polygon, star, triangle.
        doc = await NewDoc();
        editor.Tool = CanvasTool.Ellipse;
        o.FillKindIndex = (int)ShapePaintKind.Gradient;
        o.FillGradient = GradientModel.TwoColor("Custom", new RgbColor(0.1f, 0.2f, 0.9f), new RgbColor(0.9f, 0.3f, 0.6f));
        Draw(doc, 40, 40, 360, 260);
        Unselect(doc);
        o.FillKindIndex = (int)ShapePaintKind.Color;
        o.FillColor = Color.FromRgb(0x20, 0xA0, 0x60);
        editor.Tool = CanvasTool.Polygon;
        o.Sides = 6;
        o.StarRatio = 100;
        Draw(doc, 420, 40, 620, 240);
        Unselect(doc);
        o.Sides = 5;
        o.StarRatio = 45;
        o.FillColor = Color.FromRgb(0xF0, 0xB0, 0x10);
        Draw(doc, 60, 320, 300, 560);
        editor.Tool = CanvasTool.Triangle;
        Unselect(doc);
        o.StarRatio = 100;
        o.FillColor = Color.FromRgb(0x80, 0x40, 0xC0);
        o.StrokeKindIndex = (int)ShapePaintKind.Color;
        o.StrokeColor = Colors.Black;
        o.StrokeWidth = 4;
        o.StrokeAlignmentIndex = 1;
        Draw(doc, 400, 320, 700, 560);
        await Save(doc, "shapes-02-ellipse-polygon-star-triangle.psd");

        // 3: lines with arrowheads, custom shapes (a ring keeps its hole).
        doc = await NewDoc();
        editor.Tool = CanvasTool.Line;
        o.FillColor = Colors.Black;
        o.LineWeight = 6;
        o.ArrowEnd = true;
        Draw(doc, 60, 80, 400, 80);
        Unselect(doc);
        o.ArrowStart = true;
        Draw(doc, 60, 160, 400, 300);
        Unselect(doc);
        o.ArrowStart = o.ArrowEnd = false;
        editor.Tool = CanvasTool.CustomShape;
        foreach (var (name, x) in new[] { ("Heart", 40), ("Ring", 230), ("Speech Bubble", 420), ("Check Mark", 600) })
        {
            Unselect(doc);
            o.CustomShape = name;
            o.FillColor = Color.FromRgb((byte)(x % 255), 0x60, 0xC0);
            Draw(doc, x, 380, x + 160, 540);
        }
        await Save(doc, "shapes-03-lines-custom-shapes.psd");

        // 4: path operations in one shape layer.
        doc = await NewDoc();
        editor.Tool = CanvasTool.Rectangle;
        o.FillColor = Color.FromRgb(0x20, 0x60, 0xD0);
        Draw(doc, 100, 100, 700, 500);
        editor.Tool = CanvasTool.Ellipse;
        o.OperationIndex = 2; // subtract
        Draw(doc, 150, 150, 350, 350);
        o.OperationIndex = 4; // exclude
        Draw(doc, 450, 150, 750, 350);
        o.OperationIndex = 1; // combine
        editor.Tool = CanvasTool.Polygon;
        o.Sides = 3;
        Draw(doc, 300, 380, 500, 580);
        await Save(doc, "shapes-04-path-operations.psd");

        // 5: pen paths: a curved shape layer, the Work Path and a saved path.
        doc = await NewDoc();
        editor.Tool = CanvasTool.Pen;
        o.FillColor = Color.FromRgb(0xD0, 0x50, 0x90);
        Pen(doc, 100, 400);
        Pen(doc, 250, 150, 350, 150);
        Pen(doc, 450, 400, 450, 480);
        Pen(doc, 100, 400);
        Unselect(doc);
        o.Mode = ShapeToolMode.Path;
        doc.SelectedLayer = doc.Layers.SelectMany(l => l.SelfAndDescendants()).First(i => i.Node.Name == "Background");
        Pen(doc, 520, 100);
        Pen(doc, 700, 100, 760, 160);
        Pen(doc, 700, 300);
        Pen(doc, 520, 100);
        doc.SelectDocumentPath(0);
        doc.SavePath("Saved Curve");
        Pen(doc, 520, 400);
        Pen(doc, 740, 520);
        doc.FinishPen();
        await Save(doc, "shapes-05-pen-and-paths.psd");

        // 6: a Photoshop file's shapes edited (when a corpus file with live shapes is at hand).
        if (Environment.GetEnvironmentVariable("STRAYTA_CORPUS") is { } corpus && Directory.Exists(corpus)
            && Directory.EnumerateFiles(corpus, "*.psd").FirstOrDefault(f => Path.GetFileName(f).Contains("btn_AlignTop")) is { } source)
        {
            var m = PsdFile.OpenForEditing(source);
            var d = new DocumentViewModel(m, null, editor);
            editor.Factory.AddDocument(d);
            editor.ActiveDocument = d;
            await d.RenderAsync();
            var shape = m.Root.Descendants().OfType<PixelLayer>().First(ShapeLayers.IsShape);
            var data = ShapeLayers.Read(m, shape)!;
            d.Apply(ShapeLayers.Edit(m, shape, data with { Fill = ShapeContent.Solid(new RgbColor(0.9f, 0.1f, 0.1f)) }, "Change Shape Fill"));
            await Save(d, "shapes-06-photoshop-file-recolored.psd");
        }
    }
}
