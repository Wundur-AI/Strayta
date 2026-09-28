using Avalonia.Controls;
using Strayta.Core;
using Strayta.Editor.Editing;
using Strayta.Editor.ViewModels;
using Strayta.Rendering.Filters;

namespace Strayta.Editor;

/// <summary>Self-test steps and the benchmark for the Filter menu.</summary>
internal static partial class SelfTest
{
    /// <summary>Stands in for the filter dialog: runs a script against the session, and answers the rasterize prompt.</summary>
    private sealed class ScriptedFilterDialogs : IFilterDialogs
    {
        public Func<FilterSessionViewModel, Task<bool>> Script { get; set; } = _ => Task.FromResult(true);
        public bool RasterizeAnswer { get; set; }
        public string? LastPrompt { get; private set; }
        public int Runs { get; private set; }

        public async Task<bool> RunFilterAsync(FilterSessionViewModel session)
        {
            Runs++;
            return await Script(session);
        }

        public Task<bool> AskRasterizeAsync(string message)
        {
            LastPrompt = message;
            return Task.FromResult(RasterizeAnswer);
        }
    }

    /// <summary>
    /// Waits for the full-resolution lane's next image (which always shows the current state; <see cref="DocumentViewModel.LastFullRender"/>
    /// holds it), or 5 s. Zoomed in, the preview lane also renders at full resolution, but does not keep its image.
    /// </summary>
    private static async Task<bool> NextFullFrameAsync(DocumentViewModel doc)
    {
        var before = doc.LastFullRender;
        var shown = new TaskCompletionSource();
        void OnFrame(bool full)
        {
            if (full && !ReferenceEquals(doc.LastFullRender, before)) shown.TrySetResult();
        }
        doc.FrameDisplayed += OnFrame;
        bool ok = await Task.WhenAny(shown.Task, Task.Delay(5000)) == shown.Task;
        doc.FrameDisplayed -= OnFrame;
        return ok;
    }

    private static (byte R, byte G, byte B) RenderedAt(DocumentViewModel doc, int x, int y)
    {
        var render = doc.LastFullRender!;
        int i = (y * doc.Model.Width + x) * 4;
        return (render[i], render[i + 1], render[i + 2]);
    }

    private static async Task RunFilterStepsAsync(EditorViewModel editor, Action<bool, string> check)
    {
        var previousDialogs = editor.FilterDialogs;
        var dialogs = new ScriptedFilterDialogs();
        editor.FilterDialogs = dialogs;
        try
        {
            // ---- The menu -----------------------------------------------------------------------------------
            var window = (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            var menus = window is null ? [] : NativeMenu.GetMenu(window)?.Items.OfType<NativeMenuItem>().Select(i => i.Header).ToList() ?? [];
            int filterAt = menus.IndexOf("Filter");
            check(filterAt > 0 && menus[filterAt - 1] == "Select" && menus[filterAt + 1] == "View", $"the Filter menu sits between Select and View ({string.Join(", ", menus)})");
            check(!editor.ApplyLastFilterCommand.CanExecute(null) && editor.LastFilterName == "Last Filter", "Last Filter is disabled before any filter");

            // ---- A red square on a transparent layer over a white Background -------------------------------------
            var model = LayerFactory.NewDocument(400, 300, whiteBackground: true);
            var doc = new DocumentViewModel(model, null, editor);
            editor.Factory.AddDocument(doc);
            editor.ActiveDocument = doc;
            await doc.RenderAsync();
            var square = SolidLayer("Square", new PixelRect(150, 100, 250, 200), 255, 0, 0);
            doc.Apply(new InsertEdit(square, model.Root, 1, "test"));
            LayerItemViewModel Item(LayerNode n) => doc.Layers.SelectMany(l => l.SelfAndDescendants()).First(i => i.Node == n);
            doc.SelectedLayer = Item(square);
            var (pixels, bounds) = (square.Pixels, square.Bounds);
            string undoBefore = doc.UndoText;

            // ---- Gaussian Blur: preview on the canvas and in the dialog, then Cancel ---------------------------
            string previewSeen = "";
            bool previewBlurred = false, previewOff = false, untouched = false, dialogPreview = false, zooms = false, reset = false, pan = false;
            dialogs.Script = async session =>
            {
                var firstPreview = new TaskCompletionSource();
                session.PreviewShown += _ => firstPreview.TrySetResult();
                session.Radius = 8;
                bool framed = await NextFullFrameAsync(doc);
                previewSeen = $"frame {framed}, {RenderedAt(doc, 146, 150)} beside the square, {RenderedAt(doc, 200, 150)} inside";
                previewBlurred = framed && RenderedAt(doc, 146, 150) is { R: > 240, G: < 235 } && RenderedAt(doc, 200, 150) is { R: > 250, G: < 5 };
                untouched = ReferenceEquals(square.Pixels, pixels) && square.Bounds == bounds;
                await Task.WhenAny(firstPreview.Task, Task.Delay(3000));
                dialogPreview = session.PreviewImage is { PixelSize.Width: 280 } && session.PreviewWidth == 280;
                session.ZoomInCommand.Execute(null);
                string zoomedIn = session.ZoomText;
                session.ZoomOutCommand.Execute(null);
                session.ZoomOutCommand.Execute(null);
                zooms = zoomedIn == "200%" && session.ZoomText == "50%";
                session.ZoomInCommand.Execute(null);
                var center = session.PreviewCenter;
                session.BeginPan();
                session.PanBy(10, -4);
                session.EndPan();
                pan = session.PreviewCenter == (center.X - 10, center.Y + 4);
                session.Preview = false;
                previewOff = await NextFullFrameAsync(doc) && RenderedAt(doc, 146, 150) == (255, 255, 255);
                session.Preview = true;
                session.Radius = 30;
                session.Reset(); // Option turns Cancel into Reset
                reset = session.Radius == 1;
                session.Radius = 8;
                return false;
            };
            await editor.FilterCommand.ExecuteAsync(nameof(FilterKind.GaussianBlur));
            check(dialogs.Runs == 1 && previewBlurred, $"Filter › Blur › Gaussian Blur opens its dialog and previews the blur on the canvas ({previewSeen})");
            check(untouched, "the layer is untouched while the dialog is open");
            check(dialogPreview, "the dialog's own preview shows the layer at 100%");
            check(zooms && pan, "the dialog preview zooms with − and + and pans by dragging");
            check(previewOff, "unchecking Preview shows the unfiltered image again");
            check(reset, "Reset (Option-Cancel) restores the settings the dialog opened with");
            check(ReferenceEquals(square.Pixels, pixels) && doc.UndoText == undoBefore && doc.OpenFilter is null, "Cancel leaves the layer and the history alone");
            check(await NextFullFrameAsync(doc) && RenderedAt(doc, 146, 150) == (255, 255, 255), "after Cancel the canvas shows the unfiltered image");

            // ---- OK applies one undoable step --------------------------------------------------------------
            dialogs.Script = async session =>
            {
                session.Radius = 8;
                await NextFullFrameAsync(doc); // OK after the full-resolution preview reuses its result
                return true;
            };
            await editor.FilterCommand.ExecuteAsync(nameof(FilterKind.GaussianBlur));
            var blurred = square.Pixels;
            int w = square.Bounds.Width;
            int edge = (150 - square.Bounds.Top) * w + (150 - square.Bounds.Left);
            check(doc.UndoText == "Undo Gaussian Blur" && square.Bounds.Left < 150 && square.Bounds.Right > 250,
                $"OK applies the blur as one step, spreading the layer ({bounds} -> {square.Bounds}, {doc.LastFilterApplyMs:F0} ms)");
            check(blurred!.Alpha!.Data[edge] is > 90 and < 165 && blurred.ColorPlanes[0].Data[edge] == 255 && blurred.ColorPlanes[1].Data[edge] == 0,
                $"the edge fades to transparency and stays pure red (alpha {blurred.Alpha.Data[edge]}, premultiplied blur)");
            doc.Undo();
            check(ReferenceEquals(square.Pixels, pixels) && square.Bounds == bounds, "undo restores the sharp square");
            doc.Redo();
            check(ReferenceEquals(square.Pixels, blurred), "redo brings the blur back");
            doc.Undo();

            // ---- Last Filter --------------------------------------------------------------------------------
            check(editor.LastFilterName == "Gaussian Blur" && editor.ApplyLastFilterCommand.CanExecute(null), "the Filter menu's first item is now Gaussian Blur");
            int runs = dialogs.Runs;
            await editor.ApplyLastFilterCommand.ExecuteAsync(null);
            check(dialogs.Runs == runs && doc.UndoText == "Undo Gaussian Blur" && square.Pixels!.Alpha!.Data.SequenceEqual(blurred.Alpha.Data),
                "Last Filter (⌃⌘F) applies it again with the same settings, without a dialog");
            doc.Undo();

            // ---- The selection limits the filter ------------------------------------------------------------
            doc.SetSelection(Core.Selection.SelectionMask.Rectangle(new PixelRect(100, 50, 200, 250), model.Bounds), "Rectangular Marquee");
            await editor.ApplyLastFilterCommand.ExecuteAsync(null);
            var limited = square.Pixels!;
            int lw = square.Bounds.Width;
            byte AlphaAt(int x, int y) => limited.Alpha!.Data[(y - square.Bounds.Top) * lw + (x - square.Bounds.Left)];
            check(AlphaAt(150, 150) < 200 && AlphaAt(249, 150) == 255 && AlphaAt(146, 150) > 0,
                "inside the selection the edge blurs; outside it stays sharp");
            doc.Undo();
            doc.SetSelection(null, "Deselect");

            // ---- The other filters ----------------------------------------------------------------------------
            async Task<bool> RunFilter(FilterKind kind, Action<FilterSessionViewModel> settings, string name)
            {
                dialogs.Script = session =>
                {
                    settings(session);
                    return Task.FromResult(true);
                };
                await editor.FilterCommand.ExecuteAsync(kind.ToString());
                bool ok = doc.UndoText == $"Undo {name}" && !ReferenceEquals(square.Pixels, pixels);
                doc.Undo();
                return ok && ReferenceEquals(square.Pixels, pixels);
            }
            check(await RunFilter(FilterKind.MotionBlur, s => (s.Angle, s.Distance) = (30, 40), "Motion Blur"), "Motion Blur applies as one step and undoes");
            check(await RunFilter(FilterKind.BoxBlur, s => s.Radius = 5, "Box Blur"), "Box Blur applies as one step and undoes");
            check(await RunFilter(FilterKind.UnsharpMask, s => (s.Amount, s.Radius, s.Threshold) = (150, 3, 0), "Unsharp Mask"), "Unsharp Mask applies as one step and undoes");
            check(await RunFilter(FilterKind.HighPass, s => s.Radius = 5, "High Pass"), "High Pass applies as one step and undoes");

            // Noise: what the canvas previews at full resolution is exactly what OK applies.
            byte[]? previewed = null;
            dialogs.Script = async session =>
            {
                (session.Amount, session.Monochromatic) = (25, true);
                if (await NextFullFrameAsync(doc)) previewed = doc.LastFullRender;
                return true;
            };
            await editor.FilterCommand.ExecuteAsync(nameof(FilterKind.AddNoise));
            bool noiseApplied = doc.UndoText == "Undo Add Noise";
            check(noiseApplied && await NextFullFrameAsync(doc) && previewed is not null && previewed.SequenceEqual(doc.LastFullRender!),
                "Add Noise: the previewed noise is exactly the applied noise");
            var noisy = square.Pixels!;
            int mid = (50 * 100 + 50);
            check(noisy.ColorPlanes[1].Data[mid] == noisy.ColorPlanes[2].Data[mid] && noisy.Alpha!.Data.SequenceEqual(pixels!.Alpha!.Data),
                "monochromatic noise changes all channels alike and keeps transparency");
            if (noiseApplied) doc.Undo();

            // ---- A targeted layer mask ------------------------------------------------------------------------
            doc.AddMask(reveal: true);
            check(doc.EditMask && square.Mask is { Pixels: null }, "a reveal-all mask is added and targeted");
            await RunFilterOnMask();
            async Task RunFilterOnMask()
            {
                dialogs.Script = session =>
                {
                    session.Radius = 3;
                    return Task.FromResult(true);
                };
                await editor.FilterCommand.ExecuteAsync(nameof(FilterKind.HighPass));
            }
            check(doc.UndoText == "Undo High Pass" && square.Mask is { Pixels: not null } m && m.Pixels.Data[0] is 127 or 128 && ReferenceEquals(square.Pixels, pixels),
                "with the mask targeted the filter changes the mask, not the pixels");
            doc.Undo();
            doc.Undo(); // the mask
            doc.EditMask = false;

            // ---- Type layers ask to be rasterized first -----------------------------------------------------
            var type = SolidLayer("Type", new PixelRect(20, 20, 120, 60), 0, 0, 255);
            type.Tags.Add("text");
            doc.Apply(new InsertEdit(type, model.Root, model.Root.Children.Count, "test"));
            doc.SelectedLayer = Item(type);
            runs = dialogs.Runs;
            dialogs.RasterizeAnswer = false;
            await editor.FilterCommand.ExecuteAsync(nameof(FilterKind.GaussianBlur));
            check(dialogs.LastPrompt?.Contains("type layer must be rasterized") == true && dialogs.Runs == runs && type.Tags.Contains("text"),
                "a type layer shows Photoshop's rasterize prompt; Cancel does nothing");
            dialogs.RasterizeAnswer = true;
            dialogs.Script = _ => Task.FromResult(true);
            await editor.FilterCommand.ExecuteAsync(nameof(FilterKind.GaussianBlur));
            check(!type.Tags.Contains("text") && doc.UndoText == "Undo Gaussian Blur", "OK rasterizes it and the filter follows");
            doc.Undo();
            check(doc.UndoText == "Undo Rasterize Layer", "rasterizing is its own undo step, as in Photoshop");
            doc.Undo();
            check(type.Tags.Contains("text"), "undo brings the type layer back");

            // ---- Groups are refused; the Background stays opaque --------------------------------------------
            doc.NewGroup();
            runs = dialogs.Runs;
            await editor.FilterCommand.ExecuteAsync(nameof(FilterKind.GaussianBlur));
            check(dialogs.Runs == runs && doc.Notice.Contains("Group"), $"a group is refused with a notice ({doc.Notice})");
            doc.Undo();
            var background = (PixelLayer)model.Root.Children[0];
            doc.SelectedLayer = Item(background);
            var backgroundBounds = background.Bounds;
            dialogs.Script = session =>
            {
                session.Radius = 20;
                return Task.FromResult(true);
            };
            await editor.FilterCommand.ExecuteAsync(nameof(FilterKind.GaussianBlur));
            check(background.Pixels!.Alpha is null && background.Bounds == backgroundBounds && background.Pixels.ColorPlanes[0].Data[0] == 255,
                "blurring the Background keeps it opaque, with no dark or transparent edges");
            doc.Undo();

            // ---- 16 bits per channel ---------------------------------------------------------------------------
            var deep = new Document(200, 150, ColorMode.Rgb, 16);
            var planes = Enumerable.Range(0, 3).Select(_ => Plane.Create(200, 150, 16)).ToArray();
            for (int y = 0; y < 150; y++)
                for (int x = 0; x < 200; x++)
                    planes[0].AsUInt16()[y * 200 + x] = (ushort)(x < 100 ? 1000 : 60000);
            var deepLayer = new PixelLayer { Name = "Background", Bounds = deep.Bounds, Pixels = new Raster(ColorMode.Rgb, planes, null) };
            deep.Root.Add(deepLayer);
            var deepDoc = new DocumentViewModel(deep, null, editor);
            editor.Factory.AddDocument(deepDoc);
            editor.ActiveDocument = deepDoc;
            await deepDoc.RenderAsync();
            deepDoc.SelectedLayer = deepDoc.Layers[0];
            dialogs.Script = session =>
            {
                session.Radius = 4;
                return Task.FromResult(true);
            };
            await editor.FilterCommand.ExecuteAsync(nameof(FilterKind.GaussianBlur));
            var deepRow = deepLayer.Pixels!.ColorPlanes[0].AsUInt16();
            check(deepLayer.Pixels.BitDepth == 16 && deepRow[75 * 200 + 100] is > 25000 and < 36000 && deepRow[75 * 200 + 99] % 257 != 0,
                $"16-bit layers are filtered at 16 bits ({deepRow[75 * 200 + 100]})");
            editor.ActiveDocument = doc;
        }
        catch (Exception ex)
        {
            check(false, $"exception in Filter steps: {ex}");
        }
        finally
        {
            editor.FilterDialogs = previousDialogs;
        }
    }

    /// <summary>STRAYTA_FILTERBENCH=new: times every filter and the slider-to-preview latency on a generated 4000×3000 document.</summary>
    public static async Task RunFilterBenchmarkAsync(EditorViewModel editor)
    {
        const int w = 4000, h = 3000;
        var model = LayerFactory.NewDocument(w, h, whiteBackground: true);
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
        var photo = new PixelLayer { Name = "Photo", Bounds = model.Bounds, Pixels = new Raster(ColorMode.Rgb, planes[..3], planes[3]) };
        model.Root.Add(photo);
        var doc = new DocumentViewModel(model, null, editor);
        editor.Factory.AddDocument(doc);
        editor.ActiveDocument = doc;
        await doc.RenderAsync();
        await Task.Delay(1500); // let the view fit the image and the preview caches warm up
        doc.SelectedLayer = doc.Layers.SelectMany(l => l.SelfAndDescendants()).First(i => i.Node == photo);
        await doc.RunFilterBenchmarkAsync(editor.OpenFilterSession);
        Console.WriteLine("FILTERBENCH done");
    }
}
