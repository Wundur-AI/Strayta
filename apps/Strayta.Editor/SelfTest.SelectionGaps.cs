using System.Diagnostics;
using Vector2 = System.Numerics.Vector2;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.VisualTree;
using Strayta.Core;
using Strayta.Core.Painting;
using Strayta.Core.Selection;
using Strayta.Editor.Controls;
using Strayta.Editor.ViewModels;
using Strayta.Editor.Views;
using Strayta.Segmentation;

namespace Strayta.Editor;

/// <summary>
/// Self-test steps for the selection, sampling, zoom and history additions: Magic Wand Sample Size and masks, the
/// Eyedropper's Sample menu, Quick Selection modes, zoom (spring-loaded keys, animated steps), History (limit,
/// snapshots, the History Brush, Reset Layout), the Object Finder and Object Selection's Lasso mode, and the Select and
/// Mask workspace (its tools, views, undo, Smart Radius, Decontaminate Colors, Remember Settings, refining a mask), with
/// timings on a 4000×3000 document.
/// </summary>
internal static partial class SelfTest
{
    /// <summary>STRAYTA_SELFTEST=selection: only these steps, for a quicker run.</summary>
    public static async Task RunSelectionGapsOnlyAsync(EditorViewModel editor)
    {
        int failures = 0;
        await RunSelectionGapStepsAsync(editor, (ok, what) =>
        {
            Console.WriteLine($"SELFTEST {(ok ? "ok  " : "FAIL")} {what}");
            if (!ok) failures++;
        });
        Console.WriteLine(failures == 0 ? "SELFTEST PASSED" : $"SELFTEST FAILED ({failures})");
    }

    private static async Task RunSelectionGapStepsAsync(EditorViewModel editor, Action<bool, string> check)
    {
        foreach (var (name, steps) in new (string, Func<Task>)[]
                 {
                     ("sampling", () => SamplingStepsAsync(editor, check)),
                     ("zoom", () => ZoomStepsAsync(editor, check)),
                     ("history", () => HistoryStepsAsync(editor, check)),
                     ("Object Finder", () => ObjectFinderStepsAsync(editor, check)),
                     ("Select and Mask workspace", () => WorkspaceStepsAsync(editor, check)),
                     ("Select and Mask timings", () => WorkspaceTimingsAsync(editor, check)),
                 })
        {
            try
            {
                await steps();
            }
            catch (Exception ex)
            {
                check(false, $"exception in {name} steps: {ex}");
            }
        }
    }

    private static async Task<DocumentViewModel> OpenTestDocumentAsync(EditorViewModel editor, Document model)
    {
        var doc = new DocumentViewModel(model, null, editor);
        editor.Factory.AddDocument(doc);
        editor.ActiveDocument = doc;
        await doc.RenderAsync();
        return doc;
    }

    private static LayerItemViewModel ItemOf(DocumentViewModel doc, LayerNode node) =>
        doc.Layers.SelectMany(l => l.SelfAndDescendants()).First(i => i.Node == node);

    // ---- Magic Wand, Eyedropper, Quick Selection ---------------------------------------------------------

    private static async Task SamplingStepsAsync(EditorViewModel editor, Action<bool, string> check)
    {
        // Background white; Red below an Invert adjustment; Top: blue over the left half.
        var model = Editing.LayerFactory.NewDocument(200, 100, whiteBackground: true);
        var red = ObjectTestLayer("Red", model.Bounds, (_, _) => (255, 0, 0, 255));
        var invert = new AdjustmentLayer { Name = "Invert", Kind = "Invert", Adjustment = new InvertAdjustment() };
        var top = ObjectTestLayer("Top", new PixelRect(0, 0, 100, 100), (_, _) => (0, 0, 255, 255));
        model.Root.Add(red);
        model.Root.Add(invert);
        model.Root.Add(top);
        var doc = await OpenTestDocumentAsync(editor, model);

        async Task<Color> Sample(LayerNode selected, LayerSample sample, int x)
        {
            doc.SelectedLayer = ItemOf(doc, selected);
            editor.EyedropperSampleIndex = (int)sample;
            await doc.LayerSampleImageAsync(sample);
            editor.ForegroundColor = Colors.Green;
            doc.BeginEyedropper(x, 50, background: false);
            doc.EndEyedropper();
            return editor.ForegroundColor;
        }
        check(editor.EyedropperSampleNames.Count == 5 && editor.EyedropperSample == LayerSample.AllLayers, "the Eyedropper's Sample menu has Photoshop's five choices, All Layers first chosen");
        var allRight = await Sample(top, LayerSample.AllLayers, 150);
        var noAdjRight = await Sample(top, LayerSample.AllLayersNoAdjustments, 150);
        var belowRed = await Sample(red, LayerSample.CurrentAndBelow, 150);
        var belowTop = await Sample(top, LayerSample.CurrentAndBelow, 150);
        var belowTopNoAdj = await Sample(top, LayerSample.CurrentAndBelowNoAdjustments, 150);
        var belowTopLeft = await Sample(top, LayerSample.CurrentAndBelowNoAdjustments, 50);
        check(allRight == Color.FromRgb(0, 255, 255) && noAdjRight == Colors.Red,
            $"All Layers samples through the Invert adjustment, All Layers No Adjustments without it ({allRight}, {noAdjRight})");
        check(belowRed == Colors.Red && belowTop == Color.FromRgb(0, 255, 255) && belowTopNoAdj == Colors.Red && belowTopLeft == Colors.Blue,
            $"Current & Below ignores the layers above, and the No Adjustments variant the adjustments ({belowRed}, {belowTop}, {belowTopNoAdj}, {belowTopLeft})");
        editor.EyedropperSampleIndex = (int)LayerSample.AllLayers;

        // Magic Wand in current-layer mode honors the layer's mask; Sample Size reaches the options.
        var green = ObjectTestLayer("Green", model.Bounds, (_, _) => (0, 200, 0, 255));
        var hide = new Plane(100, 100, 8, new byte[100 * 100]);
        green.Mask = new LayerMask { Bounds = new PixelRect(100, 0, 200, 100), Pixels = hide, DefaultColor = 255 };
        doc.Apply(new Editing.InsertEdit(green, model.Root, model.Root.Children.Count, "test"));
        doc.SelectedLayer = ItemOf(doc, green);
        (editor.WandSampleAllLayers, editor.WandTolerance, editor.WandContiguous, editor.WandAntiAlias) = (false, 10, true, false);
        editor.WandSampleSizeIndex = 1;
        check(editor.CurrentWandOptions.SampleSize == 3, "the wand's Sample Size (3 by 3 Average) reaches its options");
        await doc.MagicWandAsync(20, 50, SelectionMode.Replace);
        var wand = doc.Selection;
        check(wand is not null && wand.Bounds.Right == 100 && wand.CoverageAt(150, 50) == 0,
            $"the Magic Wand on the current layer stops where its layer mask hides it ({wand?.Bounds})");
        editor.WandSampleSizeIndex = 0;
        editor.WandAntiAlias = true;
        doc.Deselect();

        // Quick Selection: New starts over and switches to Add; Subtract takes away without Option.
        editor.Tool = CanvasTool.QuickSelect;
        editor.IsQuickSelectNew = true; // earlier steps left it on Add
        check(editor.QuickSelectMode == QuickSelectionMode.New && editor.QuickSelectDefaultMode == SelectionMode.Replace,
            "Quick Selection's New mode starts a new selection");
        doc.SelectedLayer = ItemOf(doc, red);
        editor.QuickSelectSampleAllLayers = true;
        doc.BeginQuickSelection(150, 50, editor.QuickSelectDefaultMode);
        doc.ContinueQuickSelection(160, 50);
        await doc.EndQuickSelectionAsync();
        check(editor.QuickSelectMode == QuickSelectionMode.Add && editor.QuickSelectDefaultMode == SelectionMode.Add && doc.Selection is not null,
            "after the first stroke the mode switches to Add, as in Photoshop");
        editor.IsQuickSelectSubtract = true;
        check(editor.QuickSelectDefaultMode == SelectionMode.Subtract, "the Subtract button makes plain strokes subtract");
        editor.QuickSelectMode = QuickSelectionMode.New;
        editor.QuickSelectSampleAllLayers = false;
        editor.Tool = CanvasTool.Move;
        editor.Factory.CloseDockable(doc);
    }

    // ---- Zoom ----------------------------------------------------------------------------------------

    private static async Task ZoomStepsAsync(EditorViewModel editor, Action<bool, string> check)
    {
        // Spring-loaded keys: holding Z uses the Zoom tool only while held; a tap switches for good.
        editor.Tool = CanvasTool.Brush;
        editor.SpringKeyDown("Z");
        editor.HandleToolKey("Z", shift: false);
        bool during = editor.Tool == CanvasTool.Zoom;
        editor.SpringKeyUp("Z", TimeSpan.FromMilliseconds(600));
        check(during && editor.Tool == CanvasTool.Brush, "holding Z zooms temporarily; releasing it returns to the Brush");
        editor.SpringKeyDown("Z");
        editor.HandleToolKey("Z", shift: false);
        editor.SpringKeyUp("Z", TimeSpan.FromMilliseconds(80));
        check(editor.Tool == CanvasTool.Zoom, "tapping Z switches to the Zoom tool for good");
        editor.Tool = CanvasTool.Move;

        // Animated zoom: a step glides to the next preset level in a short ease.
        var model = Editing.LayerFactory.NewDocument(400, 300, whiteBackground: true);
        var doc = await OpenTestDocumentAsync(editor, model);
        await Task.Delay(200);
        var window = (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        var canvas = window?.GetVisualDescendants().OfType<ImageCanvas>().FirstOrDefault(c => ReferenceEquals(c.DataContext, doc));
        if (canvas is null)
        {
            check(false, "the document's canvas is on screen for the zoom steps");
            return;
        }
        editor.AnimatedZoom = true;
        editor.ActualSizeCommand.Execute(null);
        var clock = Stopwatch.StartNew();
        editor.ZoomInCommand.Execute(null);
        bool animating = canvas.IsZoomAnimating && canvas.Zoom < 2;
        while (canvas.IsZoomAnimating && clock.ElapsedMilliseconds < 2000) await Task.Delay(5);
        long ms = clock.ElapsedMilliseconds;
        check(animating && canvas.Zoom == 2 && ms < 400, $"with Animated Zoom a step glides to 200% ({ms} ms)");
        editor.ZoomInCommand.Execute(null);
        editor.ZoomInCommand.Execute(null); // a second step during the animation continues from where it was heading
        while (canvas.IsZoomAnimating && clock.ElapsedMilliseconds < 4000) await Task.Delay(5);
        check(canvas.Zoom == 4, $"steps during an animation add up (at {canvas.Zoom * 100:0}%)");
        editor.AnimatedZoom = false;
        editor.ZoomOutCommand.Execute(null);
        check(!canvas.IsZoomAnimating && canvas.Zoom == 3, "with Animated Zoom off a step lands at once");
        editor.AnimatedZoom = true;
        editor.ScrubbyZoom = false;
        check(!canvas.ScrubbyZoom, "Scrubby Zoom off reaches the canvas (dragging then zooms into a rectangle)");
        editor.ScrubbyZoom = true;
        editor.FitCommand.Execute(null);
        editor.Factory.CloseDockable(doc);
    }

    // ---- History --------------------------------------------------------------------------------------

    private static async Task HistoryStepsAsync(EditorViewModel editor, Action<bool, string> check)
    {
        var model = Editing.LayerFactory.NewDocument(300, 200, whiteBackground: true);
        var doc = await OpenTestDocumentAsync(editor, model);
        HistoryToolViewModel? Panel() => editor.Factory.Find(d => d.Id == "History").OfType<HistoryToolViewModel>().FirstOrDefault();
        var panel = Panel();
        check(panel is not null && panel.Snapshots.Count == 1 && panel.Snapshots[0].Snapshot.Name == doc.Title && panel.Snapshots[0].IsBrushSource,
            "History starts with the opened document's snapshot, the History Brush's default source");
        var open = doc.Snapshots[0];
        var thumbClock = Stopwatch.StartNew();
        while (open.Thumbnail is null && thumbClock.ElapsedMilliseconds < 3000) await Task.Delay(20);
        check(open.Thumbnail is not null && panel?.Items[0].Thumbnail is not null, $"the snapshot and the Open row show a thumbnail ({thumbClock.ElapsedMilliseconds} ms)");

        // Paint red on a new layer, snapshot, paint blue over it.
        doc.NewLayer();
        var layer = (PixelLayer)doc.SelectedLayer!.Node;
        var brush = new BrushSettings(40, 1f, 1f);
        doc.BeginStroke(40, 100, brush, new RgbColor(1, 0, 0), erase: false);
        doc.ContinueStroke(260, 100);
        await doc.EndStrokeAsync();
        var redPixels = layer.Pixels;
        var snapshot = doc.NewSnapshot();
        check(panel?.Snapshots.Count == 2 && panel.Snapshots[1].Snapshot == snapshot && snapshot.Name == "Snapshot 1", "New Snapshot adds a row under the opened document");
        doc.BeginStroke(40, 100, brush, new RgbColor(0, 0, 1), erase: false);
        doc.ContinueStroke(260, 100);
        await doc.EndStrokeAsync();
        var bluePixels = layer.Pixels;

        // Clicking the snapshot restores it as a new step; undo brings the blue back.
        int steps = doc.HistoryEdits.Count;
        panel?.RestoreSnapshotCommand.Execute(panel.Snapshots[1]);
        var restored = doc.Model.Root.Children.OfType<PixelLayer>().FirstOrDefault(l => l.Name == layer.Name);
        check(restored is not null && ReferenceEquals(restored.Pixels, redPixels) && doc.UndoText == "Undo Snapshot 1" && doc.HistoryEdits.Count == steps + 1
              && panel?.Items[^1].Icon == "IconSnapshot",
            $"clicking a snapshot restores it as a new step ({doc.UndoText})");
        doc.Undo();
        check(ReferenceEquals(layer.Pixels, bluePixels) && model.Root.Children.Contains(layer), "undo goes back to the state before the snapshot was restored");

        // History Brush from the state after the red stroke: paints the left part red again.
        var redState = panel!.Items.First(i => i.Name == "Brush" && !i.IsFuture);
        panel.SetBrushSourceCommand.Execute(redState);
        check(redState.IsBrushSource && !panel.Snapshots[0].IsBrushSource && doc.HistoryBrushSource == redState.Edit,
            "clicking the left column of a state makes it the History Brush's source");
        editor.Tool = CanvasTool.HistoryBrush;
        check(editor.ToolName == "History Brush" && editor.IsPaintTool && editor.ToolGroups.Any(g => g.Key == "Y" && g.IsActive), "the History Brush is the Y tool with the brush options");
        doc.SelectedLayer = ItemOf(doc, layer);
        check(doc.BeginHistoryBrushStroke(40, 100), "a History Brush stroke starts on the painted layer");
        doc.ContinueStroke(120, 100);
        await doc.EndStrokeAsync();
        int At(int x, int y) => (y - layer.Bounds.Top) * layer.Bounds.Width + (x - layer.Bounds.Left);
        check(doc.UndoText == "Undo History Brush" && layer.Pixels!.ColorPlanes[0].Data[At(80, 100)] == 255 && layer.Pixels.ColorPlanes[2].Data[At(80, 100)] == 0
              && layer.Pixels.ColorPlanes[2].Data[At(220, 100)] == 255,
            "the History Brush paints the stroke's pixels back to the chosen state, one step");

        // From the opened document (the default snapshot) it takes pixels away: the layer did not exist then.
        panel.SetSnapshotBrushSourceCommand.Execute(panel.Snapshots[0]);
        check(!doc.BeginHistoryBrushStroke(200, 100) && doc.Notice.Contains("corresponding layer"),
            "painting from a state without the layer is refused, as in Photoshop");
        doc.SelectedLayer = doc.Layers.Last(); // the Background existed when opened
        panel.SetSnapshotBrushSourceCommand.Execute(panel.Snapshots[0]);
        doc.SelectedLayer = ItemOf(doc, layer);
        var added = layer.Pixels;
        // The restored snapshot's layer is a copy; the brush still finds "the same layer" in the snapshot.
        panel.SetSnapshotBrushSourceCommand.Execute(panel.Snapshots[1]);
        check(doc.BeginHistoryBrushStroke(240, 100), "a snapshot is a History Brush source too");
        doc.ContinueStroke(250, 100);
        await doc.EndStrokeAsync();
        check(!ReferenceEquals(layer.Pixels, added) && layer.Pixels!.ColorPlanes[0].Data[At(245, 100)] == 255, "painting from the snapshot restores its red");
        editor.Tool = CanvasTool.Move;

        // The history-state limit: the oldest steps are dropped.
        editor.HistoryStates = 3;
        check(doc.HistoryEdits.Count == 3 && doc.HistoryBaseEdit is not null && panel.Items.Count == 4 && panel.Items[0].Name == doc.HistoryBaseEdit.Description,
            $"History States 3 keeps the last three steps; the oldest row is the state the last dropped step made ({panel.Items[0].Name})");
        for (int i = 0; i < 5; i++) doc.NewLayer();
        check(doc.HistoryEdits.Count == 3 && panel.Items.Count == 4 && panel.Items.Skip(1).All(r => r.Name == "New Layer"), "new steps push the oldest out");
        while (doc.CanUndo) doc.Undo();
        check(doc.HistoryPosition == 0 && model.Root.Children.Count == 4, $"undo stops at the oldest kept state ({model.Root.Children.Count} layers)");
        editor.HistoryStates = EditorViewModel.DefaultHistoryStates;

        // Reset Layout replaces the panel; the old one stops following the editor and document.
        editor.ResetLayoutCommand.Execute(null);
        var fresh = Panel();
        int oldRows = panel.Items.Count;
        doc.NewLayer();
        check(panel.IsDisposed && fresh is not null && !ReferenceEquals(fresh, panel) && !fresh.IsDisposed && panel.Items.Count == 0 && fresh.Items.Count > 1,
            $"after Reset Layout the old History panel is detached (rows {oldRows} → {panel.Items.Count}) and the new one follows");
        editor.Factory.CloseDockable(doc);
    }

    // ---- Object Finder and Lasso mode ------------------------------------------------------------------

    private static async Task ObjectFinderStepsAsync(EditorViewModel editor, Action<bool, string> check)
    {
        if (!SegmentationEngine.Shared.CanSelectObjects)
        {
            Console.WriteLine("SELFTEST skip Object Finder steps: models not installed (dotnet build tools/FetchModels.proj)");
            return;
        }
        const int w = 1200, h = 900;
        const float cx = 420, cy = 430, r = 200;
        bool InDisc(int x, int y) => (x - cx + 0.5f) * (x - cx + 0.5f) + (y - cy + 0.5f) * (y - cy + 0.5f) <= r * r;
        var model = Editing.LayerFactory.NewDocument(w, h, whiteBackground: true);
        model.Root.Add(ObjectTestLayer("Scene", model.Bounds, (x, y) =>
            InDisc(x, y) ? ((byte)235, (byte)190, (byte)50, (byte)255)
            : x is >= 800 and < 1050 && y is >= 300 and < 550 ? ((byte)50, (byte)140, (byte)220, (byte)255)
            : ((byte)40, (byte)44, (byte)48, (byte)255)));
        var doc = await OpenTestDocumentAsync(editor, model);
        editor.ObjectFinder = true;
        var cpu = Process.GetCurrentProcess().TotalProcessorTime;
        var clock = Stopwatch.StartNew();
        editor.Tool = CanvasTool.ObjectSelect;
        bool done = await doc.WaitForObjectFinderAsync(TimeSpan.FromSeconds(90));
        double wall = clock.Elapsed.TotalMilliseconds, cores = (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds / wall;
        Console.WriteLine($"FINDERBENCH {w}×{h}: analysis + {DocumentViewModel.ObjectFinderGrid}² grid finder {wall:0} ms (finder alone {doc.ObjectFinderMs:0} ms), " +
                          $"{doc.FoundObjects.Count} objects, average CPU {cores:0.0} cores over the whole time");
        check(done && doc.FoundObjects.Count >= 2, $"the Object Finder finds the image's objects in the background ({doc.FoundObjects.Count} in {wall:0} ms)");

        var hover = Stopwatch.StartNew();
        doc.HoverObject(new Vector2(cx, cy));
        double hoverMs = hover.Elapsed.TotalMilliseconds;
        var outline = doc.ObjectHoverOutline;
        check(outline is { Count: > 0 } && outline.SelectMany(l => l).All(p => Math.Abs(Vector2.Distance(p, new Vector2(cx, cy)) - r) < 20),
            $"hovering highlights the object under the pointer ({hoverMs:0.00} ms)");
        doc.HoverObject(new Vector2(925, 425));
        check(doc.ObjectHoverOutline is { Count: > 0 } box && box.SelectMany(l => l).All(p => p.X > 780 && p.X < 1070), "hovering the other object highlights it instead");
        doc.HoverObject(null);
        check(doc.ObjectHoverOutline is null, "leaving the image clears the highlight");

        await doc.ApplySelectionGestureAsync(new SelectionGesture(CanvasTool.ObjectSelect, SelectionMode.Replace, PixelRect.Empty, [new Vector2(cx, cy)]));
        double iou = ObjectIou(doc.Selection, w, h, InDisc);
        check(iou > 0.9 && doc.UndoText == "Undo Object Selection", $"a click on a found object selects it (IoU {iou:F3}, {doc.LastObjectSelectionMs:0} ms)");
        doc.Deselect();

        // Lasso mode: a loose lasso around the disc is the prompt's box and bounds the result.
        editor.ObjectModeIndex = (int)ObjectSelectionShape.Lasso;
        check(editor.IsObjectLassoMode, "Object Selection has a Lasso mode");
        var path = Enumerable.Range(0, 12).Select(i => new Vector2(cx + (r + 45) * MathF.Cos(i * MathF.PI / 6), cy + (r + 45) * MathF.Sin(i * MathF.PI / 6))).ToList();
        var bounds = new PixelRect((int)(cx - r - 45), (int)(cy - r - 45), (int)(cx + r + 46), (int)(cy + r + 46));
        await doc.ApplySelectionGestureAsync(new SelectionGesture(CanvasTool.ObjectSelect, SelectionMode.Replace, bounds, path));
        double lassoIou = ObjectIou(doc.Selection, w, h, InDisc);
        check(lassoIou > 0.9 && doc.Selection!.Bounds.Right <= bounds.Right, $"a lasso drawn around an object selects it ({lassoIou:F3}; {doc.Notice})");
        // The result stays inside the path: here the path cuts through the disc's right side.
        var cut = new List<Vector2> { new(cx - r - 40, cy - r - 40), new(cx + 100, cy - r - 40), new(cx + 100, cy + r + 40), new(cx - r - 40, cy + r + 40) };
        await doc.ApplySelectionGestureAsync(new SelectionGesture(CanvasTool.ObjectSelect, SelectionMode.Replace,
            new PixelRect((int)(cx - r - 40), (int)(cy - r - 40), (int)(cx + 100), (int)(cy + r + 40)), cut));
        check(doc.Selection is { } clipped && clipped.Bounds.Right <= cx + 101, $"the result never leaves the lasso ({doc.Selection?.Bounds})");
        editor.ObjectModeIndex = 0;
        doc.Deselect();

        // An edit makes the objects stale; another tool pauses the finder.
        doc.NewLayer();
        check(doc.FoundObjects.Count == 0, "after an edit the found objects are out of date");
        editor.Tool = CanvasTool.Move;
        editor.Factory.CloseDockable(doc);
    }

    // ---- Select and Mask workspace ----------------------------------------------------------------------

    private static async Task WorkspaceStepsAsync(EditorViewModel editor, Action<bool, string> check)
    {
        var dialogs = new ScriptedSelectionDialogs();
        editor.SelectionDialogs = dialogs;
        try
        {
            const int W = 400, H = 300;
            var model = new Document(W, H, ColorMode.Rgb, 8);
            // Orange foreground left of x = 200 fading over 8 pixels into a blue background (a soft edge).
            var photo = ObjectTestLayer("Photo", model.Bounds, (x, _) =>
            {
                float a = Math.Clamp((204 - x) / 8f, 0, 1);
                return ((byte)(230 * a + 20 * (1 - a)), (byte)(150 * a + 60 * (1 - a)), (byte)(40 * a + 210 * (1 - a)), 255);
            });
            model.Root.Add(photo);
            var doc = await OpenTestDocumentAsync(editor, model);
            doc.SelectedLayer = ItemOf(doc, photo);
            var hard = SelectionMask.Rectangle(new PixelRect(0, 0, 200, H), model.Bounds);
            doc.SetSelection(hard, "Rectangular Marquee");
            var window = (MainWindow)(Avalonia.Application.Current!.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)!.MainWindow!;

            // The workspace replaces the tools and panels; its tools, views and undo work through the session.
            SelectAndMaskViewModel? seen = null;
            dialogs.SelectAndMask = async session =>
            {
                seen = session;
                var run = window.RunSelectAndMaskAsync(session);
                await session.WaitForFullAsync(TimeSpan.FromSeconds(5));
                check(window.SelectAndMaskView.IsVisible && editor.IsSelectAndMaskOpen && !editor.IsWorkspaceHidden,
                    "Select and Mask opens as a workspace in the main window, hiding the tool strip and options bar");

                // Quick Selection inside the workspace adds the brushed area, and is one step of the workspace's undo.
                session.Tool = RefineTool.QuickSelection;
                check(session.CanvasTool == CanvasTool.QuickSelect, "the workspace's Quick Selection drives the canvas tool");
                var before = session.BaseSelection;
                session.BeginQuickSelection(300, 150, SelectionMode.Replace);
                session.ContinueQuickSelection(320, 160);
                await session.EndQuickSelectionAsync();
                check(session.BaseSelection is { } grown && grown.CoverageAt(300, 150) == 255 && session.UndoText == "Undo Quick Selection",
                    "Quick Selection in the workspace adds to the selection being refined");
                editor.UndoCommand.Execute(null); // Edit › Undo acts inside the workspace
                check(ReferenceEquals(session.BaseSelection, before) && doc.UndoText == "Undo Rectangular Marquee", "⌘Z undoes inside the workspace, not in the document");
                session.Redo();
                check(session.BaseSelection?.CoverageAt(300, 150) == 255, "⇧⌘Z redoes it");
                session.Undo();

                // Brush subtracts with Option; Lasso adds a shape.
                session.Tool = RefineTool.Brush;
                session.BeginStroke(100, 20, option: true);
                session.ContinueStroke(100, 60);
                await Task.Delay(50);
                await session.EndStrokeAsync();
                check(session.BaseSelection?.CoverageAt(100, 40) == 0 && session.BaseSelection.CoverageAt(100, 200) == 255, "the workspace Brush with Option takes away");
                session.Tool = RefineTool.Lasso;
                await session.ApplyLassoAsync(new SelectionGesture(CanvasTool.Lasso, SelectionMode.Replace, PixelRect.Empty,
                    [new(320, 250), new(380, 250), new(380, 290), new(320, 290)]));
                check(session.BaseSelection?.CoverageAt(350, 270) == 255 && session.BaseSelection.CoverageAt(100, 200) == 255, "the workspace Lasso adds to the selection");
                session.Undo();
                session.Undo();

                // Every view mode draws; Marching Ants shows the refined outline.
                foreach (RefineView view in Enum.GetValues<RefineView>())
                {
                    session.View = view;
                    await session.WaitForFullAsync(TimeSpan.FromSeconds(5));
                    check(session.IsFullShown && session.Preview is not null && (view != RefineView.MarchingAnts || session.AntsOutline is { Count: > 0 }),
                        $"the {view} view draws the refined selection{(view == RefineView.MarchingAnts ? " with its outline" : "")}");
                }
                session.View = RefineView.OnionSkin;
                session.ViewAmount = 100;
                check(session.HasViewAmount && session.ViewAmountLabel.StartsWith("Transparency", StringComparison.Ordinal), "Onion Skin has a Transparency slider");
                session.View = RefineView.Overlay;

                // Smart Radius and Decontaminate; slider changes are undoable, a drag merges into one step.
                session.Radius = 20;
                session.Radius = 22;
                session.SmartRadius = true;
                session.Decontaminate = true;
                session.DecontaminateAmount = 100;
                check(session.EffectiveOutput == RefineOutput.NewLayerWithLayerMask && session.OutputNote.Length > 0,
                    "Decontaminate Colors forces the output to a new layer with a layer mask");
                session.Undo(); // amount
                session.Undo(); // decontaminate
                session.Undo(); // smart radius
                check(session.Radius == 22 && !session.SmartRadius && !session.Decontaminate, "settings changes undo one at a time");
                session.Undo();
                check(session.Radius == 0, "a slider drag is one step");
                session.Redo();
                session.SmartRadius = true;
                session.Decontaminate = true;
                session.DecontaminateAmount = 100;
                session.RememberSettings = true;
                await session.WaitForFullAsync(TimeSpan.FromSeconds(5));
                check(session.IsFullShown, $"Smart Radius and Decontaminate preview at full resolution ({session.Info})");
                window.CompleteSelectAndMask(ok: true);
                return await run;
            };
            await Click(MenuItem("Select", "Select and Mask…"));
            var copy = doc.SelectedLayer?.Node as PixelLayer;
            int At(int x, int y) => y * W + x;
            check(!editor.IsSelectAndMaskOpen && !window.SelectAndMaskView.IsVisible && copy is { Name: "Photo copy", Mask: not null } && !photo.Visible,
                "OK closes the workspace and outputs a masked copy of the layer");
            // The fringe's blue is replaced with the nearby orange.
            byte fringeBlue = copy?.Pixels?.ColorPlanes[2].Data[At(202, 150)] ?? 255, originalBlue = photo.Pixels!.ColorPlanes[2].Data[At(202, 150)];
            check(fringeBlue < originalBlue - 50 && copy!.Pixels!.ColorPlanes[2].Data[At(100, 150)] == photo.Pixels.ColorPlanes[2].Data[At(100, 150)],
                $"Decontaminate Colors replaces the fringe's background color ({originalBlue} → {fringeBlue}) and keeps the rest");
            doc.Undo();

            // Remember Settings: the next session starts with them.
            dialogs.SelectAndMask = session =>
            {
                seen = session;
                return Task.FromResult(false);
            };
            await Click(MenuItem("Select", "Select and Mask…"));
            check(seen is { RememberSettings: true, Radius: 22, SmartRadius: true, Decontaminate: true }, "Remember Settings starts the next session with the same settings");
            dialogs.SelectAndMask = session =>
            {
                session.RememberSettings = false;
                session.Decontaminate = false;
                session.Radius = 0;
                return Task.FromResult(true);
            };
            await Click(MenuItem("Select", "Select and Mask…"));
            dialogs.SelectAndMask = session =>
            {
                seen = session;
                return Task.FromResult(false);
            };
            await Click(MenuItem("Select", "Select and Mask…"));
            check(seen is { RememberSettings: false, Radius: 0, Decontaminate: false }, "turning Remember Settings off forgets them");

            // Refining an existing layer mask: target the mask, Select and Mask edits it and leaves the selection alone.
            doc.SelectedLayer = ItemOf(doc, photo);
            doc.EditMask = false;
            doc.SetSelection(hard, "Rectangular Marquee");
            doc.AddMaskFromSelection(reveal: true);
            doc.SetSelection(SelectionMask.Rectangle(new PixelRect(10, 10, 50, 50), model.Bounds), "Rectangular Marquee");
            var marquee = doc.Selection;
            doc.EditMask = true;
            var oldMask = photo.Mask;
            dialogs.SelectAndMask = session =>
            {
                seen = session;
                session.Feather = 4;
                return Task.FromResult(true);
            };
            await Click(MenuItem("Select", "Select and Mask…"));
            check(seen is { RefinesMaskOf: var owner, Output: RefineOutput.LayerMask } && owner == photo && seen.BaseSelection?.CoverageAt(100, 100) == 255
                  && seen.BaseSelection.CoverageAt(300, 100) == 0,
                "with the mask targeted, Select and Mask starts from the layer mask");
            check(photo.Mask is { } refinedMask && !ReferenceEquals(refinedMask, oldMask) && MaskBaker.Sample(refinedMask, 199, 150) is > 0.2f and < 0.8f
                  && ReferenceEquals(doc.Selection, marquee) && doc.UndoText == "Undo Select and Mask",
                "OK puts the refined result back into the mask as one step, keeping the selection");
            doc.Undo();
            check(ReferenceEquals(photo.Mask, oldMask), "undo restores the mask");

            // Option-click on the Layers panel's mask button hides instead of reveals.
            doc.DeleteMask();
            doc.Deselect();
            editor.AddHidingLayerMask();
            check(photo.Mask is { DefaultColor: 0 } && doc.UndoText == "Undo Hide All", "Option-click on the mask button adds a hiding mask (Hide All)");
            editor.Factory.CloseDockable(doc);
        }
        finally
        {
            editor.SelectionDialogs = null;
        }
    }

    /// <summary>Interactivity of the workspace's new parts on a 4000×3000 image: Smart Radius, Decontaminate, the Brush, Quick Selection.</summary>
    private static async Task WorkspaceTimingsAsync(EditorViewModel editor, Action<bool, string> check)
    {
        const int W = 4000, H = 3000;
        var model = Editing.LayerFactory.NewDocument(W, H, whiteBackground: false);
        var planes = Enumerable.Range(0, 3).Select(_ => Plane.Create(W, H, 8)).ToArray();
        Parallel.For(0, H, y =>
        {
            uint seed = (uint)y * 2654435761u;
            for (int x = 0; x < W; x++)
            {
                seed = seed * 1664525u + 1013904223u;
                int noise = (int)(seed >> 28) - 8, i = y * W + x;
                bool inside = (x - 2000) * (x - 2000) / 1.9 + (y - 1500) * (y - 1500) < 1100 * 1100;
                planes[0].Data[i] = (byte)Math.Clamp((inside ? 210 : 40) + noise, 0, 255);
                planes[1].Data[i] = (byte)Math.Clamp((inside ? 160 : 70 + y / 40) + noise, 0, 255);
                planes[2].Data[i] = (byte)Math.Clamp((inside ? 90 : 180) + noise, 0, 255);
            }
        });
        model.Root.Add(new PixelLayer { Name = "Photo", Bounds = model.Bounds, Pixels = new Raster(ColorMode.Rgb, planes, null) });
        var doc = await OpenTestDocumentAsync(editor, model);
        doc.SetSelection(SelectionMask.Ellipse(new PixelRect(500, 400, 3500, 2600), model.Bounds), "Elliptical Marquee");
        using var session = (await doc.BeginSelectAndMaskAsync())!;
        session.SetViewScale(0.5);
        await session.WaitForFullAsync(TimeSpan.FromSeconds(10));

        var clock = Stopwatch.StartNew();
        session.Radius = 30;
        session.SmartRadius = true;
        await session.WaitForFullAsync(TimeSpan.FromSeconds(20));
        double smart = clock.Elapsed.TotalMilliseconds, smartPreview = session.LastPreviewLatencyMs;
        clock.Restart();
        session.Decontaminate = true;
        await session.WaitForFullAsync(TimeSpan.FromSeconds(20));
        double decon = clock.Elapsed.TotalMilliseconds, deconPreview = session.LastPreviewLatencyMs;

        // A Feather drag with both on, and a Brush stroke: preview frames and their latency.
        var latencies = new List<double>();
        var infos = new List<string>();
        void OnShown()
        {
            if (!session.IsFullShown) latencies.Add(session.LastPreviewLatencyMs);
            infos.Add(session.Info);
        }
        session.PreviewShown += OnShown;
        for (int step = 1; step <= 30; step++)
        {
            session.Feather = step * 0.3;
            await Task.Delay(16);
        }
        await session.WaitForFullAsync(TimeSpan.FromSeconds(20));
        var feather = latencies.OrderBy(t => t).ToList();
        latencies.Clear();
        session.Tool = RefineTool.Brush;
        session.SelectBrushSize = 200;
        var stroke = Stopwatch.StartNew();
        session.BeginStroke(3400, 1500, option: false);
        for (int i = 1; i <= 90; i++)
        {
            session.ContinueStroke(3400 + i * 5, 1500 + i * 3);
            await Task.Delay(8);
        }
        double strokeMs = stroke.Elapsed.TotalMilliseconds;
        int brushFrames = latencies.Count;
        var release = Stopwatch.StartNew();
        await session.EndStrokeAsync();
        double releaseMs = release.Elapsed.TotalMilliseconds;
        await session.WaitForFullAsync(TimeSpan.FromSeconds(20));
        session.PreviewShown -= OnShown;
        double Median(List<double> v) => v.Count == 0 ? double.NaN : v[v.Count / 2];
        Console.WriteLine($"MASKBENCH 4000×3000 at 1/2 preview: Smart Radius 30 → preview {smartPreview:0} ms, full {smart:0} ms; Decontaminate → preview {deconPreview:0} ms, " +
                          $"full {decon:0} ms; Feather drag with both: {feather.Count} previews, median {Median(feather):0} ms after the change; Brush stroke: " +
                          $"{brushFrames} live frames in {strokeMs:0} ms ({brushFrames / (strokeMs / 1000):0.0} fps), release to selection {releaseMs:0} ms; frames: {string.Join(" / ", infos.TakeLast(12))}");
        check(Median(feather) < 300 && brushFrames >= 3,
            $"the workspace stays interactive on 4000×3000 with Smart Radius and Decontaminate (Feather median {Median(feather):0} ms, Brush {brushFrames} frames)");
        editor.Factory.CloseDockable(doc);
    }
}
