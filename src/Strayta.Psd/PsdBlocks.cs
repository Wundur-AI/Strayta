using System.Buffers.Binary;
using System.Text;
using Strayta.Core;

namespace Strayta.Psd;

/// <summary>Decoders for the tagged blocks the converter understands.</summary>
public static class PsdBlocks
{
    public static string? ReadUnicodeName(TaggedBlock? block)
    {
        if (block?.Data is not { Length: >= 4 } d) return null;
        long chars = BinaryPrimitives.ReadUInt32BigEndian(d);
        int bytes = (int)Math.Min(chars * 2, d.Length - 4);
        return Encoding.BigEndianUnicode.GetString(d, 4, bytes & ~1).TrimEnd('\0');
    }

    public static (PsdSectionType Type, string? BlendModeKey) ReadSection(TaggedBlock? block)
    {
        if (block?.Data is not { Length: >= 4 } d) return (PsdSectionType.None, null);
        var type = (PsdSectionType)BinaryPrimitives.ReadUInt32BigEndian(d);
        string? key = d.Length >= 12 ? Encoding.ASCII.GetString(d, 8, 4) : null;
        return (type, key);
    }

    /// <summary>Fill opacity from the 'iOpa' block, 0..255.</summary>
    public static byte? ReadFillOpacity(TaggedBlock? block) =>
        block?.Data is { Length: >= 1 } d ? d[0] : null;

    private static readonly Dictionary<string, BlendMode> BlendKeys = new()
    {
        ["pass"] = BlendMode.PassThrough,
        ["norm"] = BlendMode.Normal,
        ["diss"] = BlendMode.Dissolve,
        ["dark"] = BlendMode.Darken,
        ["mul "] = BlendMode.Multiply,
        ["idiv"] = BlendMode.ColorBurn,
        ["lbrn"] = BlendMode.LinearBurn,
        ["dkCl"] = BlendMode.DarkerColor,
        ["lite"] = BlendMode.Lighten,
        ["scrn"] = BlendMode.Screen,
        ["div "] = BlendMode.ColorDodge,
        ["lddg"] = BlendMode.LinearDodge,
        ["lgCl"] = BlendMode.LighterColor,
        ["over"] = BlendMode.Overlay,
        ["sLit"] = BlendMode.SoftLight,
        ["hLit"] = BlendMode.HardLight,
        ["vLit"] = BlendMode.VividLight,
        ["lLit"] = BlendMode.LinearLight,
        ["pLit"] = BlendMode.PinLight,
        ["hMix"] = BlendMode.HardMix,
        ["diff"] = BlendMode.Difference,
        ["smud"] = BlendMode.Exclusion,
        ["fsub"] = BlendMode.Subtract,
        ["fdiv"] = BlendMode.Divide,
        ["hue "] = BlendMode.Hue,
        ["sat "] = BlendMode.Saturation,
        ["colr"] = BlendMode.Color,
        ["lum "] = BlendMode.Luminosity,
    };

    public static bool TryMapBlendMode(string key, out BlendMode mode) => BlendKeys.TryGetValue(key, out mode);

    public static string BlendKeyOf(BlendMode mode) => BlendKeys.First(kv => kv.Value == mode).Key;
}
