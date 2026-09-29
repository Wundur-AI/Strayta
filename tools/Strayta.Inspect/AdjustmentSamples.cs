using System.Text;
using Strayta.Core;
using Strayta.Core.Painting;
using Strayta.Psd;
using Strayta.Rendering;

namespace Strayta.Inspect;

/// <summary>
/// Writes one PSD per adjustment kind the test corpora have no sample of, for checking in Photoshop:
/// <c>adjustsamples &lt;out dir&gt;</c>. Each file is a generated test image (hue sweep over a gray ramp, plus a
/// skin-tone patch) under one adjustment layer; next to it go Strayta's render (<c>.strayta.png</c>) and a note of the
/// settings (<c>.txt</c>).
/// </summary>
internal static class AdjustmentSamples
{
    public static int Run(string[] args)
    {
        if (args.Length < 1) { Console.Error.WriteLine("usage: adjustsamples <out dir>"); return 2; }
        string dir = args[0];
        Directory.CreateDirectory(dir);

        var bw = GradientModel.TwoColor("Black, White", RgbColor.Black, new RgbColor(1, 1, 1));
        var tri = new Gradient(
            [new GradientColorStop(0, 0.5f, new RgbColor(0.1f, 0.05f, 0.4f)), new GradientColorStop(0.5f, 0.5f, new RgbColor(0.9f, 0.2f, 0.3f)),
             new GradientColorStop(1, 0.5f, new RgbColor(1f, 0.95f, 0.6f))],
            [new GradientOpacityStop(0, 0.5f, 1), new GradientOpacityStop(1, 0.5f, 1)]) { Name = "Violet, Red, Cream" };
        var warmCube = LookupTable3D.WriteCube("Strayta check warm", 17, (r, g, b) => (MathF.Min(1, r * 1.1f + 0.03f), g, b * 0.8f));

        var samples = new (string File, string Kind, Adjustment Adjustment, string Settings)[]
        {
            ("adjust-01-exposure", "Exposure", new ExposureAdjustment(1.5f, -0.02f, 0.8f), "Exposure +1.50, Offset -0.0200, Gamma Correction 0.80"),
            ("adjust-02-exposure-minus", "Exposure", new ExposureAdjustment(-1f, 0.05f, 1.2f), "Exposure -1.00, Offset +0.0500, Gamma Correction 1.20"),
            ("adjust-03-vibrance", "Vibrance", new VibranceAdjustment(80, 0), "Vibrance +80, Saturation 0"),
            ("adjust-04-vibrance-saturation", "Vibrance", new VibranceAdjustment(-30, 60), "Vibrance -30, Saturation +60"),
            ("adjust-05-color-balance", "Color Balance", new ColorBalanceAdjustment(new(0, 0, 40), new(30, -20, 0), new(0, 0, -30), false),
                "Shadows 0/0/+40, Midtones +30/-20/0, Highlights 0/0/-30, Preserve Luminosity off"),
            ("adjust-06-color-balance-preserve", "Color Balance", new ColorBalanceAdjustment(default, new(60, 0, -40), default, true),
                "Midtones +60/0/-40, Preserve Luminosity on"),
            ("adjust-07-black-white", "Black & White", BlackWhiteAdjustment.Default, "Default (Reds 40, Yellows 60, Greens 40, Cyans 60, Blues 20, Magentas 80), no tint"),
            ("adjust-08-black-white-tint", "Black & White", new BlackWhiteAdjustment(-40, 235, 144, -68, -3, -107, true, BlackWhiteAdjustment.DefaultTint),
                "Infrared (-40, 235, 144, -68, -3, -107), Tint on with the default tint color"),
            ("adjust-09-photo-filter", "Photo Filter", new PhotoFilterAdjustment(new RgbColor(0xEC / 255f, 0x8A / 255f, 0), 50, true),
                "Warming Filter (85), Density 50%, Preserve Luminosity on"),
            ("adjust-10-photo-filter-color", "Photo Filter", new PhotoFilterAdjustment(new RgbColor(0, 0.43f, 1f), 80, false),
                "Color #006DFF, Density 80%, Preserve Luminosity off"),
            ("adjust-11-channel-mixer", "Channel Mixer", ChannelMixerAdjustment.Default with { Red = new(80, 30, -10, 5), Blue = new(10, -20, 110, -4) },
                "Red output R+80 G+30 B-10 Constant+5; Green output default; Blue output R+10 G-20 B+110 Constant-4"),
            ("adjust-12-channel-mixer-mono", "Channel Mixer", ChannelMixerAdjustment.Default with { Monochrome = true, Gray = new(-70, 200, -30, 0) },
                "Monochrome: Black & White Infrared (R-70 G+200 B-30)"),
            ("adjust-13-selective-color", "Selective Color", SelectiveColorAdjustment.Default.With(SelectiveColorRange.Reds, new(-40, 20, 30, 0))
                .With(SelectiveColorRange.Neutrals, new(10, 0, -10, 0)).With(SelectiveColorRange.Blacks, new(0, 0, 0, 20)),
                "Relative: Reds C-40 M+20 Y+30; Neutrals C+10 Y-10; Blacks K+20"),
            ("adjust-14-selective-color-absolute", "Selective Color", SelectiveColorAdjustment.Default.With(SelectiveColorRange.Blues, new(50, 0, -50, 0))
                .With(SelectiveColorRange.Whites, new(0, 0, 30, 0)) with { Absolute = true },
                "Absolute: Blues C+50 Y-50; Whites Y+30"),
            ("adjust-15-gradient-map", "Gradient Map", new GradientMapAdjustment(tri, false, false, GradientMethod.Classic),
                "Violet (26,13,102) → Red (230,51,77) at 50% → Cream (255,242,153), Method Classic (writes 'grdm' version 1)"),
            ("adjust-16-gradient-map-reverse-dither", "Gradient Map", new GradientMapAdjustment(bw, true, true, GradientMethod.Classic),
                "Black, White, Reverse on, Dither on, Classic"),
            ("adjust-17-gradient-map-perceptual", "Gradient Map", new GradientMapAdjustment(tri, false, false, GradientMethod.Perceptual),
                "Same gradient as 15, Method Perceptual (writes 'grdm' version 3 with the method key 'Perc' — the layout is Strayta's reading, please check it opens)"),
            ("adjust-18-color-lookup", "Color Lookup", new ColorLookupAdjustment(ColorLookupKind.Lut3D, "Strayta check warm.cube", "CUBE", warmCube, false),
                "3DLUT File: Strayta check warm.cube (17³, generated: R×1.1+0.03, G, B×0.8), embedded without Photoshop's derived 'profile'"),
        };

        foreach (var (file, kind, adjustment, settings) in samples)
        {
            var doc = TestImage();
            doc.Root.Add(new AdjustmentLayer { Name = kind, Kind = kind, Adjustment = adjustment, Mask = LayerMasks.Solid(reveal: true) });
            string psd = Path.Combine(dir, file + ".psd");
            using (var fs = File.Create(psd)) PsdWriter.Write(doc, fs);

            // Check the file reads back to the same settings before handing it over.
            var again = PsdFile.OpenForEditing(psd);
            var read = again.Root.Children.OfType<AdjustmentLayer>().Single().Adjustment;
            bool same = PsdAdjustmentWriter.SameSettings(adjustment, read);
            var render = Compositor.Render(again);
            PngWriter.Write(Path.Combine(dir, file + ".strayta.png"), doc.Width, doc.Height, render.ToRgba8());
            File.WriteAllText(Path.Combine(dir, file + ".txt"),
                $"{kind} adjustment layer over Strayta's test image.\nSettings: {settings}\n" +
                $"Open {file}.psd in Photoshop, check the layer's Properties show these settings, and compare the canvas with {file}.strayta.png.\n" +
                (render.Warnings.Count > 0 ? $"Strayta notes: {string.Join("; ", render.Warnings.Distinct())}\n" : ""), Encoding.UTF8);
            // Colors are stored as 16-bit values, so a float color may come back a hair different: compare the renders then.
            int err = same ? 0 : FidelityReport.Compare(Compositor.Render(doc).ToRgba8(), render.ToRgba8(), doc.Width, doc.Height).MaxError;
            Console.WriteLine($"{file}: {(same ? "reads back the same" : err <= 1 ? "reads back the same to 16-bit color precision" : $"READS BACK DIFFERENTLY (max error {err})")}{(render.Warnings.Count > 0 ? " (" + string.Join("; ", render.Warnings.Distinct()) + ")" : "")}");
        }
        return 0;
    }

    /// <summary>A 512×256 image: a hue sweep (full saturation at the top fading to gray at the bottom) over the left 384 columns, a gray ramp and a skin-tone patch on the right.</summary>
    private static Document TestImage()
    {
        const int w = 512, h = 256;
        var doc = new Document(w, h, ColorMode.Rgb, 8);
        var planes = Enumerable.Range(0, 3).Select(_ => Plane.Create(w, h, 8)).ToArray();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float r, g, b;
                if (x < 384)
                {
                    float hue = x / 384f * 6f, sat = 1f - y / (float)(h - 1), v = 0.15f + 0.85f * (1f - MathF.Abs(y - 64) / 256f);
                    int i6 = (int)hue % 6;
                    float f = hue - (int)hue;
                    (r, g, b) = i6 switch
                    {
                        0 => (1f, f, 0f), 1 => (1 - f, 1f, 0f), 2 => (0f, 1f, f), 3 => (0f, 1 - f, 1f), 4 => (f, 0f, 1f), _ => (1f, 0f, 1 - f),
                    };
                    r = (0.5f + (r - 0.5f) * sat) * v; g = (0.5f + (g - 0.5f) * sat) * v; b = (0.5f + (b - 0.5f) * sat) * v;
                }
                else if (y < 192)
                {
                    float t = y / 191f;
                    (r, g, b) = (t, t, t);
                }
                else (r, g, b) = x < 448 ? (0.87f, 0.67f, 0.55f) : (0.55f, 0.38f, 0.28f); // light and dark skin tones
                int k = y * w + x;
                planes[0].Data[k] = (byte)Math.Round(Math.Clamp(r, 0, 1) * 255);
                planes[1].Data[k] = (byte)Math.Round(Math.Clamp(g, 0, 1) * 255);
                planes[2].Data[k] = (byte)Math.Round(Math.Clamp(b, 0, 1) * 255);
            }
        doc.Root.Add(new PixelLayer { Name = "Test image", Bounds = new PixelRect(0, 0, w, h), Pixels = new Raster(ColorMode.Rgb, planes, null) });
        return doc;
    }
}
