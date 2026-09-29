using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.VisualTree;
using Strayta.Core;
using Strayta.Core.Painting;
using Strayta.Core.Selection;
using Strayta.Editor.Controls;
using Strayta.Editor.Editing;
using Strayta.Editor.ViewModels;
using Strayta.Editor.Views;
using Strayta.Psd;

namespace Strayta.Editor;

internal static partial class SelfTest
{
    /// <summary>Answers the Fill, Stroke and Pattern Name dialogs from the script.</summary>
    private sealed class ScriptedFillDialogs : IFillDialogs
    {
        public Func<FillDialogViewModel, bool>? Fill;
        public Func<StrokeDialogViewModel, bool>? Stroke;
        public string? PatternName;

        public Task<bool> AskFillAsync(FillDialogViewModel fill) => Task.FromResult(Fill?.Invoke(fill) ?? false);
        public Task<bool> AskStrokeAsync(StrokeDialogViewModel stroke) => Task.FromResult(Stroke?.Invoke(stroke) ?? false);
        public Task<string?> AskPatternNameAsync(string suggested, Pattern preview) => Task.FromResult(PatternName);
    }

    /// <summary>
    /// Gradient Editor, gradient picker, Gradient tool options (stops, Method, Mode, Transparency), Paint Bucket with a
    /// pattern, Edit › Fill…, Edit › Stroke…, Edit › Define Pattern… and document patterns in a saved PSD, through the
    /// real view models, with presets kept in a temporary folder.
    /// </summary>
    private static async Task RunGradientAndFillStepsAsync(EditorViewModel editor, Action<bool, string> check)
    {
        string presetDir = Path.Combine(Path.GetTempPath(), $"strayta-selftest-presets-{Guid.NewGuid():N}");
        var dialogs = new ScriptedFillDialogs();
        var savedDialogs = editor.FillDialogs;
        editor.FillDialogs = dialogs;
        UserPresets.UseDirectory(presetDir);
        try
        {
            await GradientEditorStepsAsync(editor, check);
            await GradientToolStepsAsync(editor, check);
            await FillStepsAsync(editor, dialogs, check);
        }
        catch (Exception ex)
        {
            check(false, $"exception in gradient and fill steps: {ex}");
        }
        finally
        {
            editor.FillDialogs = savedDialogs;
            UserPresets.UseDirectory(UserPresets.DefaultDirectory());
            try { Directory.Delete(presetDir, recursive: true); } catch (IOException) { }
        }
    }

    private static int PixelIndex(PixelLayer l, int x, int y) => (y - l.Bounds.Top) * l.Bounds.Width + (x - l.Bounds.Left);

    private static (byte R, byte G, byte B, byte A) Rgba(PixelLayer l, int x, int y)
    {
        if (l.Pixels is not { } p || x < l.Bounds.Left || y < l.Bounds.Top || x >= l.Bounds.Right || y >= l.Bounds.Bottom) return default;
        int i = PixelIndex(l, x, y);
        return (p.ColorPlanes[0].Data[i], p.ColorPlanes[1].Data[i], p.ColorPlanes[2].Data[i], p.Alpha?.Data[i] ?? (byte)255);
    }

    private static async Task<(DocumentViewModel Doc, PixelLayer Layer)> NewTestDocumentAsync(EditorViewModel editor, int w, int h)
    {
        var model = Editing.LayerFactory.NewDocument(w, h, whiteBackground: true);
        var doc = new DocumentViewModel(model, null, editor);
        editor.Factory.AddDocument(doc);
        editor.ActiveDocument = doc;
        await doc.RenderAsync();
        doc.SelectedLayer = doc.Layers[0];
        doc.NewLayer();
        return (doc, (PixelLayer)doc.SelectedLayer!.Node);
    }

    private static async Task GradientEditorStepsAsync(EditorViewModel editor, Action<bool, string> check)
    {
        var ed = new GradientEditorViewModel(GradientPresets.BlackToWhite, new RgbColor(1, 0, 0), new RgbColor(0, 0, 1));
        check(ed.ColorStops.Count == 2 && ed.OpacityStops.Count == 2 && ed.Name == "Black, White" && ed.Presets.Count >= GradientPresets.BuiltIn.Count,
            $"the Gradient Editor opens on a preset with its stops and the preset grid ({ed.Presets.Count} presets)");

        var mid = ed.AddStop(color: true, 0.5f);
        check(ed.ColorStops.Count == 3 && ReferenceEquals(ed.SelectedStop, mid) && mid.Color.R is > 80 and < 160,
            $"clicking below the bar adds a color stop with the gradient's color there ({mid.Color})");
        ed.StopColor = Colors.Red;
        ed.StopLocation = 40;
        ed.SelectedStop = ed.Ordered(true)[0];
        ed.MidpointSelected = true;
        ed.StopLocation = 25;
        ed.Smoothness = 0;
        var g = ed.Gradient;
        check(g.Colors.Count == 3 && Math.Abs(g.Colors[1].Location - 0.4f) < 1e-4 && g.Colors[1].Color == new RgbColor(1, 0, 0)
              && Math.Abs(g.Colors[0].Midpoint - 0.25f) < 1e-4 && g.Smoothness == 0,
            $"location, color, midpoint and smoothness edit the Core gradient (stops at {string.Join(", ", g.Colors.Select(c => c.Location))})");
        var lut = GradientLut.Build(g, GradientMethod.Classic);
        check(Math.Abs(lut.At(0.1f).R - 0.5f) < 0.02f, $"the 25% midpoint puts the half-way color a quarter of the way ({lut.At(0.1f).R:F2} at 10%)");

        var opacity = ed.AddStop(color: false, 0.7f);
        opacity.Opacity = 0.2f;
        ed.StopOpacity = 20;
        check(ed.Gradient.Opacities.Count == 3 && Math.Abs(ed.Gradient.Opacities[1].Opacity - 0.2f) < 1e-4, "clicking above the bar adds an opacity stop");
        check(ed.RemoveStop(opacity) && ed.Gradient.Opacities.Count == 2, "dragging a stop off the bar deletes it");
        check(!ed.RemoveStop(ed.OpacityStops[0]), "a row keeps at least two stops");

        ed.SelectedStop = ed.Ordered(true)[2];
        ed.StopKindIndex = 2;
        check(ed.Gradient.Colors[2].Kind == GradientStopKind.Background && ed.Gradient.Colors[2].Color == new RgbColor(0, 0, 1),
            "a stop can follow the background color");

        ed.Name = "Selftest Gradient";
        ed.NewPresetCommand.Execute(null);
        check(UserPresets.Shared.Gradients.Count == 1 && ed.Presets.Any(p => p.IsUser && p.Name == "Selftest Gradient") && File.Exists(UserPresets.Shared.FilePath),
            "New saves a user preset, listed after the built-in ones and written to the presets file");
        var reloaded = UserPresets.UseDirectory(UserPresets.Shared.Directory);
        check(reloaded.Gradients.Count == 1 && reloaded.Gradients[0] == ed.Gradient with { Name = "Selftest Gradient" },
            "the user preset survives a restart exactly");

        ed.TypeIndex = 1;
        ed.Roughness = 80;
        var noise = ed.Gradient;
        ed.RandomizeCommand.Execute(null);
        check(noise.Noise is { Roughness: 0.8f } && ed.Gradient.Noise!.Seed != noise.Noise.Seed && ed.BarImage is not null, "Noise type with Roughness and Randomize");
        ed.Detach();

        // The real dialog lays out with its bar (shown without input and closed again).
        var window = new GradientEditorWindow(new GradientEditorViewModel(GradientPresets.BuiltIn.First(p => p.Name == "Spectrum"), RgbColor.Black, new RgbColor(1, 1, 1)));
        window.Show();
        await Task.Delay(150);
        var bar = window.GetVisualDescendants().OfType<GradientBar>().FirstOrDefault();
        check(bar is { Bounds.Width: > 200 }, $"the Gradient Editor window shows its gradient bar ({bar?.Bounds.Width:F0} px wide)");
        window.Close();
    }

    private static async Task GradientToolStepsAsync(EditorViewModel editor, Action<bool, string> check)
    {
        var (doc, layer) = await NewTestDocumentAsync(editor, 200, 60);
        editor.SetToolCommand.Execute("Gradient");
        editor.ForegroundColor = Colors.Black;
        editor.BackgroundColor = Colors.White;
        editor.GradientDither = false;
        editor.GradientOpacity = 100;
        editor.GradientReverse = false;
        editor.GradientType = GradientType.Linear;

        // The options bar's picker is bound to the tool's gradient.
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
        {
            await Task.Delay(100);
            var picker = main.GetVisualDescendants().OfType<GradientPicker>().FirstOrDefault();
            var spectrum = GradientPresets.BuiltIn.First(p => p.Name == "Spectrum");
            if (picker is null) check(false, "the Gradient tool's options bar shows a gradient picker");
            else
            {
                picker.Gradient = spectrum;
                check(ReferenceEquals(editor.ToolGradient, spectrum), "choosing a preset in the options-bar picker sets the tool's gradient");
            }
        }

        async Task Drag(float x0, float x1)
        {
            doc.BeginGradient(x0, 30);
            doc.MoveGradient((x0 + x1) / 2, 30);
            await Task.Delay(30);
            doc.MoveGradient(x1, 30);
            await doc.EndGradientAsync();
        }

        // Red to green, Classic vs Perceptual: the perceptual middle is lighter.
        editor.ToolGradient = GradientPresets.BuiltIn.First(p => p.Name == "Red, Green");
        editor.GradientMethod = GradientMethod.Classic;
        await Drag(0, 200);
        var classic = Rgba(layer, 100, 30);
        doc.Undo();
        editor.GradientMethod = GradientMethod.Perceptual;
        await Drag(0, 200);
        var perceptual = Rgba(layer, 100, 30);
        static double Luma((byte R, byte G, byte B, byte A) c) => 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;
        check(Luma(perceptual) > Luma(classic) + 10 && Rgba(layer, 1, 30).R > 200 && Rgba(layer, 198, 30).G > 140,
            $"Method: Perceptual's middle is lighter than Classic's (luma {Luma(perceptual):F0} vs {Luma(classic):F0})");
        doc.Undo();

        // A multi-stop gradient from the editor.
        editor.ToolGradient = new Gradient(
            [new GradientColorStop(0, 0.5f, new RgbColor(1, 0, 0)), new GradientColorStop(0.5f, 0.5f, new RgbColor(0, 0, 1)), new GradientColorStop(1, 0.5f, new RgbColor(1, 0, 0))],
            [new GradientOpacityStop(0, 0.5f, 1), new GradientOpacityStop(1, 0.5f, 1)]) { Name = "RBR" };
        await Drag(0, 200);
        check(Rgba(layer, 100, 30) is { B: > 240, R: < 15 } && Rgba(layer, 2, 30) is { R: > 240 } && Rgba(layer, 197, 30) is { R: > 240 },
            $"a three-stop gradient draws its middle stop ({Rgba(layer, 100, 30)})");
        doc.Undo();

        // Transparency off draws Foreground to Transparent opaque.
        editor.ToolGradient = GradientPresets.ForegroundToTransparent;
        editor.GradientTransparency = false;
        await Drag(0, 200);
        check(Rgba(layer, 190, 30).A == 255, "Transparency off ignores the opacity stops");
        doc.Undo();
        editor.GradientTransparency = true;
        await Drag(0, 200);
        check(Rgba(layer, 190, 30).A < 20, "Transparency on fades to transparent");
        doc.Undo();

        // Mode: Multiply over gray.
        editor.ToolGradient = GradientPresets.ForegroundToBackground;
        editor.ForegroundColor = Color.FromRgb(128, 128, 128);
        editor.BackgroundColor = Color.FromRgb(128, 128, 128);
        await Drag(0, 200); // flat gray layer
        editor.GradientModeIndex = PaintModeNames.IndexOf(PaintMode.Multiply);
        await Drag(0, 200);
        check(Rgba(layer, 100, 30).R is >= 62 and <= 66 && doc.UndoText == "Undo Gradient", $"Mode Multiply multiplies with the layer ({Rgba(layer, 100, 30).R})");
        editor.GradientMode = PaintMode.Normal;
        editor.ForegroundColor = Colors.Black;
        editor.BackgroundColor = Colors.White;
        editor.ToolGradient = GradientPresets.ForegroundToBackground;
        editor.GradientMethod = GradientMethod.Perceptual;
        doc.Undo();
        doc.Undo();
        editor.Factory.CloseDockable(doc);
    }

    private static async Task FillStepsAsync(EditorViewModel editor, ScriptedFillDialogs dialogs, Action<bool, string> check)
    {
        var (doc, layer) = await NewTestDocumentAsync(editor, 120, 80);

        // Menu items.
        check(MenuItem("Edit", "Fill…") is not null && MenuItem("Edit", "Stroke…") is not null && MenuItem("Edit", "Define Pattern…") is not null,
            "the Edit menu has Fill…, Stroke… and Define Pattern…");

        // ---- Paint Bucket with a pattern at 50% -------------------------------------------------------
        editor.SetToolCommand.Execute("PaintBucket");
        var checker = PatternLibrary.BuiltIn.First(p => p.Name == "Checkerboard");
        editor.BucketSourceIndex = 1;
        editor.BucketPattern = checker;
        editor.BucketOpacity = 100;
        await doc.PaintBucketAsync(10, 10);
        check(layer.Pixels is not null && Rgba(layer, 2, 2).R == 204 && Rgba(layer, 10, 2).R == 255 && doc.UndoText == "Undo Paint Bucket",
            $"the Paint Bucket fills with a pattern tiled from the document's corner ({Rgba(layer, 2, 2).R}, {Rgba(layer, 10, 2).R})");
        doc.Undo();
        editor.BucketSourceIndex = 0;
        editor.ForegroundColor = Colors.Black;
        editor.BucketOpacity = 50;
        await doc.PaintBucketAsync(10, 10);
        check(Rgba(layer, 10, 10) is { R: 0, A: >= 126 and <= 129 }, $"bucket Opacity 50% ({Rgba(layer, 10, 10)})");
        doc.Undo();
        editor.BucketOpacity = 100;

        // ---- Edit › Fill… ----------------------------------------------------------------------------
        FillDialogViewModel? seen = null;
        dialogs.Fill = f =>
        {
            seen = f;
            f.ContentsIndex = (int)FillContents.Gray50;
            f.Opacity = 100;
            return true;
        };
        doc.SetSelection(SelectionMask.Rectangle(new PixelRect(10, 10, 50, 40), doc.Model.Bounds), "Rectangular Marquee");
        await Click(MenuItem("Edit", "Fill…"));
        check(seen is { ContentAwareAvailable: true } && seen.ContentOptions[(int)FillContents.ContentAware].IsEnabled
              && Rgba(layer, 20, 20) is { R: 128, A: 255 } && Rgba(layer, 60, 20).A == 0 && doc.UndoText == "Undo Fill",
            $"Edit › Fill… with 50% Gray fills the selection (Content-Aware offered with a selection) ({Rgba(layer, 20, 20)})");

        dialogs.Fill = f =>
        {
            check(f.Contents == FillContents.Gray50, "the Fill dialog remembers the last contents");
            f.ContentsIndex = (int)FillContents.Pattern;
            f.Pattern = checker;
            f.PreserveTransparency = true;
            return true;
        };
        doc.SelectAll();
        await Click(MenuItem("Edit", "Fill…"));
        check(Rgba(layer, 20, 20) is { A: 255 } && Rgba(layer, 70, 60).A == 0 && Rgba(layer, 18, 18).R != Rgba(layer, 26, 18).R,
            "Preserve Transparency fills only the layer's pixels, here with a pattern");
        doc.Undo(); // the pattern fill
        doc.Undo(); // Select All
        doc.Undo(); // the gray fill
        check(layer.Pixels is null, "undo takes the fills back");

        // ---- Edit › Stroke… --------------------------------------------------------------------------
        dialogs.Stroke = s =>
        {
            s.Width = 4;
            s.Color = Colors.Red;
            s.Location = StrokeLocation.Outside;
            return true;
        };
        doc.SetSelection(SelectionMask.Rectangle(new PixelRect(20, 20, 60, 50), doc.Model.Bounds), "Rectangular Marquee");
        await Click(MenuItem("Edit", "Stroke…"));
        check(Rgba(layer, 17, 30) is { R: 255, G: 0, A: 255 } && Rgba(layer, 21, 30).A == 0 && Rgba(layer, 14, 30).A == 0 && doc.UndoText == "Undo Stroke",
            $"Edit › Stroke… Outside draws a 4 px band outside the selection ({Rgba(layer, 17, 30)}; bake {doc.LastFillMs:F0} ms)");
        doc.Undo();
        dialogs.Stroke = s =>
        {
            s.Location = StrokeLocation.Inside;
            return true;
        };
        await Click(MenuItem("Edit", "Stroke…"));
        check(Rgba(layer, 21, 30).A == 255 && Rgba(layer, 25, 30).A == 0 && Rgba(layer, 18, 30).A == 0, "Stroke Inside stays inside the outline");
        doc.Undo();

        // ---- Edit › Define Pattern… ------------------------------------------------------------------
        doc.SetSelection(SelectionMask.Rectangle(new PixelRect(0, 0, 8, 6), doc.Model.Bounds), "Rectangular Marquee");
        dialogs.PatternName = "Selftest Pattern";
        await Click(MenuItem("Edit", "Define Pattern…"));
        var defined = UserPresets.Shared.Patterns.LastOrDefault();
        check(defined is { Name: "Selftest Pattern", Width: 8, Height: 6 } && ReferenceEquals(editor.BucketPattern, defined)
              && UserPresets.UseDirectory(UserPresets.Shared.Directory).Patterns.Any(p => p.Id == defined.Id),
            "Define Pattern makes a user pattern from the selection, kept across restarts");
        editor.BucketPattern = PatternLibrary.BuiltIn[0];

        // ---- Document patterns round-trip through PSD --------------------------------------------------
        doc.Model.Patterns.Add(checker);
        string path = Path.Combine(Path.GetTempPath(), $"strayta-selftest-{Guid.NewGuid():N}.psd");
        try
        {
            await doc.SaveAsync(path);
            var reopened = PsdFile.OpenForEditing(path);
            check(reopened.Patterns.Count == 0, "a pattern no layer refers to is not written into the file");
        }
        finally
        {
            File.Delete(path);
        }
        editor.Factory.CloseDockable(doc);
    }
}
