using Strayta.Core;
using Strayta.Psd.Descriptors;

namespace Strayta.Psd;

/// <summary>Reads layer styles from the 'lfx2' / 'lmfx' descriptor blocks.</summary>
public static class PsdEffects
{
    private static readonly Dictionary<string, string> UnsupportedNames = new()
    {
        ["IrSh"] = "Inner Shadow", ["IrGl"] = "Inner Glow", ["ebbl"] = "Bevel & Emboss", ["ChFX"] = "Satin",
        ["patternFill"] = "Pattern Overlay",
        ["innerShadowMulti"] = "Inner Shadow",
    };

    private static readonly Dictionary<string, string> MultiKeys = new()
    {
        ["dropShadowMulti"] = "DrSh", ["frameFXMulti"] = "FrFX", ["solidFillMulti"] = "SoFi",
        ["gradientFillMulti"] = "GrFl", ["innerShadowMulti"] = "IrSh",
    };

    /// <param name="globalAngle">Document light angle (image resource 1037), used when an effect says "use global light".</param>
    public static LayerEffects? Read(PsdLayerRecord record, float globalAngle)
    {
        var block = record.FindBlock("lmfx") ?? record.FindBlock("lfx2");
        if (block?.Data is not { Length: > 8 } data) return null;

        Descriptor d;
        try
        {
            d = DescriptorReader.ReadVersioned(data, 4); // 4-byte object effects version comes first
        }
        catch (PsdFormatException)
        {
            return new LayerEffects([new UnsupportedEffect("Layer style (unreadable)")]);
        }

        if (d.Bool("masterFXSwitch") == false) return null;
        // 'Scl ' records the "Scale Effects" ratio; stored sizes already include it (verified against real files).
        const float scale = 1f;

        var items = new List<LayerEffect>();
        foreach (var (key, value) in d.Items)
        {
            string type = MultiKeys.GetValueOrDefault(key, key);
            var descriptors = value switch
            {
                ObjectValue o => [o.Value],
                ListValue l => l.Items.OfType<ObjectValue>().Select(o => o.Value).ToList(),
                _ => new List<Descriptor>(),
            };
            foreach (var fx in descriptors)
            {
                if (fx.Bool("enab") != true) continue;
                if (Parse(type, fx, scale, globalAngle) is { } effect) items.Add(effect);
            }
        }
        return items.Count == 0 ? null : new LayerEffects(items);
    }

    private static LayerEffect? Parse(string type, Descriptor fx, float scale, float globalAngle)
    {
        var mode = BlendModeOf(fx.Enum("Md  "));
        float opacity = (float)(fx.Number("Opct") ?? 100) / 100f;

        switch (type)
        {
            case "DrSh":
                return new DropShadowEffect
                {
                    BlendMode = mode, Opacity = opacity,
                    Color = ColorOf(fx.Object("Clr ")),
                    Angle = fx.Bool("uglg") == true ? globalAngle : (float)(fx.Number("lagl") ?? 120),
                    Distance = (float)(fx.Number("Dstn") ?? 0) * scale,
                    Spread = (float)(fx.Number("Ckmt") ?? 0) / 100f,
                    Size = (float)(fx.Number("blur") ?? 0) * scale,
                    Knockout = fx.Bool("layerConceals") ?? true,
                };

            case "OrGl" when fx.Has("Clr "):
                return new OuterGlowEffect
                {
                    BlendMode = mode, Opacity = opacity,
                    Color = ColorOf(fx.Object("Clr ")),
                    Spread = (float)(fx.Number("Ckmt") ?? 0) / 100f,
                    Size = (float)(fx.Number("blur") ?? 0) * scale,
                };

            case "SoFi":
                return new ColorOverlayEffect { BlendMode = mode, Opacity = opacity, Color = ColorOf(fx.Object("Clr ")) };

            case "GrFl" when GradientOf(fx.Object("Grad")) is { } gradient:
                return new GradientOverlayEffect
                {
                    BlendMode = mode, Opacity = opacity,
                    Gradient = gradient,
                    Style = fx.Enum("Type") switch
                    {
                        "Rdl " => GradientStyle.Radial,
                        "Angl" => GradientStyle.Angle,
                        "Rflc" => GradientStyle.Reflected,
                        "Dmnd" => GradientStyle.Diamond,
                        _ => GradientStyle.Linear,
                    },
                    Angle = (float)(fx.Number("Angl") ?? 90),
                    Scale = (float)(fx.Number("Scl ") ?? 100) / 100f,
                    Reverse = fx.Bool("Rvrs") ?? false,
                    AlignWithLayer = fx.Bool("Algn") ?? true,
                    OffsetX = (float)(fx.Object("Ofst")?.Number("Hrzn") ?? 0) / 100f,
                    OffsetY = (float)(fx.Object("Ofst")?.Number("Vrtc") ?? 0) / 100f,
                };

            case "FrFX" when fx.Enum("PntT") is null or "SClr":
                return new StrokeEffect
                {
                    BlendMode = mode, Opacity = opacity,
                    Color = ColorOf(fx.Object("Clr ")),
                    Size = (float)(fx.Number("Sz  ") ?? 1) * scale,
                    Position = fx.Enum("Styl") switch
                    {
                        "InsF" => StrokePosition.Inside,
                        "CtrF" => StrokePosition.Center,
                        _ => StrokePosition.Outside,
                    },
                };

            case "FrFX":
                return new UnsupportedEffect("Gradient or pattern stroke");
            case "OrGl":
                return new UnsupportedEffect("Gradient outer glow");
            case "GrFl":
                return new UnsupportedEffect("Gradient Overlay (unreadable gradient)");
            default:
                return UnsupportedNames.TryGetValue(type, out var name) ? new UnsupportedEffect(name) : null;
        }
    }

    /// <summary>Reads 'RGBC' (0..255 or the newer 0..1 float keys), 'HSBC', 'Grsc' and 'CMYC' colors.</summary>
    public static RgbColor ColorOf(Descriptor? c)
    {
        if (c is null) return RgbColor.Black;
        switch (c.ClassId)
        {
            case "RGBC" when c.Has("redFloat"):
                return new RgbColor((float)c.Number("redFloat")!, (float)(c.Number("greenFloat") ?? 0), (float)(c.Number("blueFloat") ?? 0));
            case "RGBC":
                return new RgbColor((float)(c.Number("Rd  ") ?? 0) / 255f, (float)(c.Number("Grn ") ?? 0) / 255f, (float)(c.Number("Bl  ") ?? 0) / 255f);
            case "Grsc":
                float g = 1f - (float)(c.Number("Gry ") ?? 0) / 100f;
                return new RgbColor(g, g, g);
            case "HSBC":
                return FromHsb((float)(c.Number("H   ") ?? 0), (float)(c.Number("Strt") ?? 0) / 100f, (float)(c.Number("Brgh") ?? 0) / 100f);
            case "CMYC":
                float k = 1f - (float)(c.Number("Blck") ?? 0) / 100f;
                return new RgbColor(
                    (1f - (float)(c.Number("Cyn ") ?? 0) / 100f) * k,
                    (1f - (float)(c.Number("Mgnt") ?? 0) / 100f) * k,
                    (1f - (float)(c.Number("Ylw ") ?? 0) / 100f) * k);
            default:
                return RgbColor.Black;
        }
    }

    private static RgbColor FromHsb(float h, float s, float v)
    {
        h = ((h % 360) + 360) % 360 / 60f;
        int i = (int)h;
        float f = h - i, p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
        return i switch
        {
            0 => new(v, t, p), 1 => new(q, v, p), 2 => new(p, v, t),
            3 => new(p, q, v), 4 => new(t, p, v), _ => new(v, p, q),
        };
    }

    /// <summary>Custom gradients store color and opacity stops with locations in 0..4096 and midpoints in percent.</summary>
    public static Gradient? GradientOf(Descriptor? g)
    {
        if (g?.List("Clrs") is not { } colors) return null;
        var colorStops = colors.OfType<ObjectValue>().Select(o => new GradientColorStop(
            (float)(o.Value.Number("Lctn") ?? 0) / 4096f,
            (float)(o.Value.Number("Mdpn") ?? 50) / 100f,
            ColorOf(o.Value.Object("Clr ")))).OrderBy(s => s.Location).ToList();
        var opacityStops = (g.List("Trns") ?? []).OfType<ObjectValue>().Select(o => new GradientOpacityStop(
            (float)(o.Value.Number("Lctn") ?? 0) / 4096f,
            (float)(o.Value.Number("Mdpn") ?? 50) / 100f,
            (float)(o.Value.Number("Opct") ?? 100) / 100f)).OrderBy(s => s.Location).ToList();
        return colorStops.Count == 0 ? null : new Gradient(colorStops, opacityStops);
    }

    private static readonly Dictionary<string, BlendMode> Modes = new()
    {
        ["Nrml"] = BlendMode.Normal, ["Dslv"] = BlendMode.Dissolve, ["Drkn"] = BlendMode.Darken, ["Mltp"] = BlendMode.Multiply,
        ["CBrn"] = BlendMode.ColorBurn, ["linearBurn"] = BlendMode.LinearBurn, ["darkerColor"] = BlendMode.DarkerColor,
        ["Lghn"] = BlendMode.Lighten, ["Scrn"] = BlendMode.Screen, ["CDdg"] = BlendMode.ColorDodge,
        ["linearDodge"] = BlendMode.LinearDodge, ["lighterColor"] = BlendMode.LighterColor, ["Ovrl"] = BlendMode.Overlay,
        ["SftL"] = BlendMode.SoftLight, ["HrdL"] = BlendMode.HardLight, ["vividLight"] = BlendMode.VividLight,
        ["linearLight"] = BlendMode.LinearLight, ["pinLight"] = BlendMode.PinLight, ["hardMix"] = BlendMode.HardMix,
        ["Dfrn"] = BlendMode.Difference, ["Xclu"] = BlendMode.Exclusion, ["blendSubtraction"] = BlendMode.Subtract,
        ["blendDivide"] = BlendMode.Divide, ["H   "] = BlendMode.Hue, ["Strt"] = BlendMode.Saturation,
        ["Clr "] = BlendMode.Color, ["Lmns"] = BlendMode.Luminosity,
    };

    public static BlendMode BlendModeOf(string? key) => key is not null && Modes.TryGetValue(key, out var m) ? m : BlendMode.Normal;
}
