using System.Buffers.Binary;
using System.Text;
using Strayta.Core;
using Strayta.Core.Painting;
using Strayta.Psd.Descriptors;

namespace Strayta.Psd;

/// <summary>Encoders for the adjustment blocks read by <c>PsdAdjustments.Extended</c>; see there for the layouts.</summary>
public static partial class PsdAdjustmentWriter
{
    private static string ExtendedKeyOf(Adjustment adjustment) => adjustment switch
    {
        ExposureAdjustment => "expA",
        VibranceAdjustment => "vibA",
        ColorBalanceAdjustment => "blnc",
        BlackWhiteAdjustment => "blwh",
        PhotoFilterAdjustment => "phfl",
        ChannelMixerAdjustment => "mixr",
        SelectiveColorAdjustment => "selc",
        GradientMapAdjustment => "grdm",
        ColorLookupAdjustment => "clrL",
        _ => throw new NotSupportedException($"Saving {adjustment.GetType().Name} is not supported yet."),
    };

    private static byte[] EncodeExtended(Adjustment adjustment, byte[]? original) => adjustment switch
    {
        ExposureAdjustment e => Exposure(e, original),
        VibranceAdjustment v => Vibrance(v, original),
        ColorBalanceAdjustment c => ColorBalance(c, original),
        BlackWhiteAdjustment b => BlackWhite(b, original),
        PhotoFilterAdjustment p => PhotoFilter(p),
        ChannelMixerAdjustment m => ChannelMixer(m, original),
        SelectiveColorAdjustment s => SelectiveColor(s, original),
        GradientMapAdjustment g => GradientMap(g, original),
        ColorLookupAdjustment l => ColorLookup(l, original),
        _ => throw new NotSupportedException($"Saving {adjustment.GetType().Name} is not supported yet."),
    };

    /// <summary>A copy of <paramref name="original"/> when it is at least <paramref name="length"/> bytes, else a new zeroed block.</summary>
    private static byte[] Base(byte[]? original, int length) =>
        original is not null && original.Length >= length ? (byte[])original.Clone() : new byte[length];

    private static void PutFloat(byte[] o, int at, float v) => BinaryPrimitives.WriteSingleBigEndian(o.AsSpan(at), v);
    private static void PutInt(byte[] o, int at, int v) => BinaryPrimitives.WriteInt32BigEndian(o.AsSpan(at), v);

    // ---- Exposure --------------------------------------------------------------------------------

    private static byte[] Exposure(ExposureAdjustment e, byte[]? original)
    {
        var o = Base(original, 14);
        Put(o, 0, 1);
        PutFloat(o, 2, Math.Clamp(e.Exposure, -20f, 20f));
        PutFloat(o, 6, Math.Clamp(e.Offset, -0.5f, 0.5f));
        PutFloat(o, 10, Math.Clamp(e.Gamma, 0.01f, 9.99f));
        return o;
    }

    // ---- Descriptor-based blocks -----------------------------------------------------------------

    /// <summary>
    /// The descriptor read from <paramref name="original"/> (at <paramref name="offset"/>) with <paramref name="items"/>
    /// set: existing keys keep their place, new ones are appended, and keys not mentioned stay as they were.
    /// </summary>
    private static Descriptor Merge(byte[]? original, int offset, IEnumerable<(string Key, DescriptorValue? Value)> items)
    {
        Descriptor d;
        try
        {
            d = original is { Length: > 0 } ? DescriptorReader.ReadVersioned(original, offset) : new Descriptor { ClassId = "null" };
        }
        catch (PsdFormatException)
        {
            d = new Descriptor { ClassId = "null" };
        }
        var list = d.Items.ToList();
        foreach (var (key, value) in items)
        {
            int i = list.FindIndex(kv => kv.Key == key);
            if (value is null)
            {
                if (i >= 0) list.RemoveAt(i);
            }
            else if (i >= 0) list[i] = new(key, value);
            else list.Add(new(key, value));
        }
        return new Descriptor { Name = d.Name, ClassId = d.ClassId, Items = list };
    }

    private static byte[] Vibrance(VibranceAdjustment v, byte[]? original) => DescriptorWriter.WriteVersioned(Merge(original, 0,
    [
        ("vibrance", new IntegerValue(Math.Clamp(v.Vibrance, -100, 100))),
        ("Strt", new IntegerValue(Math.Clamp(v.Saturation, -100, 100))),
    ]));

    private static byte[] BlackWhite(BlackWhiteAdjustment b, byte[]? original) => DescriptorWriter.WriteVersioned(Merge(original, 0,
    [
        ("Rd  ", new IntegerValue(Math.Clamp(b.Reds, -200, 300))),
        ("Yllw", new IntegerValue(Math.Clamp(b.Yellows, -200, 300))),
        ("Grn ", new IntegerValue(Math.Clamp(b.Greens, -200, 300))),
        ("Cyn ", new IntegerValue(Math.Clamp(b.Cyans, -200, 300))),
        ("Bl  ", new IntegerValue(Math.Clamp(b.Blues, -200, 300))),
        ("Mgnt", new IntegerValue(Math.Clamp(b.Magentas, -200, 300))),
        ("useTint", new BoolValue(b.Tint)),
        ("tintColor", new ObjectValue(RgbDescriptor(b.TintColor))),
    ]));

    private static Descriptor RgbDescriptor(RgbColor c) => new()
    {
        ClassId = "RGBC",
        Items =
        [
            new("Rd  ", new DoubleValue(Math.Round(Math.Clamp(c.R, 0f, 1f) * 255.0, 3))),
            new("Grn ", new DoubleValue(Math.Round(Math.Clamp(c.G, 0f, 1f) * 255.0, 3))),
            new("Bl  ", new DoubleValue(Math.Round(Math.Clamp(c.B, 0f, 1f) * 255.0, 3))),
        ],
    };

    // ---- Color Balance ---------------------------------------------------------------------------

    private static byte[] ColorBalance(ColorBalanceAdjustment c, byte[]? original)
    {
        var o = Base(original, 20);
        void Tone(int at, ColorBalanceTone t)
        {
            Put(o, at, Math.Clamp(t.CyanRed, -100, 100));
            Put(o, at + 2, Math.Clamp(t.MagentaGreen, -100, 100));
            Put(o, at + 4, Math.Clamp(t.YellowBlue, -100, 100));
        }
        Tone(0, c.Shadows);
        Tone(6, c.Midtones);
        Tone(12, c.Highlights);
        o[18] = c.PreserveLuminosity ? (byte)1 : (byte)0;
        return o;
    }

    // ---- Photo Filter ----------------------------------------------------------------------------

    /// <summary>Always version 2 (an RGB color structure), which Photoshop reads as well as its own version 3.</summary>
    private static byte[] PhotoFilter(PhotoFilterAdjustment p)
    {
        var o = new byte[18];
        Put(o, 0, 2);
        ColorStructure(o, 2, p.Color);
        PutInt(o, 12, Math.Clamp(p.Density, 1, 100));
        o[16] = p.PreserveLuminosity ? (byte)1 : (byte)0;
        return o;
    }

    /// <summary>An RGB color structure: space 0 and 16-bit components.</summary>
    private static void ColorStructure(byte[] o, int at, RgbColor c)
    {
        Put(o, at, 0);
        static ushort C(float v) => (ushort)Math.Round(Math.Clamp(v, 0f, 1f) * 65535f);
        BinaryPrimitives.WriteUInt16BigEndian(o.AsSpan(at + 2), C(c.R));
        BinaryPrimitives.WriteUInt16BigEndian(o.AsSpan(at + 4), C(c.G));
        BinaryPrimitives.WriteUInt16BigEndian(o.AsSpan(at + 6), C(c.B));
        BinaryPrimitives.WriteUInt16BigEndian(o.AsSpan(at + 8), 0);
    }

    // ---- Channel Mixer ---------------------------------------------------------------------------

    private static byte[] ChannelMixer(ChannelMixerAdjustment m, byte[]? original)
    {
        var o = Base(original, 4 + 4 * 10);
        Put(o, 0, 1);
        Put(o, 2, m.Monochrome ? 1 : 0);
        void Record(int i, MixerChannel c)
        {
            int at = 4 + i * 10;
            if (at + 10 > o.Length) return;
            Put(o, at, Math.Clamp(c.Red, -200, 200));
            Put(o, at + 2, Math.Clamp(c.Green, -200, 200));
            Put(o, at + 4, Math.Clamp(c.Blue, -200, 200));
            Put(o, at + 6, 0);
            Put(o, at + 8, Math.Clamp(c.Constant, -200, 200));
        }
        Record(0, m.Monochrome ? m.Gray : m.Red);
        Record(1, m.Green);
        Record(2, m.Blue);
        if (original is null) Record(3, default);
        return o;
    }

    // ---- Selective Color -------------------------------------------------------------------------

    private static byte[] SelectiveColor(SelectiveColorAdjustment s, byte[]? original)
    {
        var o = Base(original, 4 + 10 * 8);
        Put(o, 0, 1);
        Put(o, 2, s.Absolute ? 1 : 0);
        for (int i = 0; i < 9; i++)
        {
            var v = s[(SelectiveColorRange)i];
            int at = 4 + (i + 1) * 8;
            Put(o, at, Math.Clamp(v.Cyan, -100, 100));
            Put(o, at + 2, Math.Clamp(v.Magenta, -100, 100));
            Put(o, at + 4, Math.Clamp(v.Yellow, -100, 100));
            Put(o, at + 6, Math.Clamp(v.Black, -100, 100));
        }
        return o;
    }

    // ---- Gradient Map ----------------------------------------------------------------------------

    /// <summary>
    /// Version 1 for Classic, version 3 with the method key otherwise; 20-byte color stops with the stop type; the
    /// noise settings after the stops are kept from <paramref name="original"/> (only the smoothness is written).
    /// </summary>
    private static byte[] GradientMap(GradientMapAdjustment g, byte[]? original)
    {
        PsdAdjustments.GradientMapLayout? layout = null;
        if (original is not null)
        {
            try { PsdAdjustments.ReadGradientMap(original, out layout); }
            catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException or ArgumentException) { layout = null; }
        }
        int version = layout?.Version == 3 || g.Method != GradientMethod.Classic ? 3 : 1;
        var gradient = g.Gradient.Sorted();

        var ms = new MemoryStream();
        void W16(int v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteInt16BigEndian(b, (short)v); ms.Write(b); }
        void W32(int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); ms.Write(b); }

        W16(version);
        ms.WriteByte(g.Reverse ? (byte)1 : (byte)0);
        ms.WriteByte(g.Dither ? (byte)1 : (byte)0);
        if (version == 3)
            ms.Write(Encoding.ASCII.GetBytes(PsdAdjustments.MethodKeys.First(m => m.Method == g.Method).Key));
        W32(gradient.Name.Length + 1);
        ms.Write(Encoding.BigEndianUnicode.GetBytes(gradient.Name));
        W16(0);

        var colorMids = PsdEffects.ToSegmentEnd(gradient.Colors.Select(c => c.Midpoint).ToList());
        W16(gradient.Colors.Count);
        var stop = new byte[20];
        for (int i = 0; i < gradient.Colors.Count; i++)
        {
            var c = gradient.Colors[i];
            Array.Clear(stop);
            PutInt(stop, 0, (int)Math.Round(Math.Clamp(c.Location, 0f, 1f) * 4096));
            PutInt(stop, 4, (int)Math.Round(Math.Clamp(colorMids[i], 0f, 1f) * 100));
            ColorStructure(stop, 8, c.Color);
            Put(stop, 18, c.Kind switch { GradientStopKind.Foreground => 1, GradientStopKind.Background => 2, _ => 0 });
            ms.Write(stop);
        }
        var opacityMids = PsdEffects.ToSegmentEnd(gradient.Opacities.Select(c => c.Midpoint).ToList());
        W16(gradient.Opacities.Count);
        for (int i = 0; i < gradient.Opacities.Count; i++)
        {
            var t = gradient.Opacities[i];
            W32((int)Math.Round(Math.Clamp(t.Location, 0f, 1f) * 4096));
            W32((int)Math.Round(Math.Clamp(opacityMids[i], 0f, 1f) * 100));
            W16((int)Math.Round(Math.Clamp(t.Opacity, 0f, 1f) * 255));
        }

        int smoothness = (int)Math.Round(Math.Clamp(gradient.Smoothness, 0f, 1f) * 4096);
        if (layout is not null && original is not null)
        {
            var tail = original.AsSpan(layout.TailOffset).ToArray();
            BinaryPrimitives.WriteInt16BigEndian(tail.AsSpan(2), (short)smoothness);
            ms.Write(tail);
        }
        else
        {
            W16(2);              // expansion
            W16(smoothness);     // interpolation
            W16(32);             // length
            W16(0);              // mode: stops, not noise
            W32(0);              // random seed
            W16(0);              // show transparency
            W16(0);              // use vector color
            W32(2048);           // roughness
            W16(3);              // color model (RGB)
            for (int i = 0; i < 4; i++) W16(0);      // minimum color
            for (int i = 0; i < 4; i++) W16(100);    // maximum color
            W16(0);              // dummy
        }
        return ms.ToArray();
    }

    // ---- Color Lookup ----------------------------------------------------------------------------

    private static byte[] ColorLookup(ColorLookupAdjustment l, byte[]? original)
    {
        bool lut = l.Kind == ColorLookupKind.Lut3D;
        var items = new List<(string, DescriptorValue?)>
        {
            ("lookupType", new EnumValue("colorLookupType", l.Kind switch
            {
                ColorLookupKind.AbstractProfile => "abstractProfile",
                ColorLookupKind.DeviceLinkProfile => "deviceLinkProfile",
                _ => "3DLUT",
            })),
            ("Nm  ", new TextValue(l.Name)),
            ("Dthr", new BoolValue(l.Dither)),
        };
        if (lut)
        {
            // An edited LUT invalidates the profile Photoshop derived from the previous one.
            bool sameData = original is not null && PsdAdjustments.ReadColorLookupData(original) is { } old && old.AsSpan().SequenceEqual(l.Data);
            if (!sameData) items.Add(("profile", null));
            items.Add(("LUTFormat", new EnumValue("LUTFormatType", "LUTFormat" + l.Format)));
            items.Add(("dataOrder", new EnumValue("colorLookupOrder", "rgbOrder")));
            items.Add(("tableOrder", new EnumValue("colorLookupOrder", "bgrOrder")));
            items.Add(("LUT3DFileData", new RawValue("tdta", l.Data)));
            items.Add(("LUT3DFileName", new TextValue(l.Name)));
        }
        else items.Add(("profile", new RawValue("tdta", l.Data)));

        var body = DescriptorWriter.WriteVersioned(Merge(original, 2, items));
        var o = new byte[2 + body.Length];
        Put(o, 0, 1);
        body.CopyTo(o, 2);
        return o;
    }
}
