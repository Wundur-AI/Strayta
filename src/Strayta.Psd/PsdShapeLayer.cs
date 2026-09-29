using System.Buffers.Binary;
using System.Text;
using Strayta.Core;
using Strayta.Core.Painting;
using Strayta.Core.Paths;
using Strayta.Psd.Descriptors;

namespace Strayta.Psd;

/// <summary>
/// Shape layers as Photoshop stores them, read into <see cref="ShapeLayerData"/> and written back:
/// <list type="bullet">
/// <item>'vmsk' / 'vsms' (vector mask): version 3, flags (1 inverted, 2 not linked, 4 disabled), then path records
/// (<see cref="PsdPaths"/>), padded to a multiple of 4 bytes. Photoshop CC names a shape layer's vector mask 'vsms'.</item>
/// <item>The fill: 'vscg' (vector stroke content, Photoshop CC: a 4-byte key 'SoCo' / 'GdFl' / 'PtFl' then a
/// versioned descriptor), or the older 'SoCo' / 'GdFl' / 'PtFl' block alone. Solid colors are "Clr "; gradients
/// "Grad", "Type", "Angl", "Scl ", "Rvrs", "Algn", "Ofst", "Dthr"; patterns "Ptrn" (name and id), "Scl ", "Algn",
/// "phase".</item>
/// <item>'vstk' (vector stroke, Photoshop CC): a versioned 'strokeStyle' descriptor: strokeEnabled, fillEnabled,
/// strokeStyleLineWidth (points at strokeStyleResolution), DashOffset, MiterLimit, LineCapType (Butt / Round /
/// Square), LineJoinType (Miter / Round / Bevel), LineAlignment (Inside / Center / Outside), ScaleLock, StrokeAdjust,
/// LineDashSet (multiples of the width), BlendMode, Opacity and Content (a solidColorLayer, gradientLayer or
/// patternLayer descriptor like the fill's).</item>
/// <item>'vogk' (vector origination): version 1, then a versioned descriptor whose keyDescriptorList holds one entry
/// per live shape: keyOriginType (1 rectangle, 2 rounded rectangle, 5 ellipse), keyOriginRRectRadii (topRight,
/// topLeft, bottomLeft, bottomRight), keyOriginShapeBBox (Top, Left, Btom, Rght in pixels), keyShapeInvalidated
/// (the path was edited since) and keyOriginIndex, which the subpaths it drew carry in their length records.</item>
/// </list>
/// Writing keeps every block whose content did not change byte for byte, and patches changed descriptors key by
/// key so settings Strayta does not model survive.
/// </summary>
public static class PsdShapeLayer
{
    private static readonly string[] FillKeys = ["SoCo", "GdFl", "PtFl"];

    /// <summary>True when the record is a shape: a fill with a vector mask.</summary>
    public static bool IsShape(PsdLayerRecord record) =>
        (record.FindBlock("vmsk") ?? record.FindBlock("vsms")) is not null
        && (record.FindBlock("vscg") is not null || FillKeys.Any(k => record.FindBlock(k) is not null));

    /// <summary>
    /// The shape a record describes, with coordinates on a <paramref name="width"/>×<paramref name="height"/> canvas,
    /// or null when it is not a (readable) shape layer. <paramref name="patterns"/> resolves pattern fills.
    /// </summary>
    public static ShapeLayerData? Read(PsdLayerRecord record, int width, int height, IReadOnlyList<Pattern>? patterns = null, double resolution = 72)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!IsShape(record)) return null;
        var mask = record.FindBlock("vsms") ?? record.FindBlock("vmsk");
        if (mask?.Data is not { Length: >= 8 } maskData) return null;
        try
        {
            int flags = BinaryPrimitives.ReadInt32BigEndian(maskData.AsSpan(4));
            var path = PsdPaths.Decode(maskData, 8, width, height);
            var context = new PsdEffectContext(120, 30, patterns ?? []);
            var (fillKey, fillDescriptor) = FillDescriptor(record);
            var fill = fillDescriptor is null ? ShapeContent.Solid(RgbColor.Black) : ContentOf(fillKey!, fillDescriptor, context);
            var stroke = new ShapeStroke();
            bool fillEnabled = true;
            if (record.FindBlock("vstk")?.Data is { } vstk)
            {
                var d = DescriptorReader.ReadVersioned(vstk);
                stroke = StrokeOf(d, context, resolution);
                fillEnabled = d.Bool("fillEnabled") ?? true;
            }
            var shapes = record.FindBlock("vogk")?.Data is { Length: > 8 } vogk ? LiveShapesOf(vogk) : [];
            return new ShapeLayerData
            {
                Path = path,
                Fill = fill,
                FillEnabled = fillEnabled,
                Stroke = stroke,
                LiveShapes = shapes,
                Inverted = (flags & 1) != 0,
                Unlinked = (flags & 2) != 0,
                Disabled = (flags & 4) != 0,
                SourceData = record,
            };
        }
        catch (Exception e) when (e is PsdFormatException or ArgumentException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>
    /// The record with its shape blocks replaced by <paramref name="data"/>'s: blocks whose content is unchanged keep
    /// their bytes. The rasterized vector mask Photoshop caches as the user mask is dropped when the outline changed
    /// (the caller supplies new pixels; Photoshop rebuilds the cache).
    /// </summary>
    public static PsdLayerRecord Apply(PsdLayerRecord record, ShapeLayerData data, int width, int height, double resolution = 72)
    {
        ArgumentNullException.ThrowIfNull(record);
        var old = Read(record, width, height, null, resolution);
        var blocks = new List<TaggedBlock>(record.Blocks);
        void Put(string key, byte[] bytes, params string[] replacing)
        {
            int at = blocks.FindIndex(b => b.Key == key || replacing.Contains(b.Key));
            var block = new TaggedBlock("8BIM", key, 0, bytes.Length, bytes);
            if (at >= 0)
            {
                blocks[at] = block;
                blocks.RemoveAll(b => !ReferenceEquals(b, block) && (b.Key == key || replacing.Contains(b.Key)));
            }
            else blocks.Add(block);
        }

        // Vector mask.
        string maskKey = record.FindBlock("vsms") is not null ? "vsms" : record.FindBlock("vmsk") is not null ? "vmsk" : "vsms";
        var oldMask = record.FindBlock(maskKey)?.Data;
        var maskBytes = EncodeMask(data, width, height);
        if (oldMask is null || !SameMask(oldMask, maskBytes)) Put(maskKey, maskBytes);

        // Fill content, where the record keeps it.
        if (old is null || !Equals(old.Fill, data.Fill))
        {
            var (fillKey, fillDescriptor) = FillDescriptor(record);
            if (record.FindBlock("vscg") is not null || fillKey is null)
            {
                var (key, d) = EncodeContent(data.Fill, fillKey == KeyOf(data.Fill) ? fillDescriptor : null);
                Put("vscg", [.. Encoding.ASCII.GetBytes(key), .. DescriptorWriter.WriteVersioned(d)]);
            }
            else
            {
                var (key, d) = EncodeContent(data.Fill, fillKey == KeyOf(data.Fill) ? fillDescriptor : null);
                Put(key, DescriptorWriter.WriteVersioned(d), FillKeys);
            }
        }

        // Stroke and fill enable.
        // (A layer from before Photoshop CC has none; it gets one once a stroke or "no fill" is set.)
        if (old is null || !Equals(old.Stroke, data.Stroke) || old.FillEnabled != data.FillEnabled)
        {
            var source = record.FindBlock("vstk")?.Data is { } v ? DescriptorReader.ReadVersioned(v) : null;
            Put("vstk", DescriptorWriter.WriteVersioned(EncodeStroke(data.Stroke, data.FillEnabled, source, resolution)));
        }

        // Live shapes.
        if (old is null || !SameShapes(old.LiveShapes, data.LiveShapes))
        {
            if (data.LiveShapes.Count == 0) blocks.RemoveAll(b => b.Key == "vogk");
            else Put("vogk", EncodeOrigination(data.LiveShapes, record.FindBlock("vogk")?.Data, resolution));
        }

        // The cached rasterization of the old outline no longer applies.
        var mask = record.Mask;
        bool outlineChanged = oldMask is null || !SameMask(oldMask, maskBytes);
        if (outlineChanged && mask is { } m && (m.Flags & 0x08) != 0) mask = null;
        var copy = record.WithBlocks(blocks, mask);
        if (mask is null)
        {
            copy.ChannelData.Remove(PsdChannelId.UserMask);
            copy.ChannelData.Remove(PsdChannelId.RealUserMask);
        }
        return copy;
    }

    /// <summary>
    /// A record for a new shape layer as Photoshop CC writes one: 'vscg' fill, 'vsms' outline, 'vogk' live shapes and
    /// 'vstk' stroke. Pixels come from the caller (see <see cref="ShapeRenderer"/>).
    /// </summary>
    public static PsdLayerRecord Create(ShapeLayerData data, int width, int height, double resolution = 72)
    {
        var (key, fill) = EncodeContent(data.Fill, null);
        var blocks = new List<TaggedBlock>
        {
            new("8BIM", "vscg", 0, 0, [.. Encoding.ASCII.GetBytes(key), .. DescriptorWriter.WriteVersioned(fill)]),
            new("8BIM", "vsms", 0, 0, EncodeMask(data, width, height)),
        };
        if (data.LiveShapes.Count > 0) blocks.Add(new("8BIM", "vogk", 0, 0, EncodeOrigination(data.LiveShapes, null, resolution)));
        blocks.Add(new("8BIM", "vstk", 0, 0, DescriptorWriter.WriteVersioned(EncodeStroke(data.Stroke, data.FillEnabled, null, resolution))));
        return new PsdLayerRecord { Blocks = blocks.Select(b => b with { Length = b.Data!.Length }).ToList() };
    }

    // ---- Vector mask --------------------------------------------------------------------------------------

    /// <summary>A 'vmsk' / 'vsms' block: version 3, flags, path records, padded to 4 bytes.</summary>
    public static byte[] EncodeMask(ShapeLayerData data, int width, int height)
    {
        var records = PsdPaths.Encode(data.Path, width, height);
        int length = 8 + records.Length;
        var o = new byte[(length + 3) & ~3];
        BinaryPrimitives.WriteInt32BigEndian(o, 3);
        BinaryPrimitives.WriteInt32BigEndian(o.AsSpan(4), (data.Inverted ? 1 : 0) | (data.Unlinked ? 2 : 0) | (data.Disabled ? 4 : 0));
        records.CopyTo(o, 8);
        return o;
    }

    /// <summary>Two mask blocks with the same version, flags and records (padding aside).</summary>
    private static bool SameMask(byte[] a, byte[] b)
    {
        int la = a.Length, lb = b.Length;
        while (la > 8 && (la - 8) % 26 != 0 && a[la - 1] == 0) la--;
        while (lb > 8 && (lb - 8) % 26 != 0 && b[lb - 1] == 0) lb--;
        return a.AsSpan(0, la).SequenceEqual(b.AsSpan(0, lb));
    }

    // ---- Fill content -------------------------------------------------------------------------------------

    private static (string? Key, Descriptor? Descriptor) FillDescriptor(PsdLayerRecord record)
    {
        if (record.FindBlock("vscg")?.Data is { Length: > 8 } vscg)
            return (Encoding.ASCII.GetString(vscg, 0, 4), DescriptorReader.ReadVersioned(vscg, 4));
        foreach (var key in FillKeys)
            if (record.FindBlock(key)?.Data is { Length: > 4 } d)
                return (key, DescriptorReader.ReadVersioned(d));
        return (null, null);
    }

    private static string KeyOf(ShapeContent c) => c switch
    {
        ShapeContent.GradientContent => "GdFl",
        ShapeContent.PatternContent => "PtFl",
        _ => "SoCo",
    };

    private static ShapeContent ContentOf(string key, Descriptor d, PsdEffectContext context) => key switch
    {
        "GdFl" when PsdEffects.GradientFillOf(d) is { } g => new ShapeContent.GradientContent(g),
        "PtFl" when d.Object("Ptrn") is { } p && p.Text("Idnt") is { } id => new ShapeContent.PatternContent(new PatternFill(context.Resolve(id, p.Text("Nm  ") ?? ""))
        {
            Scale = (float)(d.Number("Scl ") ?? 100) / 100f,
            LinkWithLayer = d.Bool("Algn") ?? true,
            PhaseX = (float)(d.Object("phase")?.Number("Hrzn") ?? 0),
            PhaseY = (float)(d.Object("phase")?.Number("Vrtc") ?? 0),
        }),
        _ => ShapeContent.Solid(PsdEffects.ColorOf(d.Object("Clr "))),
    };

    /// <summary>A fill content descriptor, patched onto <paramref name="source"/> (same kind) when given.</summary>
    private static (string Key, Descriptor Descriptor) EncodeContent(ShapeContent content, Descriptor? source)
    {
        switch (content)
        {
            case ShapeContent.GradientContent { Fill: var g }:
            {
                var d = new Patch(source ?? Obj("null"));
                if (!(d.Get("Grad") is ObjectValue go && PsdEffects.GradientOf(go.Value) == g.Gradient))
                    d.Set("Grad", new ObjectValue(PsdEffectsWriter.GradientDescriptor(g.Gradient)), before: null);
                if (!d.Has("Dthr")) d.Set("Dthr", new BoolValue(true), before: "Grad");
                d.Set("Angl", new UnitFloatValue("#Ang", g.Angle), before: "Grad");
                d.Set("Type", new EnumValue("GrdT", PsdEffects.GradientTypeOf(g.Style)), before: "Grad");
                SetIf(d, "Rvrs", new BoolValue(g.Reverse), g.Reverse);
                SetIf(d, "Scl ", new UnitFloatValue("#Prc", Math.Round(g.Scale * 100.0, 3)), g.Scale != 1f);
                SetIf(d, "Algn", new BoolValue(g.AlignWithLayer), !g.AlignWithLayer);
                SetIf(d, "Ofst", new ObjectValue(Obj("Pnt ", ("Hrzn", new UnitFloatValue("#Prc", g.OffsetX * 100.0)), ("Vrtc", new UnitFloatValue("#Prc", g.OffsetY * 100.0)))),
                    g.OffsetX != 0 || g.OffsetY != 0);
                return ("GdFl", d.Build());
            }
            case ShapeContent.PatternContent { Fill: var p }:
            {
                var d = new Patch(source ?? Obj("null"));
                d.Set("Ptrn", new ObjectValue(Obj("Ptrn", ("Nm  ", new TextValue(p.Pattern.Name)), ("Idnt", new TextValue(p.Pattern.Id)))), before: null);
                SetIf(d, "Scl ", new UnitFloatValue("#Prc", Math.Round(p.Scale * 100.0, 3)), p.Scale != 1f);
                SetIf(d, "Algn", new BoolValue(p.LinkWithLayer), !p.LinkWithLayer);
                SetIf(d, "phase", new ObjectValue(Obj("Pnt ", ("Hrzn", new DoubleValue(p.PhaseX)), ("Vrtc", new DoubleValue(p.PhaseY)))), p.PhaseX != 0 || p.PhaseY != 0);
                return ("PtFl", d.Build());
            }
            case ShapeContent.SolidColor { Color: var c }:
            {
                var d = new Patch(source ?? Obj("null"));
                if (!(d.Get("Clr ") is ObjectValue co && Close(PsdEffects.ColorOf(co.Value), c)))
                    d.Set("Clr ", new ObjectValue(PsdEffectsWriter.Rgb(c)), before: null);
                return ("SoCo", d.Build());
            }
            default:
                throw new NotSupportedException(content.GetType().Name);
        }

        void SetIf(Patch d, string key, DescriptorValue v, bool needed)
        {
            if (needed || d.Has(key)) d.Set(key, v, before: null);
        }
    }

    // ---- Stroke -------------------------------------------------------------------------------------------

    private static ShapeStroke StrokeOf(Descriptor d, PsdEffectContext context, double resolution)
    {
        double res = d.Number("strokeStyleResolution") ?? resolution;
        double width = (d.Number("strokeStyleLineWidth") ?? 1) * res / 72.0;
        var content = d.Object("strokeStyleContent") is { } c
            ? c.ClassId switch
            {
                "gradientLayer" => ContentOf("GdFl", c, context),
                "patternLayer" => ContentOf("PtFl", c, context),
                _ => ContentOf("SoCo", c, context),
            }
            : ShapeContent.Solid(RgbColor.Black);
        return new ShapeStroke
        {
            Enabled = d.Bool("strokeEnabled") ?? false,
            Width = width,
            DashOffset = d.Number("strokeStyleLineDashOffset") ?? 0,
            MiterLimit = d.Number("strokeStyleMiterLimit") ?? 100,
            Cap = d.Enum("strokeStyleLineCapType") switch
            {
                "strokeStyleRoundCap" => LineCap.Round,
                "strokeStyleSquareCap" => LineCap.Square,
                _ => LineCap.Butt,
            },
            Join = d.Enum("strokeStyleLineJoinType") switch
            {
                "strokeStyleRoundJoin" => LineJoin.Round,
                "strokeStyleBevelJoin" => LineJoin.Bevel,
                _ => LineJoin.Miter,
            },
            Alignment = d.Enum("strokeStyleLineAlignment") switch
            {
                "strokeStyleAlignCenter" => StrokeAlignment.Center,
                "strokeStyleAlignOutside" => StrokeAlignment.Outside,
                _ => StrokeAlignment.Inside,
            },
            Dashes = d.List("strokeStyleLineDashSet")?.Select(v => v switch
            {
                UnitFloatValue u => u.Value,
                DoubleValue x => x.Value,
                _ => 0,
            }).ToArray() ?? [],
            BlendMode = PsdEffects.BlendModeOf(d.Enum("strokeStyleBlendMode")),
            Opacity = (float)((d.Number("strokeStyleOpacity") ?? 100) / 100.0),
            Content = content,
            SourceData = d,
        };
    }

    /// <summary>The 'vstk' descriptor, patched onto the source's so unmodelled keys stay; a new one in Photoshop CC's key order.</summary>
    private static Descriptor EncodeStroke(ShapeStroke s, bool fillEnabled, Descriptor? source, double resolution)
    {
        var d = new Patch(source ?? Obj("strokeStyle"));
        double res = source?.Number("strokeStyleResolution") ?? resolution;
        string CapName(LineCap c) => c switch { LineCap.Round => "strokeStyleRoundCap", LineCap.Square => "strokeStyleSquareCap", _ => "strokeStyleButtCap" };
        string JoinName(LineJoin j) => j switch { LineJoin.Round => "strokeStyleRoundJoin", LineJoin.Bevel => "strokeStyleBevelJoin", _ => "strokeStyleMiterJoin" };
        string AlignName(StrokeAlignment a) => a switch
        {
            StrokeAlignment.Center => "strokeStyleAlignCenter",
            StrokeAlignment.Outside => "strokeStyleAlignOutside",
            _ => "strokeStyleAlignInside",
        };
        if (!d.Has("strokeStyleVersion")) d.Set("strokeStyleVersion", new IntegerValue(2), null);
        d.Set("strokeEnabled", new BoolValue(s.Enabled), null);
        d.Set("fillEnabled", new BoolValue(fillEnabled), null);
        d.Set("strokeStyleLineWidth", new UnitFloatValue("#Pnt", Math.Round(s.Width * 72.0 / res, 6)), null);
        d.Set("strokeStyleLineDashOffset", new UnitFloatValue("#Pnt", s.DashOffset), null);
        d.Set("strokeStyleMiterLimit", new DoubleValue(s.MiterLimit), null);
        d.Set("strokeStyleLineCapType", new EnumValue("strokeStyleLineCapType", CapName(s.Cap)), null);
        d.Set("strokeStyleLineJoinType", new EnumValue("strokeStyleLineJoinType", JoinName(s.Join)), null);
        d.Set("strokeStyleLineAlignment", new EnumValue("strokeStyleLineAlignment", AlignName(s.Alignment)), null);
        if (!d.Has("strokeStyleScaleLock")) d.Set("strokeStyleScaleLock", new BoolValue(false), null);
        if (!d.Has("strokeStyleStrokeAdjust")) d.Set("strokeStyleStrokeAdjust", new BoolValue(false), null);
        d.Set("strokeStyleLineDashSet", new ListValue(s.Dashes.Select(v => (DescriptorValue)new UnitFloatValue("#Nne", v)).ToList()), null);
        d.Set("strokeStyleBlendMode", new EnumValue("BlnM", PsdEffects.DescriptorKeyOf(s.BlendMode)), null);
        d.Set("strokeStyleOpacity", new UnitFloatValue("#Prc", Math.Round(s.Opacity * 100.0, 3)), null);
        var oldContent = source?.Object("strokeStyleContent");
        string classId = s.Content switch
        {
            ShapeContent.GradientContent => "gradientLayer",
            ShapeContent.PatternContent => "patternLayer",
            _ => "solidColorLayer",
        };
        var (_, content) = EncodeContent(s.Content, oldContent?.ClassId == classId ? oldContent : null);
        d.Set("strokeStyleContent", new ObjectValue(new Descriptor { Name = content.Name, ClassId = classId, Items = content.Items }), null);
        if (!d.Has("strokeStyleResolution")) d.Set("strokeStyleResolution", new DoubleValue(resolution), null);
        return d.Build();
    }

    // ---- Live shapes -------------------------------------------------------------------------------------

    private static List<LiveShape> LiveShapesOf(byte[] vogk)
    {
        var d = DescriptorReader.ReadVersioned(vogk, 4);
        var list = new List<LiveShape>();
        foreach (var v in d.List("keyDescriptorList") ?? [])
        {
            if (v is not ObjectValue { Value: var e }) continue;
            var kind = (int)(e.Number("keyOriginType") ?? 0) switch
            {
                1 => LiveShapeKind.Rectangle,
                2 => LiveShapeKind.RoundedRectangle,
                5 => LiveShapeKind.Ellipse,
                _ => LiveShapeKind.Other,
            };
            var box = e.Object("keyOriginShapeBBox");
            var radii = e.Object("keyOriginRRectRadii");
            list.Add(new LiveShape(kind, box?.Number("Left") ?? 0, box?.Number("Top ") ?? 0, box?.Number("Rght") ?? 0, box?.Number("Btom") ?? 0)
            {
                Radii = radii is null ? default : new CornerRadii(radii.Number("topLeft") ?? 0, radii.Number("topRight") ?? 0,
                    radii.Number("bottomRight") ?? 0, radii.Number("bottomLeft") ?? 0),
                Invalidated = e.Bool("keyShapeInvalidated") ?? false,
                Index = (int)(e.Number("keyOriginIndex") ?? 0),
                SourceData = e,
            });
        }
        return list;
    }

    private static bool SameShapes(IReadOnlyList<LiveShape> a, IReadOnlyList<LiveShape> b) =>
        a.Count == b.Count && a.Zip(b).All(p => p.First with { SourceData = null } == p.Second with { SourceData = null });

    /// <summary>
    /// A 'vogk' block. Entries of kinds Photoshop records are written as it does; entries read from the file and not
    /// changed keep their descriptors; shapes Photoshop has no origination for (polygons, lines, custom shapes) are
    /// written invalidated, which Photoshop shows as an ordinary path.
    /// </summary>
    private static byte[] EncodeOrigination(IReadOnlyList<LiveShape> shapes, byte[]? source, double resolution)
    {
        var old = source is { Length: > 8 } ? LiveShapesOf(source) : [];
        var items = new List<DescriptorValue>();
        foreach (var s in shapes)
        {
            if (s.SourceData is Descriptor sd && old.FirstOrDefault(o => ReferenceEquals(o.SourceData, sd) || o.SourceData is Descriptor od && SameDescriptor(od, sd)) is { } was
                && was with { SourceData = null } == s with { SourceData = null })
            {
                items.Add(new ObjectValue(sd));
                continue;
            }
            var e = new Patch(s.SourceData as Descriptor ?? Obj("null"));
            int type = s.Kind switch
            {
                LiveShapeKind.Rectangle => 1,
                LiveShapeKind.RoundedRectangle => 2,
                LiveShapeKind.Ellipse => 5,
                _ => 0,
            };
            if (type == 0 || s.Invalidated)
            {
                e.Set("keyShapeInvalidated", new BoolValue(true), before: "keyOriginIndex");
                if (!e.Has("keyOriginIndex")) e.Set("keyOriginIndex", new IntegerValue(s.Index), null);
                items.Add(new ObjectValue(e.Build()));
                continue;
            }
            e.Remove("keyShapeInvalidated");
            e.Set("keyOriginType", new IntegerValue(type), before: null);
            if (s.Kind == LiveShapeKind.RoundedRectangle)
                e.Set("keyOriginRRectRadii", new ObjectValue(Obj("radii", ("unitValueQuadVersion", new IntegerValue(1)),
                    ("topRight", Px(s.Radii.TopRight)), ("topLeft", Px(s.Radii.TopLeft)), ("bottomLeft", Px(s.Radii.BottomLeft)), ("bottomRight", Px(s.Radii.BottomRight)))), before: null);
            else e.Remove("keyOriginRRectRadii");
            e.Set("keyOriginShapeBBox", new ObjectValue(Obj("unitRect", ("unitValueQuadVersion", new IntegerValue(1)),
                ("Top ", Px(s.Top)), ("Left", Px(s.Left)), ("Btom", Px(s.Bottom)), ("Rght", Px(s.Right)))), before: null);
            e.Set("keyOriginIndex", new IntegerValue(s.Index), null);
            if (!e.Has("keyOriginResolution")) e.Set("keyOriginResolution", new DoubleValue(resolution), null);
            items.Add(new ObjectValue(e.Build()));
        }
        var top = source is { Length: > 8 } ? DescriptorReader.ReadVersioned(source, 4) : Obj("null");
        var t = new Patch(top);
        t.Set("keyDescriptorList", new ListValue(items), null);
        var version = source is { Length: >= 4 } ? source.AsSpan(0, 4).ToArray() : [0, 0, 0, 1];
        return [.. version, .. DescriptorWriter.WriteVersioned(t.Build())];

        static DescriptorValue Px(double v) => new UnitFloatValue("#Pxl", v);
    }

    private static bool SameDescriptor(Descriptor a, Descriptor b) =>
        DescriptorWriter.Write(a).AsSpan().SequenceEqual(DescriptorWriter.Write(b));

    // ---- Helpers -----------------------------------------------------------------------------------------

    private static bool Close(RgbColor a, RgbColor b) =>
        Math.Abs(a.R - b.R) < 0.5f / 255f && Math.Abs(a.G - b.G) < 0.5f / 255f && Math.Abs(a.B - b.B) < 0.5f / 255f;

    private static Descriptor Obj(string classId, params (string Key, DescriptorValue Value)[] items) =>
        new() { Name = "", ClassId = classId, Items = items.Select(i => new KeyValuePair<string, DescriptorValue>(i.Key, i.Value)).ToList() };

    /// <summary>Edits a descriptor's items in place of their old positions (new keys at the end, or before a given key).</summary>
    private sealed class Patch(Descriptor from)
    {
        private readonly List<KeyValuePair<string, DescriptorValue>> _items = [.. from.Items];

        public bool Has(string key) => _items.Any(kv => kv.Key == key);

        public DescriptorValue? Get(string key) => _items.FirstOrDefault(kv => kv.Key == key).Value;

        public void Set(string key, DescriptorValue value, string? before)
        {
            int i = _items.FindIndex(kv => kv.Key == key);
            if (i >= 0)
            {
                _items[i] = new(key, value);
                return;
            }
            int at = before is null ? -1 : _items.FindIndex(kv => kv.Key == before);
            if (at >= 0) _items.Insert(at, new(key, value));
            else _items.Add(new(key, value));
        }

        public void Remove(string key) => _items.RemoveAll(kv => kv.Key == key);

        public Descriptor Build() => new() { Name = from.Name, ClassId = from.ClassId, Items = _items };
    }
}
