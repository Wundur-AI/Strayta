using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.VisualTree;
using Strayta.Core;
using Strayta.Core.Painting;
using Strayta.Core.Selection;
using Strayta.Editor.Controls;
using Strayta.Editor.ViewModels;

namespace Strayta.Editor;

internal static partial class SelfTest
{
    private static async Task<bool> Until(Func<bool> condition, int timeoutMs = 3000)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.ElapsedMilliseconds > timeoutMs) return false;
            await Task.Delay(10);
        }
        return true;
    }

    /// <summary>
    /// Eyedropper, Paint Bucket, Gradient, Zoom and the History panel through the real view models: tool keys, options,
    /// selections, undo, and jumping through history.
    /// </summary>
    private static async Task RunEverydayToolStepsAsync(EditorViewModel editor, Action<bool, string> check)
    {
        try
        {
            // ---- Tool keys ------------------------------------------------------------------------------
            editor.HandleToolKey("I", shift: false);
            check(editor.IsEyedropperTool && editor.ToolName == "Eyedropper", "I selects the Eyedropper");
            editor.HandleToolKey("G", shift: false);
            check(editor.IsGradientTool && editor.ToolName == "Gradient", "G selects the Gradient first, as in Photoshop");
            editor.HandleToolKey("G", shift: true);
            check(editor.IsPaintBucketTool && editor.ToolName == "Paint Bucket", "Shift+G switches to the Paint Bucket");
            editor.HandleToolKey("Z", shift: false);
            editor.HandleToolKey("G", shift: false);
            check(editor.IsPaintBucketTool, "G comes back to the Paint Bucket, the tool used last");
            editor.HandleToolKey("Z", shift: false);
            check(editor.IsZoomTool && editor.ToolName == "Zoom", "Z selects the Zoom tool");

            // A document: white Background, a "Stripes" layer with 1-pixel black/white columns on the left half
            // (transparent on the right), and a red square in the middle of the right half.
            const int w = 200, h = 100;
            var model = Editing.LayerFactory.NewDocument(w, h, whiteBackground: true);
            var planes = Enumerable.Range(0, 4).Select(_ => Plane.Create(w, h, 8)).ToArray();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    (byte v, byte a) = x < 100 ? (x % 2 == 0 ? (byte)0 : (byte)255, (byte)255) : ((byte)0, (byte)0);
                    (planes[0].Data[i], planes[1].Data[i], planes[2].Data[i], planes[3].Data[i]) = (v, v, v, a);
                    if (x is >= 140 and < 160 && y is >= 40 and < 60)
                        (planes[0].Data[i], planes[1].Data[i], planes[2].Data[i], planes[3].Data[i]) = (220, 20, 20, 255);
                }
            var stripes = new PixelLayer { Name = "Stripes", Bounds = model.Bounds, Pixels = new Raster(ColorMode.Rgb, planes[..3], planes[3]) };
            model.Root.Add(stripes);
            var doc = new DocumentViewModel(model, null, editor);
            editor.Factory.AddDocument(doc);
            editor.ActiveDocument = doc;
            await doc.RenderAsync();
            LayerItemViewModel Item(LayerNode n) => doc.Layers.SelectMany(l => l.SelfAndDescendants()).First(i => i.Node == n);
            doc.SelectedLayer = Item(stripes);

            // ---- Eyedropper -----------------------------------------------------------------------------
            editor.SetToolCommand.Execute("Eyedropper");
            editor.EyedropperSampleIndex = 0; // Current Layer
            editor.EyedropperSizeIndex = 0;   // Point
            editor.ForegroundColor = Colors.Green;
            doc.BeginEyedropper(10, 50, background: false);
            doc.EndEyedropper();
            check(await Until(() => editor.ForegroundColor == Colors.Black), $"a point sample of a black column picks black ({editor.ForegroundColor})");
            doc.BeginEyedropper(11, 50, background: false);
            doc.SampleEyedropper(12, 50);
            doc.SampleEyedropper(11, 50); // a drag: the last position wins
            doc.EndEyedropper();
            check(editor.ForegroundColor == Colors.White && doc.LastEyedropperMs < 5,
                $"once prepared, samples are immediate during a drag ({doc.LastEyedropperMs:F3} ms)");
            editor.EyedropperSizeIndex = 1; // 3 by 3: columns 10, 11, 12 are black, white, black
            doc.BeginEyedropper(11, 50, background: false);
            doc.EndEyedropper();
            check(editor.ForegroundColor.R is 85 && editor.ForegroundColor.G == 85, $"3 by 3 Average mixes the columns ({editor.ForegroundColor})");
            editor.EyedropperSizeIndex = 2; // 5 by 5 around a white column: 3 white of 5
            doc.BeginEyedropper(11, 50, background: false);
            doc.EndEyedropper();
            check(editor.ForegroundColor.R == 102 || editor.ForegroundColor.R == 153, $"5 by 5 Average mixes five columns ({editor.ForegroundColor})");
            editor.EyedropperSizeIndex = 0;
            var before = editor.ForegroundColor;
            doc.BeginEyedropper(120, 10, background: false);
            doc.EndEyedropper();
            check(editor.ForegroundColor == before, "sampling a transparent pixel of the current layer leaves the color alone");
            editor.EyedropperSampleIndex = (int)LayerSample.AllLayers; // the white background shows through there
            editor.BackgroundColor = Colors.Blue;
            doc.BeginEyedropper(120, 10, background: true);
            doc.EndEyedropper();
            check(await Until(() => editor.BackgroundColor == Colors.White), "All Layers sees the Background through the transparent part");
            doc.BeginEyedropper(150, 50, background: true);
            doc.EndEyedropper();
            check(await Until(() => editor.BackgroundColor == Color.FromRgb(220, 20, 20)) && editor.ForegroundColor == before,
                $"All Layers samples the image, and Option samples into the background color ({editor.BackgroundColor})");

            // ---- Paint Bucket -----------------------------------------------------------------------------
            editor.SetToolCommand.Execute("PaintBucket");
            doc.NewLayer();
            var paint = (PixelLayer)doc.SelectedLayer!.Node;
            editor.ForegroundColor = Color.FromRgb(0, 200, 0);
            editor.BucketSampleAllLayers = false;
            doc.SetSelection(SelectionMask.Rectangle(new PixelRect(20, 20, 60, 50), model.Bounds), "Rectangular Marquee");
            await doc.PaintBucketAsync(5, 5);
            check(paint.Pixels is null && doc.UndoText == "Undo Rectangular Marquee", "a Paint Bucket click outside the selection does nothing");
            await doc.PaintBucketAsync(30, 30);
            int At(PixelLayer l, int x, int y) => (y - l.Bounds.Top) * l.Bounds.Width + (x - l.Bounds.Left);
            check(paint.Pixels is not null && paint.Bounds == new PixelRect(20, 20, 60, 50) && paint.Pixels.ColorPlanes[1].Data[At(paint, 30, 30)] == 200
                  && paint.Pixels.Alpha!.Data[At(paint, 30, 30)] == 255 && doc.UndoText == "Undo Paint Bucket",
                $"the bucket fills the empty layer inside the selection only, as one step ({paint.Bounds}, {doc.LastBucketMs:F0} ms)");
            doc.Undo();
            check(paint.Pixels is null, "undo removes the fill");
            doc.Deselect();
            editor.BucketSampleAllLayers = true;
            editor.BucketTolerance = 10;
            editor.BucketAntiAlias = false;
            await doc.PaintBucketAsync(150, 50);
            check(paint.Pixels is not null && paint.Bounds == new PixelRect(140, 40, 160, 60),
                $"with All Layers the bucket fills the red square it sees in the image ({paint.Bounds})");
            doc.Undo();
            editor.BucketAntiAlias = true;
            doc.NewGroup();
            await doc.PaintBucketAsync(150, 50);
            check(doc.PaintBlock is not null && doc.UndoText == "Undo New Group", "the bucket on a group is refused like the brush");
            doc.Undo();
            doc.SelectedLayer = Item(paint);

            // ---- Gradient ---------------------------------------------------------------------------------
            editor.SetToolCommand.Execute("Gradient");
            editor.ForegroundColor = Colors.Black;
            editor.BackgroundColor = Colors.White;
            editor.GradientPresetIndex = 0;
            editor.GradientDither = false;
            editor.GradientOpacity = 100;
            (byte R, byte A) Pixel(int x, int y) => paint.Pixels is { } p ? (p.ColorPlanes[0].Data[At(paint, x, y)], p.Alpha!.Data[At(paint, x, y)]) : ((byte)0, (byte)0);
            foreach (var type in Enum.GetValues<GradientType>())
            {
                editor.GradientType = type;
                int frames = 0;
                void OnFrame(bool full) => frames++;
                doc.FrameDisplayed += OnFrame;
                check(doc.BeginGradient(100, 50), $"{type} gradient drag starts");
                for (int x = 110; x <= 180; x += 10)
                {
                    doc.MoveGradient(x, 50);
                    await Task.Delay(20);
                }
                bool untouched = paint.Pixels is null;
                doc.FrameDisplayed -= OnFrame;
                await doc.EndGradientAsync();
                var (start, end, far, left) = (Pixel(100, 50), Pixel(179, 50), Pixel(199, 99), Pixel(20, 50));
                bool shape = type switch
                {
                    GradientType.Linear => start.R < 10 && end.R > 245 && left.R < 5 && far.R > 245,
                    GradientType.Radial => start.R < 10 && end.R > 245 && Pixel(100, 85).R is > 100 and < 140,
                    // Counterclockwise on screen from the drag line: just above it the sweep starts, just below it ends.
                    GradientType.Angle => Pixel(150, 49).R < 60 && Pixel(150, 51).R > 200 && Pixel(50, 50).R is > 110 and < 145,
                    GradientType.Reflected => start.R < 10 && left.R > 245 && end.R > 245,
                    GradientType.Diamond => start.R < 10 && end.R > 245 && Pixel(140, 90).R > 245 && Pixel(130, 60).R is > 110 and < 150,
                    _ => false,
                };
                check(untouched && frames > 0 && shape && paint.Bounds == model.Bounds && start.A == 255 && doc.UndoText == "Undo Gradient",
                    $"{type}: previewed without touching the layer ({frames} frames), drawn over the canvas as one step " +
                    $"(start {start.R}, end {end.R}, left {left.R}, far {far.R}; bake {doc.LastGradientBakeMs:F0} ms)");
                doc.Undo();
                check(paint.Pixels is null, $"undo removes the {type} gradient");
            }

            editor.GradientType = GradientType.Linear;
            editor.GradientPresetIndex = 1; // foreground to transparent
            editor.GradientReverse = true;
            doc.SetSelection(SelectionMask.Rectangle(new PixelRect(0, 0, 100, 100), model.Bounds), "Rectangular Marquee");
            doc.BeginGradient(0, 0);
            doc.MoveGradient(100, 0);
            await doc.EndGradientAsync();
            check(paint.Bounds == new PixelRect(0, 0, 100, 100) && Pixel(2, 5).A < 10 && Pixel(97, 5) is { R: < 5, A: > 245 },
                $"Foreground to Transparent, reversed, fills only the selection ({paint.Bounds}, alpha {Pixel(2, 5).A} → {Pixel(97, 5).A})");
            editor.GradientReverse = false;
            editor.GradientPresetIndex = 0;
            doc.Deselect();
            doc.BeginGradient(0, 0);
            await doc.EndGradientAsync(); // a click without a drag
            check(doc.UndoText == "Undo Deselect", "a click without a drag draws nothing");

            // Into a layer mask: black to white fades the layer out.
            doc.AddMask(reveal: true);
            var unmasked = paint.Pixels;
            doc.BeginGradient(0, 0);
            doc.MoveGradient(200, 0);
            await doc.EndGradientAsync();
            check(paint.Mask is { Pixels: { } mp } && mp.Data[10] < 20 && mp.Data[190] > 235 && ReferenceEquals(paint.Pixels, unmasked) && doc.UndoText == "Undo Gradient",
                "with the mask targeted the gradient paints the mask in gray");
            doc.Undo();
            doc.Undo();
            doc.EditMask = false;
            doc.SelectAll(); // a new edit: the undone mask steps are gone

            // ---- Zoom -------------------------------------------------------------------------------------
            if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
            {
                await Task.Delay(100);
                var canvas = window.GetVisualDescendants().OfType<ImageCanvas>().FirstOrDefault(c => ReferenceEquals(c.DataContext, doc));
                if (canvas is null) check(false, "the document's canvas is on screen");
                else
                {
                    editor.AnimatedZoom = false; // these steps land at once (animated steps: SelfTest.SelectionGaps.cs)
                    editor.ActualSizeCommand.Execute(null);
                    check(canvas.Zoom == 1, "⌘1 shows actual pixels");
                    editor.ZoomInCommand.Execute(null);
                    check(canvas.Zoom == 2, $"⌘+ zooms in one step ({canvas.Zoom})");
                    editor.ZoomOutCommand.Execute(null);
                    editor.ZoomOutCommand.Execute(null);
                    check(Math.Abs(canvas.Zoom - 2 / 3.0) < 1e-9, $"⌘− steps out through Photoshop's levels ({canvas.Zoom:F4})");
                    var anchor = canvas.ImageToControl(150, 50);
                    canvas.ZoomStep(zoomIn: true, anchor);
                    var after = canvas.ImageToControl(150, 50);
                    check(canvas.Zoom == 1 && Math.Abs(after.X - anchor.X) <= 1 && Math.Abs(after.Y - anchor.Y) <= 1,
                        "a Zoom-tool click zooms in keeping the clicked point under the pointer");
                    editor.ToolGroups.Single(g => g.Key == "Z").DoubleClicked();
                    check(canvas.Zoom == 1, "double-clicking the Zoom tool shows 100%");
                    editor.FitCommand.Execute(null);
                    editor.AnimatedZoom = true;
                }
            }

            // ---- History panel ----------------------------------------------------------------------------
            var history = editor.Factory.Find(d => d.Id == "History").OfType<HistoryToolViewModel>().FirstOrDefault();
            if (history is null) check(false, "the History panel is in the layout");
            else
            {
                int edits = doc.HistoryEdits.Count;
                check(history.Items.Count == edits + 1 && history.Items[0].Name == "Open" && history.Current == history.Items[^1]
                      && history.Items.All(i => !i.IsFuture),
                    $"History lists Open and every step, the last one current ({history.Items.Count} rows: …{string.Join(", ", history.Items.TakeLast(3).Select(i => i.Name))})");
                check(history.Items.Any(i => i.Name == "Gradient" && i.Icon == "IconGradient") && history.Items.Any(i => i.Name == "Rectangular Marquee" && i.Icon == "IconMarqueeRect")
                      && history.Items.Any(i => i.Name == "New Layer" && i.Icon == "IconNewLayer"),
                    "steps are named as in the Edit menu, with the tool's icon");
                // Jump back to just before the gradient, then forward again.
                int gradientRow = history.Items.ToList().FindIndex(i => i.Name == "Gradient");
                history.Current = history.Items[gradientRow - 1];
                check(doc.HistoryPosition == gradientRow - 1 && paint.Pixels is null && history.Items.Skip(gradientRow).All(i => i.IsFuture)
                      && !history.Items[gradientRow - 1].IsFuture && history.Current == history.Items[gradientRow - 1],
                    "clicking a step undoes to it; the later steps are dimmed");
                history.Current = history.Items[^1];
                check(doc.HistoryPosition == edits && history.Items.All(i => !i.IsFuture) && paint.Pixels is not null,
                    "clicking the last step redoes everything");
                history.Current = history.Items[0];
                check(doc.HistoryPosition == 0 && !doc.CanUndo && model.Root.Children.Count == 2, "clicking Open returns to the document as opened");
                history.Current = history.Items[gradientRow];
                doc.Deselect(); // a new edit after stepping back
                check(history.Items.Count == gradientRow + 2 && history.Items[^1].Name == "Deselect" && history.Items.All(i => !i.IsFuture) && !doc.CanRedo,
                    $"a new edit after stepping back discards the later steps ({history.Items.Count} rows)");

                // It follows the active document.
                var other = new DocumentViewModel(Editing.LayerFactory.NewDocument(50, 50, whiteBackground: true), null, editor);
                editor.Factory.AddDocument(other);
                editor.ActiveDocument = other;
                check(history.Items.Count == 1 && history.Items[0].Name == "Open", "History follows the active document");
                other.SelectAll();
                check(history.Items.Count == 2 && history.Items[1].Name == "Select All", "and shows its edits");
                editor.ActiveDocument = doc;
                check(history.Items.Count == gradientRow + 2, "switching back shows the first document's history again");
                editor.Factory.CloseDockable(other);
            }
            editor.ActiveDocument = doc;
            editor.Factory.CloseDockable(doc);
            editor.SetToolCommand.Execute("Move");
        }
        catch (Exception ex)
        {
            check(false, $"exception in everyday tool steps: {ex}");
        }
    }

    /// <summary>
    /// STRAYTA_TOOLBENCH=new: a generated 4000×3000 document (a noisy photo-like layer with flat discs), then the
    /// Gradient drag, Paint Bucket and Eyedropper timings.
    /// </summary>
    public static async Task RunSyntheticToolsBenchmarkAsync(EditorViewModel editor)
    {
        const int w = 4000, h = 3000;
        var model = Editing.LayerFactory.NewDocument(w, h, whiteBackground: true);
        var planes = Enumerable.Range(0, 4).Select(_ => Plane.Create(w, h, 8)).ToArray();
        Parallel.For(0, h, y =>
        {
            uint seed = (uint)y * 2654435761u;
            for (int x = 0; x < w; x++)
            {
                seed = seed * 1664525u + 1013904223u;
                int noise = (int)(seed >> 28) - 8, i = y * w + x;
                bool disc = (x - 2000) * (x - 2000) + (y - 1500) * (y - 1500) < 900 * 900;
                planes[0].Data[i] = (byte)Math.Clamp((disc ? 200 : x * 255 / w) + noise, 0, 255);
                planes[1].Data[i] = (byte)Math.Clamp((disc ? 90 : y * 255 / h) + noise, 0, 255);
                planes[2].Data[i] = (byte)Math.Clamp((disc ? 60 : 150) + noise, 0, 255);
                planes[3].Data[i] = 255;
            }
        });
        model.Root.Add(new PixelLayer { Name = "Photo", Bounds = model.Bounds, Pixels = new Raster(ColorMode.Rgb, planes[..3], planes[3]) });
        var doc = new DocumentViewModel(model, null, editor);
        editor.Factory.AddDocument(doc);
        editor.ActiveDocument = doc;
        await doc.RenderAsync();
        await Task.Delay(1500); // let the view fit the image and the preview caches warm up
        await doc.RunEverydayToolsBenchmarkAsync();
    }
}
