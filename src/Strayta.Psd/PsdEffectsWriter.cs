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
/// model holds are replaced, so contour, noise, anti-aliasing, technique and any keys this library does not know stay
/// exactly as Photoshop wrote them. New effects get Photoshop's complete default descriptor. The top level keeps the
/// source's 'Scl ' (Scale Effects) and other keys, and effects that were removed from the layer stay in the block
/// marked not present, as Photoshop does, so its dialog still remembers their settings.
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
        StrokeEffect => "FrFX",
        UnsupportedEffect { SourceData: PsdEffectSource s } => s.Type,
        _ => null,
    };

    /// <summary>
    /// Replaces the layer's effect blocks in <paramref name="blocks"/> (copied from <paramref name="source"/>) when its
    /// effects differ from what the source record holds; unchanged layers keep their original bytes.
    /// </summary>
    /// <param name="sourceGlobalAngle">The source file's global light angle, which reading the source record needs.</param>
    internal static void Refresh(LayerNode node, PsdLayerRecord? source, float sourceGlobalAngle, List<(string Key, byte[] Data)> blocks)
    {
        var original = source is null ? null : PsdEffects.Read(source, sourceGlobalAngle);
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

        d.Set("Md  ", new EnumValue("BlnM", PsdEffects.DescriptorKeyOf(effect.BlendMode)));
        d.Unit("Opct", "#Prc", Percent(effect.Opacity));

        switch (effect)
        {
            case DropShadowEffect e:
                d.Color("Clr ", e.Color);
                d.Set("uglg", new BoolValue(e.UseGlobalLight));
                if (!e.UseGlobalLight || !d.Has("lagl")) d.Unit("lagl", "#Ang", e.Angle);
                d.Unit("Dstn", "#Pxl", e.Distance);
                d.Unit("Ckmt", "#Pxl", Percent(e.Spread));
                d.Unit("blur", "#Pxl", e.Size);
                d.Set("layerConceals", new BoolValue(e.Knockout));
                break;
            case InnerShadowEffect e:
                d.Color("Clr ", e.Color);
                d.Set("uglg", new BoolValue(e.UseGlobalLight));
                if (!e.UseGlobalLight || !d.Has("lagl")) d.Unit("lagl", "#Ang", e.Angle);
                d.Unit("Dstn", "#Pxl", e.Distance);
                d.Unit("Ckmt", "#Pxl", Percent(e.Choke));
                d.Unit("blur", "#Pxl", e.Size);
                break;
            case OuterGlowEffect e:
                d.Color("Clr ", e.Color);
                d.Unit("Ckmt", "#Pxl", Percent(e.Spread));
                d.Unit("blur", "#Pxl", e.Size);
                break;
            case InnerGlowEffect e:
                d.Color("Clr ", e.Color);
                d.Unit("Ckmt", "#Pxl", Percent(e.Choke));
                d.Unit("blur", "#Pxl", e.Size);
                d.Set("glwS", new EnumValue("IGSr", e.FromCenter ? "SrcC" : "SrcE"));
                break;
            case ColorOverlayEffect e:
                d.Color("Clr ", e.Color);
                break;
            case GradientOverlayEffect e:
                if (source?.Object("Grad") is not { } grad || PsdEffects.GradientOf(grad) != e.Gradient)
                    d.Set("Grad", new ObjectValue(GradientDescriptor(e.Gradient)));
                d.Unit("Angl", "#Ang", e.Angle);
                d.Set("Type", new EnumValue("GrdT", e.Style switch
                {
                    GradientStyle.Radial => "Rdl ",
                    GradientStyle.Angle => "Angl",
                    GradientStyle.Reflected => "Rflc",
                    GradientStyle.Diamond => "Dmnd",
                    _ => "Lnr ",
                }));
                d.Set("Rvrs", new BoolValue(e.Reverse));
                d.Set("Algn", new BoolValue(e.AlignWithLayer));
                d.Unit("Scl ", "#Prc", Percent(e.Scale));
                d.Set("Ofst", new ObjectValue(Object("Pnt ",
                    ("Hrzn", new UnitFloatValue("#Prc", Percent(e.OffsetX))), ("Vrtc", new UnitFloatValue("#Prc", Percent(e.OffsetY))))));
                break;
            case StrokeEffect e:
                d.Set("Styl", new EnumValue("FStl", e.Position switch
                {
                    StrokePosition.Inside => "InsF",
                    StrokePosition.Center => "CtrF",
                    _ => "OutF",
                }));
                d.Set("PntT", new EnumValue("FrFl", "SClr"));
                d.Unit("Sz  ", "#Pxl", e.Size);
                d.Color("Clr ", e.Color);
                break;
        }
        return d.Build();
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

    private static DescriptorValue LinearContour() => new ObjectValue(Object("ShpC",
        ("Nm  ", new TextValue("Linear")),
        ("Crv ", new ListValue([
            new ObjectValue(Object("CrPt", ("Hrzn", new DoubleValue(0)), ("Vrtc", new DoubleValue(0)))),
            new ObjectValue(Object("CrPt", ("Hrzn", new DoubleValue(255)), ("Vrtc", new DoubleValue(255)))),
        ]))));

    private static DescriptorValue Pixels(double v) => new UnitFloatValue("#Pxl", v);
    private static DescriptorValue Percents(double v) => new UnitFloatValue("#Prc", v);

    /// <summary>
    /// The complete descriptor Photoshop CC writes for a new effect of each kind (key order included), with its
    /// dialog defaults. The modeled settings are then written over it.
    /// </summary>
    private static Descriptor Defaults(string type)
    {
        var items = new List<(string, DescriptorValue)>
        {
            ("enab", new BoolValue(true)), ("present", new BoolValue(true)), ("showInDialog", new BoolValue(true)),
        };
        void Add(string key, DescriptorValue value) => items.Add((key, value));
        void Mode(string m) => Add("Md  ", new EnumValue("BlnM", m));
        void Color(double r, double g, double b) =>
            Add("Clr ", new ObjectValue(Object("RGBC", ("Rd  ", new DoubleValue(r)), ("Grn ", new DoubleValue(g)), ("Bl  ", new DoubleValue(b)))));

        switch (type)
        {
            case "DrSh" or "IrSh":
                Mode("Mltp"); Color(0, 0, 0); Add("Opct", Percents(35));
                Add("uglg", new BoolValue(true)); Add("lagl", new UnitFloatValue("#Ang", 120));
                Add("Dstn", Pixels(5)); Add("Ckmt", Pixels(0)); Add("blur", Pixels(5)); Add("Nose", Percents(0));
                Add("AntA", new BoolValue(false)); Add("TrnS", LinearContour());
                if (type == "DrSh") Add("layerConceals", new BoolValue(true));
                break;
            case "OrGl" or "IrGl":
                Mode("Scrn"); Color(255, 255, 190); Add("Opct", Percents(75));
                Add("GlwT", new EnumValue("BETE", "SfBL")); Add("Ckmt", Pixels(0)); Add("blur", Pixels(5)); Add("Nose", Percents(0));
                Add("ShdN", Percents(0)); Add("AntA", new BoolValue(false)); Add("TrnS", LinearContour()); Add("Inpr", Percents(50));
                if (type == "IrGl") Add("glwS", new EnumValue("IGSr", "SrcE"));
                break;
            case "SoFi":
                Mode("Nrml"); Color(255, 0, 0); Add("Opct", Percents(100));
                break;
            case "GrFl":
                Mode("Nrml"); Add("Opct", Percents(100));
                Add("Grad", new ObjectValue(GradientDescriptor(new Gradient(
                    [new(0, 0.5f, RgbColor.Black), new(1, 0.5f, new RgbColor(1, 1, 1))],
                    [new(0, 0.5f, 1), new(1, 0.5f, 1)]) { Name = "Black, White" })));
                Add("Angl", new UnitFloatValue("#Ang", 90)); Add("Type", new EnumValue("GrdT", "Lnr "));
                Add("Rvrs", new BoolValue(false)); Add("Dthr", new BoolValue(false)); Add("Algn", new BoolValue(true));
                Add("Scl ", Percents(100));
                Add("Ofst", new ObjectValue(Object("Pnt ", ("Hrzn", Percents(0)), ("Vrtc", Percents(0)))));
                break;
            case "FrFX":
                Add("Styl", new EnumValue("FStl", "OutF")); Add("PntT", new EnumValue("FrFl", "SClr")); Mode("Nrml");
                Add("Opct", Percents(100)); Add("Sz  ", Pixels(3)); Color(255, 0, 0); Add("overprint", new BoolValue(false));
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

        public void Set(string key, DescriptorValue value)
        {
            int i = _items.FindIndex(kv => kv.Key == key);
            if (i >= 0) _items[i] = new(key, value);
            else _items.Add(new(key, value));
        }

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

        private static bool Close(RgbColor a, RgbColor b) =>
            MathF.Abs(a.R - b.R) < 1e-4f && MathF.Abs(a.G - b.G) < 1e-4f && MathF.Abs(a.B - b.B) < 1e-4f;

        public Descriptor Build() => new() { Name = from.Name, ClassId = from.ClassId, Items = _items };
    }
}
