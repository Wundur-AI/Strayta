using System.Buffers.Binary;

namespace Strayta.Psd;

/// <summary>
/// Image resource 1005 (ResolutionInfo): horizontal and vertical resolution as 16.16 fixed-point pixels per inch,
/// each followed by the unit Photoshop displays it in (pixels per inch or per centimeter) and the unit for the
/// width or height (inches, centimeters, points, picas, columns). 16 bytes.
/// </summary>
internal static class PsdResolution
{
    public const int ResourceId = 1005;

    /// <summary>The horizontal resolution in pixels per inch, or null if the block is missing or unusable.</summary>
    public static double? Read(byte[]? data)
    {
        if (data is not { Length: >= 16 }) return null;
        double ppi = BinaryPrimitives.ReadInt32BigEndian(data) / 65536.0;
        return double.IsFinite(ppi) && ppi > 0 ? ppi : null;
    }

    /// <summary>Encodes <paramref name="ppi"/> for both axes, keeping the display units of <paramref name="existing"/>.</summary>
    public static byte[] Write(double ppi, byte[]? existing)
    {
        var data = existing is { Length: >= 16 } ? (byte[])existing.Clone() : [0, 0, 0, 0, 0, 1, 0, 1, 0, 0, 0, 0, 0, 1, 0, 1];
        int value = (int)Math.Clamp(Math.Round(ppi * 65536.0), 1, int.MaxValue);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(0), value);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(8), value);
        return data;
    }
}
