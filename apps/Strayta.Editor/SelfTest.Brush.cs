using Strayta.Core;
using Strayta.Core.Painting;
using Strayta.Core.Selection;
using Strayta.Editor.Controls;
using Strayta.Editor.ViewModels;

namespace Strayta.Editor;

internal static partial class SelfTest
{
    private static float Alpha(PixelLayer layer, int x, int y) =>
        layer.Pixels?.Alpha is { } a && x >= layer.Bounds.Left && x < layer.Bounds.Right && y >= layer.Bounds.Top && y < layer.Bounds.Bottom
            ? a.GetNormalized((y - layer.Bounds.Top) * layer.Bounds.Width + (x - layer.Bounds.Left))
            : 0f;

    /// <summary>Brush options (Flow, Mode, Airbrush, Smoothing, pressure, tip) through the real view models.</summary>
    private static async Task RunBrushStepsAsync(EditorViewModel editor, Action<bool, string> check)
    {
        try
        {
            var model = Editing.LayerFactory.NewDocument(240, 160, whiteBackground: true);
            var doc = new DocumentViewModel(model, null, editor);
            editor.Factory.AddDocument(doc);
            editor.ActiveDocument = doc;
            await doc.RenderAsync();
            doc.SelectedLayer = doc.Layers[0];
            doc.NewLayer();
            var layer = (PixelLayer)doc.SelectedLayer!.Node;
            editor.Tool = CanvasTool.Brush;
            (editor.BrushSize, editor.BrushHardness, editor.BrushOpacity) = (20, 100, 100);
            editor.ForegroundColor = Avalonia.Media.Color.FromRgb(255, 0, 0);

            async Task Stroke(params (float X, float Y)[] points)
            {
                doc.BeginStroke(points[0].X, points[0].Y, editor.CurrentBrush, editor.CurrentColor, erase: editor.Tool == CanvasTool.Eraser);
                foreach (var p in points.Skip(1)) doc.ContinueStroke(p.X, p.Y);
                await doc.EndStrokeAsync();
            }

            // Flow: one dab lays down the flow; overlapping dabs build up, never past the opacity.
            editor.BrushFlow = 30;
            await Stroke((30, 30));
            check(Math.Abs(Alpha(layer, 30, 30) - 0.3f) < 0.01f, $"at 30% flow one dab covers 30% ({Alpha(layer, 30, 30):F3})");
            await Stroke((30, 60), (50, 60));
            float built = Alpha(layer, 40, 60);
            check(built > 0.6f && built < 1f, $"overlapping dabs build up within a stroke ({built:F3} after the ~4 dabs that cover a pixel)");
            editor.BrushOpacity = 50;
            await Stroke((30, 90), (60, 90));
            check(Alpha(layer, 45, 90) > 0.3f && Alpha(layer, 45, 90) <= 0.5f * built + 0.01f, $"opacity scales the build-up ({Alpha(layer, 45, 90):F3} = 50% of {built:F3})");
            (editor.BrushFlow, editor.BrushOpacity) = (100, 100);
            check(doc.UndoText == "Undo Brush", "each stroke is one undo step");

            // Airbrush: holding still keeps adding flow.
            editor.BrushFlow = 10;
            editor.BrushAirbrush = true;
            doc.BeginStroke(100, 30, editor.CurrentBrush, editor.CurrentColor, erase: false);
            await Task.Delay(500);
            await doc.EndStrokeAsync();
            float air = Alpha(layer, 100, 30);
            check(air > 0.45f, $"the airbrush builds up while the pointer rests (10% flow, 0.5 s: {air:F2})");
            (editor.BrushAirbrush, editor.BrushFlow) = (false, 100);

            // Modes: Behind paints only transparent pixels; Clear erases; Multiply darkens.
            editor.ForegroundColor = Avalonia.Media.Color.FromRgb(0, 0, 255);
            editor.BrushModeIndex = (int)PaintMode.Behind;
            await Stroke((100, 30), (140, 30));
            float red = layer.Pixels!.ColorPlanes[0].GetNormalized((30 - layer.Bounds.Top) * layer.Bounds.Width + 100 - layer.Bounds.Left);
            float blue = layer.Pixels.ColorPlanes[2].GetNormalized((30 - layer.Bounds.Top) * layer.Bounds.Width + 135 - layer.Bounds.Left);
            // Where the red was 82% opaque, blue shows only through the remaining 18%: red stays at 0.82.
            check(Math.Abs(red - air) < 0.03f && Alpha(layer, 100, 30) > 0.99f && blue > 0.95f && Alpha(layer, 135, 30) > 0.99f,
                $"Behind paints under the red paint and fills the empty area blue (R {red:F2} under {air:F2} red, B {blue:F2})");
            editor.BrushModeIndex = (int)PaintMode.Clear;
            editor.BrushOpacity = 60;
            await Stroke((135, 30));
            check(Math.Abs(Alpha(layer, 135, 30) - 0.4f) < 0.01f, $"Clear at 60% removes 60% of the alpha ({Alpha(layer, 135, 30):F3})");
            editor.BrushOpacity = 100;
            var bg = (PixelLayer)model.Root.Children[0];
            doc.SelectedLayer = doc.Layers.SelectMany(l => l.SelfAndDescendants()).First(i => i.Node == bg);
            editor.BrushModeIndex = (int)PaintMode.Multiply;
            editor.ForegroundColor = Avalonia.Media.Color.FromRgb(128, 128, 128);
            await Stroke((200, 130));
            check(Math.Abs(Sample(bg, 0, 200, 130) - 128 / 255f) < 0.01f, "Multiply with gray on white gives gray");
            await Stroke((200, 130));
            check(Math.Abs(Sample(bg, 0, 200, 130) - 64 / 255f) < 0.01f, "a second Multiply stroke darkens again");
            editor.BrushModeIndex = 0;
            doc.SelectedLayer = doc.Layers.SelectMany(l => l.SelfAndDescendants()).First(i => i.Node == layer);

            // Smoothing: small jitter stays on the string; a long pull drags the brush, trailing the pointer.
            editor.BrushSmoothing = 60;
            doc.BeginStroke(20, 140, editor.CurrentBrush, editor.CurrentColor, erase: false);
            var start = doc.SmoothedBrushPosition;
            doc.ContinueStroke(22, 141);
            doc.ContinueStroke(19, 139);
            check(start is not null && doc.SmoothedBrushPosition == start, "with Smoothing, jitter shorter than the string does not move the brush");
            doc.ContinueStroke(220, 140);
            var trailing = doc.SmoothedBrushPosition;
            check(trailing is { } t && t.X < 220 && t.X > 20, $"a long pull drags the brush behind the pointer (brush at x {trailing?.X:F0}, pointer at 220)");
            await Task.Delay(700);
            var caught = doc.SmoothedBrushPosition;
            check(caught is { } c && c.X > trailing!.Value.X && c.X > 215, $"when the pointer rests, the brush catches up (x {caught?.X:F0})");
            await doc.EndStrokeAsync();
            editor.BrushSmoothing = 0;

            // Pressure: pen pressure scales the size (and nothing happens with a mouse).
            editor.BrushPressureSize = true;
            doc.PenPressure = () => 0.25f;
            doc.NewLayer();
            var pen = (PixelLayer)doc.SelectedLayer!.Node;
            await Stroke((60, 120));
            check(pen.Bounds.Width <= 8, $"at 25% pressure a 20 px brush paints a 5 px dab ({pen.Bounds.Width} px wide incl. edge)");
            doc.PenPressure = () => null;
            await Stroke((120, 120));
            check(Alpha(pen, 128, 120) > 0.99f, "a mouse (no pressure) paints at full size");
            doc.PenPressure = null;
            editor.BrushPressureSize = false;

            // Tip: roundness and angle.
            (editor.BrushRoundness, editor.BrushAngle) = (25, 90);
            await Stroke((180, 80));
            check(Alpha(pen, 180, 72) > 0.99f && Alpha(pen, 186, 80) == 0f, "a 25% round tip at 90° paints a tall ellipse");
            (editor.BrushRoundness, editor.BrushAngle) = (100, 0);

            int steps = 0;
            while (doc.CanUndo) { doc.Undo(); steps++; }
            check(layer.Pixels is null, $"undo takes every brush step back ({steps})");
        }
        catch (Exception ex)
        {
            check(false, $"exception in brush steps: {ex}");
        }
    }

    /// <summary>Clone Source panel, heal modes, Content-Aware spot healing and Edit › Content-Aware Fill.</summary>
    private static async Task RunRetouchExtrasStepsAsync(EditorViewModel editor, Action<bool, string> check)
    {
        try
        {
            // ---- Clone sources: five slots, separate Clone Stamp / Healing Brush points, transforms --------------
            const int w = 240, h = 160;
            var model = Editing.LayerFactory.NewDocument(w, h, whiteBackground: true);
            var bg = (PixelLayer)model.Root.Children[0];
            var planes = Enumerable.Range(0, 3).Select(_ => Plane.Create(w, h, 8)).ToArray();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    (planes[0].Data[y * w + x], planes[1].Data[y * w + x], planes[2].Data[y * w + x]) = ((byte)x, (byte)y, 128);
            bg.Pixels = new Raster(ColorMode.Rgb, planes, null);
            var doc = new DocumentViewModel(model, null, editor);
            editor.Factory.AddDocument(doc);
            editor.ActiveDocument = doc;
            await doc.RenderAsync();
            doc.SelectedLayer = doc.Layers[0];
            (editor.BrushSize, editor.BrushHardness, editor.BrushOpacity, editor.BrushFlow) = (6, 100, 100, 100);
            editor.CloneModeIndex = 0;
            editor.CloneSampleIndex = 0;

            check(doc.CloneSources.Count == 5 && doc.ActiveCloneSourceIndex == 0, "a document has five clone sources, the first active");
            editor.Tool = CanvasTool.CloneStamp;
            doc.ActiveCloneSourceIndex = 1;
            doc.SetCloneSource(40, 60);
            editor.Tool = CanvasTool.Healing;
            check(doc.ActiveCloneSource.HasSource == false, "the Healing Brush keeps its own source: none yet in source 2");
            doc.SetCloneSource(100, 100);
            editor.Tool = CanvasTool.CloneStamp;
            check(doc.ActiveCloneSource.SourceText == "Source 40, 60", "the Clone Stamp's source is still where it was set");
            doc.ActiveCloneSourceIndex = 0;
            check(!doc.ActiveCloneSource.HasSource, "source 1 is still empty");
            doc.ActiveCloneSourceIndex = 1;

            // 50% scale: the copy is half size, so 10 px right of where painting starts shows 20 px right of the source point.
            var slot = doc.ActiveCloneSource;
            slot.WidthPercent = 50;
            check(slot.HeightPercent == 50, "W and H are linked");
            editor.CloneAligned = true;
            doc.BeginRetouchStroke(120, 60);
            doc.ContinueStroke(130, 60);
            await doc.EndStrokeAsync();
            check(Math.Abs(Sample(bg, 0, 130, 60) * 255 - 60) <= 1 && Math.Abs(Sample(bg, 1, 130, 60) * 255 - 60) <= 1,
                $"at 50% the clone is scaled about the source point (R {Sample(bg, 0, 130, 60) * 255:F0}, expected 60)");
            check(slot.OffsetX == 80 && slot.OffsetY == 0, $"the panel shows the aligned offset ({slot.OffsetX}, {slot.OffsetY})");
            slot.ResetTransformCommand.Execute(null);
            slot.Angle = 90;
            doc.SetCloneSource(40, 60);
            doc.BeginRetouchStroke(120, 100);
            doc.ContinueStroke(130, 100);
            await doc.EndStrokeAsync();
            check(Math.Abs(Sample(bg, 0, 130, 100) * 255 - 40) <= 1 && Math.Abs(Sample(bg, 1, 130, 100) * 255 - 70) <= 1,
                $"turned 90°, painting right copies what was below the source (R {Sample(bg, 0, 130, 100) * 255:F0}, G {Sample(bg, 1, 130, 100) * 255:F0})");
            slot.ResetTransformCommand.Execute(null);
            var overlay = doc.CloneOverlayFor(150, 100);
            check(overlay is { Show: true, Clipped: true, AutoHide: true } && overlay.Value.Angle == 0, "the overlay follows the panel's options");
            slot.OffsetX = 10;
            slot.OffsetY = 5;
            check(doc.RetouchSourceFor(150, 100) is { } typed && typed == (140f, 95f), "a typed offset moves the source");
            while (doc.CanUndo) doc.Undo();

            // ---- Healing modes ----------------------------------------------------------------------------
            const int hw = 320, hh = 200;
            var truth = RetouchTexture(hw, hh, 8, background: false);
            var healModel = Editing.LayerFactory.NewDocument(hw, hh, whiteBackground: true);
            var photo = new PixelLayer { Name = "Photo", Bounds = healModel.Bounds, Pixels = RetouchTexture(hw, hh, 8, false, (200, 100, 8)) };
            healModel.Root.Add(photo);
            var healDoc = new DocumentViewModel(healModel, null, editor);
            editor.Factory.AddDocument(healDoc);
            editor.ActiveDocument = healDoc;
            await healDoc.RenderAsync();
            healDoc.SelectedLayer = healDoc.Layers[0];
            editor.Tool = CanvasTool.Healing;
            (editor.HealAligned, editor.HealSampleIndex, editor.BrushSize, editor.BrushHardness) = (false, 0, 26, 80);
            editor.HealModeIndex = (int)HealMode.Darken;
            var before = photo.Pixels;
            healDoc.SetCloneSource(152, 100);
            healDoc.BeginRetouchStroke(200, 100);
            await healDoc.EndStrokeAsync();
            check(DiscError(photo, RetouchTexture(hw, hh, 8, false, (200, 100, 8)), 200, 100, 9) < 0.01,
                "in Darken mode the heal cannot lighten the dark blemish");
            healDoc.Undo();
            editor.HealModeIndex = (int)HealMode.Lighten;
            healDoc.BeginRetouchStroke(200, 100);
            await healDoc.EndStrokeAsync();
            check(DiscError(photo, truth, 200, 100, 9) < 0.02, $"in Lighten mode it does ({DiscError(photo, truth, 200, 100, 9):F4})");
            healDoc.Undo();
            editor.HealModeIndex = (int)HealMode.Replace;
            healDoc.BeginRetouchStroke(200, 100);
            await healDoc.EndStrokeAsync();
            check(Math.Abs(Sample(photo, 0, 200, 100) - truth.ColorPlanes[0].GetNormalized(100 * hw + 152)) < 0.003,
                "Replace paints the source unadapted, like the Clone Stamp");
            healDoc.Undo();
            editor.HealModeIndex = 0;
            (editor.HealDiffusion, editor.HealMultiplicative) = (2, true);
            healDoc.BeginRetouchStroke(200, 100);
            await healDoc.EndStrokeAsync();
            check(DiscError(photo, truth, 200, 100, 9) < 0.03, $"Diffusion 2 with the multiplicative heal still heals ({DiscError(photo, truth, 200, 100, 9):F4})");
            healDoc.Undo();
            (editor.HealDiffusion, editor.HealMultiplicative) = (5, false);
            check(ReferenceEquals(photo.Pixels, before), "undo restores the photo");

            // ---- Spot Healing: Content-Aware and Create Texture -------------------------------------------------
            editor.Tool = CanvasTool.SpotHealing;
            editor.SpotModeIndex = 0;
            photo.Pixels = RetouchTexture(hw, hh, 8, false, (100, 60, 8));
            foreach (var (type, limit) in new[] { (SpotHealType.ContentAware, 0.03), (SpotHealType.CreateTexture, 0.15) })
            {
                editor.SpotTypeIndex = (int)type;
                double blemished = DiscError(photo, truth, 100, 60, 9);
                healDoc.BeginRetouchStroke(98, 59);
                healDoc.ContinueStroke(102, 61);
                await healDoc.EndStrokeAsync();
                double healed = DiscError(photo, truth, 100, 60, 9);
                check(healDoc.LastSpotType == type && healed < limit && healed < blemished * 0.4,
                    $"Spot Healing {editor.SpotTypeNames[(int)type]} removes the blemish (RMSE {blemished:F3} -> {healed:F4}, {healDoc.LastRetouchTimings.TotalMs:F0} ms)");
                healDoc.Undo();
            }
            editor.SpotTypeIndex = 0;

            // ---- Edit › Content-Aware Fill ----------------------------------------------------------------------
            var script = new ScriptedContentAwareFill();
            editor.ContentAwareFillDialogs = script;
            healDoc.SetSelection(SelectionMask.Ellipse(new PixelRect(88, 48, 113, 73), healModel.Bounds), "Elliptical Marquee");
            script.Next = new ContentAwareFillSettings(SampleAllLayers: false, OutputToNewLayer: true);
            int layersBefore = healModel.Root.Children.Count;
            await editor.ContentAwareFillCommand.ExecuteAsync(null);
            var filled = healDoc.SelectedLayer?.Node as PixelLayer;
            check(healModel.Root.Children.Count == layersBefore + 1 && filled is { Pixels: not null } && filled != photo && healDoc.UndoText == "Undo Content-Aware Fill",
                $"Content-Aware Fill to a new layer adds a layer with the fill ({healDoc.LastContentAwareFillTimings.TotalMs:F0} ms)");
            check(filled is not null && filled.Bounds.Width <= 26 && Math.Abs(Sample(filled, 0, 100, 60) - truth.ColorPlanes[0].GetNormalized(60 * hw + 100)) < 0.05,
                $"the new layer holds only the selected area, filled to match ({filled?.Bounds})");
            healDoc.Undo();
            healDoc.SelectedLayer = healDoc.Layers.SelectMany(l => l.SelfAndDescendants()).First(i => i.Node == photo);
            script.Next = new ContentAwareFillSettings();
            await editor.ContentAwareFillCommand.ExecuteAsync(null);
            double fillError = DiscError(photo, truth, 100, 60, 9);
            check(fillError < 0.03, $"Content-Aware Fill into the current layer removes the blemish (RMSE {fillError:F4}, {healDoc.LastContentAwareFillTimings.TotalMs:F0} ms)");
            healDoc.Deselect();
            script.Next = new ContentAwareFillSettings();
            await editor.ContentAwareFillCommand.ExecuteAsync(null);
            check(healDoc.Notice.Contains("needs a selection"), "without a selection it explains what it needs");
            editor.ContentAwareFillDialogs = null;
        }
        catch (Exception ex)
        {
            check(false, $"exception in retouch extras: {ex}");
        }
    }

    private sealed class ScriptedContentAwareFill : IContentAwareFillDialogs
    {
        public ContentAwareFillSettings? Next { get; set; }
        public Task<ContentAwareFillSettings?> AskContentAwareFillAsync(ContentAwareFillSettings current) => Task.FromResult(Next);
    }

    /// <summary>STRAYTA_PAINTBENCH=new: the paint benchmarks on a generated 4000×3000 textured photo.</summary>
    public static async Task RunSyntheticPaintBenchmarkAsync(EditorViewModel editor)
    {
        const int w = 4000, h = 3000;
        var model = Editing.LayerFactory.NewDocument(w, h, whiteBackground: true);
        model.Root.Add(new PixelLayer { Name = "Photo", Bounds = model.Bounds, Pixels = RetouchTexture(w, h, 8, false) });
        var doc = new DocumentViewModel(model, null, editor);
        editor.Factory.AddDocument(doc);
        editor.ActiveDocument = doc;
        await doc.RenderAsync();
        doc.SelectedLayer = doc.Layers[0];
        await Task.Delay(1500);
        await doc.RunPaintBenchmarkAsync();
        await doc.RunBrushOptionsBenchmarkAsync(); // DocumentViewModel.BrushBench.cs
    }
}
