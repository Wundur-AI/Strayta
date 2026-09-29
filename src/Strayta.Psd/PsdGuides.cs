using System.Buffers.Binary;
using Strayta.Core;

namespace Strayta.Psd;

/// <summary>
/// Image resource 1032 (grid and guides): a version (1), the horizontal and vertical grid cycle (Photoshop writes 576,
/// a quarter inch at 72 ppi in 1/32 pixel, and ignores it: the grid itself is an application preference), the guide
/// count, then per guide its location in 1/32 pixel (4 bytes) and its direction (1 byte: 0 vertical, 1 horizontal).
/// </summary>
public static class PsdGuides
{
    public const int ResourceId = 1032;

    private const int HeaderLength = 16, EntryLength = 5, DefaultGridCycle = 576;

    /// <summary>The guides stored in <paramref name="data"/>; empty when the block is missing or too short.</summary>
    public static IReadOnlyList<Guide> Read(byte[]? data)
    {
        if (data is not { Length: >= HeaderLength }) return [];
        int count = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(12));
        var guides = new List<Guide>(Math.Clamp(count, 0, (data.Length - HeaderLength) / EntryLength));
        for (int i = 0, at = HeaderLength; i < count && at + EntryLength <= data.Length; i++, at += EntryLength)
        {
            double location = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(at)) / 32.0;
            guides.Add(new Guide(data[at + 4] != 0 ? GuideOrientation.Horizontal : GuideOrientation.Vertical, location));
        }
        return guides;
    }

    /// <summary>
    /// Encodes <paramref name="guides"/>, keeping the version and grid cycle of <paramref name="existing"/> when there is
    /// one. Positions are rounded to Photoshop's 1/32 pixel.
    /// </summary>
    public static byte[] Write(IReadOnlyList<Guide> guides, byte[]? existing = null)
    {
        ArgumentNullException.ThrowIfNull(guides);
        var data = new byte[HeaderLength + guides.Count * EntryLength];
        if (existing is { Length: >= 12 }) existing.AsSpan(0, 12).CopyTo(data);
        else
        {
            BinaryPrimitives.WriteInt32BigEndian(data, 1);
            BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(4), DefaultGridCycle);
            BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(8), DefaultGridCycle);
        }
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(12), guides.Count);
        for (int i = 0; i < guides.Count; i++)
        {
            int at = HeaderLength + i * EntryLength;
            BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(at), ToFixed(guides[i].Position));
            data[at + 4] = guides[i].IsHorizontal ? (byte)1 : (byte)0;
        }
        return data;
    }

    /// <summary>A position as Photoshop stores it (1/32 pixel), so comparing with a file's guides ignores finer differences.</summary>
    public static double Quantize(double position) => ToFixed(position) / 32.0;

    /// <summary>True when the two lists store the same bytes.</summary>
    public static bool SameAsStored(IReadOnlyList<Guide> stored, IReadOnlyList<Guide> current)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(current);
        if (stored.Count != current.Count) return false;
        for (int i = 0; i < stored.Count; i++)
            if (stored[i].Orientation != current[i].Orientation || ToFixed(stored[i].Position) != ToFixed(current[i].Position)) return false;
        return true;
    }

    /// <summary>The guides after a canvas change (crop, canvas size, image size, turn); see <see cref="Remap(Guide, int, int, CanvasMap)"/>.</summary>
    public static IReadOnlyList<Guide> Remap(IReadOnlyList<Guide> guides, int newWidth, int newHeight, CanvasMap map)
    {
        ArgumentNullException.ThrowIfNull(guides);
        return guides.Select(g => Remap(g, newWidth, newHeight, map)).ToList();
    }

    /// <summary>
    /// A guide is a line; the map takes it to another line. Moves, scales and quarter turns keep it horizontal or
    /// vertical, and the guide becomes exactly that line (a quarter turn swaps its direction). A slight turn tilts it,
    /// which a guide cannot be: it stays on its nearer axis and passes through the point where the tilted line crosses
    /// the middle of the new canvas, so it is exact at the center and off by at most the tilt elsewhere.
    /// </summary>
    public static Guide Remap(Guide guide, int newWidth, int newHeight, CanvasMap map)
    {
        bool horizontal = guide.IsHorizontal;
        double location = guide.Position;
        // A point on the guide and its direction, after the map.
        var (px, py) = horizontal ? map.Apply(0, location) : map.Apply(location, 0);
        double dx = horizontal ? map.M11 : map.M12, dy = horizontal ? map.M21 : map.M22;
        bool nowHorizontal = Math.Abs(dx) > Math.Abs(dy);
        double moved = nowHorizontal ? py + (newWidth / 2.0 - px) / dx * dy : px + (newHeight / 2.0 - py) / dy * dx;
        return new Guide(nowHorizontal ? GuideOrientation.Horizontal : GuideOrientation.Vertical, Quantize(moved));
    }

    private static int ToFixed(double position) => (int)Math.Clamp(Math.Round(position * 32), int.MinValue, int.MaxValue);
}
