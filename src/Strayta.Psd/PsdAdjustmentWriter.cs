using System.Buffers.Binary;
using System.Text;
using Strayta.Core;

namespace Strayta.Psd;

/// <summary>
/// Encodes adjustment settings as the tagged blocks Photoshop stores for adjustment layers; the inverse of
/// <see cref="PsdAdjustments"/>. Layouts follow Adobe's file format specification and match what Photoshop
/// itself writes (e.g. Levels' 29 legacy records plus the 'Lvls' extension, Curves' 'Crv ' extension).
/// </summary>
public static class PsdAdjustmentWriter
{
    private const int LegacyLevelsRecords = 29;
    private const int TotalLevelsRecords = 62; // what Photoshop writes: 29 legacy + 33 in the 'Lvls' extension

    /// <summary>Photoshop's default Hue/Saturation color ranges (reds, yellows, greens, cyans, blues, magentas), in degrees.</summary>
    private static readonly short[][] DefaultHueRanges =
    [
        [315, 345, 15, 45], [15, 45, 75, 105], [75, 105, 135, 165],
        [135, 165, 195, 225], [195, 225, 255, 285], [255, 285, 315, 345],
    ];

    /// <summary>The tagged block key for an adjustment, e.g. "levl".</summary>
    public static string KeyOf(Adjustment adjustment) => adjustment switch
    {
        LevelsAdjustment => "levl",
        CurvesAdjustment => "curv",
        HueSaturationAdjustment => "hue2",
        BrightnessContrastAdjustment => "brit",
        InvertAdjustment => "nvrt",
        ThresholdAdjustment => "thrs",
        PosterizeAdjustment => "post",
        _ => throw new NotSupportedException($"Saving {adjustment.GetType().Name} is not supported yet."),
    };

    /// <summary>
    /// Encodes <paramref name="adjustment"/>. When <paramref name="original"/> (the block read from the file) is
    /// given, settings this library does not model — Hue/Saturation color ranges, extra Levels records, the
    /// Brightness/Contrast mean — are kept from it.
    /// </summary>
    public static byte[] Encode(Adjustment adjustment, byte[]? original = null) => adjustment switch
    {
        LevelsAdjustment l => Levels(l, original),
        CurvesAdjustment c => Curves(c),
        HueSaturationAdjustment h => HueSaturation(h, original),
        BrightnessContrastAdjustment b => Patch(original, 8, [b.Brightness, b.Contrast], fresh: [b.Brightness, b.Contrast, 127, 0]),
        InvertAdjustment => [],
        ThresholdAdjustment t => Patch(original, 4, [Math.Clamp(t.Level, 1, 255)], fresh: [Math.Clamp(t.Level, 1, 255), 0]),
        PosterizeAdjustment p => Patch(original, 4, [Math.Clamp(p.Levels, 2, 255)], fresh: [Math.Clamp(p.Levels, 2, 255), 0]),
        _ => throw new NotSupportedException($"Saving {adjustment.GetType().Name} is not supported yet."),
    };

    /// <summary>
    /// Replaces the adjustment block in <paramref name="blocks"/> (copied from <paramref name="source"/>) when
    /// the layer's settings were edited. Unedited layers keep their original bytes, so they round-trip exactly.
    /// </summary>
    internal static void Refresh(AdjustmentLayer layer, PsdLayerRecord source, List<(string Key, byte[] Data)> blocks)
    {
        if (layer.Adjustment is not { } adjustment) return;
        if (PsdAdjustments.Read(source) is var (_, original) && SameSettings(original, adjustment)) return;

        string key = KeyOf(adjustment);
        int index = blocks.FindIndex(b => b.Key == key);
        if (index < 0)
            throw new NotSupportedException($"Adjustment layer \"{layer.Name}\" changed type; saving that is not supported.");
        blocks[index] = (key, Encode(adjustment, blocks[index].Data));
    }

    /// <summary>Value equality for adjustments (records compare their lists by reference, which is not enough here).</summary>
    public static bool SameSettings(Adjustment? a, Adjustment? b) => (a, b) switch
    {
        (null, null) => true,
        (LevelsAdjustment x, LevelsAdjustment y) => x.Master == y.Master && x.Channels.SequenceEqual(y.Channels),
        (CurvesAdjustment x, CurvesAdjustment y) => SameCurve(x.Master, y.Master)
            && x.Channels.Count == y.Channels.Count && x.Channels.Zip(y.Channels).All(p => SameCurve(p.First, p.Second)),
        _ => Equals(a, b),
    };

    private static bool SameCurve(IReadOnlyList<CurvePoint>? a, IReadOnlyList<CurvePoint>? b) =>
        a is null ? b is null : b is not null && a.SequenceEqual(b);

    // ---- Levels ----------------------------------------------------------------------------------

    /// <summary>
    /// Version 2 and 29 records (master, then one per channel) of input black/white, output black/white and
    /// gamma × 100, then 'Lvls', version 3, the total record count and the records beyond 29.
    /// </summary>
    private static byte[] Levels(LevelsAdjustment a, byte[]? original)
    {
        var records = new List<LevelsChannel> { a.Master };
        records.AddRange(a.Channels.Take(LegacyLevelsRecords - 1));

        // Edited files keep their own extension records; only the legacy records are modeled.
        byte[] o = original is { Length: >= 2 + LegacyLevelsRecords * 10 } && Short(original, 0) == 2
            ? (byte[])original.Clone()
            : NewLevels();
        for (int i = 0; i < LegacyLevelsRecords; i++)
            WriteLevels(o, 2 + i * 10, i < records.Count ? records[i] : LevelsChannel.Identity);
        return o;
    }

    private static byte[] NewLevels()
    {
        int extra = TotalLevelsRecords - LegacyLevelsRecords;
        var o = new byte[2 + LegacyLevelsRecords * 10 + 8 + extra * 10 + 2];
        Put(o, 0, 2);
        int tag = 2 + LegacyLevelsRecords * 10;
        Encoding.ASCII.GetBytes("Lvls", o.AsSpan(tag));
        Put(o, tag + 4, 3);
        Put(o, tag + 6, TotalLevelsRecords);
        for (int i = 0; i < extra; i++) WriteLevels(o, tag + 8 + i * 10, LevelsChannel.Identity);
        return o;
    }

    private static void WriteLevels(byte[] o, int at, LevelsChannel l)
    {
        Put(o, at, Math.Clamp(l.InputBlack, 0, 255));
        Put(o, at + 2, Math.Clamp(l.InputWhite, 0, 255));
        Put(o, at + 4, Math.Clamp(l.OutputBlack, 0, 255));
        Put(o, at + 6, Math.Clamp(l.OutputWhite, 0, 255));
        Put(o, at + 8, Math.Clamp((int)MathF.Round(l.Gamma * 100f), 10, 999));
    }

    // ---- Curves ----------------------------------------------------------------------------------

    /// <summary>
    /// A zero byte (points rather than a 256-entry map), version 1, a bitmap of the channels that have curves
    /// (bit 0 = composite), each curve's point count and (output, input) pairs; then the 'Crv ' extension,
    /// version 4, which repeats the curves with explicit channel indexes.
    /// </summary>
    private static byte[] Curves(CurvesAdjustment a)
    {
        // Channel 0 is the composite; Photoshop always stores it.
        var curves = new List<(int Channel, IReadOnlyList<CurvePoint> Points)>
        {
            (0, a.Master is { Count: >= 2 } m ? m : [new(0, 0), new(255, 255)]),
        };
        for (int c = 0; c < a.Channels.Count && c < 31; c++)
            if (a.Channels[c] is { Count: >= 2 } pts) curves.Add((c + 1, pts));

        var ms = new MemoryStream();
        void U16(int v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteInt16BigEndian(b, (short)v); ms.Write(b); }
        void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); ms.Write(b); }
        void Points(IReadOnlyList<CurvePoint> pts)
        {
            var sorted = pts.OrderBy(p => p.Input).Take(19).ToList(); // Photoshop allows at most 19 points
            U16(sorted.Count);
            foreach (var p in sorted)
            {
                U16(Math.Clamp(p.Output, 0, 255));
                U16(Math.Clamp(p.Input, 0, 255));
            }
        }

        ms.WriteByte(0);
        U16(1);
        U32(curves.Aggregate(0u, (bits, c) => bits | 1u << c.Channel));
        foreach (var (_, pts) in curves) Points(pts);

        ms.Write("Crv "u8);
        U16(4);
        U32((uint)curves.Count);
        foreach (var (channel, pts) in curves)
        {
            U16(channel);
            Points(pts);
        }
        return ms.ToArray();
    }

    // ---- Hue/Saturation --------------------------------------------------------------------------

    /// <summary>
    /// Version 2, colorize flag and a padding byte, colorize hue/saturation/lightness, master
    /// hue/saturation/lightness, then six color ranges (four range bounds plus hue/saturation/lightness each).
    /// </summary>
    private static byte[] HueSaturation(HueSaturationAdjustment h, byte[]? original)
    {
        byte[] o;
        if (original is { Length: >= 100 } && Short(original, 0) == 2) o = (byte[])original.Clone();
        else
        {
            o = new byte[100];
            Put(o, 0, 2);
            for (int r = 0; r < 6; r++)
                for (int k = 0; k < 4; k++) Put(o, 16 + r * 14 + k * 2, DefaultHueRanges[r][k]);
        }
        o[2] = h.Colorize ? (byte)1 : (byte)0;
        Put(o, 4, Math.Clamp(h.ColorizeHue, 0, 360));
        Put(o, 6, Math.Clamp(h.ColorizeSaturation, 0, 100));
        Put(o, 8, Math.Clamp(h.ColorizeLightness, -100, 100));
        Put(o, 10, Math.Clamp(h.Hue, -180, 180));
        Put(o, 12, Math.Clamp(h.Saturation, -100, 100));
        Put(o, 14, Math.Clamp(h.Lightness, -100, 100));
        return o;
    }

    // ---- Helpers ---------------------------------------------------------------------------------

    /// <summary>
    /// Writes <paramref name="values"/> as leading shorts over a copy of <paramref name="original"/> (keeping the
    /// rest), or returns <paramref name="fresh"/> as a new block of <paramref name="length"/> bytes.
    /// </summary>
    private static byte[] Patch(byte[]? original, int length, int[] values, int[] fresh)
    {
        byte[] o;
        if (original is not null && original.Length >= values.Length * 2)
        {
            o = (byte[])original.Clone();
            for (int i = 0; i < values.Length; i++) Put(o, i * 2, values[i]);
            return o;
        }
        o = new byte[length];
        for (int i = 0; i < fresh.Length && i * 2 + 1 < length; i++) Put(o, i * 2, fresh[i]);
        return o;
    }

    private static void Put(byte[] o, int at, int v) => BinaryPrimitives.WriteInt16BigEndian(o.AsSpan(at), (short)v);
    private static short Short(byte[] d, int at) => BinaryPrimitives.ReadInt16BigEndian(d.AsSpan(at));
}
