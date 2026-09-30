using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using Strayta.Core;

namespace Strayta.Psd;

/// <summary>
/// A channel color as the file stored it (PSD's color structure: a color space and four 16-bit components), kept as
/// <see cref="DocumentChannel.SourceColor"/> so an unchanged channel is written back exactly.
/// </summary>
/// <param name="ColorSpace">0 RGB, 1 HSB, 2 CMYK, 7 Lab, 8 Gray; book colors (Pantone and others) use their own IDs.</param>
/// <param name="Components">The four components as stored.</param>
/// <param name="Rgb">The color the editor shows for it.</param>
public sealed record PsdStoredColor(short ColorSpace, ushort[] Components, RgbColor Rgb);

/// <summary>
/// Saved selections (alpha channels) and spot color channels. PSD keeps them after the color channels (and the merged
/// transparency) of the flattened image, with their names in image resource 1006 (Pascal strings) and 1045 (Unicode),
/// identifiers in 1053, and display settings (color, opacity, and whether the color marks masked areas, selected areas
/// or is a spot ink) in 1077 (and the older 1007). Those lists also hold an entry for the merged transparency when the
/// file has it ("Transparency", first). They live in the <see cref="PsdFile"/> the document came from, like saved
/// paths, so an untouched file keeps every byte and crops and resizes remap them with the rest.
/// </summary>
public static class PsdChannels
{
    public const int NamesResource = 1006, LegacyDisplayResource = 1007, UnicodeNamesResource = 1045, IdsResource = 1053, DisplayResource = 1077;

    private static readonly int[] ChannelResources = [NamesResource, LegacyDisplayResource, UnicodeNamesResource, IdsResource, DisplayResource];

    private static readonly ConditionalWeakTable<PsdFile, IReadOnlyList<DocumentChannel>> Cache = new();

    /// <summary>How many of the flattened image's channels are the image itself (color, then transparency when stored).</summary>
    public static int ImageChannelCount(PsdFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var mode = file.Header.ColorMode;
        if (mode == ColorMode.Multichannel) return file.CompositeChannels.Count;
        return mode.ColorChannelCount() + (file.CompositeHasTransparency ? 1 : 0);
    }

    /// <summary>The planes of the saved selections and spot channels, in file order.</summary>
    public static IReadOnlyList<Plane> ExtraPlanes(PsdFile file) =>
        file.CompositeChannels.Count > ImageChannelCount(file) ? file.CompositeChannels.Skip(ImageChannelCount(file)).ToList() : [];

    /// <summary>The file's saved selections and spot channels, in the Channels panel's order (the same list for the same file).</summary>
    public static IReadOnlyList<DocumentChannel> Read(PsdFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return Cache.GetValue(file, ReadUncached);
    }

    private static IReadOnlyList<DocumentChannel> ReadUncached(PsdFile file)
    {
        var planes = ExtraPlanes(file);
        if (planes.Count == 0) return [];
        var names = ReadUnicodeNames(file.FindResource(UnicodeNamesResource)?.Data) ?? ReadPascalNames(file.FindResource(NamesResource)?.Data);
        var ids = ReadIds(file.FindResource(IdsResource)?.Data);
        var display = ReadDisplay(file.FindResource(DisplayResource)?.Data, versioned: true)
            ?? ReadDisplay(file.FindResource(LegacyDisplayResource)?.Data, versioned: false) ?? [];
        // The lists start with the merged transparency's entry when there is one.
        int Offset(int count) => file.CompositeHasTransparency && count > planes.Count ? 1 : 0;
        int nameAt = Offset(names.Count), idAt = Offset(ids.Count), displayAt = Offset(display.Count);

        var list = new List<DocumentChannel>(planes.Count);
        int alphaNumber = 0;
        for (int i = 0; i < planes.Count; i++)
        {
            var info = i + displayAt < display.Count ? display[i + displayAt] : null;
            var kind = info?.Kind switch { 0 => ChannelKind.SelectedAreas, 2 => ChannelKind.Spot, _ => ChannelKind.MaskedAreas };
            string name = i + nameAt < names.Count && names[i + nameAt].Length > 0
                ? names[i + nameAt]
                : kind == ChannelKind.Spot ? $"Spot Color {i + 1}" : $"Alpha {++alphaNumber}";
            var channel = new DocumentChannel(name, planes[i], kind)
            {
                Id = i + idAt < ids.Count ? ids[i + idAt] : 0,
                Opacity = info is null ? 0.5f : Math.Clamp(info.Opacity / 100f, 0f, 1f),
            };
            if (info is not null) channel = channel with { Color = info.Color.Rgb, SourceColor = info.Color };
            list.Add(channel);
        }
        return list;
    }

    /// <summary>
    /// <paramref name="file"/> with <paramref name="channels"/> as its saved selections and spot channels: the flattened
    /// image's extra channels and the name, identifier and display resources are rewritten (keeping the merged
    /// transparency's entries), in the places the old resources had. Channels without an identifier get the next free one.
    /// </summary>
    public static PsdFile WithChannels(PsdFile file, IReadOnlyList<DocumentChannel> channels)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(channels);
        int image = ImageChannelCount(file);
        var planes = file.CompositeChannels.Take(image).ToList();
        // A stand-in file (a document that did not come from a PSD) has no image data: placeholders keep the extra
        // channels after the image's own, where the writer and canvas changes look for them.
        while (planes.Count < image) planes.Add(Plane.Create(1, 1, file.Header.BitDepth));
        planes.AddRange(channels.Select(c => c.Pixels));

        // The merged transparency's entries stay first.
        bool transparency = file.CompositeHasTransparency;
        var oldNames = ReadUnicodeNames(file.FindResource(UnicodeNamesResource)?.Data) ?? ReadPascalNames(file.FindResource(NamesResource)?.Data);
        var oldIds = ReadIds(file.FindResource(IdsResource)?.Data);
        var oldDisplay = ReadDisplay(file.FindResource(DisplayResource)?.Data, versioned: true)
            ?? ReadDisplay(file.FindResource(LegacyDisplayResource)?.Data, versioned: false) ?? [];
        var entries = new List<Entry>();
        if (transparency)
            entries.Add(new Entry(oldNames.Count > 0 ? oldNames[0] : "Transparency", oldIds.Count > 0 ? oldIds[0] : 0,
                oldDisplay.Count > 0 ? oldDisplay[0] : TransparencyDisplay));
        var used = channels.Select(c => c.Id).Concat(entries.Select(e => e.Id)).ToHashSet();
        int next = 1;
        foreach (var c in channels)
        {
            int id = c.Id > 0 ? c.Id : NextFree();
            entries.Add(new Entry(c.Name, id, DisplayOf(c)));
        }

        var header = file.Header with { Channels = Math.Max(1, planes.Count) };
        var resources = ReplaceResources(file.Resources, entries.Count == 0 ? null : entries);
        return file.With(header, resources, planes);

        int NextFree()
        {
            while (used.Contains(next)) next++;
            used.Add(next);
            return next;
        }
    }

    /// <summary>
    /// The resources to save with a new flattened image: when it gains or loses its transparency channel compared with
    /// <paramref name="file"/>, the channel lists gain or lose the transparency's entry so names still match channels.
    /// Otherwise the file's own list (unchanged).
    /// </summary>
    public static IReadOnlyList<ImageResource> ResourcesForComposite(PsdFile file, bool compositeHasTransparency)
    {
        if (file.CompositeHasTransparency == compositeHasTransparency || !file.Resources.Any(r => ChannelResources.Contains(r.Id)))
            return file.Resources;
        var channels = Read(file);
        var entries = new List<Entry>();
        if (compositeHasTransparency) entries.Add(new Entry("Transparency", 0, TransparencyDisplay));
        var used = channels.Select(c => c.Id).ToHashSet();
        int next = 1;
        foreach (var c in channels)
        {
            int id = c.Id;
            if (id <= 0)
            {
                while (used.Contains(next)) next++;
                id = next;
                used.Add(id);
            }
            entries.Add(new Entry(c.Name, id, DisplayOf(c)));
        }
        return ReplaceResources(file.Resources, entries.Count == 0 ? null : entries);
    }

    // ---- Resource data ---------------------------------------------------------------------------------------

    private sealed record Display(PsdStoredColor Color, int Opacity, byte Kind);

    private sealed record Entry(string Name, int Id, Display Display);

    private static readonly Display TransparencyDisplay = new(new PsdStoredColor(0, [65535, 0, 0, 0], new RgbColor(1f, 0f, 0f)), 100, 1);

    private static Display DisplayOf(DocumentChannel c)
    {
        var color = c.SourceColor is PsdStoredColor stored && stored.Rgb == c.Color
            ? stored
            : new PsdStoredColor(0, [ToU16(c.Color.R), ToU16(c.Color.G), ToU16(c.Color.B), 0], c.Color);
        byte kind = c.Kind switch { ChannelKind.SelectedAreas => 0, ChannelKind.Spot => 2, _ => 1 };
        return new Display(color, (int)MathF.Round(Math.Clamp(c.Opacity, 0f, 1f) * 100f), kind);

        static ushort ToU16(float v) => (ushort)MathF.Round(Math.Clamp(v, 0f, 1f) * 65535f);
    }

    /// <summary>
    /// The resources with the channel lists replaced (each where its old copy was; new ones appended), or removed when
    /// <paramref name="entries"/> is null. The older display resource (1007) is rewritten only when the file had it.
    /// </summary>
    private static List<ImageResource> ReplaceResources(IReadOnlyList<ImageResource> resources, List<Entry>? entries)
    {
        var fresh = new Dictionary<int, byte[]>();
        if (entries is not null)
        {
            fresh[NamesResource] = EncodePascalNames(entries);
            fresh[UnicodeNamesResource] = EncodeUnicodeNames(entries);
            fresh[IdsResource] = EncodeIds(entries);
            fresh[DisplayResource] = EncodeDisplay(entries, versioned: true);
            if (resources.Any(r => r.Id == LegacyDisplayResource)) fresh[LegacyDisplayResource] = EncodeDisplay(entries, versioned: false);
        }
        var result = new List<ImageResource>();
        var written = new HashSet<int>();
        foreach (var r in resources)
        {
            if (!ChannelResources.Contains(r.Id))
            {
                result.Add(r);
                continue;
            }
            if (!fresh.TryGetValue(r.Id, out var data) || !written.Add(r.Id)) continue;
            result.Add(r.Data.AsSpan().SequenceEqual(data) ? r : r with { Data = data });
        }
        foreach (var id in new[] { NamesResource, UnicodeNamesResource, IdsResource, DisplayResource })
            if (fresh.TryGetValue(id, out var data) && !written.Contains(id))
                result.Add(new ImageResource("8BIM", id, "", data));
        return result;
    }

    private static List<string>? ReadUnicodeNames(byte[]? data)
    {
        if (data is null) return null;
        var names = new List<string>();
        int at = 0;
        while (at + 4 <= data.Length)
        {
            long chars = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at));
            at += 4;
            int bytes = (int)Math.Min(chars * 2, data.Length - at);
            names.Add(Encoding.BigEndianUnicode.GetString(data, at, bytes & ~1).TrimEnd('\0'));
            at += bytes;
        }
        return names;
    }

    private static List<string> ReadPascalNames(byte[]? data)
    {
        var names = new List<string>();
        if (data is null) return names;
        int at = 0;
        while (at < data.Length)
        {
            int n = Math.Min(data[at], data.Length - at - 1);
            names.Add(Encoding.Latin1.GetString(data, at + 1, n));
            at += 1 + n;
        }
        return names;
    }

    private static List<int> ReadIds(byte[]? data)
    {
        var ids = new List<int>();
        if (data is null) return ids;
        for (int at = 0; at + 4 <= data.Length; at += 4) ids.Add(BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(at)));
        return ids;
    }

    /// <summary>1077: a 4-byte version, then 13 bytes per channel; 1007: 14 bytes per channel (a padding byte each).</summary>
    private static List<Display>? ReadDisplay(byte[]? data, bool versioned)
    {
        if (data is null) return null;
        var list = new List<Display>();
        int at = versioned ? 4 : 0, size = versioned ? 13 : 14;
        for (; at + 13 <= data.Length; at += size)
        {
            var d = data.AsSpan(at);
            short space = BinaryPrimitives.ReadInt16BigEndian(d);
            ushort[] c = [BinaryPrimitives.ReadUInt16BigEndian(d[2..]), BinaryPrimitives.ReadUInt16BigEndian(d[4..]),
                BinaryPrimitives.ReadUInt16BigEndian(d[6..]), BinaryPrimitives.ReadUInt16BigEndian(d[8..])];
            int opacity = BinaryPrimitives.ReadInt16BigEndian(d[10..]);
            list.Add(new Display(new PsdStoredColor(space, c, ToRgb(space, c)), opacity, d[12]));
        }
        return list;
    }

    private static byte[] EncodePascalNames(List<Entry> entries)
    {
        var o = new MemoryStream();
        foreach (var e in entries)
        {
            var bytes = Encoding.Latin1.GetBytes(e.Name.Length > 255 ? e.Name[..255] : e.Name);
            o.WriteByte((byte)bytes.Length);
            o.Write(bytes);
        }
        return o.ToArray();
    }

    private static byte[] EncodeUnicodeNames(List<Entry> entries)
    {
        var o = new MemoryStream();
        Span<byte> length = stackalloc byte[4];
        foreach (var e in entries)
        {
            // Photoshop counts and writes a terminating null.
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)(e.Name.Length + 1));
            o.Write(length);
            o.Write(Encoding.BigEndianUnicode.GetBytes(e.Name));
            o.Write([0, 0]);
        }
        return o.ToArray();
    }

    private static byte[] EncodeIds(List<Entry> entries)
    {
        var o = new byte[entries.Count * 4];
        for (int i = 0; i < entries.Count; i++) BinaryPrimitives.WriteInt32BigEndian(o.AsSpan(i * 4), entries[i].Id);
        return o;
    }

    private static byte[] EncodeDisplay(List<Entry> entries, bool versioned)
    {
        int size = versioned ? 13 : 14, start = versioned ? 4 : 0;
        var o = new byte[start + entries.Count * size];
        if (versioned) BinaryPrimitives.WriteInt32BigEndian(o, 1);
        for (int i = 0; i < entries.Count; i++)
        {
            var d = o.AsSpan(start + i * size);
            var e = entries[i].Display;
            BinaryPrimitives.WriteInt16BigEndian(d, e.Color.ColorSpace);
            for (int k = 0; k < 4; k++) BinaryPrimitives.WriteUInt16BigEndian(d[(2 + k * 2)..], e.Color.Components[k]);
            BinaryPrimitives.WriteInt16BigEndian(d[10..], (short)e.Opacity);
            d[12] = e.Kind;
        }
        return o;
    }

    /// <summary>A display color for a stored one. Book colors (Pantone and others) have no stored equivalent here: gray.</summary>
    internal static RgbColor ToRgb(short space, ushort[] c)
    {
        switch (space)
        {
            case 0:
                return new RgbColor(c[0] / 65535f, c[1] / 65535f, c[2] / 65535f);
            case 1:
            {
                float h = c[0] / 65535f * 6f, s = c[1] / 65535f, v = c[2] / 65535f;
                int sector = (int)MathF.Floor(h) % 6;
                float f = h - MathF.Floor(h), p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
                return sector switch
                {
                    0 => new RgbColor(v, t, p),
                    1 => new RgbColor(q, v, p),
                    2 => new RgbColor(p, v, t),
                    3 => new RgbColor(p, q, v),
                    4 => new RgbColor(t, p, v),
                    _ => new RgbColor(v, p, q),
                };
            }
            case 2:
            {
                // Components are stored inverted: 0 is full ink.
                float cy = 1 - c[0] / 65535f, m = 1 - c[1] / 65535f, y = 1 - c[2] / 65535f, k = 1 - c[3] / 65535f;
                return new RgbColor((1 - cy) * (1 - k), (1 - m) * (1 - k), (1 - y) * (1 - k));
            }
            case 7:
            {
                double l = c[0] / 100.0, a = (short)c[1] / 100.0, b = (short)c[2] / 100.0;
                return LabToRgb(l, a, b);
            }
            case 8:
                return new RgbColor(1 - c[0] / 10000f, 1 - c[0] / 10000f, 1 - c[0] / 10000f);
            default:
                return new RgbColor(0.5f, 0.5f, 0.5f);
        }
    }

    /// <summary>CIE Lab (D50) to sRGB, through XYZ with Bradford adaptation to D65.</summary>
    private static RgbColor LabToRgb(double l, double a, double b)
    {
        double fy = (l + 16) / 116, fx = fy + a / 500, fz = fy - b / 200;
        static double F(double t) => t > 6.0 / 29 ? t * t * t : 3 * (6.0 / 29) * (6.0 / 29) * (t - 4.0 / 29);
        double x = 0.9642 * F(fx), y = F(fy), z = 0.8249 * F(fz);
        double r = 3.1338561 * x - 1.6168667 * y - 0.4906146 * z;
        double g = -0.9787684 * x + 1.9161415 * y + 0.0334540 * z;
        double bl = 0.0719453 * x - 0.2289914 * y + 1.4052427 * z;
        static float Gamma(double v) => (float)Math.Clamp(v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055, 0, 1);
        return new RgbColor(Gamma(r), Gamma(g), Gamma(bl));
    }
}
