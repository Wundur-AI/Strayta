using System.Buffers.Binary;
using System.Text;
using Strayta.Core;
using Strayta.Core.Painting;
using Strayta.Core.Selection;
using Strayta.Editor.Controls;
using Strayta.Editor.Editing;
using Strayta.Editor.ViewModels;

namespace Strayta.Editor;

// STRAYTA_SELFTEST_ONLY=toning: Dodge, Burn, Sponge, Blur, Sharpen, Smudge, brush presets (.abr import, sampled tips),
// Shape Dynamics and Scattering, through the real view models. STRAYTA_TONEBENCH=new: their frame rates on 4000×3000.
internal static partial class SelfTest
{
    private static async Task RunToningStepsAsync(EditorViewModel editor, Action<bool, string> check)
    {
        string presetDir = Path.Combine(Path.GetTempPath(), $"strayta-selftest-brushes-{Guid.NewGuid():N}");
        UserPresets.UseDirectory(presetDir);
        var saved = (editor.BrushSize, editor.BrushHardness, editor.BrushOpacity, editor.BrushTip);
        try
        {
            await ToningStepsAsync(editor, check);
            await FocusStepsAsync(editor, check);
            await BrushPresetStepsAsync(editor, check, presetDir);
        }
        catch (Exception ex)
        {
            check(false, $"exception in toning steps: {ex}");
        }
        finally
        {
            (editor.BrushSize, editor.BrushHardness, editor.BrushOpacity, editor.BrushTip) = saved;
            (editor.ShapeDynamics, editor.Scattering) = (false, false);
            UserPresets.UseDirectory(UserPresets.DefaultDirectory());
            BrushPresetLibrary.Reload();
            try { Directory.Delete(presetDir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>A document: left half 50% gray, right half a warm color, as a Background.</summary>
    private static async Task<(DocumentViewModel Doc, PixelLayer Bg)> ToningDocumentAsync(EditorViewModel editor, int depth = 8)
    {
        const int w = 240, h = 160;
        var model = new Document(w, h, ColorMode.Rgb, depth);
        var bg = new PixelLayer { Name = "Background", Bounds = model.Bounds };
        model.Root.Add(bg);
        var planes = Enumerable.Range(0, 3).Select(_ => Plane.Create(w, h, depth)).ToArray();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                (float r, float g, float b) = x < 120 ? (0.5f, 0.5f, 0.5f) : (0.8f, 0.35f, 0.25f);
                SetValue(planes[0], i, r);
                SetValue(planes[1], i, g);
                SetValue(planes[2], i, b);
            }
        bg.Pixels = new Raster(ColorMode.Rgb, planes, null);
        var doc = new DocumentViewModel(model, null, editor);
        editor.Factory.AddDocument(doc);
        editor.ActiveDocument = doc;
        await doc.RenderAsync();
        doc.SelectedLayer = doc.Layers[0];
        return (doc, bg);
    }

    private static void SetValue(Plane p, int i, float v)
    {
        if (p.BitDepth == 16) p.AsUInt16()[i] = (ushort)MathF.Round(v * 65535f);
        else p.Data[i] = (byte)MathF.Round(v * 255f);
    }

    /// <summary>Paints a stroke with the current tool; with <paramref name="compare"/> it also checks the live render against the commit.</summary>
    private static async Task<int> ToolStrokeAsync(DocumentViewModel doc, bool compare, params (float X, float Y)[] points)
    {
        if (!doc.BeginToolStroke(points[0].X, points[0].Y)) return -1;
        foreach (var p in points.Skip(1)) doc.ContinueStroke(p.X, p.Y);
        byte[]? live = null;
        if (compare) live = (await FreshFullRenderAsync(doc)).ToArray();
        await doc.EndStrokeAsync();
        if (!compare) return 0;
        var baked = await FreshFullRenderAsync(doc);
        int max = 0;
        for (int i = 0; i < baked.Length; i++) max = Math.Max(max, Math.Abs(baked[i] - live![i]));
        return max;
    }

    /// <summary>
    /// A full-resolution render made now. The preview lane reschedules (and so cancels) pending full renders, so this
    /// renders until one actually lands.
    /// </summary>
    private static async Task<byte[]> FreshFullRenderAsync(DocumentViewModel doc)
    {
        var before = doc.LastFullRender;
        for (int attempt = 0; attempt < 50; attempt++)
        {
            await doc.RenderAsync();
            if (doc.LastFullRender is { } now && !ReferenceEquals(now, before)) return now;
            await Task.Delay(50);
        }
        return doc.LastFullRender!;
    }

    private static float Sat(PixelLayer l, int x, int y)
    {
        float r = Sample(l, 0, x, y), g = Sample(l, 1, x, y), b = Sample(l, 2, x, y);
        return MathF.Max(r, MathF.Max(g, b)) - MathF.Min(r, MathF.Min(g, b));
    }

    private static async Task ToningStepsAsync(EditorViewModel editor, Action<bool, string> check)
    {
        // Tool strip: the slot without a key (Blur, Sharpen, Smudge) sits after Gradient, then O (Dodge, Burn, Sponge).
        var keys = editor.ToolGroups.Select(g => g.Key).ToList();
        int gradient = editor.ToolGroups.ToList().FindIndex(g => g.Contains(CanvasTool.Gradient));
        check(editor.ToolGroups[gradient + 1].Contains(CanvasTool.Blur) && editor.ToolGroups[gradient + 1].Key == ""
              && editor.ToolGroups[gradient + 2].Contains(CanvasTool.Dodge) && editor.ToolGroups[gradient + 2].Key == "O",
            "Blur / Sharpen / Smudge (no shortcut) and Dodge / Burn / Sponge (O) follow the Gradient slot");
        editor.HandleToolKey("O", shift: false);
        check(editor.Tool == CanvasTool.Dodge, "O picks the Dodge tool");
        editor.HandleToolKey("O", shift: true);
        check(editor.Tool == CanvasTool.Burn, "Shift+O steps to Burn");
        check(!editor.ToolGroups[gradient + 1].Tip.Contains("()"), $"the keyless slot's tip names no key ({editor.ToolGroups[gradient + 1].Tip.Split('\n')[0]})");

        var (doc, bg) = await ToningDocumentAsync(editor);
        (editor.BrushSize, editor.BrushHardness, editor.BrushTip) = (24, 100, null);

        // Dodge midtones at 50%: one stroke goes to the curve at half strength, however often it passes.
        editor.Tool = CanvasTool.Dodge;
        (editor.DodgeRangeIndex, editor.DodgeExposure, editor.DodgeProtectTones) = (1, 50, true);
        int diff = await ToolStrokeAsync(doc, compare: true, (20, 40), (100, 40), (20, 40), (100, 40));
        float expected = Toning.Curve(true, ToneRange.Midtones, 128 / 255f, 0.5f);
        check(Math.Abs(Sample(bg, 0, 60, 40) - expected) < 0.01f && doc.UndoText == "Undo Dodge Tool",
            $"Dodge (midtones, 50%) lightens gray to the curve, capped within the stroke ({Sample(bg, 0, 60, 40) * 255:F0}, expected {expected * 255:F0})");
        check(diff <= 2, $"the live Dodge preview matches the commit (max difference {diff})");
        check(Math.Abs(Sample(bg, 0, 60, 70) - 128 / 255f) < 0.003f, "pixels outside the stroke are untouched");

        // Burn highlights on the warm color, protecting tones: darker, same hue order, no clipping.
        editor.Tool = CanvasTool.Burn;
        (editor.BurnRangeIndex, editor.BurnExposure) = (2, 80);
        diff = await ToolStrokeAsync(doc, compare: true, (140, 40), (220, 40));
        float r = Sample(bg, 0, 180, 40), g = Sample(bg, 1, 180, 40), b = Sample(bg, 2, 180, 40);
        check(r < 0.79f && r > g && g > b && Math.Abs(r / b - 0.8f / 0.25f) < 0.25f,
            $"Burn (highlights, protect tones) darkens the color and keeps its hue ({r * 255:F0}, {g * 255:F0}, {b * 255:F0})");
        check(diff <= 2, $"the live Burn preview matches the commit (max difference {diff})");

        // Sponge: desaturate, then saturate with vibrance (never clipping).
        editor.Tool = CanvasTool.Sponge;
        (editor.SpongeModeIndex, editor.SpongeFlow, editor.SpongeVibrance) = (0, 100, false);
        float before = Sat(bg, 180, 80);
        diff = await ToolStrokeAsync(doc, compare: true, (140, 80), (220, 80));
        check(Sat(bg, 180, 80) < before * 0.2f && doc.UndoText == "Undo Sponge Tool",
            $"Sponge (desaturate, 100% flow) takes the color nearly to gray ({before:F2} → {Sat(bg, 180, 80):F2})");
        check(diff <= 2, $"the live Sponge preview matches the commit (max difference {diff})");
        (editor.SpongeModeIndex, editor.SpongeVibrance) = (1, true);
        await ToolStrokeAsync(doc, compare: false, (140, 120), (220, 120));
        float sat = Sat(bg, 180, 120);
        check(sat > before && Sample(bg, 0, 180, 120) <= 1f && Sample(bg, 2, 180, 120) >= 0f, $"Sponge (saturate, vibrance) adds saturation ({before:F2} → {sat:F2})");

        // Selection clipping: dodge across a selection edge.
        editor.Tool = CanvasTool.Dodge;
        editor.DodgeExposure = 100;
        doc.SetSelection(SelectionMask.Rectangle(new PixelRect(0, 100, 60, 160), doc.Model.Bounds), "Rectangular Marquee");
        await ToolStrokeAsync(doc, compare: false, (20, 130), (100, 130));
        check(Sample(bg, 0, 40, 130) > 0.6f && Math.Abs(Sample(bg, 0, 80, 130) - 128 / 255f) < 0.003f, "a toning stroke stays inside the selection");
        doc.SetSelection(null, "Deselect");

        // Layer mask targeted: Burn works on the mask's gray.
        doc.AddMask(reveal: true);
        doc.EditMask = true;
        editor.Tool = CanvasTool.Burn;
        (editor.BurnRangeIndex, editor.BurnExposure) = (2, 100);
        await ToolStrokeAsync(doc, compare: false, (60, 20), (61, 20));
        float m = MaskBaker.Sample(bg.Mask!, 60, 20);
        check(Math.Abs(m - 0.5f) < 0.02f && doc.UndoText == "Undo Burn Tool", $"Burn on a targeted mask greys its white ({m:F2})");
        doc.EditMask = false;

        int steps = 0;
        while (doc.CanUndo) { doc.Undo(); steps++; }
        check(Math.Abs(Sample(bg, 0, 60, 40) - 128 / 255f) < 0.003f, $"undo takes every toning stroke back, one step each ({steps})");

        // 16-bit: the same curve at full precision.
        var (doc16, bg16) = await ToningDocumentAsync(editor, depth: 16);
        editor.Tool = CanvasTool.Dodge;
        (editor.DodgeRangeIndex, editor.DodgeExposure) = (1, 50);
        await ToolStrokeAsync(doc16, compare: false, (20, 40), (100, 40));
        check(Math.Abs(Sample(bg16, 0, 60, 40) - Toning.Curve(true, ToneRange.Midtones, 0.5f, 0.5f)) < 0.001f && bg16.Pixels!.BitDepth == 16,
            $"Dodge on a 16-bit document keeps 16-bit precision ({Sample(bg16, 0, 60, 40):F4})");
    }

    private static async Task FocusStepsAsync(EditorViewModel editor, Action<bool, string> check)
    {
        var (doc, bg) = await ToningDocumentAsync(editor);
        (editor.BrushSize, editor.BrushHardness, editor.BrushTip) = (20, 100, null);

        // Blur along the gray / color edge at x = 120.
        editor.Tool = CanvasTool.Blur;
        (editor.BlurStrength, editor.BlurModeIndex, editor.BlurSampleAllLayers) = (100, 0, false);
        var pts = Enumerable.Range(0, 12).Select(i => (120f, 20f + i * 10)).ToArray();
        int diff = await ToolStrokeAsync(doc, compare: true, pts);
        float left = Sample(bg, 0, 119, 60), right = Sample(bg, 0, 120, 60);
        check(left > 0.51f && right < 0.79f && doc.UndoText == "Undo Blur Tool", $"Blur mixes the two sides of an edge (R {left * 255:F0} | {right * 255:F0})");
        check(diff <= 2, $"the live Blur preview matches the commit (max difference {diff})");

        // Sharpen across the same (now soft) edge increases its contrast.
        editor.Tool = CanvasTool.Sharpen;
        (editor.SharpenStrength, editor.SharpenProtectDetail) = (100, false);
        float spread = Sample(bg, 0, 121, 60) - Sample(bg, 0, 118, 60);
        diff = await ToolStrokeAsync(doc, compare: true, pts);
        float sharper = Sample(bg, 0, 121, 60) - Sample(bg, 0, 118, 60);
        check(sharper > spread, $"Sharpen steepens the edge ({spread * 255:F0} → {sharper * 255:F0} levels over 3 px)");
        check(diff <= 2, $"the live Sharpen preview matches the commit (max difference {diff})");

        // Smudge from the gray into the color drags gray along.
        editor.Tool = CanvasTool.Smudge;
        (editor.SmudgeStrength, editor.SmudgeFingerPainting) = (90, false);
        diff = await ToolStrokeAsync(doc, compare: true, Enumerable.Range(0, 16).Select(i => (90f + i * 5, 130f)).ToArray());
        float smudged = Sample(bg, 0, 150, 130);
        check(smudged < 0.75f && doc.UndoText == "Undo Smudge Tool", $"Smudge carries the gray into the color ({smudged * 255:F0} red, was 204)");
        check(diff <= 2, $"the live Smudge preview matches the commit (max difference {diff})");

        // Finger Painting starts with the foreground color.
        editor.ForegroundColor = Avalonia.Media.Color.FromRgb(0, 0, 255);
        editor.SmudgeFingerPainting = true;
        await ToolStrokeAsync(doc, compare: false, (30, 150), (60, 150));
        check(Sample(bg, 2, 30, 150) > 0.9f && Sample(bg, 0, 30, 150) < 0.1f, "Smudge with Finger Painting starts with the foreground color");
        editor.SmudgeFingerPainting = false;

        // Sample All Layers: blurring on an empty layer puts the blurred image on it.
        doc.NewLayer();
        var empty = (PixelLayer)doc.SelectedLayer!.Node;
        editor.Tool = CanvasTool.Blur;
        editor.BlurSampleAllLayers = true;
        await ToolStrokeAsync(doc, compare: false, pts);
        check(empty.Pixels is not null && Alpha(empty, 120, 60) > 0.9f && Math.Abs(Sample(empty, 0, 119, 60) - Sample(bg, 0, 119, 60)) > 0.005f,
            $"Blur with Sample All Layers paints the blurred image onto an empty layer (alpha {Alpha(empty, 120, 60):F2})");
        editor.BlurSampleAllLayers = false;

        // A mask: blur softens its edge.
        doc.SelectedLayer = doc.Layers.SelectMany(l => l.SelfAndDescendants()).First(i => i.Node == bg);
        doc.SetSelection(SelectionMask.Rectangle(new PixelRect(0, 0, 60, 160), doc.Model.Bounds), "Rectangular Marquee");
        doc.AddMaskFromSelection(reveal: true);
        doc.SetSelection(null, "Deselect");
        doc.EditMask = true;
        await ToolStrokeAsync(doc, compare: false, Enumerable.Range(0, 12).Select(i => (60f, 20f + i * 10)).ToArray());
        float edge = MaskBaker.Sample(bg.Mask!, 60, 60);
        check(edge > 0.05f && edge < 0.95f, $"Blur on a targeted mask softens its edge ({edge:F2})");
        doc.EditMask = false;
        while (doc.CanUndo) doc.Undo();
    }

    /// <summary>A version 2 .abr with one sampled tip (raw) and one computed brush, built from the format description.</summary>
    private static byte[] MinimalAbr()
    {
        var o = new MemoryStream();
        void U16(int v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v); o.Write(b); }
        void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); o.Write(b); }
        void Name(string s) { U32((uint)s.Length + 1); o.Write(Encoding.BigEndianUnicode.GetBytes(s + "\0")); }
        const int w = 24, h = 8;
        var tip = new byte[w * h];
        for (int i = 0; i < tip.Length; i++) tip[i] = (byte)((i / w) % 2 == 0 ? 255 : 90);
        U16(2);
        U16(2);
        long lengthAt(Action body)
        {
            long at = o.Position;
            U32(0);
            long start = o.Position;
            body();
            long end = o.Position;
            o.Position = at;
            U32((uint)(end - start));
            o.Position = end;
            return end - start;
        }
        U16(2);
        lengthAt(() =>
        {
            U32(0); U16(20); Name("Striped Tip");
            o.WriteByte(1); o.Write(new byte[8]);
            U32(0); U32(0); U32(h); U32(w); U16(8); o.WriteByte(0);
            o.Write(tip);
        });
        U16(1);
        lengthAt(() => { U32(0); U16(25); Name("Round 9"); U16(9); U16(100); U16(0); U16(80); });
        return o.ToArray();
    }

    private static async Task BrushPresetStepsAsync(EditorViewModel editor, Action<bool, string> check, string presetDir)
    {
        var (doc, _) = await ToningDocumentAsync(editor);
        doc.NewLayer();
        var layer = (PixelLayer)doc.SelectedLayer!.Node;
        var presets = editor.BrushPresets;
        presets.Rebuild();
        check(presets.Folders.Any(f => f.Name == BrushPresetLibrary.GeneralFolder) && presets.Items.Any(i => i.Preset.Tip is not null),
            $"the Brushes panel lists the built-in round and sampled presets ({presets.Items.Count()} presets)");
        editor.ShowPanelCommand.Execute("Brushes"); // Window › Brushes: the panel and its thumbnails are built and drawn
        await Task.Delay(400);
        check(editor.Factory.Find(d => d.Id == "Brushes").FirstOrDefault() is { } panel && ReferenceEquals(panel.Owner is Dock.Model.Core.IDock dock ? dock.ActiveDockable : null, panel),
            "Window › Brushes shows the Brushes panel");
        editor.ShowPanelCommand.Execute("Color");
        var first = presets.Items.First();
        check(first.TipImage.PixelSize.Width == 32 && first.StrokeImage.PixelSize.Width == 120, "presets have tip and stroke thumbnails");

        // A sampled built-in preset: its tip paints.
        var spatter = presets.Items.First(i => i.Name == "Spatter");
        presets.Select(spatter);
        check(editor.BrushTip is not null && editor.BrushSize == spatter.Preset.Size && editor.BrushPresetName == "Spatter", "picking a preset sets the brush and its tip");
        editor.Tool = CanvasTool.Brush;
        editor.ForegroundColor = Avalonia.Media.Color.FromRgb(0, 0, 0);
        doc.BeginStroke(40, 40, editor.CurrentBrush, editor.CurrentColor, erase: false);
        await doc.EndStrokeAsync();
        int painted = 0, clear = 0;
        for (int y = 22; y < 58; y++)
            for (int x = 22; x < 58; x++)
                if (Alpha(layer, x, y) > 0.5f) painted++;
                else clear++;
        check(painted > 30 && clear > 200, $"a sampled tip paints its spatter, not a disc ({painted} painted, {clear} clear pixels)");

        // New Brush Preset: saved in the person's presets (a new session sees it).
        editor.BrushSize = 77;
        presets.AddPreset("Self-test spatter", "My Brushes");
        var reloaded = BrushPresetLibrary.Reload();
        var kept = reloaded.Presets.FirstOrDefault(p => p.Name == "Self-test spatter");
        check(kept is { Size: 77, Folder: "My Brushes" } && kept.Tip?.Width == spatter.Preset.Tip!.Width && File.Exists(Path.Combine(presetDir, "brushes.json")),
            "New Brush Preset saves the brush with its tip in the user presets");

        // Import an .abr file (built here, no third-party files).
        string abr = Path.Combine(presetDir, "tone-test.abr");
        await File.WriteAllBytesAsync(abr, MinimalAbr());
        int added = presets.Import([abr]);
        var folder = presets.Folders.FirstOrDefault(f => f.Name == "tone-test");
        check(added == 2 && folder?.Items.Count == 2 && folder.Items[0].Name == "Striped Tip" && folder.Items[0].Preset.Tip is { Width: 24, Height: 8 }
              && folder.Items[1].Preset is { Tip: null, Size: 9, Hardness: 0.8f },
            $"Import Brushes reads an .abr's sampled and computed brushes into a folder named after it ({added})");
        presets.Select(folder!.Items[0]);
        editor.BrushSize = 48;
        doc.BeginStroke(120, 100, editor.CurrentBrush, editor.CurrentColor, erase: false);
        await doc.EndStrokeAsync();
        check(Alpha(layer, 120, 100) > 0.5f && Alpha(layer, 120, 115) < 0.05f && Alpha(layer, 140, 100) > 0.2f,
            "an imported tip paints scaled to the brush size (48 px wide, 16 px tall)");

        // Shape Dynamics and Scattering.
        presets.Select(presets.Items.First(i => i.Name == "Hard Round"));
        (editor.BrushSize, editor.Scattering, editor.Scatter, editor.ScatterCount, editor.ShapeDynamics, editor.SizeJitter) = (8, true, 300, 2, true, 60);
        var brush = editor.CurrentBrush;
        doc.NewLayer();
        var scattered = (PixelLayer)doc.SelectedLayer!.Node;
        doc.BeginStroke(20, 80, brush, editor.CurrentColor, erase: false);
        doc.ContinueStroke(220, 80);
        await doc.EndStrokeAsync();
        check(brush.Dynamics is { Count: 2 } && scattered.Bounds.Height > 16, $"Scattering spreads dabs across the stroke ({scattered.Bounds.Height} px tall for an 8 px brush)");
        (editor.Scattering, editor.ShapeDynamics) = (false, false);

        // Edit › Define Brush Preset…: the selected image becomes a tip (dark paints; 50% gray paints half).
        doc.SetSelection(SelectionMask.Rectangle(new PixelRect(10, 10, 40, 30), doc.Model.Bounds), "Rectangular Marquee");
        var defined = await presets.DefineAsync(doc, "Self-test defined");
        doc.SetSelection(null, "Deselect");
        check(defined?.Tip is { Width: 30, Height: 20 } t && Math.Abs(t.Alpha[t.Alpha.Length / 2] - 128) <= 2 && editor.BrushTip == defined.Tip,
            $"Define Brush Preset makes a tip from the selected image ({defined?.Tip?.Width}×{defined?.Tip?.Height}, center {defined?.Tip?.Alpha[defined.Tip.Alpha.Length / 2]})");

        presets.Delete(presets.Items.First(i => i.Name == "Self-test spatter"));
        check(!BrushPresetLibrary.Reload().Presets.Any(p => p.Name == "Self-test spatter"), "deleting a preset removes it from the saved presets");
        while (doc.CanUndo) doc.Undo();
    }

    /// <summary>STRAYTA_TONEBENCH=new: the toning and focus tools on a generated 4000×3000 document.</summary>
    public static async Task RunToningBenchmarkAsync(EditorViewModel editor)
    {
        const int w = 4000, h = 3000;
        var model = LayerFactory.NewDocument(w, h, whiteBackground: true);
        model.Root.Add(new PixelLayer { Name = "Photo", Bounds = model.Bounds, Pixels = RetouchTexture(w, h, 8, false) });
        var doc = new DocumentViewModel(model, null, editor);
        editor.Factory.AddDocument(doc);
        editor.ActiveDocument = doc;
        await doc.RenderAsync();
        doc.SelectedLayer = doc.Layers[0];
        await Task.Delay(1500);
        await doc.RunToningBenchmarkAsync();
        Console.WriteLine("TONEBENCH done");
    }
}
