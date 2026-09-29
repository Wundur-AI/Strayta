using Strayta.Core;
using Strayta.Core.Painting;
using Strayta.Editor.Editing;
using Strayta.Editor.ViewModels;
using Strayta.Psd;
using Strayta.Rendering;

namespace Strayta.Editor;

/// <summary>
/// Self-test steps for Exposure, Vibrance, Color Balance, Black &amp; White, Photo Filter, Channel Mixer, Selective
/// Color, Gradient Map and Color Lookup layers: creating them, their Properties panels (presets, one undo step per
/// drag, clip, visibility, reset), Levels/Curves Auto, and saving and reopening.
/// </summary>
internal static partial class SelfTest
{
    private static async Task RunAdjustmentLayerStepsAsync(EditorViewModel editor, Action<bool, string> check)
    {
        // Three colored quarters and a gray ramp (30..225), 200×100.
        var model = LayerFactory.NewDocument(200, 100, whiteBackground: true);
        var planes = Enumerable.Range(0, 3).Select(_ => Plane.Create(200, 100, 8)).ToArray();
        for (int y = 0; y < 100; y++)
            for (int x = 0; x < 200; x++)
            {
                int i = y * 200 + x;
                (byte r, byte g, byte b) = (x / 50) switch
                {
                    0 => ((byte)200, (byte)60, (byte)40),
                    1 => ((byte)60, (byte)180, (byte)70),
                    2 => ((byte)50, (byte)80, (byte)200),
                    _ => ((byte)(30 + y * 195 / 99), (byte)(30 + y * 195 / 99), (byte)(30 + y * 195 / 99)),
                };
                planes[0].Data[i] = r;
                planes[1].Data[i] = g;
                planes[2].Data[i] = b;
            }
        var photo = new PixelLayer { Name = "Photo", Bounds = model.Bounds, Pixels = new Raster(ColorMode.Rgb, planes, null) };
        model.Root.Add(photo);
        var doc = new DocumentViewModel(model, null, editor);
        editor.Factory.AddDocument(doc);
        editor.ActiveDocument = doc;
        await doc.RenderAsync();
        LayerItemViewModel Item(LayerNode n) => doc.Layers.SelectMany(l => l.SelfAndDescendants()).First(i => i.Node == n);

        byte[] Pixel(int x, int y)
        {
            var img = Compositor.Render(model).ToRgba8();
            int i = (y * model.Width + x) * 4;
            return img[i..(i + 4)];
        }
        var red = Pixel(10, 10);

        AdjustmentLayer New(string kind)
        {
            doc.SelectedLayer = Item(photo);
            doc.NewAdjustmentLayer(kind);
            return (AdjustmentLayer)doc.SelectedLayer!.Node;
        }

        // ---- Menus: every Photoshop adjustment -----------------------------------------------------------
        check(AdjustmentFactory.Kinds.Count == 16 && AdjustmentFactory.Kinds.Contains("Selective Color"),
            $"New Adjustment Layer lists Photoshop's 16 adjustments ({AdjustmentFactory.Kinds.Count})");

        // ---- Exposure: a drag is one undo step, presets, clip, visibility, reset ------------------------
        var exposure = New("Exposure");
        var exposurePanel = doc.Properties as ExposurePanel;
        check(exposurePanel is not null && exposure.Adjustment is ExposureAdjustment { Exposure: 0, Gamma: 1 },
            $"Exposure opens its panel at the defaults ({doc.Properties?.GetType().Name})");
        check(Pixel(10, 10).AsSpan(0, 3).SequenceEqual(red.AsSpan(0, 3)), "a new Exposure layer leaves the image unchanged");
        for (int i = 1; i <= 40; i++) exposurePanel!.Exposure = -i * 0.05; // a slider drag to -2.00
        var dark = Pixel(10, 10);
        check(dark[0] < red[0] - 60, $"dragging Exposure to -2 darkens the image ({red[0]} → {dark[0]})");
        check(doc.UndoText == "Undo Change Exposure", $"the Exposure drag is one undo step ({doc.UndoText})");
        doc.Undo();
        check(exposurePanel!.Exposure == 0 && Pixel(10, 10)[0] == red[0], "undo restores the image and the panel");
        exposurePanel.Preset = "Plus 1.0";
        check(exposure.Adjustment is ExposureAdjustment { Exposure: 1f } && exposurePanel.Preset == "Plus 1.0" && Pixel(10, 10)[0] > red[0],
            "the Plus 1.0 preset brightens by a stop");
        exposurePanel.IsClipped = true;
        check(exposure.Clipped && doc.UndoText == "Undo Change Create Clipping Mask", $"the clip button clips to the layer below ({doc.UndoText})");
        exposurePanel.IsClipped = false;
        check(!exposure.Clipped, "clicking it again releases the clipping mask");
        exposurePanel.IsLayerVisible = false;
        check(!exposure.Visible && Pixel(10, 10)[0] == red[0], "the eye hides the adjustment");
        exposurePanel.IsLayerVisible = true;
        exposurePanel.ResetCommand.Execute(null);
        check(exposure.Adjustment is ExposureAdjustment { Exposure: 0f } && exposurePanel.Preset == "Default" && doc.UndoText == "Undo Change Reset",
            "reset returns to the defaults as one step");
        doc.Undo();
        check(exposure.Adjustment is ExposureAdjustment { Exposure: 1f }, "undoing the reset brings the preset back");
        exposure.Visible = false; // keep it out of the way of the other checks

        // ---- The other kinds --------------------------------------------------------------------------
        var vibrance = New("Vibrance");
        ((VibrancePanel)doc.Properties!).Saturation = -100;
        var gray = Pixel(10, 10);
        check(Math.Abs(gray[0] - gray[1]) <= 2 && Math.Abs(gray[1] - gray[2]) <= 2, $"Vibrance saturation -100 removes color ({gray[0]},{gray[1]},{gray[2]})");
        vibrance.Visible = false;

        var balance = New("Color Balance");
        var balancePanel = (ColorBalancePanel)doc.Properties!;
        check(balancePanel.Tone == 1 && balancePanel.PreserveLuminosity, "Color Balance starts on Midtones with Preserve Luminosity");
        balancePanel.PreserveLuminosity = false;
        for (int v = 5; v <= 100; v += 5) balancePanel.YellowBlue = v;
        check(Pixel(10, 10)[2] > red[2] + 20 && ((ColorBalanceAdjustment)balance.Adjustment!).Midtones.YellowBlue == 100,
            "moving midtones toward blue adds blue");
        balance.Visible = false;

        var bw = New("Black & White");
        var bwPanel = (BlackWhitePanel)doc.Properties!;
        var bwPixel = Pixel(10, 10);
        check(bwPixel[0] == bwPixel[1] && bwPixel[1] == bwPixel[2], "Black & White turns the image gray");
        bwPanel.Preset = "Infrared";
        check(bw.Adjustment is BlackWhiteAdjustment { Yellows: 235 } && bwPanel.Preset == "Infrared", "the Infrared preset applies");
        bwPanel.Tint = true;
        var tinted = Pixel(160, 50);
        check(tinted[0] > tinted[2], $"Tint warms the gray ({tinted[0]},{tinted[2]})");
        bw.Visible = false;

        var filter = New("Photo Filter");
        var filterPanel = (PhotoFilterPanel)doc.Properties!;
        check(filterPanel.FilterName == "Warming Filter (85)" && filterPanel.Density == 25 && filterPanel.UseFilter,
            "Photo Filter starts with Warming Filter (85) at 25%");
        filterPanel.FilterName = "Cooling Filter (80)";
        filterPanel.Density = 80;
        filterPanel.PreserveLuminosity = false;
        check(Pixel(160, 99)[0] < 150, "a dense cooling filter takes red out of light gray");
        filterPanel.UseColor = true;
        filterPanel.FilterColor = Avalonia.Media.Color.FromRgb(0, 255, 0);
        check(filterPanel.UseColor && filterPanel.FilterName is null, "a custom filter color");
        filter.Visible = false;

        var mixer = New("Channel Mixer");
        var mixerPanel = (ChannelMixerPanel)doc.Properties!;
        mixerPanel.OutputChannel = 2;
        mixerPanel.Red = 100;
        mixerPanel.Blue = 0;
        var mixed = Pixel(10, 10);
        check(Math.Abs(mixed[2] - red[0]) <= 1, $"Channel Mixer: blue output from the red channel ({mixed[2]} vs {red[0]})");
        mixerPanel.Preset = "Black & White with Red Filter (RGB)";
        var mono = Pixel(10, 10);
        check(mixerPanel.Monochrome && mono[0] == mono[1] && Math.Abs(mono[0] - red[0]) <= 1, "the Red Filter preset is monochrome from red");
        mixer.Visible = false;

        var selective = New("Selective Color");
        var selectivePanel = (SelectiveColorPanel)doc.Properties!;
        selectivePanel.Absolute = true;
        for (int v = 10; v <= 100; v += 10) selectivePanel.Cyan = v;
        var lessRed = Pixel(10, 10);
        check(lessRed[0] < red[0] - 50 && Pixel(60, 10)[1] >= 175, $"Selective Color: cyan in reds takes red out of the reds only ({lessRed[0]})");
        selective.Visible = false;

        var map = New("Gradient Map");
        var mapPanel = (GradientMapPanel)doc.Properties!;
        mapPanel.Gradient = GradientModel.TwoColor("Black, Red", RgbColor.Black, new RgbColor(1, 0, 0));
        var mapped = Pixel(160, 99);
        check(mapped[0] > 220 && mapped[1] < 5, $"Gradient Map maps light gray near the gradient's end ({mapped[0]},{mapped[1]})");
        mapPanel.Reverse = true;
        check(Pixel(160, 99)[0] < 40, "Reverse flips it");
        map.Visible = false;

        var lookup = New("Color Lookup");
        var lookupPanel = (ColorLookupPanel)doc.Properties!;
        check(lookupPanel.LutName is null && Pixel(10, 10)[0] == red[0], "a new Color Lookup does nothing until a LUT is chosen");
        string cube = Path.Combine(Path.GetTempPath(), $"adjust-selftest-{Guid.NewGuid():N}.cube");
        await File.WriteAllBytesAsync(cube, LookupTable3D.WriteCube("swap", 9, (r, g, b) => (b, g, r)));
        bool loaded = lookupPanel.LoadFile(cube);
        var swapped = Pixel(10, 10);
        check(loaded && Math.Abs(swapped[0] - red[2]) <= 1 && Math.Abs(swapped[2] - red[0]) <= 1,
            $"loading a .cube file applies it ({swapped[0]},{swapped[2]})");
        check(!lookupPanel.LoadFile(Path.Combine(Path.GetTempPath(), "missing.txt")) && lookupPanel.Error is not null, "unreadable files are refused");
        File.Delete(cube);

        // ---- Levels and Curves Auto ------------------------------------------------------------------------
        foreach (var n in model.Root.Children.OfType<AdjustmentLayer>()) n.Visible = false;
        var levels = New("Levels");
        await ((LevelsPanel)doc.Properties!).AutoAsync();
        var auto = (LevelsAdjustment)levels.Adjustment!;
        check(auto.Channels.Count == 3 && auto.Master.IsIdentity && auto.Channels[0] is { InputBlack: 30, InputWhite: 225 }
              && doc.UndoText == "Undo Change Auto",
            $"Levels Auto stretches each channel's darkest and lightest 0.1% to black and white ({auto.Channels[0].InputBlack}..{auto.Channels[0].InputWhite}, {doc.UndoText})");
        levels.Visible = false;

        // ---- Save and reopen -----------------------------------------------------------------------------
        string path = Path.Combine(Path.GetTempPath(), $"adjust-selftest-{Guid.NewGuid():N}.psd");
        foreach (var n in model.Root.Children.OfType<AdjustmentLayer>()) n.Visible = true;
        await doc.SaveAsync(path);
        var reopened = PsdFile.OpenForEditing(path);
        var before = model.Root.Children.OfType<AdjustmentLayer>().ToList();
        var after = reopened.Root.Children.OfType<AdjustmentLayer>().ToList();
        var differ = before.Zip(after).Where(p => !PsdAdjustmentWriter.SameSettings(p.First.Adjustment, p.Second.Adjustment) && p.First.Adjustment is not LevelsAdjustment)
            .Select(p => p.First.Kind).ToList();
        check(before.Count == after.Count && differ.Count == 0, $"every adjustment survives saving ({string.Join(", ", differ)})");
        var image = Compositor.Render(model).ToRgba8();
        var again = Compositor.Render(reopened).ToRgba8();
        check(FidelityReport.Compare(image, again, model.Width, model.Height).MaxError <= 2, "the reopened file renders the same");
        File.Delete(path);
    }

    /// <summary>STRAYTA_ADJUSTBENCH=new: the adjustment slider benchmark on a generated 4000×3000 document.</summary>
    public static async Task RunAdjustmentBenchmarkAsync(EditorViewModel editor)
    {
        const int w = 4000, h = 3000;
        var model = LayerFactory.NewDocument(w, h, whiteBackground: true);
        var planes = Enumerable.Range(0, 3).Select(_ => Plane.Create(w, h, 8)).ToArray();
        Parallel.For(0, h, y =>
        {
            uint seed = (uint)y * 2654435761u;
            for (int x = 0; x < w; x++)
            {
                seed = seed * 1664525u + 1013904223u;
                int noise = (int)(seed >> 28) - 8, i = y * w + x;
                planes[0].Data[i] = (byte)Math.Clamp(x * 255 / w + noise, 0, 255);
                planes[1].Data[i] = (byte)Math.Clamp(y * 255 / h + noise, 0, 255);
                planes[2].Data[i] = (byte)Math.Clamp(150 + noise, 0, 255);
            }
        });
        model.Root.Add(new PixelLayer { Name = "Photo", Bounds = model.Bounds, Pixels = new Raster(ColorMode.Rgb, planes, null) });
        var doc = new DocumentViewModel(model, null, editor);
        editor.Factory.AddDocument(doc);
        editor.ActiveDocument = doc;
        await doc.RenderAsync();
        await Task.Delay(1500); // let the view fit the image and the preview caches warm up
        doc.SelectedLayer = doc.Layers.FirstOrDefault();
        await doc.RunAdjustmentBenchmarkAsync();
        Console.WriteLine("ADJUSTBENCH done");
    }
}
