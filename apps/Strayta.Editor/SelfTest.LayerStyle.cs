using Strayta.Core;
using Strayta.Editor.Editing;
using Strayta.Editor.ViewModels;
using Strayta.Psd;
using Strayta.Rendering;

namespace Strayta.Editor;

/// <summary>
/// Self-test steps for layer styles through the real menu commands and the dialog's view model (answered by a script
/// instead of a person): live preview, Preview off, Cancel, OK as one undo step, the global light, Copy / Paste /
/// Clear Layer Style, effect eyes in the Layers panel, and saving and reopening.
/// </summary>
internal static partial class SelfTest
{
    /// <summary>Answers the Layer Style dialog from a script.</summary>
    private sealed class ScriptedLayerStyleDialogs : ILayerStyleDialogs
    {
        public Func<LayerStyleViewModel, Task<bool>>? Script { get; set; }
        public Func<GlobalLightViewModel, Task<bool>>? GlobalLightScript { get; set; }
        public Func<ScaleEffectsViewModel, Task<bool>>? ScaleScript { get; set; }
        public LayerStyleViewModel? Last { get; private set; }

        public async Task<bool> RunLayerStyleAsync(LayerStyleViewModel session)
        {
            Last = session;
            return Script is null ? false : await Script(session);
        }

        public async Task<bool> RunGlobalLightAsync(GlobalLightViewModel session) =>
            GlobalLightScript is not null && await GlobalLightScript(session);

        public async Task<bool> RunScaleEffectsAsync(ScaleEffectsViewModel session) =>
            ScaleScript is not null && await ScaleScript(session);
    }

    private static async Task RunLayerStyleStepsAsync(EditorViewModel editor, Action<bool, string> check)
    {
        var dialogs = new ScriptedLayerStyleDialogs();
        editor.LayerStyleDialogs = dialogs;
        try
        {
            await LayerStyleStepsAsync(editor, dialogs, check);
            await LayerStyleEffectStepsAsync(editor, dialogs, check); // SelfTest.LayerStyleEffects.cs
        }
        catch (Exception ex)
        {
            check(false, $"exception in Layer Style steps: {ex}");
        }
        finally
        {
            editor.LayerStyleDialogs = null;
        }
    }

    private static async Task LayerStyleStepsAsync(EditorViewModel editor, ScriptedLayerStyleDialogs dialogs, Action<bool, string> check)
    {
        var model = LayerFactory.NewDocument(300, 200, whiteBackground: true);
        var shape = SolidLayer("Shape", new PixelRect(60, 50, 160, 130), 220, 40, 40);
        var other = SolidLayer("Other", new PixelRect(190, 60, 260, 120), 40, 90, 220);
        model.Root.Add(shape);
        model.Root.Add(other);
        var doc = new DocumentViewModel(model, null, editor);
        editor.Factory.AddDocument(doc);
        editor.ActiveDocument = doc;
        await doc.RenderAsync();
        LayerItemViewModel Item(LayerNode n) => doc.Layers.SelectMany(l => l.SelfAndDescendants()).First(i => i.Node == n);
        doc.SelectedLayer = Item(shape);

        // ---- Menu: Layer › Layer Style › Drop Shadow…, then Cancel -------------------------------------------
        var menu = MenuItem("Layer", "Layer Style", "Drop Shadow…");
        check(menu is not null && MenuItem("Layer", "Layer Style", "Copy Layer Style") is not null, "Layer › Layer Style lists the styles and Copy/Paste/Clear");
        string undoBefore = doc.UndoText;
        bool sawPreview = false, previewOffRestores = false, globalFollows = false;
        dialogs.Script = async session =>
        {
            var shadow = session.SelectedEntry as ShadowEntry;
            check(shadow is { IsDropShadow: true, IsChecked: true }, "the dialog opens on Drop Shadow, turned on");
            shadow!.Size = 12;
            shadow.Distance = 9;
            shadow.Opacity = 80;
            await Task.Delay(150); // let the preview render
            sawPreview = shape.Effects?.Items.SingleOrDefault() is DropShadowEffect { Size: 12, Distance: 9 } d && Math.Abs(d.Opacity - 0.8f) < 1e-6;

            // Moving the global light moves this shadow and the drop shadow on no other layer (none has one yet).
            shadow.Angle = 45;
            globalFollows = session.GlobalAngle == 45 && model.GlobalLightAngle == 45;

            session.Preview = false;
            previewOffRestores = shape.Effects is null && model.GlobalLightAngle == 120;
            session.Preview = true;
            return false; // Cancel
        };
        await Click(menu);
        check(sawPreview, "changing the drop shadow shows it on the canvas while the dialog is open");
        check(globalFollows, "the angle of a shadow using the global light moves the document's global light");
        check(previewOffRestores, "Preview off shows the layer as it was");
        check(shape.Effects is null && model.GlobalLightAngle == 120 && doc.UndoText == undoBefore && !doc.IsModified,
            "Cancel restores the layer and the global light exactly and records nothing");

        // ---- OK: drop shadow, stroke and blending options, one undo step --------------------------------------
        dialogs.Script = session =>
        {
            var shadow = (ShadowEntry)session.SelectedEntry!;
            shadow.Size = 10;
            shadow.Distance = 8;
            shadow.Color = Avalonia.Media.Color.FromRgb(0, 0, 128);
            var stroke = session.Entries.OfType<StrokeEntry>().Single();
            session.SelectedEntry = stroke; // selecting a style by name turns it on
            stroke.Size = 4;
            stroke.PositionIndex = 0;
            session.Blending.Opacity = 90;
            return Task.FromResult(true);
        };
        await Click(menu);
        var fx = shape.Effects;
        var drop = fx?.Items.OfType<DropShadowEffect>().SingleOrDefault();
        check(drop is { Size: 10, Distance: 8, UseGlobalLight: true } && drop.Color == new RgbColor(0, 0, 128 / 255f)
              && fx!.Items.OfType<StrokeEffect>().SingleOrDefault() is { Size: 4, Position: StrokePosition.Outside } && Math.Abs(shape.Opacity - 0.9f) < 1e-6,
            $"OK applies the drop shadow, stroke and opacity ({string.Join(", ", fx?.Items.Select(e => e.GetType().Name) ?? [])})");
        check(doc.UndoText == "Undo Layer Style" && doc.IsModified, $"OK is one undo step called Layer Style ({doc.UndoText})");
        await Task.Delay(150); // let the preview render
        var rendered = Compositor.Render(model).ToRgba8();
        int below = ((130 + 5) * 300 + 150) * 4; // under the shape's bottom edge, where the shadow falls
        check(rendered[below] < 245 && rendered[below + 2] > rendered[below], $"the shadow is drawn below the layer in navy ({rendered[below]},{rendered[below + 1]},{rendered[below + 2]})");
        doc.Undo();
        check(shape.Effects is null && shape.Opacity == 1f, "one undo removes the whole style");
        doc.Redo();
        check(shape.Effects == fx && Math.Abs(shape.Opacity - 0.9f) < 1e-6, "redo brings it back");

        // Opening and closing with OK and no changes records nothing.
        string undoText = doc.UndoText;
        dialogs.Script = _ => Task.FromResult(true);
        await Click(MenuItem("Layer", "Layer Style", "Blending Options…"));
        check(doc.UndoText == undoText && ReferenceEquals(shape.Effects, fx), "OK without changes records nothing and keeps the style as it is");

        // ---- Global light across layers -------------------------------------------------------------------
        doc.SelectedLayer = Item(other);
        dialogs.Script = session =>
        {
            var inner = (ShadowEntry)session.SelectedEntry!;
            inner.Angle = 30; // uses the global light: the shape's drop shadow follows
            return Task.FromResult(true);
        };
        await Click(MenuItem("Layer", "Layer Style", "Inner Shadow…"));
        check(other.Effects?.Items.SingleOrDefault() is InnerShadowEffect { Angle: 30 } && model.GlobalLightAngle == 30
              && shape.Effects!.Items.OfType<DropShadowEffect>().Single().Angle == 30,
            "moving the global light in one layer's style moves the shadows of every layer that uses it");
        doc.Undo();
        check(other.Effects is null && model.GlobalLightAngle == 120 && shape.Effects!.Items.OfType<DropShadowEffect>().Single().Angle == 120,
            "undo puts the global light and the other layer's shadow back");

        // ---- Copy, Paste and Clear Layer Style -------------------------------------------------------------
        doc.SelectedLayer = Item(shape);
        await Click(MenuItem("Layer", "Layer Style", "Copy Layer Style"));
        doc.SelectedLayer = Item(other);
        await Click(MenuItem("Layer", "Layer Style", "Paste Layer Style"));
        check(Equals(other.Effects, shape.Effects) && other.Opacity == shape.Opacity && doc.UndoText == "Undo Paste Layer Style",
            "Paste Layer Style copies the effects and blending options as one step");
        await Click(MenuItem("Layer", "Layer Style", "Clear Layer Style"));
        check(other.Effects is null && doc.UndoText == "Undo Clear Layer Style", "Clear Layer Style removes the effects");
        doc.Undo();
        check(other.Effects is not null, "undo brings the cleared style back");

        // ---- Effect rows and eyes in the Layers panel -------------------------------------------------------
        var shapeItem = Item(shape);
        shapeItem.EffectsExpanded = true;
        var rows = shapeItem.EffectRows;
        check(shapeItem.ShowEffectRows && rows.Select(r => r.Name).SequenceEqual(["Effects", "Stroke", "Drop Shadow"]),
            $"the panel lists the effects under the layer ({string.Join(", ", rows.Select(r => r.Name))})");
        var dropRow = rows.First(r => r.Name == "Drop Shadow");
        shapeItem.ToggleEffect(dropRow);
        check(shape.Effects!.Items.OfType<DropShadowEffect>().Single() is { Enabled: false } && doc.UndoText == "Undo Hide Effect"
              && !EffectVisible(shape), "an effect's eye hides it, undoably");
        shapeItem.ToggleEffect(shapeItem.EffectRows[0]);
        check(shape.Effects is { Enabled: false } && doc.UndoText == "Undo Hide Layer Effects", "the Effects eye hides the whole style");
        doc.Undo();
        doc.Undo();
        check(shape.Effects is { Enabled: true } && EffectVisible(shape), "undo shows them again");
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
        {
            await Task.Delay(100); // let the panel lay out the effect rows
            var editButtons = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Avalonia.Controls.Border>()
                .Where(b => Avalonia.Controls.ToolTip.GetTip(b) as string == "Edit in Layer Style" && b.IsEffectivelyVisible).ToList();
            check(editButtons.Count >= rows.Count, $"each effect row has an edit button next to its eye ({editButtons.Count} for {rows.Count} rows)");
            if (Environment.GetEnvironmentVariable("STRAYTA_SELFTEST_SHOTS") is { Length: > 0 } dir
                && Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Views.LayersView>().FirstOrDefault() is { } panel)
            {
                var size = new Avalonia.PixelSize((int)panel.Bounds.Width * 2, (int)panel.Bounds.Height * 2);
                using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(size, new Avalonia.Vector(192, 192));
                bitmap.Render(panel);
                bitmap.Save(Path.Combine(dir, "layers-effect-rows.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
        }

        // ---- Save and reopen -------------------------------------------------------------------------------
        string path = Path.Combine(Path.GetTempPath(), $"strayta-selftest-styles-{Guid.NewGuid():N}.psd");
        await doc.SaveAsync(path);
        var reopened = PsdFile.OpenForEditing(path);
        var again = reopened.Root.Children.First(n => n.Name == "Shape");
        check(Equals(again.Effects, shape.Effects) && Math.Abs(again.Opacity - shape.Opacity) < 0.003f,
            "the saved file has the same layer style");
        check(Equals(reopened.Root.Children.First(n => n.Name == "Other").Effects, other.Effects), "and the pasted one");
        File.Delete(path);

        doc.MarkSavedForTest();
        editor.Factory.CloseDockable(doc);

        static bool EffectVisible(LayerNode n) => n.Effects!.Visible.OfType<DropShadowEffect>().Any();
    }

    /// <summary>
    /// STRAYTA_STYLEBENCH=new: a generated 4000×3000 document with a 3200×2400 layer (a disc on a transparent field,
    /// so the shadow shows), then the drop shadow slider drags (STRAYTA_STYLEBENCH=bevel: Bevel &amp; Emboss Size and Depth
    /// with Chisel Hard); STRAYTA_STYLEBENCH=1 uses the first opened file's
    /// selected (or largest) layer.
    /// </summary>
    public static async Task RunLayerStyleBenchmarkAsync(EditorViewModel editor, bool synthetic, bool bevel = false)
    {
        DocumentViewModel? doc;
        if (synthetic)
        {
            const int w = 4000, h = 3000, lw = 3200, lh = 2400;
            var model = LayerFactory.NewDocument(w, h, whiteBackground: true);
            var planes = Enumerable.Range(0, 4).Select(_ => Plane.Create(lw, lh, 8)).ToArray();
            Parallel.For(0, lh, y =>
            {
                for (int x = 0; x < lw; x++)
                {
                    int i = y * lw + x;
                    double dx = (x - lw / 2.0) / (lw / 2.0), dy = (y - lh / 2.0) / (lh / 2.0);
                    planes[0].Data[i] = (byte)(x * 255 / lw);
                    planes[1].Data[i] = (byte)(y * 255 / lh);
                    planes[2].Data[i] = 160;
                    planes[3].Data[i] = dx * dx + dy * dy < 0.9 ? (byte)255 : (byte)0;
                }
            });
            var layer = new PixelLayer { Name = "Disc", Bounds = new PixelRect(400, 300, 400 + lw, 300 + lh), Pixels = new Raster(ColorMode.Rgb, planes[..3], planes[3]) };
            model.Root.Add(layer);
            doc = new DocumentViewModel(model, null, editor);
            editor.Factory.AddDocument(doc);
            editor.ActiveDocument = doc;
            await doc.RenderAsync();
            doc.SelectedLayer = doc.Layers.First(i => i.Node == layer);
        }
        else
        {
            for (int i = 0; i < 300 && editor.ActiveDocument is null; i++) await Task.Delay(100);
            doc = editor.ActiveDocument;
            if (doc is null) return;
            doc.SelectedLayer ??= doc.Layers.SelectMany(l => l.SelfAndDescendants())
                .Where(i => i.Node is PixelLayer { Pixels: not null })
                .OrderByDescending(i => ((PixelLayer)i.Node).Bounds.Width * ((PixelLayer)i.Node).Bounds.Height).FirstOrDefault();
        }
        await Task.Delay(1500); // let the view fit the image and the preview caches warm up
        await doc.RunLayerStyleBenchmarkAsync(bevel);
    }
}
