using System.Buffers.Binary;
using Strayta.Core;

namespace Strayta.Psd;

/// <summary>Parses the tagged blocks that describe adjustment layers.</summary>
public static partial class PsdAdjustments
{
    private static readonly Dictionary<string, string> Names = new()
    {
        ["levl"] = "Levels", ["curv"] = "Curves", ["hue2"] = "Hue/Saturation", ["hue "] = "Hue/Saturation (legacy)",
        ["brit"] = "Brightness/Contrast", ["nvrt"] = "Invert", ["thrs"] = "Threshold", ["post"] = "Posterize",
        ["blnc"] = "Color Balance", ["vibA"] = "Vibrance", ["mixr"] = "Channel Mixer", ["selc"] = "Selective Color",
        ["grdm"] = "Gradient Map", ["phfl"] = "Photo Filter", ["expA"] = "Exposure", ["blwh"] = "Black & White",
        ["clrL"] = "Color Lookup",
    };

    /// <summary>Returns the adjustment block of a record, its display name, and the parsed adjustment (null if unsupported).</summary>
    public static (string Kind, Adjustment? Adjustment)? Read(PsdLayerRecord record)
    {
        foreach (var block in record.Blocks)
        {
            if (!Names.TryGetValue(block.Key, out var name)) continue;
            Adjustment? adjustment = null;
            try
            {
                adjustment = block.Data is { } d ? Parse(block.Key, d) : null;
            }
            catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException or PsdFormatException)
            {
                // Truncated or unexpected data: report as unsupported rather than failing the file.
            }
            return (name, adjustment);
        }
        return null;
    }

    private static Adjustment? Parse(string key, byte[] d) => key switch
    {
        "levl" => ParseLevels(d),
        "curv" => ParseCurves(d),
        "hue2" => ParseHueSaturation(d),
        "brit" => new BrightnessContrastAdjustment(I16(d, 0), I16(d, 2)),
        "nvrt" => new InvertAdjustment(),
        "thrs" => new ThresholdAdjustment(Math.Clamp((int)I16(d, 0), 1, 255)),
        "post" => new PosterizeAdjustment(Math.Clamp((int)I16(d, 0), 2, 255)),
        _ => ParseExtended(key, d),
    };

    private static short I16(byte[] d, int offset) => BinaryPrimitives.ReadInt16BigEndian(d.AsSpan(offset));

    /// <summary>Version 2, then up to 29 records of input black/white, output black/white, gamma × 100.</summary>
    private static LevelsAdjustment? ParseLevels(byte[] d)
    {
        if (I16(d, 0) != 2) return null;
        int count = Math.Min(29, (d.Length - 2) / 10);
        var records = new List<LevelsChannel>(count);
        for (int i = 0; i < count; i++)
        {
            int o = 2 + i * 10;
            int gamma = I16(d, o + 8);
            records.Add(new LevelsChannel(I16(d, o), I16(d, o + 2), I16(d, o + 4), I16(d, o + 6), Math.Clamp(gamma, 10, 999) / 100f));
        }
        return records.Count == 0 ? null : new LevelsAdjustment(records[0], records.Skip(1).ToArray());
    }

    /// <summary>
    /// Filler byte, version, a bitmap of which channels have curves (bit 0 = composite), then for each
    /// a point count and (output, input) pairs.
    /// </summary>
    private static CurvesAdjustment? ParseCurves(byte[] d)
    {
        int version = I16(d, 1);
        if (version is not (1 or 4)) return null;
        uint bitmap = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(3));
        int o = 7;
        var curves = new Dictionary<int, IReadOnlyList<CurvePoint>>();
        for (int bit = 0; bit < 32; bit++)
        {
            if ((bitmap & (1u << bit)) == 0) continue;
            int n = I16(d, o);
            o += 2;
            var points = new List<CurvePoint>(n);
            for (int i = 0; i < n; i++, o += 4)
                points.Add(new CurvePoint(Input: I16(d, o + 2), Output: I16(d, o)));
            curves[bit] = points.OrderBy(p => p.Input).ToArray();
        }

        int channelCount = curves.Count == 0 ? 0 : curves.Keys.Max();
        var channels = Enumerable.Range(1, channelCount).Select(c => curves.GetValueOrDefault(c)).ToArray();
        return new CurvesAdjustment(curves.GetValueOrDefault(0), channels);
    }

    /// <summary>
    /// Version 2, colorize flag, padding, colorize H/S/L, master H/S/L, then six color ranges of
    /// four range bounds plus H/S/L each.
    /// </summary>
    private static HueSaturationAdjustment? ParseHueSaturation(byte[] d)
    {
        if (I16(d, 0) != 2) return null;
        bool colorize = d[2] != 0;
        bool rangeEdits = false;
        for (int r = 0; r < 6; r++)
        {
            int o = 16 + r * 14 + 8;
            if (o + 6 <= d.Length && (I16(d, o) != 0 || I16(d, o + 2) != 0 || I16(d, o + 4) != 0))
                rangeEdits = true;
        }
        return new HueSaturationAdjustment(
            I16(d, 10), I16(d, 12), I16(d, 14),
            colorize, I16(d, 4), I16(d, 6), I16(d, 8),
            rangeEdits);
    }
}
