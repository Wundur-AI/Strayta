using Strayta.Core;
using Strayta.Psd.Descriptors;

namespace Strayta.Psd;

/// <summary>
/// Encodes layer styles as the 'lfx2' block Photoshop reads (object-based effects); the inverse of
/// <see cref="PsdEffects"/>. Key names, value types and units are the ones Photoshop CC writes (checked against
/// Photoshop-authored files): sizes and distances as '#Pxl' unit floats, opacities as '#Prc', angles as '#Ang',
/// spread and choke as a percentage in a '#Pxl' unit float (Photoshop's own quirk), colors as 'RGBC' objects with
/// 0..255 doubles.
/// </summary>
/// <remarks>
/// An edited effect that was read from a file is written by patching its original descriptor: only the settings the
/// model holds are replaced, and a setting whose value did not change keeps its original value (and unit), so keys
/// this library does not know stay exactly as Photoshop wrote them. New effects get Photoshop's complete default
/// descriptor. The top level keeps the source's 'Scl ' (Scale Effects) and other keys, and effects that were removed
/// from the layer stay in the block marked not present, as Photoshop does, so its dialog still remembers their settings.
/// <para>
/// When a layer's effects are rewritten, the legacy 'lrFX' block (Photoshop 5 effects) and the older multi-effect
/// 'lmfx' block are dropped: Photoshop reads 'lfx2' whenever it is present, 'lfx2' now holds every effect (including
/// several of one kind, under the "…Multi" keys), and a stale 'lrFX' would describe the old style to the older readers
/// that still use it. Layers whose effects did not change keep all three blocks byte for byte.
/// </para>
/// </remarks>
public static class PsdEffectsWriter
{
    /// <summary>The order Photoshop CC writes effect keys in.</summary>
    private static readonly string[] TypeOrder = ["DrSh", "IrSh", "OrGl", "SoFi", "GrFl", "patternFill", "FrFX", "IrGl", "ebbl", "ChFX"];

    private static readonly Dictionary<string, string> MultiKeyOf =
        PsdEffects.MultiKeys.ToDictionary(kv => kv.Value, kv => kv.Key);

    private static readonly string[] BlockKeys = ["lfx2", "lmfx", "lrFX"];

    /// <summary>The effect type key ("DrSh", "FrFX", ...) an effect is stored under, or null if it cannot be written.</summary>
    public static string? TypeOf(LayerEffect effect) => effect switch
    {
        DropShadowEffect => "DrSh",
        InnerShadowEffect => "IrSh",
        OuterGlowEffect => "OrGl",
        InnerGlowEffect => "IrGl",
        ColorOverlayEffect => "SoFi",
        GradientOverlayEffect => "GrFl",
        PatternOverlayEffect => "patternFill",
        StrokeEffect => "FrFX",
        BevelEffect => "ebbl",
        SatinEffect => "ChFX",
        UnsupportedEffect { SourceData: PsdEffectSource s } => s.Type,
        _ => null,
    };

    /// <summary>
    /// Replaces the layer's effect blocks in <paramref name="blocks"/> (copied from <paramref name="source"/>) when its
    /// effects differ from what the source record holds; unchanged layers keep their original bytes.
    /// </summary>
    /// <param name="sourceContext">The source file's global light, which reading the source record needs.</param>
    internal static void Refresh(LayerNode node, PsdLayerRecord? source, PsdEffectContext sourceContext, List<(string Key, byte[] Data)> blocks)
    {
        var original = source is null ? null : PsdEffects.Read(source, sourceContext);
        if (Equals(original, node.Effects)) return;

        int at = -1;
        for (int i = blocks.Count - 1; i >= 0; i--)
        {
            if (!BlockKeys.Contains(blocks[i].Key)) continue;
            blocks.RemoveAt(i);
            at = i;
        }
        if (node.Effects is not { } effects) return;
        var data = Encode(effects);
        if (at < 0) blocks.Add(("lfx2", data));
        else blocks.Insert(at, ("lfx2", data));
    }

    /// <summary>The 'lfx2' block data: object effects version (0), descriptor version (16), then the descriptor.</summary>
    public static byte[] Encode(LayerEffects effects) => [0, 0, 0, 0, .. DescriptorWriter.WriteVersioned(Describe(effects))];

    /// <summary>The top-level 'lfx2' descriptor for <paramref name="effects"/>.</summary>
    public static Descriptor Describe(LayerEffects effects)
    {
        var byType = new Dictionary<string, List<LayerEffect>>();
        foreach (var e in effects.Items)
            if (TypeOf(e) is { } type)
            {
                if (!byType.TryGetValue(type, out var list)) byType[type] = list = [];
                list.Add(e);
            }

        var items = new List<KeyValuePair<string, DescriptorValue>>();
        var written = new HashSet<string>();
        var usedKeys = new HashSet<string>();
        void Add(string key, DescriptorValue value)
        {
            if (usedKeys.Add(key)) items.Add(new(key, value));
        }

        if ((effects.SourceData as PsdEffectsSource)?.Descriptor is { } top)
        {
            if (!top.Has("masterFXSwitch")) Add("masterFXSwitch", new BoolValue(effects.Enabled));
            foreach (var (key, value) in top.Items)
            {
                if (key == "masterFXSwitch")
                {
                    Add(key, new BoolValue(effects.Enabled));
                    continue;
                }
                string type = PsdEffects.MultiKeys.GetValueOrDefault(key, key);
                if (!TypeOrder.Contains(type))
                {
                    Add(key, value); // 'Scl ', 'numModifyingFX' and anything newer
                    continue;
                }
                if (written.Add(type) && byType.TryGetValue(type, out var list))
                {
                    var (k, v) = Entry(type, multi: key != type, list);
                    Add(k, v);
                }
                else if (Absent(value) is { } kept)
                    Add(key, kept);
            }
        }
        else
        {
            Add("Scl ", new UnitFloatValue("#Prc", 100));
            Add("masterFXSwitch", new BoolValue(effects.Enabled));
        }

        foreach (var type in TypeOrder)
            if (!written.Contains(type) && byType.TryGetValue(type, out var list))
            {
                var (k, v) = Entry(type, multi: false, list);
                Add(k, v);
            }

        return new Descriptor { Name = "", ClassId = "null", Items = items };
    }

    /// <summary>
    /// One effect under its own key, or several (or one that was stored that way) as a list under the "…Multi" key.
    /// Effects that Photoshop allows only once keep the first.
    /// </summary>
    private static (string Key, DescriptorValue Value) Entry(string type, bool multi, List<LayerEffect> list)
    {
        if ((multi || list.Count > 1) && MultiKeyOf.TryGetValue(type, out var multiKey))
            return (multiKey, new ListValue(list.Select(e => (DescriptorValue)new ObjectValue(Describe(e))).ToList()));
        return (type, new ObjectValue(Describe(list[0])));
    }

    /// <summary>
    /// An effect that is no longer on the layer: Photoshop keeps its settings for the dialog, marked neither enabled
    /// nor present. Entries from files that predate the 'present' key are dropped, since there they would still count.
    /// </summary>
    private static DescriptorValue? Absent(DescriptorValue value)
    {
        static Descriptor? Off(Descriptor d) => d.Has("present")
            ? With(d, ("enab", new BoolValue(false)), ("present", new BoolValue(false)))
            : null;

        switch (value)
        {
            case ObjectValue o:
                return Off(o.Value) is { } d ? new ObjectValue(d) : null;
            case ListValue l:
                var kept = l.Items.OfType<ObjectValue>().Select(o => Off(o.Value)).OfType<Descriptor>()
                    .Select(d => (DescriptorValue)new ObjectValue(d)).ToList();
                return kept.Count == 0 ? null : new ListValue(kept);
            default:
                return null;
        }
    }

    // ---- One effect --------------------------------------------------------------------------------

    /// <summary>The descriptor for one effect: its source descriptor with the modeled settings replaced, or a new one.</summary>
    public static Descriptor Describe(LayerEffect effect)
    {
        string type = TypeOf(effect) ?? throw new NotSupportedException($"Cannot write {effect.GetType().Name}.");
        var source = effect.SourceData is PsdEffectSource s && s.Type == type ? s.Descriptor : null;
        var d = new Builder(source ?? Defaults(type));

        d.Set("enab", new BoolValue(effect.Enabled));
        if (d.Has("present")) d.Set("present", new BoolValue(true));
        if (effect is UnsupportedEffect) return d.Build();

        if (effect is not BevelEffect)
        {
            d.Set("Md  ", new EnumValue("BlnM", PsdEffects.DescriptorKeyOf(effect.BlendMode)));
            d.Unit("Opct", "#Prc", Percent(effect.Opacity));
        }

        switch (effect)
        {
            case DropShadowEffect e:
                d.Color("Clr ", e.Color);
                d.Set("uglg", new BoolValue(e.UseGlobalLight));
                if (!e.UseGlobalLight || !d.Has("lagl")) d.Unit("lagl", "#Ang", e.Angle);
                d.Unit("Dstn", "#Pxl", e.Distance);
                d.Unit("Ckmt", "#Pxl", Percent(e.Spread));
                d.Unit("blur", "#Pxl", e.Size);
                Quality(d, e.Noise, e.AntiAliased, e.Contour);
                d.Set("layerConceals", new BoolValue(e.Knockout));
                break;
            case InnerShadowEffect e:
                d.Color("Clr ", e.Color);
                d.Set("uglg", new BoolValue(e.UseGlobalLight));
                if (!e.UseGlobalLight || !d.Has("lagl")) d.Unit("lagl", "#Ang", e.Angle);
                d.Unit("Dstn", "#Pxl", e.Distance);
                d.Unit("Ckmt", "#Pxl", Percent(e.Choke));
                d.Unit("blur", "#Pxl", e.Size);
                Quality(d, e.Noise, e.AntiAliased, e.Contour);
                break;
            case OuterGlowEffect e:
                Glow(d, e.Color, e.Gradient, e.Technique, e.Spread, e.Size, e.Noise, e.Jitter, e.AntiAliased, e.Contour, e.Range);
                break;
            case InnerGlowEffect e:
                Glow(d, e.Color, e.Gradient, e.Technique, e.Choke, e.Size, e.Noise, e.Jitter, e.AntiAliased, e.Contour, e.Range);
                d.Set("glwS", new EnumValue("IGSr", e.FromCenter ? "SrcC" : "SrcE"));
                break;
            case ColorOverlayEffect e:
                d.Color("Clr ", e.Color);
                break;
            case GradientOverlayEffect e:
                GradientSettings(d, new GradientFill(e.Gradient)
                {
                    Style = e.Style, Angle = e.Angle, Scale = e.Scale, Reverse = e.Reverse, AlignWithLayer = e.AlignWithLayer,
                    OffsetX = e.OffsetX, OffsetY = e.OffsetY,
                });
                break;
            case PatternOverlayEffect e:
                if (e.Fill is { } fill) PatternSettings(d, fill, "Algn", before: "Scl ");
                break;
            case StrokeEffect e:
                d.Set("Styl", new EnumValue("FStl", e.Position switch
                {
                    StrokePosition.Inside => "InsF",
                    StrokePosition.Center => "CtrF",
                    _ => "OutF",
                }));
                string paint = e.FillType switch
                {
                    StrokeFillType.Gradient => "GrFl",
                    StrokeFillType.Pattern => "Ptrn",
                    _ => "SClr",
                };
                // Photoshop stores only the settings of the chosen fill type ('Scl ' is the gradient's or the pattern's),
                // so a stroke that changes its fill type drops the old type's settings.
                if (d.EnumValue("PntT") is { } before && before != paint)
                    d.Remove("Grad", "Angl", "Type", "Rvrs", "Dthr", "Algn", "Ofst", "Ptrn", "Lnkd", "phase", "Scl ");
                d.Set("PntT", new EnumValue("FrFl", paint));
                d.Unit("Sz  ", "#Pxl", e.Size);
                d.Color("Clr ", e.Color);
                if (e.FillType == StrokeFillType.Gradient && e.GradientFill is { } gradient) GradientSettings(d, gradient);
                if (e.FillType == StrokeFillType.Pattern && e.PatternFill is { } pattern) PatternSettings(d, pattern, "Lnkd", before: null);
                break;
            case BevelEffect e:
                Bevel(d, e);
                break;
            case SatinEffect e:
                d.Color("Clr ", e.Color);
                d.Set("AntA", new BoolValue(e.AntiAliased));
                d.Set("Invr", new BoolValue(e.Invert));
                d.Unit("lagl", "#Ang", e.Angle);
                d.Unit("Dstn", "#Pxl", e.Distance);
                d.Unit("blur", "#Pxl", e.Size);
                d.Contour("MpgS", e.Contour);
                break;
        }
        return d.Build();
    }

    /// <summary>Outer and inner glow: a solid color or a gradient (which replaces the color), and the Quality settings.</summary>
    private static void Glow(Builder d, RgbColor color, Gradient? gradient, GlowTechnique technique, float spread, float size,
        float noise, float jitter, bool antiAliased, Contour contour, float range)
    {
        if (gradient is null) d.Color("Clr ", color);
        else
        {
            d.Gradient("Grad", gradient, replacing: "Clr ");
            d.Remove("Clr ");
        }
        if (d.Has("GlwT") || technique == GlowTechnique.Precise)
            d.Set("GlwT", new EnumValue("BETE", technique == GlowTechnique.Precise ? "PrBL" : "SfBL"));
        d.Unit("Ckmt", "#Pxl", Percent(spread));
        d.Unit("blur", "#Pxl", size);
        Quality(d, noise, antiAliased, contour);
        if (d.Has("ShdN") || jitter != 0) d.Unit("ShdN", "#Prc", Percent(jitter));
        if (d.Has("Inpr") || range != 0.5f) d.Unit("Inpr", "#Prc", Percent(range));
    }

    /// <summary>
    /// Noise, anti-aliasing and contour: set when the descriptor has them (Photoshop CC always writes them) or when
    /// they differ from the defaults, so older files that lack them are not given keys they never had.
    /// </summary>
    private static void Quality(Builder d, float noise, bool antiAliased, Contour contour)
    {
        if (d.Has("Nose") || noise != 0) d.Unit("Nose", "#Prc", Percent(noise));
        if (d.Has("AntA") || antiAliased) d.Set("AntA", new BoolValue(antiAliased));
        if (d.Has("TrnS") || !contour.Equals(Contour.Linear)) d.Contour("TrnS", contour);
    }

    private static void GradientSettings(Builder d, GradientFill g)
    {
        bool fresh = !d.Has("Grad"); // a stroke that just became a gradient stroke gets Photoshop's full key set
        d.Gradient("Grad", g.Gradient, replacing: null);
        d.Unit("Angl", "#Ang", g.Angle);
        d.Set("Type", new EnumValue("GrdT", PsdEffects.GradientTypeOf(g.Style)));
        d.Set("Rvrs", new BoolValue(g.Reverse));
        if (fresh && !d.Has("Dthr")) d.Set("Dthr", new BoolValue(false));
        d.Set("Algn", new BoolValue(g.AlignWithLayer));
        d.Unit("Scl ", "#Prc", Percent(g.Scale));
        d.Set("Ofst", new ObjectValue(Object("Pnt ",
            ("Hrzn", new UnitFloatValue("#Prc", Percent(g.OffsetX))), ("Vrtc", new UnitFloatValue("#Prc", Percent(g.OffsetY))))));
    }

    /// <summary>A pattern reference ('Ptrn': name and id) with its scale, link and phase.</summary>
    private static void PatternSettings(Builder d, PatternFill fill, string linkKey, string? before)
    {
        d.Pattern("Ptrn", fill.Pattern, before);
        d.Unit("Scl ", "#Prc", Percent(fill.Scale));
        d.Set(linkKey, new BoolValue(fill.LinkWithLayer));
        d.Set("phase", new ObjectValue(Object("Pnt ", ("Hrzn", new DoubleValue(fill.PhaseX)), ("Vrtc", new DoubleValue(fill.PhaseY)))));
    }

    private static void Bevel(Builder d, BevelEffect e)
    {
        d.Set("hglM", new EnumValue("BlnM", PsdEffects.DescriptorKeyOf(e.HighlightMode)));
        d.Color("hglC", e.HighlightColor);
        d.Unit("hglO", "#Prc", Percent(e.HighlightOpacity));
        d.Set("sdwM", new EnumValue("BlnM", PsdEffects.DescriptorKeyOf(e.ShadowMode)));
        d.Color("sdwC", e.ShadowColor);
        d.Unit("sdwO", "#Prc", Percent(e.ShadowOpacity));
        d.Set("bvlT", new EnumValue("bvlT", e.Technique switch
        {
            BevelTechnique.ChiselHard => "PrBL",
            BevelTechnique.ChiselSoft => "Slmt",
            _ => "SfBL",
        }));
        d.Set("bvlS", new EnumValue("BESl", e.Style switch
        {
            BevelStyle.OuterBevel => "OtrB",
            BevelStyle.Emboss => "Embs",
            BevelStyle.PillowEmboss => "PlEb",
            BevelStyle.StrokeEmboss => "strokeEmboss",
            _ => "InrB",
        }));
        d.Set("uglg", new BoolValue(e.UseGlobalLight));
        if (!e.UseGlobalLight || !d.Has("lagl")) d.Unit("lagl", "#Ang", e.Angle);
        if (!e.UseGlobalLight || !d.Has("Lald")) d.Unit("Lald", "#Ang", e.Altitude);
        d.Unit("srgR", "#Prc", Percent(e.Depth));
        d.Unit("blur", "#Pxl", e.Size);
        if (d.EnumValue("bvlD")?.TrimEnd() != (e.Up ? "In" : "Out")) d.Set("bvlD", new EnumValue("BESs", e.Up ? "In  " : "Out "));
        d.Contour("TrnS", e.GlossContour);
        d.Set("antialiasGloss", new BoolValue(e.GlossAntiAliased));
        d.Unit("Sftn", "#Pxl", e.Soften);
        d.Set("useShape", new BoolValue(e.UseContour));
        if (e.UseContour || d.Has("MpgS") || !e.Contour.Equals(Contour.Linear) || e.ContourAntiAliased || e.ContourRange != 0.5f)
        {
            d.Contour("MpgS", e.Contour);
            d.Set("AntA", new BoolValue(e.ContourAntiAliased));
            d.Unit("Inpr", "#Prc", Percent(e.ContourRange));
        }
        d.Set("useTexture", new BoolValue(e.UseTexture));
        if (e.Texture is { } texture)
        {
            d.Pattern("Ptrn", texture.Pattern, before: null);
            d.Unit("Scl ", "#Prc", Percent(texture.Scale));
            d.Unit("textureDepth", "#Prc", Percent(e.TextureDepth));
            d.Set("InvT", new BoolValue(e.TextureInvert));
            d.Set("Algn", new BoolValue(texture.LinkWithLayer));
            d.Set("phase", new ObjectValue(Object("Pnt ", ("Hrzn", new DoubleValue(texture.PhaseX)), ("Vrtc", new DoubleValue(texture.PhaseY)))));
        }
    }

    /// <summary>Percentages as Photoshop's dialog keeps them: rounded to a thousandth of a percent.</summary>
    private static double Percent(float fraction) => Math.Round(fraction * 100.0, 3);

    /// <summary>A custom gradient ('Grdn'): color and opacity stops at 0..4096 with midpoints in percent.</summary>
    private static Descriptor GradientDescriptor(Gradient g)
    {
        static DescriptorValue Stop(string classId, float location, float midpoint, List<(string, DescriptorValue)> items)
        {
            items.Add(("Lctn", new IntegerValue((int)MathF.Round(Math.Clamp(location, 0f, 1f) * 4096f))));
            items.Add(("Mdpn", new IntegerValue((int)MathF.Round(Math.Clamp(midpoint, 0f, 1f) * 100f))));
            return new ObjectValue(Object(classId, [.. items]));
        }

        return Object("Grdn",
            ("Nm  ", new TextValue(g.Name)),
            ("GrdF", new EnumValue("GrdF", "CstS")),
            ("Intr", new DoubleValue(4096)),
            ("Clrs", new ListValue(g.Colors.Select(c => Stop("Clrt", c.Location, c.Midpoint,
                [("Clr ", new ObjectValue(Rgb(c.Color))), ("Type", new EnumValue("Clry", "UsrS"))])).ToList())),
            ("Trns", new ListValue(g.Opacities.Select(o => Stop("TrnS", o.Location, o.Midpoint,
                [("Opct", new UnitFloatValue("#Prc", Percent(o.Opacity)))])).ToList())));
    }

    /// <summary>A contour ('ShpC'): name and points, corner points marked with 'Cnty' false as Photoshop does.</summary>
    private static Descriptor ContourDescriptor(Contour c) => Object("ShpC",
        ("Nm  ", new TextValue(c.Name)),
        ("Crv ", new ListValue(c.Points.Select(p => (DescriptorValue)new ObjectValue(p.Corner
            ? Object("CrPt", ("Hrzn", new DoubleValue(p.X)), ("Vrtc", new DoubleValue(p.Y)), ("Cnty", new BoolValue(false)))
            : Object("CrPt", ("Hrzn", new DoubleValue(p.X)), ("Vrtc", new DoubleValue(p.Y))))).ToList())));

    private static Descriptor Rgb(RgbColor c) => Object("RGBC",
        ("Rd  ", new DoubleValue(Channel(c.R))), ("Grn ", new DoubleValue(Channel(c.G))), ("Bl  ", new DoubleValue(Channel(c.B))));

    /// <summary>0..1 to Photoshop's 0..255 doubles, rounded to what 16-bit precision can tell apart.</summary>
    private static double Channel(float v) => Math.Round(Math.Clamp(v, 0f, 1f) * 255.0, 3);

    private static Descriptor Object(string classId, params (string Key, DescriptorValue Value)[] items) =>
        new() { Name = "", ClassId = classId, Items = items.Select(i => new KeyValuePair<string, DescriptorValue>(i.Key, i.Value)).ToList() };

    private static Descriptor With(Descriptor d, params (string Key, DescriptorValue Value)[] changes)
    {
        var b = new Builder(d);
        foreach (var (k, v) in changes) b.Set(k, v);
        return b.Build();
    }

    // ---- Photoshop's defaults --------------------------------------------------------------------------

    private static DescriptorValue Pixels(double v) => new UnitFloatValue("#Pxl", v);
    private static DescriptorValue Percents(double v) => new UnitFloatValue("#Prc", v);
    private static DescriptorValue Degrees(double v) => new UnitFloatValue("#Ang", v);

    /// <summary>
    /// The complete descriptor Photoshop CC writes for a new effect of each kind (key order included, as found in
    /// Photoshop-authored files), with its dialog defaults. The modeled settings are then written over it.
    /// </summary>
    private static Descriptor Defaults(string type)
    {
        var items = new List<(string, DescriptorValue)>
        {
            ("enab", new BoolValue(true)), ("present", new BoolValue(true)), ("showInDialog", new BoolValue(true)),
        };
        void Add(string key, DescriptorValue value) => items.Add((key, value));
        void Mode(string m) => Add("Md  ", new EnumValue("BlnM", m));
        void Color(string key, double r, double g, double b) =>
            Add(key, new ObjectValue(Object("RGBC", ("Rd  ", new DoubleValue(r)), ("Grn ", new DoubleValue(g)), ("Bl  ", new DoubleValue(b)))));
        var linear = new ObjectValue(ContourDescriptor(Contour.Linear));

        switch (type)
        {
            case "DrSh" or "IrSh":
                Mode("Mltp"); Color("Clr ", 0, 0, 0); Add("Opct", Percents(35));
                Add("uglg", new BoolValue(true)); Add("lagl", Degrees(120));
                Add("Dstn", Pixels(5)); Add("Ckmt", Pixels(0)); Add("blur", Pixels(5)); Add("Nose", Percents(0));
                Add("AntA", new BoolValue(false)); Add("TrnS", linear);
                if (type == "DrSh") Add("layerConceals", new BoolValue(true));
                break;
            case "OrGl" or "IrGl":
                Mode("Scrn"); Color("Clr ", 255, 255, 190); Add("Opct", Percents(75));
                Add("GlwT", new EnumValue("BETE", "SfBL")); Add("Ckmt", Pixels(0)); Add("blur", Pixels(5)); Add("Nose", Percents(0));
                Add("ShdN", Percents(0)); Add("AntA", new BoolValue(false)); Add("TrnS", linear); Add("Inpr", Percents(50));
                if (type == "IrGl") Add("glwS", new EnumValue("IGSr", "SrcE"));
                break;
            case "SoFi":
                Mode("Nrml"); Color("Clr ", 255, 0, 0); Add("Opct", Percents(100));
                break;
            case "GrFl":
                Mode("Nrml"); Add("Opct", Percents(100));
                Add("Grad", new ObjectValue(GradientDescriptor(new Gradient(
                    [new(0, 0.5f, RgbColor.Black), new(1, 0.5f, new RgbColor(1, 1, 1))],
                    [new(0, 0.5f, 1), new(1, 0.5f, 1)]) { Name = "Black, White" })));
                Add("Angl", Degrees(90)); Add("Type", new EnumValue("GrdT", "Lnr "));
                Add("Rvrs", new BoolValue(false)); Add("Dthr", new BoolValue(false)); Add("Algn", new BoolValue(true));
                Add("Scl ", Percents(100));
                Add("Ofst", new ObjectValue(Object("Pnt ", ("Hrzn", Percents(0)), ("Vrtc", Percents(0)))));
                break;
            case "patternFill":
                Mode("Nrml"); Add("Opct", Percents(100)); Add("Scl ", Percents(100)); Add("Algn", new BoolValue(true));
                Add("phase", new ObjectValue(Object("Pnt ", ("Hrzn", new DoubleValue(0)), ("Vrtc", new DoubleValue(0)))));
                break;
            case "FrFX":
                Add("Styl", new EnumValue("FStl", "OutF")); Add("PntT", new EnumValue("FrFl", "SClr")); Mode("Nrml");
                Add("Opct", Percents(100)); Add("Sz  ", Pixels(3)); Color("Clr ", 255, 0, 0); Add("overprint", new BoolValue(false));
                break;
            case "ebbl":
                Add("hglM", new EnumValue("BlnM", "Scrn")); Color("hglC", 255, 255, 255); Add("hglO", Percents(75));
                Add("sdwM", new EnumValue("BlnM", "Mltp")); Color("sdwC", 0, 0, 0); Add("sdwO", Percents(75));
                Add("bvlT", new EnumValue("bvlT", "SfBL")); Add("bvlS", new EnumValue("BESl", "InrB"));
                Add("uglg", new BoolValue(true)); Add("lagl", Degrees(120)); Add("Lald", Degrees(30));
                Add("srgR", Percents(100)); Add("blur", Pixels(5)); Add("bvlD", new EnumValue("BESs", "In  "));
                Add("TrnS", linear); Add("antialiasGloss", new BoolValue(false)); Add("Sftn", Pixels(0));
                Add("useShape", new BoolValue(false)); Add("useTexture", new BoolValue(false));
                break;
            case "ChFX":
                Mode("Mltp"); Color("Clr ", 0, 0, 0); Add("AntA", new BoolValue(true)); Add("Invr", new BoolValue(true));
                Add("Opct", Percents(50)); Add("lagl", Degrees(19)); Add("Dstn", Pixels(11)); Add("blur", Pixels(14));
                Add("MpgS", linear);
                break;
            default:
                throw new NotSupportedException($"No defaults for effect type '{type}'.");
        }
        return Object(type, [.. items]);
    }

    /// <summary>Edits a descriptor's items in place of order: replaced keys keep their position, new ones go last.</summary>
    private sealed class Builder(Descriptor from)
    {
        private readonly List<KeyValuePair<string, DescriptorValue>> _items = [.. from.Items];

        public bool Has(string key) => _items.Any(kv => kv.Key == key);

        private DescriptorValue? Get(string key) => _items.FirstOrDefault(kv => kv.Key == key).Value;

        public string? EnumValue(string key) => Get(key) is EnumValue e ? e.Value : null;

        public void Set(string key, DescriptorValue value)
        {
            int i = _items.FindIndex(kv => kv.Key == key);
            if (i >= 0) _items[i] = new(key, value);
            else _items.Add(new(key, value));
        }

        /// <summary>Sets a key that is new here in front of <paramref name="before"/> (or at the end).</summary>
        private void SetBefore(string key, DescriptorValue value, string? before)
        {
            if (Has(key) || before is null || _items.FindIndex(kv => kv.Key == before) is var at && at < 0) Set(key, value);
            else _items.Insert(at, new(key, value));
        }

        public void Remove(params string[] keys) => _items.RemoveAll(kv => keys.Contains(kv.Key));

        /// <summary>A number in the unit the source used for it (Photoshop is not always consistent), else <paramref name="unit"/>.</summary>
        public void Unit(string key, string unit, double value) => Set(key, Get(key) switch
        {
            UnitFloatValue u => new UnitFloatValue(u.Unit, value),
            DoubleValue => new DoubleValue(value),
            _ => new UnitFloatValue(unit, value),
        });

        /// <summary>Keeps the source's color object (and its color space) when it already is this color.</summary>
        public void Color(string key, RgbColor color)
        {
            if (Get(key) is ObjectValue o && Close(PsdEffects.ColorOf(o.Value), color)) return;
            Set(key, new ObjectValue(Rgb(color)));
        }

        /// <summary>Keeps the source's contour object when it already describes this contour.</summary>
        public void Contour(string key, Contour contour)
        {
            if (Get(key) is ObjectValue o && PsdEffects.ContourOf(o.Value).Equals(contour)) return;
            Set(key, new ObjectValue(ContourDescriptor(contour)));
        }

        /// <summary>Keeps the source's gradient object (a preset keeps its own keys) when it is this gradient.</summary>
        public void Gradient(string key, Gradient gradient, string? replacing)
        {
            if (Get(key) is ObjectValue o && PsdEffects.GradientOf(o.Value) == gradient) return;
            var value = new ObjectValue(GradientDescriptor(gradient));
            if (!Has(key) && replacing is not null && _items.FindIndex(kv => kv.Key == replacing) is var at and >= 0)
                _items.Insert(at, new(key, value));
            else Set(key, value);
        }

        /// <summary>A pattern reference ('Ptrn' with 'Nm  ' and 'Idnt'), kept when it already names this pattern.</summary>
        public void Pattern(string key, Pattern pattern, string? before)
        {
            if (Get(key) is ObjectValue o && o.Value.Text("Idnt") == pattern.Id && o.Value.Text("Nm  ") == pattern.Name) return;
            SetBefore(key, new ObjectValue(Object("Ptrn", ("Nm  ", new TextValue(pattern.Name)), ("Idnt", new TextValue(pattern.Id)))), before);
        }

        private static bool Close(RgbColor a, RgbColor b) =>
            MathF.Abs(a.R - b.R) < 1e-4f && MathF.Abs(a.G - b.G) < 1e-4f && MathF.Abs(a.B - b.B) < 1e-4f;

        public Descriptor Build() => new() { Name = from.Name, ClassId = from.ClassId, Items = _items };
    }
}
