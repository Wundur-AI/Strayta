using Avalonia.Media;
using Strayta.Core;
using Strayta.Core.Painting;
using Strayta.Core.Selection;
using Strayta.Editor.Editing;
using Strayta.Editor.ViewModels;
using Strayta.Psd;
using Strayta.Rendering.Filters;

namespace Strayta.Editor;

/// <summary>Self-test steps for the Channels panel, saved selections, spot channels, Quick Mask and Image › Adjustments.</summary>
internal static partial class SelfTest
{
    /// <summary>Stands in for the channel dialogs: answers are set by the steps.</summary>
    private sealed class ScriptedChannelDialogs : IChannelDialogs
    {
        public ChannelOptions? Options { get; set; }
        public SaveSelectionChoice? Save { get; set; }
        public LoadSelectionChoice? Load { get; set; }
        public QuickMaskOptions? QuickMask { get; set; }
        public double[]? AdjustmentValues { get; set; }
        public int Previews { get; private set; }

        public Task<ChannelOptions?> AskChannelOptionsAsync(string title, ChannelOptions initial, bool spot) => Task.FromResult(Options);
        public Task<SaveSelectionChoice?> AskSaveSelectionAsync(IReadOnlyList<DocumentChannel> channels, string suggestedName) => Task.FromResult(Save);
        public Task<LoadSelectionChoice?> AskLoadSelectionAsync(IReadOnlyList<DocumentChannel> channels, int targetedId, bool hasSelection) => Task.FromResult(Load);
        public Task<QuickMaskOptions?> AskQuickMaskOptionsAsync(QuickMaskOptions current) => Task.FromResult(QuickMask);
        public Task<string?> AskChannelNameAsync(string title, string initial) => Task.FromResult<string?>(initial);

        public Task<(double[] Values, bool Option)?> AskAdjustmentAsync(string title, IReadOnlyList<AdjustmentSetting> settings, string? option,
            Action<double[], bool> preview)
        {
            if (AdjustmentValues is not { } values) return Task.FromResult<(double[], bool)?>(null);
            preview(values, false);
            Previews++;
            return Task.FromResult<(double[], bool)?>((values, false));
        }
    }

    private static byte ChannelAt(DocumentChannel c, int x, int y) => c.Pixels.Data[y * c.Pixels.Width + x];

    private static (byte R, byte G, byte B) ViewAt(byte[] view, int width, int x, int y)
    {
        int i = (y * width + x) * 4;
        return (view[i], view[i + 1], view[i + 2]);
    }

    private static async Task RunChannelStepsAsync(EditorViewModel editor, Action<bool, string> check)
    {
        var previousDialogs = editor.ChannelDialogs;
        var dialogs = new ScriptedChannelDialogs();
        editor.ChannelDialogs = dialogs;
        try
        {
            var panel = editor.Factory.Find(d => d.Id == "Channels").OfType<ChannelsToolViewModel>().FirstOrDefault();
            check(panel is not null, "Window › Channels is a docked panel");

            // A red square on a transparent layer over a white Background.
            var model = LayerFactory.NewDocument(400, 300, whiteBackground: true);
            var doc = new DocumentViewModel(model, null, editor);
            editor.Factory.AddDocument(doc);
            editor.ActiveDocument = doc;
            await doc.RenderAsync();
            var square = SolidLayer("Square", new PixelRect(150, 100, 250, 200), 255, 0, 0);
            doc.Apply(new InsertEdit(square, model.Root, 1, "test"));
            LayerItemViewModel Item(LayerNode n) => doc.Layers.SelectMany(l => l.SelfAndDescendants()).First(i => i.Node == n);
            doc.SelectedLayer = Item(square);
            await NextFullFrameAsync(doc);
            panel?.Refresh();
            check(panel?.Rows.Select(r => r.Name).SequenceEqual(["RGB", "Red", "Green", "Blue"]) == true
                  && panel.Rows.All(r => r.IsVisible) && panel.Rows[0].IsTargeted && panel.Rows[0].Thumbnail is not null,
                $"the panel lists RGB, Red, Green and Blue with eyes and thumbnails ({string.Join(", ", panel?.Rows.Select(r => r.Name) ?? [])})");

            // ---- Viewing channels -----------------------------------------------------------------------------------
            doc.TargetByShortcut(4); // ⌘4 Green
            var view = doc.ChannelViewOfLastRender()!;
            check(ViewAt(view, 400, 200, 150) == (0, 0, 0) && ViewAt(view, 400, 20, 20) == (255, 255, 255) && doc.IsColorTargeted(1) && !doc.IsColorVisible(0),
                "⌘4 shows the green channel alone in gray (the red square is black in it)");
            doc.TargetColor(0, add: true); // Shift-click Red: red and green in color
            view = doc.ChannelViewOfLastRender()!;
            check(ViewAt(view, 400, 200, 150) == (255, 0, 0) && ViewAt(view, 400, 20, 20) == (255, 255, 0),
                "two channels show in their colors (white becomes yellow without blue)");
            doc.TargetComposite();
            check(doc.ChannelViewOfLastRender()!.AsSpan().SequenceEqual(doc.LastFullRender) && doc.IsCompositeTargeted, "⌘2 shows the composite again");

            // ---- Painting and adjusting one color channel -----------------------------------------------------------------
            doc.TargetByShortcut(4);
            check(doc.RestrictsColorChannels, "with Green targeted, edits change only green");
            doc.BeginStroke(200, 150, new BrushSettings(30, 1f, 1f), new RgbColor(1, 1, 1), erase: false);
            await doc.EndStrokeAsync();
            var px = square.Pixels!;
            int c = 50 * 100 + 50;
            check(px.ColorPlanes[0].Data[c] == 255 && px.ColorPlanes[1].Data[c] == 255 && px.ColorPlanes[2].Data[c] == 0 && square.Bounds == new PixelRect(150, 100, 250, 200),
                $"a white stroke in the green channel turns the red square yellow there, blue untouched ({px.ColorPlanes[0].Data[c]}, {px.ColorPlanes[1].Data[c]}, {px.ColorPlanes[2].Data[c]})");
            doc.Undo();
            await editor.ImageAdjustmentCommand.ExecuteAsync("Invert");
            px = square.Pixels!;
            check(px.ColorPlanes[0].Data[c] == 255 && px.ColorPlanes[1].Data[c] == 255 && px.ColorPlanes[2].Data[c] == 0 && doc.UndoText == "Undo Invert",
                "Image › Adjustments › Invert with Green targeted inverts only green");
            doc.Undo();
            dialogs.AdjustmentValues = [128];
            await editor.ImageAdjustmentCommand.ExecuteAsync("Threshold");
            px = square.Pixels!;
            check(px.ColorPlanes[0].Data[c] == 255 && px.ColorPlanes[1].Data[c] == 0 && px.ColorPlanes[2].Data[c] == 0 && dialogs.Previews == 1 && doc.UndoText == "Undo Threshold",
                "Threshold… previews, then applies (green only: the square's luminance is below the level)");
            doc.Undo();
            await doc.FillAsync(background: true); // white background color into green
            px = square.Pixels!;
            check(px.ColorPlanes[0].Data[c] == 255 && px.ColorPlanes[1].Data[c] == 255 && px.ColorPlanes[2].Data[c] == 0, "Fill with a channel targeted fills only it");
            doc.Undo();
            check(doc.BeginFilter("Gaussian Blur"), "a filter starts with a color channel targeted");
            await doc.ApplyFilterAsync(new GaussianBlurFilter(4));
            px = square.Pixels!;
            check(px.ColorPlanes[0].Data[c] == 255 && px.ColorPlanes[2].Data[0] == 0 && square.Bounds == new PixelRect(150, 100, 250, 200),
                "a filter with Green targeted leaves red, blue and the layer's bounds alone");
            doc.Undo();
            doc.TargetComposite();

            // ---- Saved selections ----------------------------------------------------------------------------
            doc.SetSelection(SelectionMask.Rectangle(new PixelRect(50, 50, 150, 150), model.Bounds), "test");
            dialogs.Save = new SaveSelectionChoice(0, "Left", SelectionMode.Replace);
            await editor.SaveSelectionCommand.ExecuteAsync(null);
            var left = doc.Channels.SingleOrDefault();
            check(left is { Name: "Left", Kind: ChannelKind.MaskedAreas } && ChannelAt(left, 60, 60) == 255 && ChannelAt(left, 10, 10) == 0 && doc.UndoText == "Undo Save Selection",
                "Select › Save Selection makes a channel (white selected)");
            panel?.Refresh();
            check(panel?.Rows.Any(r => r.Kind == ChannelRowKind.Channel && r.Name == "Left" && r.Shortcut == "⌘6" && r.Thumbnail is not null) == true,
                "the saved selection is listed below the color channels with ⌘6");
            doc.SetSelection(SelectionMask.Rectangle(new PixelRect(100, 100, 200, 200), model.Bounds), "test");
            dialogs.Save = new SaveSelectionChoice(left!.Id, "", SelectionMode.Add);
            await editor.SaveSelectionCommand.ExecuteAsync(null);
            left = doc.Channels.Single();
            check(ChannelAt(left, 60, 60) == 255 && ChannelAt(left, 180, 180) == 255 && ChannelAt(left, 60, 180) == 0, "Save Selection can add to an existing channel");
            dialogs.Save = new SaveSelectionChoice(left.Id, "", SelectionMode.Subtract);
            await editor.SaveSelectionCommand.ExecuteAsync(null);
            left = doc.Channels.Single();
            check(ChannelAt(left, 60, 60) == 255 && ChannelAt(left, 180, 180) == 0 && ChannelAt(left, 120, 120) == 0, "… and subtract from it");
            doc.Undo();
            doc.Undo();
            left = doc.Channels.Single();
            check(ChannelAt(left, 180, 180) == 0 && ChannelAt(left, 60, 60) == 255, "undo restores the channel");
            doc.Deselect();

            dialogs.Load = new LoadSelectionChoice(left.Id, false, SelectionMode.Replace);
            await editor.LoadSelectionCommand.ExecuteAsync(null);
            check(doc.Selection?.Bounds == new PixelRect(50, 50, 150, 150), "Select › Load Selection brings it back");
            doc.SetSelection(SelectionMask.Rectangle(new PixelRect(100, 0, 400, 300), model.Bounds), "test");
            doc.LoadSelection(left.Id, mode: SelectionMode.Intersect);
            check(doc.Selection?.Bounds == new PixelRect(100, 50, 150, 150), "Load Selection intersects with the current selection");
            doc.LoadSelection(left.Id, invert: true);
            check(doc.Selection is { } inv && inv.CoverageAt(60, 60) == 0 && inv.CoverageAt(10, 10) == 255, "… or loads it inverted");
            doc.Deselect();

            // Editing a saved selection: click it (shown alone, in gray), paint, gradient, filter, fill.
            doc.TargetChannel(left.Id);
            check(doc.ChannelTarget == ChannelTargetKind.Channel && !doc.IsColorVisible(0), "clicking a saved selection targets it and shows it alone");
            view = doc.ChannelViewOfLastRender()!;
            check(ViewAt(view, 400, 60, 60) == (255, 255, 255) && ViewAt(view, 400, 10, 10) == (0, 0, 0), "it shows in gray: white where selected");
            var squarePixels = square.Pixels;
            check(doc.BeginStroke(60, 60, new BrushSettings(20, 1f, 1f), new RgbColor(0, 0, 0), erase: false), "the brush paints the channel");
            doc.ContinueStroke(80, 60);
            await Task.Delay(100);
            view = doc.ChannelViewOfLastRender();
            await doc.EndStrokeAsync();
            left = doc.Channels.Single();
            check(ChannelAt(left, 70, 60) == 0 && ChannelAt(left, 60, 120) == 255 && ReferenceEquals(square.Pixels, squarePixels) && doc.UndoText == "Undo Brush",
                "black paint removes from the saved selection; the layer is untouched");
            doc.Undo();
            check(ChannelAt(doc.Channels.Single(), 70, 60) == 255, "undo restores the painted channel");
            doc.BeginGradient(0, 150);
            doc.MoveGradient(400, 150);
            await doc.EndGradientAsync();
            left = doc.Channels.Single();
            // Earlier sections may leave other gradient settings: check that it ramps across the channel, not exact values.
            check(ChannelAt(left, 5, 250) < ChannelAt(left, 200, 250) && ChannelAt(left, 200, 250) < ChannelAt(left, 395, 250)
                  && ChannelAt(left, 395, 250) - ChannelAt(left, 5, 250) > 120 && ReferenceEquals(square.Pixels, squarePixels),
                $"the Gradient draws into the channel ({ChannelAt(left, 5, 250)}, {ChannelAt(left, 200, 250)}, {ChannelAt(left, 395, 250)})");
            doc.Undo();
            check(doc.FilterRasterizePrompt() is null && doc.BeginFilter("Gaussian Blur"), "a filter starts on the channel");
            doc.PreviewFilter(new GaussianBlurFilter(6));
            await Task.Delay(150);
            await doc.ApplyFilterAsync(new GaussianBlurFilter(6));
            left = doc.Channels.Single();
            check(ChannelAt(left, 50, 100) is > 60 and < 200 && ReferenceEquals(square.Pixels, squarePixels), $"Gaussian Blur softens the channel's edge ({ChannelAt(left, 50, 100)})");
            doc.Undo();
            doc.SetSelection(SelectionMask.Rectangle(new PixelRect(300, 200, 350, 250), model.Bounds), "test");
            await doc.FillAsync(background: true); // white
            check(ChannelAt(doc.Channels.Single(), 320, 220) == 255 && ChannelAt(doc.Channels.Single(), 10, 10) == 0, "Edit › Fill fills the channel inside the selection");
            await doc.ClearAsync(); // Delete: the background color (white) again
            doc.Undo();
            doc.Undo();
            doc.Deselect();

            // Channel options: selected areas store the selection inverted and still load the same selection.
            left = doc.Channels.Single();
            doc.SetChannelOptions(left.Id, "Left side", ChannelKind.SelectedAreas, new RgbColor(0, 0, 1), 0.7f);
            var sel = doc.Channels.Single();
            check(sel is { Name: "Left side", Kind: ChannelKind.SelectedAreas, Opacity: 0.7f } && ChannelAt(sel, 60, 60) == 0 && ChannelAt(sel, 10, 10) == 255,
                "Channel Options switches to selected areas (the channel inverts), renames, recolors");
            doc.LoadSelection(sel.Id);
            check(doc.Selection?.Bounds == new PixelRect(50, 50, 150, 150), "a selected-areas channel still loads the same selection");
            doc.Deselect();
            doc.Undo(); // deselect
            doc.Undo(); // load
            doc.Undo(); // options

            // New channel, duplicate, delete.
            var alpha = doc.NewChannel();
            check(alpha is { Name: "Alpha 1" } && doc.Channels.Count == 2 && ChannelAt(doc.Channels[1], 200, 150) == 0 && doc.IsChannelTargeted(alpha.Id),
                "New Channel adds an empty (black) saved selection, targeted");
            var copy = doc.DuplicateChannel(left.Id);
            check(copy is { Name: "Left copy" } && doc.Channels.Count == 3 && doc.Channels[1].Id == copy.Id && ChannelAt(copy, 60, 60) == 255, "Duplicate Channel copies it after the original");
            doc.DeleteChannel(copy!.Id);
            check(doc.Channels.Count == 2 && doc.UndoText == "Undo Delete Channel", "Delete Channel");
            doc.Undo();
            check(doc.Channels.Count == 3, "undo brings the deleted channel back");
            doc.Undo();
            doc.TargetComposite();

            // Shown with the composite: a quick-mask-like overlay.
            doc.SetChannelVisible(left.Id, true);
            await NextFullFrameAsync(doc);
            view = doc.ChannelViewOfLastRender()!;
            var outside = ViewAt(view, 400, 10, 10);
            check(ViewAt(view, 400, 60, 60) == (255, 255, 255) && outside.R == 255 && outside.G is > 100 and < 150,
                $"with the composite visible, a saved selection tints its masked areas red at 50% ({outside})");
            doc.SetChannelVisible(left.Id, false);

            // ---- Spot channels ------------------------------------------------------------------------------
            doc.SetSelection(SelectionMask.Rectangle(new PixelRect(300, 20, 380, 80), model.Bounds), "test");
            var spot = doc.NewSpotChannel("Spot Blue", new RgbColor(0, 0, 1), 1f);
            await NextFullFrameAsync(doc);
            view = doc.ChannelViewOfLastRender()!;
            check(spot is { IsSpot: true } && ChannelAt(spot, 320, 40) == 0 && ChannelAt(spot, 10, 10) == 255 && ViewAt(view, 400, 320, 40) == (0, 0, 255) && ViewAt(view, 400, 10, 10) == (255, 255, 255),
                "New Spot Channel inks the selection and prints its color over the image");
            doc.Deselect();
            doc.TargetComposite();

            // ---- Quick Mask ---------------------------------------------------------------------------------------
            doc.SetSelection(SelectionMask.Rectangle(new PixelRect(100, 100, 300, 200), model.Bounds), "test");
            doc.ToggleQuickMask();
            await NextFullFrameAsync(doc);
            view = doc.ChannelViewOfLastRender()!;
            var masked = ViewAt(view, 400, 10, 10);
            check(doc.IsQuickMask && doc.Selection is null && doc.ChannelTarget == ChannelTargetKind.QuickMask && masked.R == 255 && masked.G is > 100 and < 150
                  && ViewAt(view, 400, 120, 150) == (255, 255, 255),
                $"Q enters Quick Mask mode: the unselected area is red ({masked})");
            doc.BeginStroke(120, 150, new BrushSettings(30, 1f, 1f), new RgbColor(0, 0, 0), erase: false);
            await doc.EndStrokeAsync();
            check(doc.QuickMaskPlane is { } qm && qm.Data[150 * 400 + 120] == 0 && doc.QuickMaskPlane!.Data[150 * 400 + 250] == 255, "black paint masks part of the quick mask");
            doc.ToggleQuickMask();
            check(!doc.IsQuickMask && doc.Selection is { } fromMask && fromMask.CoverageAt(120, 150) == 0 && fromMask.CoverageAt(250, 150) == 255 && fromMask.CoverageAt(10, 10) == 0,
                "Q again turns the quick mask into the selection");
            doc.Undo();
            check(doc.IsQuickMask && doc.Selection is null, "undo goes back into Quick Mask mode");
            doc.Undo();
            doc.Undo();
            check(!doc.IsQuickMask && doc.Selection?.Bounds == new PixelRect(100, 100, 300, 200), "undo leaves Quick Mask with the old selection");
            doc.Deselect();

            // ---- Layer mask channel ----------------------------------------------------------------------------
            doc.SelectedLayer = Item(square);
            doc.AddMask(reveal: false); // Hide All
            panel?.Refresh();
            check(doc.ChannelTarget == ChannelTargetKind.LayerMask && panel?.Rows.LastOrDefault() is { Kind: ChannelRowKind.LayerMask, Name: "Square Mask", IsTargeted: true },
                "a targeted layer mask shows as \"Square Mask\" in the panel");
            doc.ToggleLayerMaskOverlay();
            await NextFullFrameAsync(doc);
            view = doc.ChannelViewOfLastRender()!;
            var ruby = ViewAt(view, 400, 10, 10);
            check(doc.IsLayerMaskVisible && ruby.R == 255 && ruby.G is > 100 and < 150, $"\\ shows the mask as a red overlay ({ruby})");
            doc.ToggleLayerMaskOverlay();
            doc.Undo();
            doc.TargetComposite();

            // ---- Crop and saving ---------------------------------------------------------------------------------
            doc.SetSelection(SelectionMask.Rectangle(new PixelRect(40, 40, 240, 240), model.Bounds), "test");
            await doc.CropToSelectionAsync();
            check(model.Width == 200 && doc.Channels.All(ch => ch.Pixels.Width == 200 && ch.Pixels.Height == 200) && ChannelAt(doc.Channels[0], 20, 20) == 255 && ChannelAt(doc.Channels[0], 5, 5) == 0,
                "saved selections and spot channels follow a crop");
            doc.Undo();
            check(model.Width == 400 && doc.Channels.All(ch => ch.Pixels.Width == 400), "undo restores them");
            doc.Deselect();

            string path = Path.Combine(Path.GetTempPath(), $"channels-selftest-{Environment.ProcessId}.psd");
            try
            {
                await doc.SaveAsync(path);
                var file = PsdFile.Open(path);
                var saved = PsdChannels.Read(file);
                check(saved.Select(ch => (ch.Name, ch.Kind)).SequenceEqual(doc.Channels.Select(ch => (ch.Name, ch.Kind)))
                      && saved.Zip(doc.Channels).All(p => p.First.Pixels.Data.AsSpan().SequenceEqual(p.Second.Pixels.Data)),
                    $"saving writes the channels ({string.Join(", ", saved.Select(ch => ch.Name))})");
                var reopened = PsdFile.OpenForEditing(path);
                var again = Path.Combine(Path.GetTempPath(), $"channels-selftest-again-{Environment.ProcessId}.psd");
                PsdWriter.Save(reopened, again, new PsdWriteOptions { Composite = reopened.Composite });
                var file2 = PsdFile.Open(again);
                check(new[] { 1006, 1045, 1053, 1077 }.All(id => file.FindResource(id)!.Data.AsSpan().SequenceEqual(file2.FindResource(id)!.Data))
                      && PsdChannels.ExtraPlanes(file).Zip(PsdChannels.ExtraPlanes(file2)).All(p => p.First.Data.AsSpan().SequenceEqual(p.Second.Data)),
                    "an unchanged file keeps its channel data and lists byte for byte");
                File.Delete(again);
            }
            finally
            {
                File.Delete(path);
            }

            await WriteChannelSampleAsync(editor, doc, check);
            doc.CloseWithoutAsking();
        }
        finally
        {
            editor.ChannelDialogs = previousDialogs;
        }
    }

    /// <summary>
    /// With STRAYTA_CHANNEL_SAMPLES set to a folder: a document with two saved selections and a spot channel, made with the
    /// editor's commands, saved there for checking in Photoshop.
    /// </summary>
    private static async Task WriteChannelSampleAsync(EditorViewModel editor, DocumentViewModel from, Action<bool, string> check)
    {
        if (Environment.GetEnvironmentVariable("STRAYTA_CHANNEL_SAMPLES") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir);
        var model = LayerFactory.NewDocument(600, 400, whiteBackground: true);
        var doc = new DocumentViewModel(model, null, editor);
        editor.Factory.AddDocument(doc);
        editor.ActiveDocument = doc;
        await doc.RenderAsync();
        var disc = SolidLayer("Orange", new PixelRect(80, 60, 330, 310), 240, 120, 30);
        doc.Apply(new InsertEdit(disc, model.Root, 1, "Orange"));

        doc.SetSelection(SelectionMask.Ellipse(new PixelRect(80, 60, 330, 310), model.Bounds), "Elliptical Marquee");
        doc.SaveSelection(name: "Circle");
        doc.SetSelection(SelectionMask.Rectangle(new PixelRect(360, 80, 560, 330), model.Bounds), "Rectangular Marquee");
        var box = doc.SaveSelection(name: "Box (selected areas)");
        if (box is not null) doc.SetChannelOptions(box.Id, box.Name, ChannelKind.SelectedAreas, new RgbColor(0f, 0.4f, 1f), 0.6f);
        doc.SetSelection(SelectionMask.Ellipse(new PixelRect(380, 120, 540, 280), model.Bounds), "Elliptical Marquee");
        doc.NewSpotChannel("Spot Green", new RgbColor(0.1f, 0.7f, 0.3f), 0.8f);
        doc.Deselect();
        string path = Path.Combine(dir, "channels-two-alphas-and-spot.psd");
        await doc.SaveAsync(path);
        var channels = PsdChannels.Read(PsdFile.Open(path));
        check(channels.Count == 3, $"the Photoshop check file has two saved selections and a spot channel: {path}");
        doc.CloseWithoutAsking();
        editor.ActiveDocument = from;
    }
}
