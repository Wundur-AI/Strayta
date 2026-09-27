using System.Diagnostics;
using Avalonia;
using PixelRect = Strayta.Core.PixelRect;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Strayta.Core;
using Strayta.Core.Painting;
using Strayta.Core.Selection;
using Strayta.Editor.ViewModels;
using Strayta.Editor.Views;
using Strayta.Rendering;

namespace Strayta.Editor;

/// <summary>
/// Self-test steps for Select › Modify, masks from the selection, and Select and Mask, through the real menu
/// commands with scripted dialogs; then timings on a 4000×3000 document.
/// </summary>
internal static partial class SelfTest
{
    /// <summary>Answers the selection dialogs from a script instead of a person.</summary>
    private sealed class ScriptedSelectionDialogs : ISelectionDialogs
    {
        public (double Amount, bool AtBounds)? ModifyAnswer { get; set; }
        public SelectionModification? AskedFor { get; private set; }
        public Func<SelectAndMaskViewModel, Task<bool>>? SelectAndMask { get; set; }

        public Task<(double Amount, bool ApplyAtCanvasBounds)?> AskModifySelectionAsync(SelectionModification kind, double amount, bool applyAtCanvasBounds)
        {
            AskedFor = kind;
            return Task.FromResult<(double, bool)?>(ModifyAnswer is { } a ? (a.Amount, a.AtBounds) : null);
        }

        public Task<bool> RunSelectAndMaskAsync(SelectAndMaskViewModel session) => SelectAndMask?.Invoke(session) ?? Task.FromResult(false);
    }

    /// <summary>The main window's menu item at <paramref name="path"/> (e.g. "Select", "Modify", "Feather…").</summary>
    private static NativeMenuItem? MenuItem(params string[] path)
    {
        var window = (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        var menu = window is null ? null : NativeMenu.GetMenu(window);
        NativeMenuItem? item = null;
        foreach (var header in path)
        {
            item = menu?.Items.OfType<NativeMenuItem>().FirstOrDefault(i => i.Header == header);
            menu = item?.Menu;
        }
        return item;
    }

    /// <summary>Runs a menu item's command as a click would, and waits for it if it is asynchronous.</summary>
    private static async Task Click(NativeMenuItem? item)
    {
        if (item?.Command is not { } command) throw new InvalidOperationException("Menu item has no command.");
        if (command is CommunityToolkit.Mvvm.Input.IAsyncRelayCommand relay) await relay.ExecuteAsync(item.CommandParameter);
        else command.Execute(item.CommandParameter);
    }

    private static async Task RunRefineSelectionStepsAsync(EditorViewModel editor, Action<bool, string> check)
    {
        var dialogs = new ScriptedSelectionDialogs();
        editor.SelectionDialogs = dialogs;
        try
        {
            await ModifyAndMaskStepsAsync(editor, dialogs, check);
            await SelectAndMaskStepsAsync(editor, dialogs, check);
            await RefineTimingsAsync(editor, check);
        }
        catch (Exception ex)
        {
            check(false, $"exception in Select › Modify / Select and Mask steps: {ex}");
        }
        finally
        {
            editor.SelectionDialogs = null;
        }
    }

    private static async Task ModifyAndMaskStepsAsync(EditorViewModel editor, ScriptedSelectionDialogs dialogs, Action<bool, string> check)
    {
        // White background, a red layer over its left half.
        var model = Editing.LayerFactory.NewDocument(400, 300, whiteBackground: true);
        var doc = new DocumentViewModel(model, null, editor);
        editor.Factory.AddDocument(doc);
        editor.ActiveDocument = doc;
        await doc.RenderAsync();
        doc.SelectedLayer = doc.Layers[0];
        doc.NewLayer();
        var layer = (PixelLayer)doc.SelectedLayer!.Node;
        doc.BeginStroke(100, 150, new BrushSettings(400, 1f, 1f), new RgbColor(1, 0, 0), erase: false);
        await doc.EndStrokeAsync();

        // Menus: Photoshop's items and shortcuts.
        var feather = MenuItem("Select", "Modify", "Feather…");
        var cmd = Avalonia.Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;
        check(feather?.Gesture == new KeyGesture(Key.F6, KeyModifiers.Shift), "Select › Modify › Feather… is on ⇧F6");
        check(MenuItem("Select", "Select and Mask…")?.Gesture == new KeyGesture(Key.R, cmd | KeyModifiers.Alt), "Select › Select and Mask… is on ⌥⌘R");
        check(new[] { "Border…", "Smooth…", "Expand…", "Contract…", "Feather…" }.All(h => MenuItem("Select", "Modify", h) is not null), "Select › Modify has Border, Smooth, Expand, Contract, Feather");
        check(MenuItem("Layer", "Layer Mask", "Reveal Selection") is not null && MenuItem("Layer", "Layer Mask", "Hide Selection") is not null,
            "Layer › Layer Mask has Reveal Selection and Hide Selection");

        var rect = new PixelRect(100, 80, 300, 220);
        doc.SetSelection(SelectionMask.Rectangle(rect, model.Bounds), "Rectangular Marquee");
        var original = doc.Selection;

        // Each Modify command asks for its amount, applies it as one history step with Photoshop's name, and undoes.
        async Task Modify(string header, SelectionModification kind, double amount, Func<SelectionMask?, bool> expect, string what)
        {
            dialogs.ModifyAnswer = (amount, false);
            await Click(MenuItem("Select", "Modify", header));
            var result = doc.Selection;
            check(dialogs.AskedFor == kind && !ReferenceEquals(result, original) && expect(result) && doc.UndoText == $"Undo {kind}",
                $"Select › Modify › {header} {what} ({result?.Bounds}, {doc.UndoText}, {doc.LastModifyMs:0} ms)");
            doc.Undo();
            check(ReferenceEquals(doc.Selection, original), $"undo {kind} restores the selection");
        }
        await Modify("Expand…", SelectionModification.Expand, 10, s => s is { } m && m.Bounds == new PixelRect(90, 70, 310, 230) && m.CoverageAt(90, 150) == 255 && m.CoverageAt(90, 70) == 0,
            "grows by 10 pixels with round corners");
        await Modify("Contract…", SelectionModification.Contract, 10, s => s is { IsRectangular: true } m && m.Bounds == new PixelRect(110, 90, 290, 210), "shrinks by 10 pixels");
        await Modify("Feather…", SelectionModification.Feather, 5, s => s is { } m && m.CoverageAt(99, 150) + m.CoverageAt(100, 150) is >= 250 and <= 260 && m.CoverageAt(200, 150) == 255,
            "softens the edge symmetrically");
        await Modify("Smooth…", SelectionModification.Smooth, 5, s => s is { } m && m.CoverageAt(100, 150) >= 128 && m.CoverageAt(99, 150) < 128 && m.CoverageAt(100, 80) < 128,
            "keeps straight edges and rounds corners");
        await Modify("Border…", SelectionModification.Border, 10, s => s is { } m && m.CoverageAt(100, 150) > 200 && m.CoverageAt(200, 150) == 0,
            "selects a band along the edge");
        dialogs.ModifyAnswer = null;
        await Click(MenuItem("Select", "Modify", "Expand…"));
        check(ReferenceEquals(doc.Selection, original), "cancelling the dialog changes nothing");

        // Masks from the selection: Reveal Selection shows only the selected area, and deselects in the same step.
        byte[] Pixel(int x, int y)
        {
            var img = Compositor.Render(model).ToRgba8();
            int i = (y * model.Width + x) * 4;
            return img[i..(i + 4)];
        }
        bool IsRed(byte[] p) => p[0] > 200 && p[1] < 60;
        bool IsWhite(byte[] p) => p[0] > 245 && p[1] > 245 && p[2] > 245;
        await Click(MenuItem("Layer", "Layer Mask", "Reveal Selection"));
        check(layer.Mask is { DefaultColor: 0, Pixels: not null } m1 && m1.Bounds == rect && doc.Selection is null && doc.UndoText == "Undo Add Layer Mask" && doc.EditMask,
            $"Reveal Selection adds a mask from the selection and deselects ({doc.UndoText})");
        check(IsRed(Pixel(150, 150)) && IsWhite(Pixel(50, 150)), "the layer shows inside the selection only");
        doc.Undo();
        check(layer.Mask is null && ReferenceEquals(doc.Selection, original), "undo removes the mask and brings the selection back");
        await Click(MenuItem("Layer", "Layer Mask", "Hide Selection"));
        check(layer.Mask is { DefaultColor: 255 } && IsWhite(Pixel(150, 150)) && IsRed(Pixel(50, 150)), "Hide Selection hides the selected area");
        doc.Undo();

        // The Layers panel's button reveals the selection when there is one, everything otherwise.
        editor.AddLayerMaskCommand.Execute(null);
        check(layer.Mask is { DefaultColor: 0, Pixels: not null } && doc.Selection is null, "the Add layer mask button reveals the selection");
        doc.Undo();
        doc.Deselect();
        editor.AddLayerMaskCommand.Execute(null);
        check(layer.Mask is { DefaultColor: 255, Pixels: null }, "without a selection it reveals all");
        doc.Undo();

        // Soft selections make soft masks (16-bit documents get 16-bit mask samples).
        var soft = SelectionModify.Feather(SelectionMask.Rectangle(rect, model.Bounds), 4, model.Bounds)!;
        var mask16 = SelectionLayerMask.Create(soft, reveal: true, 16);
        check(mask16.Pixels!.BitDepth == 16 && Math.Abs(MaskBaker.Sample(mask16, 100, 150) - soft.CoverageAt(100, 150) / 255f) < 1e-3,
            "a feathered selection becomes a soft mask at the document's bit depth");
    }

    private static async Task SelectAndMaskStepsAsync(EditorViewModel editor, ScriptedSelectionDialogs dialogs, Action<bool, string> check)
    {
        // A two-color image with a soft vertical edge from x = 190 to 210 (fur, motion blur), and a hard selection
        // that stops at x = 200.
        const int W = 400, H = 300;
        var model = Editing.LayerFactory.NewDocument(W, H, whiteBackground: true);
        var planes = Enumerable.Range(0, 3).Select(_ => Plane.Create(W, H, 8)).ToArray();
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                float a = Math.Clamp((210 - x) / 20f, 0, 1);
                int i = y * W + x;
                planes[0].Data[i] = (byte)(230 * a + 20 * (1 - a));
                planes[1].Data[i] = (byte)(190 * a + 60 * (1 - a));
                planes[2].Data[i] = (byte)(110 * a + 200 * (1 - a));
            }
        var photo = new PixelLayer { Name = "Photo", Bounds = model.Bounds, Pixels = new Raster(ColorMode.Rgb, planes, null) };
        model.Root.Add(photo);
        var doc = new DocumentViewModel(model, null, editor);
        editor.Factory.AddDocument(doc);
        editor.ActiveDocument = doc;
        await doc.RenderAsync();
        doc.SelectedLayer = doc.Layers[0];
        var hard = SelectionMask.Rectangle(new PixelRect(0, 0, 200, H), model.Bounds);
        doc.SetSelection(hard, "Rectangular Marquee");

        // The real window, driven through its view model: edge detection, a view mode, a brush stroke, OK.
        SelectAndMaskViewModel? seen = null;
        (byte R, byte G, byte B)[]? drawn = null;
        Bitmap? shown = null;
        bool windowShowed = false;
        dialogs.SelectAndMask = async session =>
        {
            seen = session;
            var window = new SelectAndMaskWindow(session);
            window.Show();
            await session.WaitForFullAsync(TimeSpan.FromSeconds(5));
            windowShowed = window.IsVisible && session.Preview is not null && session.IsFullShown;
            await Task.Delay(300); // a frame or two for the canvas to fit the image
            drawn = WindowPixels(window, 100, 150, 350, 150);
            session.Radius = 12;
            await session.WaitForFullAsync(TimeSpan.FromSeconds(5));
            session.View = RefineView.BlackAndWhite;
            await session.WaitForFullAsync(TimeSpan.FromSeconds(5));
            shown = session.Preview;
            session.BeginBrush(300, 20, erase: false);
            session.ContinueBrush(300, 280);
            session.EndBrush();
            await session.WaitForFullAsync(TimeSpan.FromSeconds(5));
            window.Close();
            return true;
        };
        await Click(MenuItem("Select", "Select and Mask…"));
        check(seen is not null && windowShowed, "Select and Mask… opens the workspace with a full-resolution preview");
        // Overlay: the selected foreground as it is, the unselected blue tinted 50% red.
        check(drawn is [var fgPx, var bgPx] && Near(fgPx, (230, 190, 110)) && Near(bgPx, (138, 30, 100)),
            $"the workspace draws the overlay preview ({string.Join(" ", drawn ?? [])})");
        check(shown is not null && seen!.IsFullShown, $"changing the view mode and radius updates the preview ({seen?.Info})");
        var refined = doc.Selection;
        check(refined is not null && doc.UndoText == "Undo Select and Mask", $"OK with Output To Selection is one step ({doc.UndoText})");
        int Cov(int x) => refined?.CoverageAt(x, 150) ?? -1;
        check(Math.Abs(Cov(195) - 191) <= 20 && Math.Abs(Cov(205) - 64) <= 20 && Cov(150) == 255 && Cov(260) == 0,
            $"edge detection turns the hard edge into the image's soft edge ({Cov(190)}, {Cov(195)}, {Cov(200)}, {Cov(205)}, {Cov(210)})");
        doc.Undo();
        check(ReferenceEquals(doc.Selection, hard), "undo restores the hard selection");

        // Output to a new layer with a layer mask: a masked copy above, the original hidden, nothing selected.
        dialogs.SelectAndMask = async session =>
        {
            session.Radius = 12;
            session.Output = RefineOutput.NewLayerWithLayerMask;
            await session.WaitForFullAsync(TimeSpan.FromSeconds(5));
            return true;
        };
        await Click(MenuItem("Select", "Select and Mask…"));
        var copy = doc.SelectedLayer?.Node as PixelLayer;
        check(copy is { Name: "Photo copy", Mask: { DefaultColor: 0, Pixels: not null } } && !photo.Visible && copy.Visible && doc.Selection is null
              && doc.UndoText == "Undo Select and Mask" && ReferenceEquals(copy.Pixels, photo.Pixels),
            $"Output To New Layer with Layer Mask copies the layer with a mask and hides the original ({copy?.Name}, {doc.UndoText})");
        check(copy?.Mask is { } cm && Math.Abs(MaskBaker.Sample(cm, 195, 150) * 255 - 191) <= 20, "the new mask carries the refined edge");
        doc.Undo();
        check(photo.Visible && model.Root.Children.Count == 2 && ReferenceEquals(doc.Selection, hard), "undo removes the copy and shows the original again");

        // Output to a layer mask on the selected layer.
        dialogs.SelectAndMask = session =>
        {
            session.Output = RefineOutput.LayerMask;
            session.Feather = 3;
            return Task.FromResult(true);
        };
        doc.SelectedLayer = doc.Layers.SelectMany(l => l.SelfAndDescendants()).First(i => i.Node == photo);
        await Click(MenuItem("Select", "Select and Mask…"));
        check(photo.Mask is { Pixels: not null } pm && MaskBaker.Sample(pm, 199, 150) is > 0.3f and < 0.7f && doc.Selection is null,
            "Output To Layer Mask masks the selected layer with the feathered selection");
        doc.Undo();

        // Cancel leaves everything as it was.
        dialogs.SelectAndMask = session =>
        {
            session.Radius = 40;
            return Task.FromResult(false);
        };
        await Click(MenuItem("Select", "Select and Mask…"));
        check(ReferenceEquals(doc.Selection, hard) && doc.UndoText == "Undo Rectangular Marquee", "Cancel changes nothing");
    }

    private static bool Near((byte R, byte G, byte B) p, (int R, int G, int B) q) =>
        Math.Abs(p.R - q.R) <= 12 && Math.Abs(p.G - q.G) <= 12 && Math.Abs(p.B - q.B) <= 12;

    /// <summary>
    /// Renders the Select and Mask window offscreen and reads the colors it shows at image positions (x, y pairs),
    /// to check what actually reaches the screen.
    /// </summary>
    private static (byte R, byte G, byte B)[] WindowPixels(SelectAndMaskWindow window, params int[] imageXY)
    {
        var canvas = window.FindControl<Controls.ImageCanvas>("Canvas")!;
        var size = new Avalonia.PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height);
        using var rtb = new RenderTargetBitmap(size);
        rtb.Render(window);
        int stride = size.Width * 4;
        var buffer = new byte[stride * size.Height];
        unsafe
        {
            fixed (byte* p = buffer) rtb.CopyPixels(new Avalonia.PixelRect(size), (IntPtr)p, buffer.Length, stride);
        }
        bool bgra = rtb.Format == Avalonia.Platform.PixelFormat.Bgra8888;
        var result = new (byte, byte, byte)[imageXY.Length / 2];
        for (int k = 0; k < result.Length; k++)
        {
            var at = canvas.TranslatePoint(canvas.ImageToControl(imageXY[2 * k] + 0.5, imageXY[2 * k + 1] + 0.5), window)!.Value;
            int i = (int)at.Y * stride + (int)at.X * 4;
            result[k] = bgra ? (buffer[i + 2], buffer[i + 1], buffer[i]) : (buffer[i], buffer[i + 1], buffer[i + 2]);
        }
        return result;
    }

    /// <summary>
    /// Timings on a 4000×3000 photo-like document: each Modify command, and Select and Mask slider changes (latency
    /// to the preview and to full resolution, and how long the UI thread was ever blocked meanwhile).
    /// </summary>
    private static async Task RefineTimingsAsync(EditorViewModel editor, Action<bool, string> check)
    {
        const int W = 4000, H = 3000;
        var model = Editing.LayerFactory.NewDocument(W, H, whiteBackground: true);
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
        var doc = new DocumentViewModel(model, null, editor);
        editor.Factory.AddDocument(doc);
        editor.ActiveDocument = doc;
        await doc.RenderAsync();
        var ellipse = SelectionMask.Ellipse(new PixelRect(500, 400, 3500, 2600), model.Bounds);
        doc.SetSelection(ellipse, "Elliptical Marquee");

        var times = new List<string>();
        foreach (var (kind, amount) in new[] { (SelectionModification.Expand, 20.0), (SelectionModification.Contract, 20.0), (SelectionModification.Feather, 10.0),
                     (SelectionModification.Smooth, 10.0), (SelectionModification.Border, 20.0) })
        {
            var clock = Stopwatch.StartNew();
            await doc.ModifySelectionAsync(kind, amount, applyAtCanvasBounds: false);
            times.Add($"{kind} {amount:0}: {doc.LastModifyMs:0} ms (to history {clock.ElapsedMilliseconds} ms)");
            check(doc.UndoText == $"Undo {kind}" && doc.LastModifyMs < 1500, $"4000×3000 {kind} {amount:0} px in {doc.LastModifyMs:0} ms");
            doc.Undo();
        }
        Console.WriteLine("REFINEBENCH Modify on 4000×3000: " + string.Join("; ", times));

        // A Select and Mask session viewed at fit-on-screen zoom (about 1/4 on a laptop screen).
        var open = Stopwatch.StartNew();
        using var session = await doc.BeginSelectAndMaskAsync();
        session!.SetViewScale(0.25 * 2); // 25% zoom on a 2x display: preview at half resolution
        await session.WaitForFullAsync(TimeSpan.FromSeconds(10));
        Console.WriteLine($"REFINEBENCH Select and Mask opens with the full-resolution image in {open.ElapsedMilliseconds} ms");

        // UI heartbeat: the longest gap between UI-thread turns while the sliders move.
        double maxGap = 0;
        bool beating = true;
        var heartbeat = Task.Run(async () =>
        {
            while (Volatile.Read(ref beating))
            {
                var t = Stopwatch.StartNew();
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Input);
                maxGap = Math.Max(maxGap, t.Elapsed.TotalMilliseconds);
                await Task.Delay(4);
            }
        });

        session.Radius = 10;
        await session.WaitForFullAsync(TimeSpan.FromSeconds(10));
        double edgeFull = session.LastFullLatencyMs;

        // A slider drag: 20 steps, one per 16 ms frame, then a pause.
        var latencies = new List<double>();
        session.PreviewShown += OnShown;
        void OnShown()
        {
            if (!session.IsFullShown) latencies.Add(session.LastPreviewLatencyMs);
        }
        for (int step = 1; step <= 20; step++)
        {
            session.Feather = step * 0.5;
            await Task.Delay(16);
        }
        await session.WaitForFullAsync(TimeSpan.FromSeconds(10));
        double featherFull = session.LastFullLatencyMs;
        for (int step = 1; step <= 20; step++)
        {
            session.ShiftEdge = step * 2;
            await Task.Delay(16);
        }
        await session.WaitForFullAsync(TimeSpan.FromSeconds(10));
        double shiftFull = session.LastFullLatencyMs;
        session.PreviewShown -= OnShown;

        // Zoomed in to 100% (the preview is the full resolution): a drag still shows results while it moves.
        session.SetViewScale(2);
        await session.WaitForFullAsync(TimeSpan.FromSeconds(10));
        int zoomedFrames = 0;
        void OnZoomedShown() => zoomedFrames++;
        session.PreviewShown += OnZoomedShown;
        var drag = Stopwatch.StartNew();
        for (int step = 1; step <= 30; step++)
        {
            session.Contrast = step;
            await Task.Delay(16);
        }
        int framesDuringDrag = zoomedFrames;
        await session.WaitForFullAsync(TimeSpan.FromSeconds(10));
        session.PreviewShown -= OnZoomedShown;
        Volatile.Write(ref beating, false);
        await heartbeat;
        Console.WriteLine($"REFINEBENCH zoomed to 100%: {framesDuringDrag} full-resolution frames during a {drag.ElapsedMilliseconds} ms Contrast drag, last one {session.LastFullLatencyMs:0} ms after it");
        check(framesDuringDrag >= 3, $"at 100% zoom a slider drag keeps updating at full resolution ({framesDuringDrag} frames)");

        latencies.Sort();
        double median = latencies.Count > 0 ? latencies[latencies.Count / 2] : double.NaN, worst = latencies.Count > 0 ? latencies[^1] : double.NaN;
        Console.WriteLine($"REFINEBENCH Select and Mask 4000×3000: Radius 10 → full {edgeFull:0} ms; slider drag previews ({latencies.Count} shown): median {median:0} ms, " +
                          $"max {worst:0} ms after the change; full resolution {featherFull:0} ms (Feather) / {shiftFull:0} ms (Shift Edge) after the last change " +
                          $"(incl. a 150 ms pause); UI thread: longest wait {maxGap:0} ms, longest bitmap handoff {session.MaxUiMs:0} ms");
        check(latencies.Count >= 5 && median < 100, $"Select and Mask previews follow a slider drag on 4000×3000 (median {median:0} ms, {latencies.Count} frames)");
        check(maxGap < 100, $"the UI thread never waits long while refining ({maxGap:0} ms)");
    }
}
