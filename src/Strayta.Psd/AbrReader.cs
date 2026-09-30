using System.Buffers.Binary;
using System.Text;
using Strayta.Core.Painting;
using Strayta.Psd.Descriptors;

namespace Strayta.Psd;

/// <summary>
/// One brush preset from a brush file: its tip (a sampled image, or null for a computed round tip) and the tip
/// settings the file gives. Hardness is 0..1 (computed tips only), Roundness 0..1, Angle in degrees counter-clockwise,
/// Spacing in percent of the diameter.
/// </summary>
public sealed record AbrBrush(string Name, BrushTip? Tip, float Diameter, float Hardness, float SpacingPercent, float Angle, float Roundness);

/// <summary>
/// Reads Photoshop brush files (.abr): sampled-tip and computed round brushes. Brush dynamics, textures, dual brushes and
/// the rest of a preset's settings are skipped; the tip and its size, spacing, angle and roundness are kept.
/// </summary>
/// <remarks>
/// <para>Written from Adobe's public descriptions of the format and inspection of the structure, not from other
/// readers' code. All numbers are big-endian.</para>
/// <para><b>Versions 1 and 2</b>: version (2 bytes), brush count (2), then per brush a type (2: 1 computed, 2 sampled),
/// a byte length and that many bytes. Both types start with 4 unused bytes and the spacing (2, percent; 0 means the
/// default 25%); version 2 then has a Unicode name (4-byte character count, UTF-16). A computed brush continues with
/// diameter, roundness (percent), angle (signed, degrees) and hardness (percent), 2 bytes each. A sampled brush continues
/// with an anti-aliasing byte, a short bounds rectangle (4 × 2 bytes), the long bounds (top, left, bottom, right, 4 × 4
/// bytes), the depth (2) and a compression byte (0 raw, 1 PackBits rows preceded by 2-byte row lengths), then the
/// grayscale tip, whose values are how much paint each pixel lays down.</para>
/// <para><b>Versions 6 and later</b> (6, 7, 10): version (2), subversion (2: 1 or 2), then sections, each "8BIM", a
/// 4-character key, a length (4) and data. The "samp" section holds the tips: each is a length (4) and that many bytes,
/// padded to a multiple of 4; inside, a Pascal-string ID, 10 more bytes (subversion 1) or 264 (subversion 2), the long
/// bounds, depth and compression as above, and the image (16-bit tips keep their high byte). The "desc" section is an
/// action descriptor (version 16) whose "Brsh" list holds the presets, in the order the file shows them: each has a name
/// ("Nm  ") and a "Brsh" object, either a computed brush (diameter "Dmtr", hardness "Hrdn", angle "Angl", roundness
/// "Rndn", spacing "Spcn") or a sampled brush, whose "sampledData" names the tip's ID. Tips no preset refers to are
/// listed after the presets. Files without a readable "desc" list every tip at its own size.</para>
/// <para>Damaged or unsupported brushes are skipped rather than failing the whole file.</para>
/// </remarks>
public static class AbrReader
{
    /// <summary>Reads every brush of a file; <paramref name="fileName"/> names brushes that have no name of their own.</summary>
    public static IReadOnlyList<AbrBrush> Read(byte[] data, string fileName = "Brush")
    {
        if (data.Length < 4) throw new PsdFormatException("Not a brush file: too short.");
        int version = BinaryPrimitives.ReadUInt16BigEndian(data);
        string stem = Path.GetFileNameWithoutExtension(fileName);
        return version switch
        {
            1 or 2 => ReadClassic(data, version, stem),
            >= 6 and <= 10 => ReadSections(data, stem),
            _ => throw new PsdFormatException($"Brush file version {version} is not supported."),
        };
    }

    public static IReadOnlyList<AbrBrush> Read(string path) => Read(File.ReadAllBytes(path), Path.GetFileName(path));

    // ---- Versions 1 and 2 ---------------------------------------------------------------------------------

    private static List<AbrBrush> ReadClassic(byte[] data, int version, string stem)
    {
        var brushes = new List<AbrBrush>();
        var r = new Cursor(data, 2);
        int count = r.U16();
        for (int n = 0; n < count && r.Remaining >= 6; n++)
        {
            int type = r.U16();
            int length = (int)Math.Min(r.U32(), (uint)r.Remaining);
            int end = r.Pos + length;
            try
            {
                var b = new Cursor(data, r.Pos, end);
                b.Skip(4);
                float spacing = b.U16();
                if (spacing <= 0) spacing = 25;
                string name = version == 2 ? b.Unicode() : "";
                if (name.Length == 0) name = $"{stem} {brushes.Count + 1}";
                if (type == 1)
                {
                    int diameter = b.U16(), roundness = b.U16();
                    short angle = (short)b.U16();
                    int hardness = b.U16();
                    brushes.Add(new AbrBrush(name, null, Math.Max(1, diameter), Math.Clamp(hardness / 100f, 0f, 1f), spacing, angle,
                        Math.Clamp(roundness == 0 ? 1f : roundness / 100f, 0.01f, 1f)));
                }
                else if (type == 2)
                {
                    b.Skip(1 + 8); // anti-aliasing, short bounds
                    if (ReadTip(ref b, name) is { } tip)
                        brushes.Add(new AbrBrush(name, tip, tip.NativeSize, 1f, spacing, 0f, 1f));
                }
            }
            catch (Exception ex) when (ex is PsdFormatException or ArgumentException or IndexOutOfRangeException)
            {
                // A damaged brush is skipped; the rest of the file still loads.
            }
            r.Pos = end;
        }
        return brushes;
    }

    // ---- Versions 6 and later -----------------------------------------------------------------------------

    private static List<AbrBrush> ReadSections(byte[] data, string stem)
    {
        var r = new Cursor(data, 2);
        int sub = r.U16();
        var tips = new List<(string Id, BrushTip Tip)>();
        Descriptor? desc = null;
        while (r.Remaining >= 12)
        {
            if (r.Ascii(4) != "8BIM")
            {
                // Sections are padded; step over up to three padding bytes to find the next one.
                r.Pos -= 3;
                continue;
            }
            string key = r.Ascii(4);
            int length = (int)Math.Min(r.U32(), (uint)r.Remaining);
            int end = r.Pos + length;
            switch (key)
            {
                case "samp":
                    ReadSamples(data, r.Pos, end, sub, tips);
                    break;
                case "desc":
                    try
                    {
                        desc = DescriptorReader.ReadVersioned(data, r.Pos);
                    }
                    catch (Exception ex) when (ex is PsdFormatException or ArgumentException or IndexOutOfRangeException)
                    {
                        desc = null; // presets without their descriptor: the tips are still listed
                    }
                    break;
            }
            r.Pos = end;
        }

        var brushes = new List<AbrBrush>();
        var used = new HashSet<string>();
        foreach (var item in desc?.List("Brsh") ?? [])
        {
            if (item is not ObjectValue { Value: var preset } || preset.Object("Brsh") is not { } tip) continue;
            string name = preset.Text("Nm  ")?.TrimEnd('\0') is { Length: > 0 } n ? n : $"{stem} {brushes.Count + 1}";
            float diameter = (float)(tip.Number("Dmtr") ?? 30);
            float angle = (float)(tip.Number("Angl") ?? 0);
            float roundness = Math.Clamp((float)(tip.Number("Rndn") ?? 100) / 100f, 0.01f, 1f);
            float spacing = (float)(tip.Number("Spcn") ?? 25);
            if (tip.Text("sampledData") is { } id)
            {
                var found = tips.FirstOrDefault(t => t.Id == id.TrimEnd('\0'));
                if (found.Tip is null) continue; // a tip the file does not carry
                used.Add(found.Id);
                brushes.Add(new AbrBrush(name, found.Tip, diameter, 1f, spacing, angle, roundness));
            }
            else
            {
                float hardness = Math.Clamp((float)(tip.Number("Hrdn") ?? 100) / 100f, 0f, 1f);
                brushes.Add(new AbrBrush(name, null, diameter, hardness, spacing, angle, roundness));
            }
        }
        foreach (var (id, tip) in tips)
            if (!used.Contains(id)) brushes.Add(new AbrBrush(tip.Name.Length > 0 ? tip.Name : $"{stem} {brushes.Count + 1}", tip, tip.NativeSize, 1f, 25, 0, 1));
        return brushes;
    }

    private static void ReadSamples(byte[] data, int pos, int end, int sub, List<(string, BrushTip)> tips)
    {
        while (end - pos >= 4)
        {
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
            pos += 4;
            if (length <= 0 || length > end - pos) break;
            int itemEnd = pos + length;
            var item = new Cursor(data, pos, itemEnd);
            string id = item.Pascal();
            int afterId = item.Pos;
            BrushTip? tip = null;
            // The block between the ID and the bounds is 10 bytes in subversion 1 and 264 in subversion 2; try the
            // file's own first, then the other, since files mislabel it.
            foreach (int skip in sub == 2 ? new[] { 264, 10 } : [10, 264])
            {
                try
                {
                    item.Pos = afterId + skip;
                    tip = ReadTip(ref item, $"Sampled Brush {tips.Count + 1}");
                    if (tip is not null) break;
                }
                catch (Exception ex) when (ex is PsdFormatException or ArgumentException or IndexOutOfRangeException)
                {
                    tip = null;
                }
            }
            if (tip is not null) tips.Add((id, tip));
            pos = itemEnd + (4 - length % 4) % 4; // items are padded to a multiple of 4 bytes
        }
    }

    /// <summary>Long bounds, depth, compression and the image; null when they make no sense.</summary>
    private static BrushTip? ReadTip(ref Cursor c, string name)
    {
        int top = c.I32(), left = c.I32(), bottom = c.I32(), right = c.I32();
        int depth = c.U16();
        int compression = c.U8();
        int w = right - left, h = bottom - top;
        if (w <= 0 || h <= 0 || w > 10000 || h > 10000 || depth is not (8 or 16) || compression > 1) return null;
        int bpp = depth / 8, rowBytes = w * bpp;
        var raw = new byte[rowBytes * h];
        if (compression == 0)
        {
            if (c.Remaining < raw.Length) return null;
            c.Bytes(raw.Length).CopyTo(raw);
        }
        else
        {
            if (c.Remaining < h * 2) return null;
            var counts = new int[h];
            for (int y = 0; y < h; y++) counts[y] = c.U16();
            int total = counts.Sum();
            if (total > c.Remaining) return null;
            ChannelDecoder.DecodeRleRows(c.Bytes(total), counts, raw, rowBytes);
        }
        var alpha = bpp == 1 ? raw : new byte[w * h];
        if (bpp == 2)
            for (int i = 0; i < w * h; i++) alpha[i] = raw[i * 2]; // the high byte of each 16-bit value
        return new BrushTip(name, w, h, alpha);
    }

    /// <summary>A bounds-checked big-endian reader over part of the file.</summary>
    private struct Cursor(byte[] data, int pos, int end = -1)
    {
        private readonly int _end = end < 0 ? data.Length : Math.Min(end, data.Length);
        public int Pos = pos;

        public readonly int Remaining => _end - Pos;

        private ReadOnlySpan<byte> Take(int n)
        {
            if (n < 0 || Pos + n > _end) throw new PsdFormatException("Brush data ends early.");
            var s = data.AsSpan(Pos, n);
            Pos += n;
            return s;
        }

        public void Skip(int n) => Take(n);
        public int U8() => Take(1)[0];
        public int U16() => BinaryPrimitives.ReadUInt16BigEndian(Take(2));
        public uint U32() => BinaryPrimitives.ReadUInt32BigEndian(Take(4));
        public int I32() => BinaryPrimitives.ReadInt32BigEndian(Take(4));
        public ReadOnlySpan<byte> Bytes(int n) => Take(n);
        public string Ascii(int n) => Encoding.ASCII.GetString(Take(n));

        public string Pascal()
        {
            int n = U8();
            return Encoding.ASCII.GetString(Take(n));
        }

        public string Unicode()
        {
            int chars = (int)U32();
            if (chars < 0 || chars > 10000) throw new PsdFormatException("Brush name is too long.");
            return Encoding.BigEndianUnicode.GetString(Take(chars * 2)).TrimEnd('\0');
        }
    }
}
