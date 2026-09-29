using Strayta.Core;
using Strayta.Psd.Descriptors;

namespace Strayta.Psd;

/// <summary>
/// What <see cref="PsdEffects"/> attaches to <see cref="LayerEffects.SourceData"/>: the block's top-level descriptor,
/// so a rewrite keeps its scale, its other keys and the settings of effects that are not on the layer.
/// </summary>
public sealed record PsdEffectsSource(Descriptor Descriptor);

/// <summary>
/// What <see cref="PsdEffects"/> attaches to <see cref="LayerEffect.SourceData"/>: the effect's own descriptor and
/// its effect type ("DrSh", "IrSh", "OrGl", "IrGl", "ebbl", "ChFX", "SoFi", "GrFl", "patternFill", "FrFX").
/// </summary>
public sealed record PsdEffectSource(string Type, Descriptor Descriptor);

/// <summary>
/// What reading effects needs from the rest of the file: the global light (effects marked "use global light" take
/// it) and the document's patterns, which pattern effects refer to by id.
/// </summary>
public sealed record PsdEffectContext(float GlobalAngle, float GlobalAltitude, IReadOnlyList<Pattern> Patterns)
{
    public static PsdEffectContext Default { get; } = new(120f, 30f, []);

    /// <summary>The document's pattern with this id, or a pattern without pixels that keeps the reference.</summary>
    internal Pattern Resolve(string id, string name) =>
        Patterns.FirstOrDefault(p => p.Id == id) ?? new Pattern(id, name);
}

/// <summary>Reads layer styles from the 'lfx2' / 'lmfx' descriptor blocks.</summary>
public static class PsdEffects
{
    /// <summary>
    /// Photoshop CC stores the effects that can be added several times as lists under these keys; one of each can
    /// also appear under the effect's own key (older files, or files from other applications).
    /// </summary>
    internal static readonly Dictionary<string, string> MultiKeys = new()
    {
        ["dropShadowMulti"] = "DrSh", ["frameFXMulti"] = "FrFX", ["solidFillMulti"] = "SoFi",
        ["gradientFillMulti"] = "GrFl", ["innerShadowMulti"] = "IrSh",
    };

    /// <summary>The document's global light angle (image resource 1037), or Photoshop's default of 120°.</summary>
    public static float GlobalAngleOf(PsdFile file) =>
        file.FindResource(1037)?.Data is { Length: >= 4 } angle ? System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(angle) : 120f;

    /// <summary>The document's global light altitude (image resource 1049), or Photoshop's default of 30°.</summary>
    public static float GlobalAltitudeOf(PsdFile file) =>
        file.FindResource(1049)?.Data is { Length: >= 4 } altitude ? System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(altitude) : 30f;

    /// <summary>The global light and the patterns of <paramref name="file"/>, for reading its effects.</summary>
    public static PsdEffectContext ContextOf(PsdFile file) => new(GlobalAngleOf(file), GlobalAltitudeOf(file), PsdPatterns.Read(file));

    /// <inheritdoc cref="Read(PsdLayerRecord, PsdEffectContext)"/>
    /// <param name="record">The layer record.</param>
    /// <param name="globalAngle">Document light angle (image resource 1037), used when an effect says "use global light".</param>
    public static LayerEffects? Read(PsdLayerRecord record, float globalAngle) =>
        Read(record, PsdEffectContext.Default with { GlobalAngle = globalAngle });

    /// <summary>
    /// Reads the effects shown in the Layers panel: those marked "present", whether their eye is open
    /// (<see cref="LayerEffect.Enabled"/>) or not. Effects that are only remembered by the dialog are left out, and a
    /// layer without any present effect has none (null). The master switch becomes <see cref="LayerEffects.Enabled"/>.
    /// </summary>
    public static LayerEffects? Read(PsdLayerRecord record, PsdEffectContext context)
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

        // 'Scl ' records the "Scale Effects" ratio; stored sizes already include it (verified against real files).
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
                // Files from before 'present' existed list only the effects on the layer.
                if (fx.Bool("present") == false) continue;
                if (Parse(type, fx, context) is { } effect)
                    items.Add(effect with { Enabled = fx.Bool("enab") == true, SourceData = new PsdEffectSource(type, fx) });
            }
        }
        return items.Count == 0 ? null : new LayerEffects(items)
        {
            Enabled = d.Bool("masterFXSwitch") != false,
            SourceData = new PsdEffectsSource(d),
        };
    }

    private static LayerEffect? Parse(string type, Descriptor fx, PsdEffectContext context)
    {
        var mode = BlendModeOf(fx.Enum("Md  "));
        float opacity = Percent(fx, "Opct", 100);
        bool global = fx.Bool("uglg") == true;
        float angle = global ? context.GlobalAngle : (float)(fx.Number("lagl") ?? 120);

        switch (type)
        {
            case "DrSh":
                return new DropShadowEffect
                {
                    BlendMode = mode, Opacity = opacity,
                    Color = ColorOf(fx.Object("Clr ")),
                    Angle = angle, UseGlobalLight = global,
                    Distance = Pixels(fx, "Dstn", 0),
                    Spread = Percent(fx, "Ckmt", 0),
                    Size = Pixels(fx, "blur", 0),
                    Knockout = fx.Bool("layerConceals") ?? true,
                    Contour = ContourOf(fx.Object("TrnS")), AntiAliased = fx.Bool("AntA") ?? false, Noise = Percent(fx, "Nose", 0),
                };

            case "IrSh":
                return new InnerShadowEffect
                {
                    BlendMode = mode, Opacity = opacity,
                    Color = ColorOf(fx.Object("Clr ")),
                    Angle = angle, UseGlobalLight = global,
                    Distance = Pixels(fx, "Dstn", 0),
                    Choke = Percent(fx, "Ckmt", 0),
                    Size = Pixels(fx, "blur", 0),
                    Contour = ContourOf(fx.Object("TrnS")), AntiAliased = fx.Bool("AntA") ?? false, Noise = Percent(fx, "Nose", 0),
                };

            case "OrGl":
                return new OuterGlowEffect
                {
                    BlendMode = mode, Opacity = opacity,
                    Color = ColorOf(fx.Object("Clr ")),
                    Gradient = GlowGradientOf(fx),
                    Spread = Percent(fx, "Ckmt", 0),
                    Size = Pixels(fx, "blur", 0),
                    Technique = TechniqueOf(fx), Contour = ContourOf(fx.Object("TrnS")), AntiAliased = fx.Bool("AntA") ?? false,
                    Noise = Percent(fx, "Nose", 0), Range = Percent(fx, "Inpr", 50), Jitter = Percent(fx, "ShdN", 0),
                };

            case "IrGl":
                return new InnerGlowEffect
                {
                    BlendMode = mode, Opacity = opacity,
                    Color = ColorOf(fx.Object("Clr ")),
                    Gradient = GlowGradientOf(fx),
                    Choke = Percent(fx, "Ckmt", 0),
                    Size = Pixels(fx, "blur", 0),
                    FromCenter = fx.Enum("glwS") == "SrcC",
                    Technique = TechniqueOf(fx), Contour = ContourOf(fx.Object("TrnS")), AntiAliased = fx.Bool("AntA") ?? false,
                    Noise = Percent(fx, "Nose", 0), Range = Percent(fx, "Inpr", 50), Jitter = Percent(fx, "ShdN", 0),
                };

            case "SoFi":
                return new ColorOverlayEffect { BlendMode = mode, Opacity = opacity, Color = ColorOf(fx.Object("Clr ")) };

            case "GrFl" when GradientFillOf(fx) is { } fill:
                return new GradientOverlayEffect
                {
                    BlendMode = mode, Opacity = opacity,
                    Gradient = fill.Gradient, Style = fill.Style, Angle = fill.Angle, Scale = fill.Scale, Reverse = fill.Reverse,
                    AlignWithLayer = fill.AlignWithLayer, OffsetX = fill.OffsetX, OffsetY = fill.OffsetY,
                };

            case "GrFl":
                return new UnsupportedEffect("Gradient Overlay (unreadable gradient)");

            case "patternFill":
                return new PatternOverlayEffect { BlendMode = mode, Opacity = opacity, Fill = PatternFillOf(fx, "Algn", context) };

            case "FrFX":
                return new StrokeEffect
                {
                    BlendMode = mode, Opacity = opacity,
                    Color = ColorOf(fx.Object("Clr ")),
                    Size = Pixels(fx, "Sz  ", 1),
                    Position = fx.Enum("Styl") switch
                    {
                        "InsF" => StrokePosition.Inside,
                        "CtrF" => StrokePosition.Center,
                        _ => StrokePosition.Outside,
                    },
                    FillType = fx.Enum("PntT") switch
                    {
                        "GrFl" => StrokeFillType.Gradient,
                        "Ptrn" => StrokeFillType.Pattern,
                        _ => StrokeFillType.Color,
                    },
                    // Photoshop keeps only the chosen fill type's settings; 'Scl ' belongs to whichever it is.
                    GradientFill = fx.Enum("PntT") == "GrFl" ? GradientFillOf(fx) : null,
                    PatternFill = fx.Enum("PntT") == "Ptrn" ? PatternFillOf(fx, "Lnkd", context) : null,
                };

            case "ebbl":
                return ReadBevel(fx, context);

            case "ChFX":
                return new SatinEffect
                {
                    BlendMode = mode, Opacity = opacity,
                    Color = ColorOf(fx.Object("Clr ")),
                    Angle = (float)(fx.Number("lagl") ?? 19),
                    Distance = Pixels(fx, "Dstn", 11),
                    Size = Pixels(fx, "blur", 14),
                    Contour = ContourOf(fx.Object("MpgS")),
                    AntiAliased = fx.Bool("AntA") ?? false,
                    Invert = fx.Bool("Invr") ?? false,
                };

            default:
                return null;
        }
    }

    private static BevelEffect ReadBevel(Descriptor fx, PsdEffectContext context)
    {
        bool global = fx.Bool("uglg") == true;
        return new BevelEffect
        {
            Style = fx.Enum("bvlS") switch
            {
                "OtrB" => BevelStyle.OuterBevel,
                "Embs" => BevelStyle.Emboss,
                "PlEb" => BevelStyle.PillowEmboss,
                "strokeEmboss" => BevelStyle.StrokeEmboss,
                _ => BevelStyle.InnerBevel,
            },
            Technique = fx.Enum("bvlT") switch
            {
                "PrBL" => BevelTechnique.ChiselHard,
                "Slmt" => BevelTechnique.ChiselSoft,
                _ => BevelTechnique.Smooth,
            },
            Depth = Percent(fx, "srgR", 100),
            Up = fx.Enum("bvlD")?.TrimEnd() != "Out",
            Size = Pixels(fx, "blur", 5),
            Soften = Pixels(fx, "Sftn", 0),
            UseGlobalLight = global,
            Angle = global ? context.GlobalAngle : (float)(fx.Number("lagl") ?? 120),
            Altitude = global ? context.GlobalAltitude : (float)(fx.Number("Lald") ?? 30),
            GlossContour = ContourOf(fx.Object("TrnS")),
            GlossAntiAliased = fx.Bool("antialiasGloss") ?? false,
            HighlightMode = fx.Enum("hglM") is { } hm ? BlendModeOf(hm) : BlendMode.Screen,
            HighlightColor = fx.Object("hglC") is { } hc ? ColorOf(hc) : new RgbColor(1, 1, 1),
            HighlightOpacity = Percent(fx, "hglO", 75),
            ShadowMode = fx.Enum("sdwM") is { } sm ? BlendModeOf(sm) : BlendMode.Multiply,
            ShadowColor = ColorOf(fx.Object("sdwC")),
            ShadowOpacity = Percent(fx, "sdwO", 75),
            UseContour = fx.Bool("useShape") ?? false,
            Contour = ContourOf(fx.Object("MpgS")),
            ContourAntiAliased = fx.Bool("AntA") ?? false,
            ContourRange = Percent(fx, "Inpr", 50),
            UseTexture = fx.Bool("useTexture") ?? false,
            Texture = PatternFillOf(fx, "Algn", context),
            TextureDepth = Percent(fx, "textureDepth", 100),
            TextureInvert = fx.Bool("InvT") ?? false,
        };
    }

    private static float Percent(Descriptor fx, string key, double fallback) => (float)(fx.Number(key) ?? fallback) / 100f;
    private static float Pixels(Descriptor fx, string key, double fallback) => (float)(fx.Number(key) ?? fallback);

    private static GlowTechnique TechniqueOf(Descriptor fx) => fx.Enum("GlwT") == "PrBL" ? GlowTechnique.Precise : GlowTechnique.Softer;

    /// <summary>A glow is a gradient glow when it stores a gradient instead of a color.</summary>
    private static Gradient? GlowGradientOf(Descriptor fx) => fx.Has("Clr ") ? null : GradientOf(fx.Object("Grad"));

    /// <summary>A contour ('ShpC'): its name and curve points (0..255), with corner points marked by 'Cnty' false.</summary>
    public static Contour ContourOf(Descriptor? c)
    {
        if (c is null) return Contour.Linear;
        if (c.List("Crv ") is not { Count: >= 2 } curve) return Contour.Linear with { Name = c.Text("Nm  ") ?? "Linear" };
        var points = curve.OfType<ObjectValue>().Select(o => new ContourPoint(
            (float)(o.Value.Number("Hrzn") ?? 0), (float)(o.Value.Number("Vrtc") ?? 0), o.Value.Bool("Cnty") == false)).ToList();
        return new Contour(points) { Name = c.Text("Nm  ") ?? "Custom" };
    }

    /// <summary>
    /// The gradient settings stored with a gradient overlay or gradient stroke ('Grad', 'Type', 'Angl', 'Scl ',
    /// 'Rvrs', 'Algn', 'Ofst'), or null without a readable gradient.
    /// </summary>
    public static GradientFill? GradientFillOf(Descriptor fx)
    {
        if (GradientOf(fx.Object("Grad")) is not { } gradient) return null;
        return new GradientFill(gradient)
        {
            Style = GradientStyleOf(fx.Enum("Type")),
            Angle = (float)(fx.Number("Angl") ?? 90),
            Scale = Percent(fx, "Scl ", 100),
            Reverse = fx.Bool("Rvrs") ?? false,
            AlignWithLayer = fx.Bool("Algn") ?? true,
            OffsetX = (float)(fx.Object("Ofst")?.Number("Hrzn") ?? 0) / 100f,
            OffsetY = (float)(fx.Object("Ofst")?.Number("Vrtc") ?? 0) / 100f,
        };
    }

    internal static GradientStyle GradientStyleOf(string? type) => type switch
    {
        "Rdl " => GradientStyle.Radial,
        "Angl" => GradientStyle.Angle,
        "Rflc" => GradientStyle.Reflected,
        "Dmnd" => GradientStyle.Diamond,
        "shapeburst" => GradientStyle.ShapeBurst,
        _ => GradientStyle.Linear,
    };

    internal static string GradientTypeOf(GradientStyle style) => style switch
    {
        GradientStyle.Radial => "Rdl ",
        GradientStyle.Angle => "Angl",
        GradientStyle.Reflected => "Rflc",
        GradientStyle.Diamond => "Dmnd",
        GradientStyle.ShapeBurst => "shapeburst",
        _ => "Lnr ",
    };

    /// <summary>
    /// The pattern settings of a pattern overlay, pattern stroke or bevel texture ('Ptrn' with its name and id,
    /// 'Scl ', the link key, 'phase'), or null when no pattern is named.
    /// </summary>
    private static PatternFill? PatternFillOf(Descriptor fx, string linkKey, PsdEffectContext context)
    {
        if (fx.Object("Ptrn") is not { } ptrn || ptrn.Text("Idnt") is not { } id) return null;
        return new PatternFill(context.Resolve(id, ptrn.Text("Nm  ") ?? ""))
        {
            Scale = Percent(fx, "Scl ", 100),
            LinkWithLayer = fx.Bool(linkKey) ?? true,
            PhaseX = (float)(fx.Object("phase")?.Number("Hrzn") ?? 0),
            PhaseY = (float)(fx.Object("phase")?.Number("Vrtc") ?? 0),
        };
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
        return colorStops.Count == 0 ? null : new Gradient(colorStops, opacityStops) { Name = g.Text("Nm  ") ?? "Custom" };
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

    /// <summary>The descriptor enum value ('BlnM' type) for a blend mode; the inverse of <see cref="BlendModeOf"/>.</summary>
    public static string DescriptorKeyOf(BlendMode mode) =>
        Modes.FirstOrDefault(kv => kv.Value == mode).Key ?? "Nrml";
}
