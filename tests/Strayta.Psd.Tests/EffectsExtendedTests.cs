using Strayta.Core;
using Strayta.Core.Painting;
using Strayta.Psd.Descriptors;

namespace Strayta.Psd.Tests;

/// <summary>
/// Bevel &amp; Emboss, Satin, Pattern Overlay, gradient and pattern strokes, gradient glows and the Quality settings
/// (contour, anti-aliasing, noise, range, jitter, technique): descriptor round trips, the keys Photoshop expects, and
/// the document's patterns ('Patt') written and read back.
/// </summary>
public class EffectsExtendedTests
{
    private static readonly Gradient Fire = new(
        [new(0, 0.5f, new RgbColor(1, 0, 0)), new(0.625f, 0.4f, new RgbColor(1, 1, 0)), new(1, 0.5f, new RgbColor(1, 1, 1))],
        [new(0, 0.5f, 1), new(1, 0.5f, 0)]) { Name = "Fire" };

    private static readonly Contour Ridge = new([new(0, 0), new(90, 200, true), new(180, 60), new(255, 255)]) { Name = "Custom" };

    internal static Pattern Checker(string id = "4a2e1f6c-0b3d-11d4-8bb5-e27e45023b5f", string name = "Checker", bool alpha = true, bool gray = false)
    {
        const int w = 6, h = 4;
        var planes = Enumerable.Range(0, gray ? 1 : 3).Select(c =>
        {
            var p = Plane.Create(w, h, 8);
            for (int i = 0; i < w * h; i++) p.Data[i] = (byte)((i % w + i / w) % 2 == 0 ? 30 + c * 40 : 220 - c * 30);
            return p;
        }).ToList();
        Plane? a = null;
        if (alpha)
        {
            a = Plane.Create(w, h, 8);
            for (int i = 0; i < w * h; i++) a.Data[i] = (byte)(i * 10);
        }
        return new Pattern(id, name, new Raster(gray ? ColorMode.Grayscale : ColorMode.Rgb, planes, a));
    }

    /// <summary>One of every new kind and setting, away from Photoshop's defaults.</summary>
    public static TheoryData<string> Kinds() => new(Samples().Keys);

    private static Dictionary<string, LayerEffect> Samples()
    {
        var pattern = PatternReference.To(Checker());
        return new()
        {
            ["bevel"] = new BevelEffect
            {
                Style = BevelStyle.Emboss, Technique = BevelTechnique.ChiselSoft, Depth = 2.5f, Up = false, Size = 17, Soften = 3,
                Angle = 33, Altitude = 55, UseGlobalLight = false, GlossContour = Contour.Presets[5], GlossAntiAliased = true,
                HighlightMode = BlendMode.ColorDodge, HighlightColor = new RgbColor(1, 0.9f, 0.5f), HighlightOpacity = 0.6f,
                ShadowMode = BlendMode.LinearBurn, ShadowColor = new RgbColor(0.1f, 0, 0.3f), ShadowOpacity = 0.4f,
                UseContour = true, Contour = Ridge, ContourAntiAliased = true, ContourRange = 0.7f,
                UseTexture = true, Texture = new PatternFill(pattern) { Scale = 1.5f, LinkWithLayer = false, PhaseX = 3, PhaseY = -2 },
                TextureDepth = -3.5f, TextureInvert = true,
            },
            ["bevel-stroke-emboss"] = new BevelEffect { Style = BevelStyle.StrokeEmboss, Technique = BevelTechnique.ChiselHard, Size = 4 },
            ["satin"] = new SatinEffect
            {
                BlendMode = BlendMode.Overlay, Opacity = 0.7f, Color = new RgbColor(0, 0.2f, 0.4f), Angle = -40, Distance = 23, Size = 31,
                Contour = Contour.Presets[7], AntiAliased = false, Invert = false,
            },
            ["pattern-overlay"] = new PatternOverlayEffect
            {
                BlendMode = BlendMode.Multiply, Opacity = 0.8f, Fill = new PatternFill(pattern) { Scale = 2.25f, LinkWithLayer = false, PhaseX = 5, PhaseY = 7 },
            },
            ["stroke-gradient"] = new StrokeEffect
            {
                Size = 9, Position = StrokePosition.Inside, FillType = StrokeFillType.Gradient, Color = new RgbColor(1, 0, 0),
                GradientFill = new GradientFill(Fire) { Style = GradientStyle.ShapeBurst, Angle = 12, Scale = 0.8f, Reverse = true, AlignWithLayer = false, OffsetX = 0.1f },
            },
            ["stroke-pattern"] = new StrokeEffect
            {
                Size = 5, FillType = StrokeFillType.Pattern, Color = new RgbColor(1, 0, 0),
                PatternFill = new PatternFill(pattern) { Scale = 0.5f, LinkWithLayer = true, PhaseX = 1 },
            },
            ["glow-gradient"] = new OuterGlowEffect
            {
                BlendMode = BlendMode.Screen, Opacity = 0.9f, Gradient = Fire, Spread = 0.1f, Size = 25, Technique = GlowTechnique.Precise,
                Contour = Ridge, AntiAliased = true, Noise = 0.3f, Range = 0.35f, Jitter = 0.2f,
            },
            ["inner-glow-quality"] = new InnerGlowEffect
            {
                Color = new RgbColor(0, 1, 0), Size = 8, Choke = 0.2f, FromCenter = true, Technique = GlowTechnique.Precise,
                Contour = Contour.Presets[2], Noise = 0.15f, Range = 0.9f, Jitter = 0.05f,
            },
            ["shadow-quality"] = new DropShadowEffect
            {
                Color = RgbColor.Black, Distance = 4, Size = 9, UseGlobalLight = false, Angle = 70,
                Contour = Contour.Presets[1], AntiAliased = true, Noise = 0.25f,
            },
            ["inner-shadow-quality"] = new InnerShadowEffect
            {
                Color = RgbColor.Black, Distance = 4, Size = 9, UseGlobalLight = false, Angle = -70,
                Contour = Ridge, AntiAliased = true, Noise = 0.5f,
            },
        };
    }

    private static Document NewDocument(LayerEffects? effects)
    {
        var doc = new Document(16, 16, ColorMode.Rgb, 8);
        var planes = Enumerable.Range(0, 4).Select(_ => new Plane(8, 8, 8, Enumerable.Repeat((byte)200, 64).ToArray())).ToArray();
        doc.Root.Add(new PixelLayer { Name = "fx", Bounds = new PixelRect(4, 4, 12, 12), Pixels = new Raster(ColorMode.Rgb, planes[..3], planes[3]), Effects = effects });
        return doc;
    }

    private static (PsdFile File, Document Doc) Reload(Document doc)
    {
        var ms = new MemoryStream();
        PsdWriter.Write(doc, ms);
        ms.Position = 0;
        var file = PsdFile.Read(ms, new PsdReadOptions { MaxRawBlockBytes = long.MaxValue });
        return (file, file.ToDocument());
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Every_new_effect_survives_write_and_read(string kind)
    {
        var effects = new LayerEffects([Samples()[kind]]);
        var (_, again) = Reload(NewDocument(effects));
        Assert.Equal(effects, again.Root.Children[0].Effects);

        // And writing what was read changes nothing: the patched descriptor keeps every value.
        var twice = Reload(again).Doc;
        Assert.Equal(effects, twice.Root.Children[0].Effects);
    }

    [Fact]
    public void Bevel_is_written_with_photoshops_keys()
    {
        var (file, _) = Reload(NewDocument(new LayerEffects([Samples()["bevel"]])));
        var fx = DescriptorReader.ReadVersioned(file.Layers[0].FindBlock("lfx2")!.Data!, 4).Object("ebbl")!;

        Assert.Equal(
            ["enab", "present", "showInDialog", "hglM", "hglC", "hglO", "sdwM", "sdwC", "sdwO", "bvlT", "bvlS", "uglg", "lagl", "Lald",
             "srgR", "blur", "bvlD", "TrnS", "antialiasGloss", "Sftn", "useShape", "useTexture", "MpgS", "AntA", "Inpr",
             "Ptrn", "Scl ", "textureDepth", "InvT", "Algn", "phase"],
            fx.Items.Select(i => i.Key));
        Assert.False(fx.Has("Md  ")); // a bevel has two modes of its own
        Assert.Equal(("Embs", "Slmt", "Out "), (fx.Enum("bvlS"), fx.Enum("bvlT"), fx.Enum("bvlD")));
        Assert.Equal((250.0, 17.0, 55.0, -350.0), (fx.Number("srgR")!.Value, fx.Number("blur")!.Value, fx.Number("Lald")!.Value, fx.Number("textureDepth")!.Value));
        Assert.Equal("#Prc", Assert.IsType<UnitFloatValue>(fx["hglO"]).Unit);
        Assert.Equal("CDdg", fx.Enum("hglM"));
        var curve = fx.Object("MpgS")!.List("Crv ")!.OfType<ObjectValue>().ToList();
        Assert.Equal([null, false, null, null], curve.Select(p => p.Value.Bool("Cnty")));
        Assert.Equal(("Checker", "4a2e1f6c-0b3d-11d4-8bb5-e27e45023b5f"), (fx.Object("Ptrn")!.Text("Nm  "), fx.Object("Ptrn")!.Text("Idnt")));
    }

    [Fact]
    public void Satin_and_pattern_overlay_use_photoshops_keys()
    {
        var (file, _) = Reload(NewDocument(new LayerEffects([Samples()["satin"], Samples()["pattern-overlay"]])));
        var top = DescriptorReader.ReadVersioned(file.Layers[0].FindBlock("lfx2")!.Data!, 4);
        Assert.Equal(["Scl ", "masterFXSwitch", "patternFill", "ChFX"], top.Items.Select(i => i.Key));
        Assert.Equal(["enab", "present", "showInDialog", "Md  ", "Clr ", "AntA", "Invr", "Opct", "lagl", "Dstn", "blur", "MpgS"],
            top.Object("ChFX")!.Items.Select(i => i.Key));
        Assert.Equal(["enab", "present", "showInDialog", "Md  ", "Opct", "Ptrn", "Scl ", "Algn", "phase"],
            top.Object("patternFill")!.Items.Select(i => i.Key));
        Assert.Equal(225, top.Object("patternFill")!.Number("Scl "));
    }

    [Fact]
    public void Patterns_used_by_effects_are_stored_in_the_document()
    {
        var effects = new LayerEffects([Samples()["pattern-overlay"]]);
        var (file, again) = Reload(NewDocument(effects));

        Assert.NotNull(file.GlobalBlocks.SingleOrDefault(b => b.Key == "Patt"));
        var pattern = Assert.Single(again.Patterns);
        var original = Checker();
        Assert.Equal((original.Id, original.Name), (pattern.Id, pattern.Name));
        Assert.Equal(original.Pixels.ColorPlanes.Select(p => p.Data), pattern.Pixels.ColorPlanes.Select(p => p.Data));
        Assert.Equal(original.Pixels.Alpha!.Data, pattern.Pixels.Alpha!.Data);
        // The effect finds its pixels when the file is read.
        var overlay = (PatternOverlayEffect)again.Root.Children[0].Effects!.Items[0];
        Assert.Same(pattern, overlay.Fill!.Pattern.Resolved);

        // Saving again does not add the pattern twice.
        var (file2, again2) = Reload(again);
        Assert.Single(again2.Patterns);
        Assert.Equal(file.GlobalBlocks.Single(b => b.Key == "Patt").Data, file2.GlobalBlocks.Single(b => b.Key == "Patt").Data);
    }

    [Fact]
    public void A_pattern_the_file_lacks_is_kept_as_a_reference()
    {
        var reference = new PatternReference("not-in-this-file", "Somebody's Preset");
        var effects = new LayerEffects([new PatternOverlayEffect { Fill = new PatternFill(reference) }]);
        var (file, again) = Reload(NewDocument(effects));
        Assert.Null(file.GlobalBlocks.SingleOrDefault(b => PsdPatterns.IsPatternKey(b.Key)));
        var read = (PatternOverlayEffect)again.Root.Children[0].Effects!.Items[0];
        Assert.Equal(reference, read.Fill!.Pattern);
        Assert.Null(read.Fill.Pattern.Resolved);
    }

    [Fact]
    public void Gradients_keep_smoothness_stop_kinds_and_noise()
    {
        var smooth = Fire with
        {
            Smoothness = 0.25f,
            Colors = [Fire.Colors[0] with { Kind = GradientStopKind.Foreground }, Fire.Colors[1], Fire.Colors[2] with { Kind = GradientStopKind.Background }],
        };
        var noise = new Gradient([], [])
        {
            Name = "Noisy",
            Noise = new GradientNoise
            {
                Roughness = 0.75f, Seed = 12345, Model = NoiseColorModel.Hsb, C1 = new ChannelRange(0.1f, 0.9f),
                C2 = new ChannelRange(0.2f, 0.8f), C3 = new ChannelRange(0f, 0.5f), RestrictColors = true, AddTransparency = true,
            },
        };
        var effects = new LayerEffects([
            new GradientOverlayEffect { Gradient = smooth },
            new OuterGlowEffect { Gradient = noise, Size = 5 },
        ]);
        var (_, again) = Reload(NewDocument(effects));
        Assert.Equal(effects, again.Root.Children[0].Effects);
    }

    [Fact]
    public void A_gradient_midpoint_is_stored_on_the_stop_that_ends_its_segment()
    {
        // In the model the midpoint between two stops belongs to the first; Photoshop's 'Mdpn' belongs to the second.
        var gradient = new Gradient(
            [new GradientColorStop(0, 0.35f, new RgbColor(0, 0, 1)), new GradientColorStop(1, 0.5f, new RgbColor(1, 1, 1))],
            [new GradientOpacityStop(0, 0.2f, 1), new GradientOpacityStop(0.5f, 0.7f, 0.5f), new GradientOpacityStop(1, 0.5f, 1)]);
        var effects = new LayerEffects([new GradientOverlayEffect { Gradient = gradient }]);
        var (file, again) = Reload(NewDocument(effects));

        var grad = DescriptorReader.ReadVersioned(file.Layers[0].FindBlock("lfx2")!.Data!, 4).Object("GrFl")!.Object("Grad")!;
        var colorMids = grad.List("Clrs")!.OfType<ObjectValue>().Select(o => o.Value.Number("Mdpn")).ToList();
        var opacityMids = grad.List("Trns")!.OfType<ObjectValue>().Select(o => o.Value.Number("Mdpn")).ToList();
        Assert.Equal([50.0, 35.0], colorMids);
        Assert.Equal([50.0, 20.0, 70.0], opacityMids);
        Assert.Equal(effects, again.Root.Children[0].Effects);
    }

    [Fact]
    public void A_stroke_that_changes_its_fill_type_drops_the_old_settings()
    {
        var doc = NewDocument(new LayerEffects([Samples()["stroke-gradient"]]));
        var read = Reload(doc).Doc;
        var stroke = (StrokeEffect)read.Root.Children[0].Effects!.Items[0];
        read.Root.Children[0].Effects = read.Root.Children[0].Effects! with
        {
            Items = [stroke with { FillType = StrokeFillType.Pattern, GradientFill = null, PatternFill = new PatternFill(PatternReference.To(Checker())) }],
        };

        var (file, again) = Reload(read);
        var fx = DescriptorReader.ReadVersioned(file.Layers[0].FindBlock("lfx2")!.Data!, 4).Object("FrFX")!;
        Assert.Equal("Ptrn", fx.Enum("PntT"));
        Assert.False(fx.Has("Grad"));
        Assert.False(fx.Has("Type"));
        Assert.Equal(StrokeFillType.Pattern, ((StrokeEffect)again.Root.Children[0].Effects!.Items[0]).FillType);
        Assert.Null(((StrokeEffect)again.Root.Children[0].Effects!.Items[0]).GradientFill);
    }

    [Fact]
    public void A_glow_switching_to_a_gradient_replaces_its_color()
    {
        var doc = NewDocument(new LayerEffects([new OuterGlowEffect { Color = new RgbColor(1, 1, 0), Size = 5 }]));
        var read = Reload(doc).Doc;
        var glow = (OuterGlowEffect)read.Root.Children[0].Effects!.Items[0];
        read.Root.Children[0].Effects = read.Root.Children[0].Effects! with { Items = [glow with { Gradient = Fire }] };

        var (file, again) = Reload(read);
        var fx = DescriptorReader.ReadVersioned(file.Layers[0].FindBlock("lfx2")!.Data!, 4).Object("OrGl")!;
        Assert.False(fx.Has("Clr "));
        Assert.Equal(4, fx.Keys().IndexOf("Grad")); // where the color was
        Assert.Equal(Fire, ((OuterGlowEffect)again.Root.Children[0].Effects!.Items[0]).Gradient);
    }

    [Fact]
    public void Bevels_follow_the_global_light_altitude()
    {
        var doc = NewDocument(new LayerEffects([new BevelEffect { UseGlobalLight = true, Angle = 60, Altitude = 45 }]));
        doc.GlobalLightAngle = 60;
        doc.GlobalLightAltitude = 45;
        var (file, again) = Reload(doc);
        Assert.Equal([0, 0, 0, 45], file.FindResource(1049)!.Data);
        var bevel = (BevelEffect)again.Root.Children[0].Effects!.Items[0];
        Assert.Equal((60f, 45f, true), (bevel.Angle, bevel.Altitude, bevel.UseGlobalLight));
    }

    [Fact]
    public void Scale_effects_scales_sizes_and_pattern_scale_only()
    {
        var bevel = (BevelEffect)Samples()["bevel"];
        var scaled = (BevelEffect)bevel.Scaled(0.5f);
        Assert.Equal((8.5f, 1.5f, 0.75f, 2.5f, 55f), (scaled.Size, scaled.Soften, scaled.Texture!.Scale, scaled.Depth, scaled.Altitude));
        var satin = (SatinEffect)Samples()["satin"].Scaled(2f);
        Assert.Equal((46f, 62f, -40f), (satin.Distance, satin.Size, satin.Angle));
    }
}

internal static class DescriptorTestExtensions
{
    public static List<string> Keys(this Descriptor d) => d.Items.Select(i => i.Key).ToList();
}
