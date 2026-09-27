using Strayta.Core;
using Strayta.Core.Painting;
using Strayta.Editor.ViewModels;
using Strayta.Psd;
using Strayta.Rendering;

namespace Strayta.Editor;

/// <summary>Self-test steps for layer masks, adjustment layers and the Properties panel.</summary>
internal static partial class SelfTest
{
    private static async Task RunMaskAndAdjustmentStepsAsync(EditorViewModel editor, Action<bool, string> check)
    {
        try
        {
            // A fresh document: white background with a red band across the middle.
            var model = Editing.LayerFactory.NewDocument(400, 300, whiteBackground: true);
            var doc = new DocumentViewModel(model, null, editor);
            editor.Factory.AddDocument(doc);
            editor.ActiveDocument = doc;
            await doc.RenderAsync();
            doc.SelectedLayer = doc.Layers[0];
            doc.NewLayer();
            var layer = (PixelLayer)doc.SelectedLayer!.Node;
            doc.BeginStroke(20, 150, new BrushSettings(60, 1f, 1f), new RgbColor(1, 0, 0), erase: false);
            doc.ContinueStroke(380, 150);
            await doc.EndStrokeAsync();

            byte[] Pixel(int x, int y)
            {
                var img = Compositor.Render(model).ToRgba8();
                int i = (y * model.Width + x) * 4;
                return img[i..(i + 4)];
            }
            bool IsRed(byte[] p) => p[0] > 200 && p[1] < 60 && p[2] < 60;
            bool IsWhite(byte[] p) => p[0] > 245 && p[1] > 245 && p[2] > 245;
            LayerItemViewModel Item(LayerNode n) => doc.Layers.SelectMany(l => l.SelfAndDescendants()).First(i => i.Node == n);

            // ---- Layer masks ----------------------------------------------------------------------------

            doc.AddMask(reveal: true);
            var item = Item(layer);
            check(layer.Mask is { DefaultColor: 255, Pixels: null } && doc.EditMask && item.IsMaskTarget && !item.IsPixelTarget && item.HasMask,
                "Add Layer Mask adds a reveal-all mask and targets it (mask thumbnail framed)");
            check(doc.Properties is MaskPanel, $"the Properties panel shows the mask ({doc.Properties?.GetType().Name})");

            // Paint black into the mask: the live preview (full-resolution lane) already hides the layer there.
            var fullFrame = new TaskCompletionSource();
            void OnFull(bool full) { if (full) fullFrame.TrySetResult(); }
            doc.FrameDisplayed += OnFull;
            check(doc.BeginStroke(200, 150, new BrushSettings(40, 1f, 1f), new RgbColor(0, 0, 0), erase: false), "the brush starts a stroke in the mask");
            await Task.WhenAny(fullFrame.Task, Task.Delay(5000));
            doc.FrameDisplayed -= OnFull;
            var live = doc.LastFullRender;
            int at = (150 * model.Width + 200) * 4;
            check(fullFrame.Task.IsCompleted && live is not null && live[at] > 245 && live[at + 1] > 245,
                "while painting, the render already shows the layer hidden under the stroke");
            check(layer.Mask!.Pixels is null && layer.Pixels is not null, "the mask is not modified until the stroke ends");
            await doc.EndStrokeAsync();
            check(layer.Mask!.Pixels is not null && MaskBaker.Sample(layer.Mask, 200, 150) == 0f && MaskBaker.Sample(layer.Mask, 100, 150) == 1f,
                "the committed stroke is a new mask plane, black where painted");
            check(IsWhite(Pixel(200, 150)) && IsRed(Pixel(100, 150)), "the layer is hidden where the mask is black, visible elsewhere");

            // The eraser paints the background color (white) into the mask, revealing again.
            doc.BeginStroke(200, 150, new BrushSettings(10, 1f, 1f), new RgbColor(0, 0, 0), erase: true);
            await doc.EndStrokeAsync();
            check(MaskBaker.Sample(layer.Mask!, 200, 150) == 1f && IsRed(Pixel(200, 150)), "the eraser paints white (the background color) into the mask");
            doc.Undo();
            check(IsWhite(Pixel(200, 150)), "undo takes the eraser stroke back");

            // Shift-click on the mask thumbnail disables it; the whole layer shows.
            doc.ToggleMaskEnabled();
            check(layer.Mask!.Disabled && item.MaskDisabled && IsRed(Pixel(200, 150)), "disabling the mask shows the whole layer (thumbnail crossed out)");
            doc.Undo();
            check(!layer.Mask!.Disabled && IsWhite(Pixel(200, 150)), "undo enables it again");

            doc.Undo();
            check(layer.Mask is { Pixels: null } && IsRed(Pixel(200, 150)), "undo removes the mask stroke");
            doc.Redo();

            // Clicking the layer thumbnail targets pixels again: the brush paints the layer, not the mask.
            doc.Target(Item(layer), mask: false);
            check(!doc.EditMask && Item(layer).IsPixelTarget, "clicking the layer thumbnail targets the layer's pixels");
            doc.Target(Item(layer), mask: true);

            // Apply bakes the mask into transparency.
            await doc.ApplyMaskAsync();
            int li = (150 - layer.Bounds.Top) * layer.Bounds.Width + (200 - layer.Bounds.Left);
            check(layer.Mask is null && layer.Pixels!.Alpha!.Data[li] == 0 && IsWhite(Pixel(200, 150)), "Apply Layer Mask moves the mask into the layer's transparency");
            doc.Undo();
            check(layer.Mask is not null && layer.Pixels!.Alpha!.Data[li] == 255, "undo restores the mask and the pixels");

            // Groups take masks too.
            doc.NewGroup();
            var group = (LayerGroup)doc.SelectedLayer!.Node;
            doc.AddMask(reveal: false);
            check(group.Mask is { DefaultColor: 0 } && doc.EditMask, "groups get masks (Hide All)");
            doc.Undo();
            doc.Undo();

            // ---- Adjustment layers and the Properties panel ------------------------------------------------

            doc.SelectedLayer = Item(layer);
            doc.NewAdjustmentLayer("Levels");
            var levels = doc.SelectedLayer?.Node as AdjustmentLayer;
            check(levels is { Kind: "Levels", Name: "Levels 1", Mask: { DefaultColor: 255 } } && levels.Parent == model.Root
                  && model.Root.IndexOf(levels) == model.Root.IndexOf(layer) + 1 && doc.EditMask,
                "New Adjustment Layer › Levels goes above the selected layer with a white mask, mask targeted");
            var levelsPanel = doc.Properties as LevelsPanel;
            check(levelsPanel is not null, $"the Properties panel shows Levels ({doc.Properties?.GetType().Name})");
            check(IsWhite(Pixel(10, 10)), "new Levels layer leaves the image unchanged");
            for (int v = 250; v >= 128; v -= 2) levelsPanel!.OutputWhite = v; // a slider drag
            var p = Pixel(10, 10);
            check(p[0] is >= 126 and <= 130, $"dragging Output White darkens the image ({p[0]})");
            check(doc.UndoText == "Undo Change Output White", $"the drag is one undo step ({doc.UndoText})");
            doc.Undo();
            check(IsWhite(Pixel(10, 10)) && levelsPanel!.OutputWhite == 255, "undo restores the image and the panel");
            levelsPanel!.Channel = 1;
            levelsPanel.InputBlack = 100;
            check(((LevelsAdjustment)levels!.Adjustment!).Channels[0].InputBlack == 100 && ((LevelsAdjustment)levels.Adjustment!).Master.IsIdentity,
                "the channel menu edits the red record only");

            doc.NewAdjustmentLayer("Hue/Saturation");
            var hue = (AdjustmentLayer)doc.SelectedLayer!.Node;
            var huePanel = doc.Properties as HueSaturationPanel;
            check(huePanel is not null && IsRed(Pixel(100, 150)), "Hue/Saturation layer starts with no effect");
            huePanel!.Hue = 120;
            p = Pixel(100, 150);
            check(p[1] > 200 && p[0] < 60, $"Hue +120 turns the red band green ({p[0]},{p[1]},{p[2]})");
            doc.Undo();
            check(IsRed(Pixel(100, 150)) && huePanel.Hue == 0, "undo turns it red again");
            huePanel.Hue = 60;
            huePanel.Colorize = true;
            huePanel.Saturation = 50;
            // Hide the adjustment on the left half with its mask.
            doc.BeginStroke(0, 150, new BrushSettings(80, 1f, 1f), new RgbColor(0, 0, 0), erase: false);
            await doc.EndStrokeAsync();
            check(hue.Mask!.Pixels is not null && IsRed(Pixel(10, 150)) && !IsRed(Pixel(300, 150)),
                "painting on an adjustment layer paints its mask: the adjustment is hidden there only");

            doc.NewAdjustmentLayer("Curves");
            var curvesPanel = doc.Properties as CurvesPanel;
            curvesPanel!.Points = [new(0, 0), new(128, 60), new(255, 255)];
            var curves = (AdjustmentLayer)doc.SelectedLayer!.Node;
            check(((CurvesAdjustment)curves.Adjustment!).Master!.Count == 3, "the curve graph's points reach the Curves layer");
            foreach (var kind in new[] { "Brightness/Contrast", "Invert", "Threshold", "Posterize" })
                doc.NewAdjustmentLayer(kind);
            ((BrightnessContrastPanel)Properties(doc, "Brightness/Contrast")).Brightness = 30;
            ((ThresholdPanel)Properties(doc, "Threshold")).Level = 90;
            ((PosterizePanel)Properties(doc, "Posterize")).Levels = 7;
            check(model.Root.Children.OfType<AdjustmentLayer>().Count() == 7, "all seven adjustment kinds can be created");

            // ---- Save and reopen ----------------------------------------------------------------------
            string path = Path.Combine(Path.GetTempPath(), $"strayta-selftest-adjust-{Guid.NewGuid():N}.psd");
            await doc.SaveAsync(path);
            var reopened = PsdFile.OpenForEditing(path);
            var before = model.Root.Children.OfType<AdjustmentLayer>().ToList();
            var after = reopened.Root.Children.OfType<AdjustmentLayer>().ToList();
            bool same = before.Count == after.Count && before.Zip(after).All(pair =>
                pair.First.Name == pair.Second.Name && Equivalent(pair.First.Adjustment!, pair.Second.Adjustment));
            check(same, $"adjustment layers survive saving ({string.Join(", ", after.Select(a => a.Name))})");
            var hueAgain = after.First(a => a.Name == hue.Name);
            check(hueAgain.Mask is { Pixels: not null } m2 && m2.Bounds == hue.Mask!.Bounds && m2.Pixels.Data.SequenceEqual(hue.Mask.Pixels!.Data),
                "painted adjustment mask survives saving");
            var layerAgain = reopened.Root.Children.OfType<PixelLayer>().First(l => l.Name == layer.Name);
            check(layerAgain.Mask is { Pixels: not null } m3 && m3.Pixels.Data.SequenceEqual(layer.Mask!.Pixels!.Data), "layer mask survives saving");
            var reopenedImage = Compositor.Render(reopened).ToRgba8();
            var image = Compositor.Render(model).ToRgba8();
            check(FidelityReport.Compare(image, reopenedImage, model.Width, model.Height).MaxError <= 2, "the reopened file renders the same");
            File.Delete(path);
        }
        catch (Exception ex)
        {
            check(false, $"exception in mask/adjustment steps: {ex}");
        }

        static PropertiesPanel Properties(DocumentViewModel doc, string kind)
        {
            doc.SelectedLayer = doc.Layers.SelectMany(l => l.SelfAndDescendants()).First(i => i.Node is AdjustmentLayer a && a.Kind == kind);
            return doc.Properties!;
        }
    }

    /// <summary>Same settings, ignoring the identity records and empty curves the file format pads with.</summary>
    private static bool Equivalent(Adjustment a, Adjustment? b) => (a, b) switch
    {
        (LevelsAdjustment x, LevelsAdjustment y) => x.Master == y.Master && x.Channels.SequenceEqual(y.Channels.Take(x.Channels.Count)),
        (CurvesAdjustment x, CurvesAdjustment y) => x.Master!.SequenceEqual(y.Master!)
            && x.Channels.Select((c, i) => c is null ? i >= y.Channels.Count || y.Channels[i] is null : c.SequenceEqual(y.Channels[i]!)).All(ok => ok),
        _ => PsdAdjustmentWriter.SameSettings(a, b),
    };
}
