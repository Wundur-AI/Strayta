using Strayta.Core;
using Strayta.Core.Painting;
using Strayta.Editor.Editing;
using Strayta.Editor.ViewModels;
using Strayta.Psd;
using Strayta.Rendering;

namespace Strayta.Editor;

/// <summary>
/// Self-test steps for Bevel &amp; Emboss (with its Contour and Texture sub-pages), Satin, Pattern Overlay, contours and
/// the Quality settings, gradient strokes and glows, several effects of one kind, styles on groups, Global Light,
/// Scale Effects and Clear Layer Style, all through the menu commands and the dialogs' view models; then saving and
/// reopening.
/// </summary>
internal static partial class SelfTest
{
    private static async Task LayerStyleEffectStepsAsync(EditorViewModel editor, ScriptedLayerStyleDialogs dialogs, Action<bool, string> check)
    {
        var model = LayerFactory.NewDocument(320, 220, whiteBackground: true);
        var shape = SolidLayer("Shape", new PixelRect(40, 40, 160, 140), 90, 140, 210);
        var a = SolidLayer("A", new PixelRect(190, 40, 230, 80), 200, 60, 60);
        var b = SolidLayer("B", new PixelRect(240, 90, 280, 130), 200, 60, 60);
        var group = new LayerGroup { Name = "Group", BlendMode = BlendMode.PassThrough };
        group.Add(a);
        group.Add(b);
        model.Root.Add(shape);
        model.Root.Add(group);
        var doc = new DocumentViewModel(model, null, editor);
        editor.Factory.AddDocument(doc);
        editor.ActiveDocument = doc;
        await doc.RenderAsync();
        LayerItemViewModel Item(LayerNode n) => doc.Layers.SelectMany(l => l.SelfAndDescendants()).First(i => i.Node == n);
        doc.SelectedLayer = Item(shape);

        // ---- Bevel & Emboss with its Contour and Texture sub-pages ------------------------------------------
        check(MenuItem("Layer", "Layer Style", "Bevel & Emboss…") is not null && MenuItem("Layer", "Layer Style", "Satin…") is not null
              && MenuItem("Layer", "Layer Style", "Pattern Overlay…") is not null && MenuItem("Layer", "Layer Style", "Global Light…") is not null
              && MenuItem("Layer", "Layer Style", "Scale Effects…") is not null,
            "Layer › Layer Style lists Bevel & Emboss, Satin, Pattern Overlay, Global Light and Scale Effects");
        byte[]? previewBefore = null, previewAfter = null;
        var texturePattern = PatternLibrary.BuiltIn[2];
        dialogs.Script = async session =>
        {
            var bevel = session.SelectedEntry as BevelEntry;
            check(bevel is { IsChecked: true }, "the dialog opens on Bevel & Emboss, turned on");
            int at = session.Entries.IndexOf(bevel!);
            check(session.Entries[at + 1] is BevelContourEntry && session.Entries[at + 2] is BevelTextureEntry,
                "Contour and Texture are listed under Bevel & Emboss");
            await Task.Delay(150);
            previewBefore = Compositor.Render(model).ToRgba8();
            bevel!.Size = 12;
            bevel.Depth = 250;
            bevel.TechniqueIndex = 1; // Chisel Hard
            bevel.StyleIndex = 1; // Inner Bevel
            bevel.Gloss.PresetIndex = ContourSetting.Names.ToList().IndexOf("Cone");
            var contour = bevel.ContourPage;
            session.SelectedEntry = contour; // selecting the sub-page turns it on
            contour.Contour.Points = [new(0, 0), new(128, 200), new(255, 255)];
            contour.Range = 70;
            var texture = bevel.TexturePage;
            session.SelectedEntry = texture;
            texture.Pattern.Pattern = texturePattern;
            texture.Pattern.Scale = 200;
            texture.Depth = 150;
            await Task.Delay(150);
            previewAfter = Compositor.Render(model).ToRgba8();
            return true;
        };
        string undoBefore = doc.UndoText;
        await Click(MenuItem("Layer", "Layer Style", "Bevel & Emboss…"));
        var bevelFx = shape.Effects?.Items.OfType<BevelEffect>().SingleOrDefault();
        check(bevelFx is { Size: 12, Technique: BevelTechnique.ChiselHard, Style: BevelStyle.InnerBevel, UseContour: true, UseTexture: true }
              && Math.Abs(bevelFx.Depth - 2.5f) < 1e-4 && bevelFx.GlossContour.Name == "Cone" && bevelFx.Contour.Name == "Custom"
              && Math.Abs(bevelFx.ContourRange - 0.7f) < 1e-4 && bevelFx.Texture?.Pattern.Id == texturePattern.Id && Math.Abs(bevelFx.Texture.Scale - 2f) < 1e-4,
            $"OK applies the bevel with its contour and texture ({bevelFx})");
        check(previewBefore is not null && previewAfter is not null && !previewBefore.SequenceEqual(previewAfter),
            "the bevel shows on the canvas while the dialog is open");
        check(doc.UndoText == "Undo Layer Style" && undoBefore != doc.UndoText, "the bevel is one undo step");
        var lit = Compositor.Render(model).ToRgba8();
        int top = (42 * 320 + 100) * 4, bottom = (137 * 320 + 100) * 4;
        check(lit[top] > lit[bottom], $"the edge facing the light is brighter than the one facing away ({lit[top]} vs {lit[bottom]})");

        // ---- Satin, Pattern Overlay and a gradient glow with Quality settings ---------------------------------
        dialogs.Script = session =>
        {
            var satin = session.Entries.OfType<SatinEntry>().Single();
            session.SelectedEntry = satin;
            satin.Distance = 20;
            satin.Size = 18;
            satin.Contour.PresetIndex = ContourSetting.Names.ToList().IndexOf("Gaussian");
            var overlay = session.Entries.OfType<PatternOverlayEntry>().Single();
            session.SelectedEntry = overlay;
            overlay.Opacity = 40;
            overlay.Pattern.Scale = 150;
            var glow = session.Entries.OfType<GlowEntry>().First(g => !g.IsInnerGlow);
            session.SelectedEntry = glow;
            glow.UseGradient = true;
            glow.Gradient = GradientPresets.BuiltIn.First(g => g.Name == "Spectrum");
            glow.TechniqueIndex = 1;
            glow.Size = 15;
            glow.Noise = 20;
            glow.Range = 30;
            glow.Jitter = 10;
            glow.Contour.AntiAliased = true;
            return Task.FromResult(true);
        };
        await Click(MenuItem("Layer", "Layer Style", "Satin…"));
        var fx = shape.Effects!;
        check(fx.Items.OfType<SatinEffect>().SingleOrDefault() is { Distance: 20, Size: 18 } s && s.Contour.Name == "Gaussian",
            "the satin is applied with its contour");
        check(fx.Items.OfType<PatternOverlayEffect>().SingleOrDefault() is { Fill: { Pattern.Resolved: not null, Scale: 1.5f } } p && Math.Abs(p.Opacity - 0.4f) < 1e-4,
            "the pattern overlay is applied with a pattern from the picker");
        check(fx.Items.OfType<OuterGlowEffect>().SingleOrDefault() is { Gradient.Name: "Spectrum", Technique: GlowTechnique.Precise, AntiAliased: true } g
              && Math.Abs(g.Noise - 0.2f) < 1e-4 && Math.Abs(g.Range - 0.3f) < 1e-4 && Math.Abs(g.Jitter - 0.1f) < 1e-4,
            "the gradient glow is applied with technique, noise, range, jitter and anti-aliasing");

        // ---- Several drop shadows and strokes; a gradient stroke ---------------------------------------------
        dialogs.Script = session =>
        {
            var shadow = (ShadowEntry)session.SelectedEntry!;
            shadow.Size = 6;
            shadow.Distance = 4;
            check(shadow.CanAddInstance && !shadow.CanRemoveInstance, "a drop shadow row offers \"+\"");
            session.AddInstance(shadow);
            var second = (ShadowEntry)session.SelectedEntry!;
            check(!ReferenceEquals(second, shadow) && second.IsChecked && second.Size == 6 && second.CanRemoveInstance,
                "\"+\" adds another drop shadow above it, with the same settings, selected and on");
            second.Distance = 14;
            second.Color = Avalonia.Media.Color.FromRgb(200, 0, 0);
            var stroke = session.Entries.OfType<StrokeEntry>().Single();
            session.SelectedEntry = stroke;
            stroke.FillTypeIndex = 1;
            stroke.Gradient = GradientPresets.BuiltIn.First(x => x.Name == "Blue, Red, Yellow");
            stroke.GradientStyleIndex = 5; // Shape Burst
            stroke.Size = 5;
            session.AddInstance(stroke);
            var stroke2 = (StrokeEntry)session.SelectedEntry!;
            stroke2.FillTypeIndex = 0;
            stroke2.Size = 2;
            session.RemoveInstance(stroke2);
            check(session.Entries.OfType<StrokeEntry>().Count() == 1, "\"−\" deletes one of several strokes");
            return Task.FromResult(true);
        };
        await Click(MenuItem("Layer", "Layer Style", "Drop Shadow…"));
        var shadows = shape.Effects!.Items.OfType<DropShadowEffect>().ToList();
        check(shadows.Count == 2 && shadows[0].Distance == 4 && shadows[1].Distance == 14,
            $"two drop shadows, the new one on top ({string.Join(", ", shadows.Select(x => x.Distance))})");
        check(shape.Effects!.Items.OfType<StrokeEffect>().SingleOrDefault() is { FillType: StrokeFillType.Gradient, GradientFill.Style: GradientStyle.ShapeBurst },
            "the stroke is a shape-burst gradient stroke");
        Item(shape).EffectsExpanded = true;
        check(Item(shape).EffectRows.Count(r => r.Name == "Drop Shadow") == 2, "the Layers panel lists both drop shadows");

        // ---- Global Light… moves every shadow and bevel that follows it --------------------------------------
        dialogs.GlobalLightScript = session =>
        {
            session.Angle = 45;
            session.Altitude = 50;
            return Task.FromResult(true);
        };
        await Click(MenuItem("Layer", "Layer Style", "Global Light…"));
        check(model.GlobalLightAngle == 45 && model.GlobalLightAltitude == 50
              && shape.Effects!.Items.OfType<BevelEffect>().Single() is { Angle: 45, Altitude: 50 }
              && shape.Effects.Items.OfType<DropShadowEffect>().All(x => x.Angle == 45) && doc.UndoText == "Undo Global Light",
            "Global Light moves the document's light, the bevel and the shadows, as one step");
        doc.Undo();
        check(model.GlobalLightAngle == 120 && model.GlobalLightAltitude == 30 && shape.Effects!.Items.OfType<BevelEffect>().Single().Angle == 120,
            "undo puts the light back");

        // ---- Scale Effects… ----------------------------------------------------------------------------------
        dialogs.ScaleScript = session =>
        {
            session.Scale = 200;
            return Task.FromResult(true);
        };
        await Click(MenuItem("Layer", "Layer Style", "Scale Effects…"));
        check(shape.Effects!.Items.OfType<BevelEffect>().Single().Size == 24 && shape.Effects.Items.OfType<SatinEffect>().Single().Distance == 40
              && doc.UndoText == "Undo Scale Effects", "Scale Effects doubles sizes and distances as one step");
        doc.Undo();
        check(shape.Effects!.Items.OfType<BevelEffect>().Single().Size == 12, "undo restores the sizes");

        // ---- Styles on a group -------------------------------------------------------------------------------
        doc.SelectedLayer = Item(group);
        var groupBefore = Compositor.Render(model).ToRgba8();
        dialogs.Script = session =>
        {
            check(session.Blending.BlendModes.Contains(BlendMode.PassThrough), "a group's blending options offer Pass Through");
            var overlay = (ColorOverlayEntry)session.SelectedEntry!;
            overlay.Color = Avalonia.Media.Color.FromRgb(0, 160, 0);
            return Task.FromResult(true);
        };
        await Click(MenuItem("Layer", "Layer Style", "Color Overlay…"));
        await Task.Delay(100);
        var groupAfter = Compositor.Render(model).ToRgba8();
        int inA = (60 * 320 + 210) * 4, inB = (110 * 320 + 260) * 4;
        check(group.Effects?.Items.SingleOrDefault() is ColorOverlayEffect && groupAfter[inA + 1] == 160 && groupAfter[inB + 1] == 160 && groupBefore[inA + 1] != 160,
            "a group takes a layer style, drawn over its content");

        // ---- Save and reopen ----------------------------------------------------------------------------------
        string path = Path.Combine(Path.GetTempPath(), $"strayta-selftest-styles2-{Guid.NewGuid():N}.psd");
        await doc.SaveAsync(path);
        var reopened = PsdFile.OpenForEditing(path);
        var again = reopened.Root.Children.First(n => n.Name == "Shape");
        var differing = shape.Effects!.Items.Where(e => again.Effects?.Items.Contains(e) != true).Select(e => e.GetType().Name);
        check(Equals(again.Effects, shape.Effects),
            $"the saved file has the same bevel, satin, pattern overlay, glow, strokes and shadows (differing: {string.Join(", ", differing)})");
        check(reopened.Patterns.Any(x => x.Id == texturePattern.Id), "the patterns the styles use are saved in the file");
        check(Equals(reopened.Root.Children.OfType<LayerGroup>().Single().Effects, group.Effects), "and the group's style");
        check(Compositor.Render(reopened).ToRgba8().SequenceEqual(Compositor.Render(model).ToRgba8()), "the reopened file renders the same");
        File.Delete(path);

        // ---- Clear Layer Style resets the blending options too ------------------------------------------------
        doc.SelectedLayer = Item(shape);
        shape.Opacity = 0.5f;
        shape.FillOpacity = 0.3f;
        shape.BlendMode = BlendMode.Multiply;
        await Click(MenuItem("Layer", "Layer Style", "Clear Layer Style"));
        check(shape.Effects is null && shape.Opacity == 1f && shape.FillOpacity == 1f && shape.BlendMode == BlendMode.Normal,
            "Clear Layer Style removes the effects and resets blend mode, opacity and fill");
        doc.Undo();
        check(shape.Effects is not null && shape.BlendMode == BlendMode.Multiply && Math.Abs(shape.FillOpacity - 0.3f) < 1e-6, "undo brings them back");

        dialogs.GlobalLightScript = null;
        dialogs.ScaleScript = null;
        doc.MarkSavedForTest();
        editor.Factory.CloseDockable(doc);
    }
}
