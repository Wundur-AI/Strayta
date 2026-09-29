using System.Buffers.Binary;
using System.Text;
using Strayta.Psd.Descriptors;

namespace Strayta.Psd;

/// <summary>
/// A placed layer (smart object) as its 'SoLd' block describes it: which embedded or linked file it shows, the
/// content's size, and where the content's corners land in the document.
/// </summary>
/// <param name="UniqueId">Matches the file's entry in the document's 'lnk2' / 'lnk3' / 'lnkD' block.</param>
/// <param name="Corners">Document positions of the content's top-left, top-right, bottom-right and bottom-left
/// corners ('nonAffineTransform', which includes perspective; 'Trnf' when absent).</param>
/// <param name="Width">Content width ('Sz'), the space the corners' source rectangle is measured in.</param>
/// <param name="Height">Content height.</param>
/// <param name="Warped">A warp other than none is applied on top of the corners.</param>
/// <param name="HasFilters">Smart filters ('filterFX') change the pixels after placing.</param>
public sealed record PsdSmartObject(string UniqueId, (double X, double Y)[] Corners, double Width, double Height, bool Warped, bool HasFilters)
{
    /// <summary>The warp ("quiltWarp" when it has one, else "warp"), or null when none.</summary>
    public Core.WarpSpec? Warp { get; init; }

    /// <summary>The whole placed-layer descriptor.</summary>
    public Descriptor? Descriptor { get; init; }
}

/// <summary>A file embedded in the document for a smart object ('liFD' entry of a 'lnk2' / 'lnk3' / 'lnkD' block).</summary>
/// <param name="FileType">Mac file type, e.g. "8BPB" (PSB), "8BPS" (PSD), "png ", "JPEG".</param>
public sealed record PsdEmbeddedFile(string UniqueId, string Name, string FileType, byte[] Data);

/// <summary>
/// Keeps live layer content (smart objects, type, vector shapes) in step with an affine change of document
/// coordinates, so layers stay editable through Free Transform, rotated crops and Image Size instead of being
/// rasterized. The formats, as found in files Photoshop writes:
/// <list type="bullet">
/// <item>'SoLd' / 'SoLE' (placed layer data): the key, a 4-byte version (4), then a versioned descriptor whose
/// "Trnf" and "nonAffineTransform" are lists of eight doubles, the x and y of the content's four corners in document
/// pixels (clockwise from top-left). Its warp ("warp", including custom meshes) is in content space and stays.</item>
/// <item>'PlLd' (the older placed layer block Photoshop still writes alongside): 'plcL', version 3, the unique ID as
/// a Pascal string (no padding), page number, total pages, anti-alias policy and layer type (4 bytes each), then the
/// same eight corner doubles, followed by the warp descriptor.</item>
/// <item>'vogk' (vector origination: the live rectangle / ellipse / rounded rectangle behind a shape): a 4-byte
/// version (1), then a versioned descriptor with "keyDescriptorList"; each entry's "keyOriginShapeBBox" is a
/// document-space rectangle and "keyOriginRRectRadii" its corner radii. A move or an axis-aligned scale updates
/// them; a turn or skew marks the entry "keyShapeInvalidated", as Photoshop does when a live shape becomes an
/// ordinary path.</item>
/// </list>
/// Numbers are patched in place where possible, so everything else in the blocks stays byte for byte.
/// </summary>
public static class PsdLiveContent
{
    /// <summary>
    /// Maps the corners of a placed layer's 'SoLd' / 'SoLE' block. Returns <paramref name="data"/> itself when the
    /// block cannot be read.
    /// </summary>
    public static byte[] TransformPlacedDescriptor(byte[] data, CanvasMap map)
    {
        if (data.Length < 12 || BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(8)) != 16) return data;
        var offsets = new List<(string Path, int Offset)>();
        try
        {
            var reader = new DescriptorReader(data, 12) { NumberOffsets = offsets };
            reader.ReadDescriptor();
        }
        catch (PsdFormatException)
        {
            return data;
        }
        var o = (byte[])data.Clone();
        foreach (var key in new[] { "Trnf", "nonAffineTransform" })
        {
            var at = new int[8];
            int found = 0;
            foreach (var (path, offset) in offsets)
                if (path.StartsWith(key + "/", StringComparison.Ordinal) && int.TryParse(path.AsSpan(key.Length + 1), out int i) && i is >= 0 and < 8)
                {
                    at[i] = offset;
                    found++;
                }
            if (found == 8) MapCorners(o, at, map);
        }
        return o;
    }

    /// <summary>Maps the eight corner doubles of an old-style 'PlLd' block. Returns <paramref name="data"/> itself when unreadable.</summary>
    public static byte[] TransformPlacedLegacy(byte[] data, CanvasMap map)
    {
        if (data.Length < 9 || Encoding.ASCII.GetString(data, 0, 4) != "plcL") return data;
        int start = 8 + 1 + data[8] + 16;
        if (start + 64 > data.Length) return data;
        var o = (byte[])data.Clone();
        MapCorners(o, Enumerable.Range(0, 8).Select(i => start + i * 8).ToArray(), map);
        return o;
    }

    private static void MapCorners(byte[] o, int[] at, CanvasMap map)
    {
        for (int c = 0; c < 4; c++)
        {
            double x = BinaryPrimitives.ReadDoubleBigEndian(o.AsSpan(at[2 * c]));
            double y = BinaryPrimitives.ReadDoubleBigEndian(o.AsSpan(at[2 * c + 1]));
            var (nx, ny) = map.Apply(x, y);
            BinaryPrimitives.WriteDoubleBigEndian(o.AsSpan(at[2 * c]), nx);
            BinaryPrimitives.WriteDoubleBigEndian(o.AsSpan(at[2 * c + 1]), ny);
        }
    }

    /// <summary>
    /// Updates the live shape parameters of a 'vogk' block. Returns <paramref name="data"/> itself when nothing
    /// changed or the block cannot be read.
    /// </summary>
    public static byte[] TransformOrigination(byte[] data, CanvasMap map)
    {
        if (data.Length < 8 || BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(4)) != 16) return data;
        bool axisAligned = Math.Abs(map.M12) < 1e-12 && Math.Abs(map.M21) < 1e-12;
        if (axisAligned)
        {
            // Moves and scales only change numbers: patch them where they are.
            var offsets = new List<(string Path, int Offset)>();
            try
            {
                new DescriptorReader(data, 8) { NumberOffsets = offsets }.ReadDescriptor();
            }
            catch (PsdFormatException)
            {
                return data;
            }
            double sx = map.M11, sy = map.M22;
            bool uniform = Math.Abs(Math.Abs(sx) - Math.Abs(sy)) < 1e-9;
            var o = (byte[])data.Clone();
            var boxes = new Dictionary<string, Dictionary<string, int>>();
            bool radii = false;
            foreach (var (path, offset) in offsets)
            {
                int split = path.LastIndexOf('/');
                if (split < 0) continue;
                string owner = path[..split], field = path[(split + 1)..];
                if (owner.EndsWith("/keyOriginShapeBBox", StringComparison.Ordinal) && field is "Top" or "Left" or "Btom" or "Rght")
                {
                    if (!boxes.TryGetValue(owner, out var box)) boxes[owner] = box = [];
                    box[field] = offset;
                }
                else if (owner.EndsWith("/keyOriginRRectRadii", StringComparison.Ordinal) && field is "topRight" or "topLeft" or "bottomLeft" or "bottomRight")
                {
                    radii = true;
                    if (uniform) Scale(o, offset, Math.Abs(sx));
                }
            }
            if (!radii || uniform)
            {
                foreach (var box in boxes.Values.Where(b => b.Count == 4))
                {
                    double top = Read(o, box["Top"]), left = Read(o, box["Left"]), bottom = Read(o, box["Btom"]), right = Read(o, box["Rght"]);
                    var (x0, y0) = map.Apply(left, top);
                    var (x1, y1) = map.Apply(right, bottom);
                    Write(o, box["Top"], Math.Min(y0, y1));
                    Write(o, box["Left"], Math.Min(x0, x1));
                    Write(o, box["Btom"], Math.Max(y0, y1));
                    Write(o, box["Rght"], Math.Max(x0, x1));
                }
                return o;
            }
        }
        return Invalidate(data);
    }

    /// <summary>Marks every live shape of a 'vogk' block as an ordinary path ("keyShapeInvalidated").</summary>
    private static byte[] Invalidate(byte[] data)
    {
        Descriptor d;
        try
        {
            d = DescriptorReader.ReadVersioned(data, 4);
        }
        catch (PsdFormatException)
        {
            return data;
        }
        if (d.List("keyDescriptorList") is not { } list) return data;
        bool changed = false;
        var items = list.Select(v =>
        {
            if (v is not ObjectValue { Value: var shape } || !shape.Has("keyOriginType") || shape.Bool("keyShapeInvalidated") == true) return v;
            changed = true;
            var fields = shape.Items.Where(kv => kv.Key != "keyShapeInvalidated").ToList();
            int at = fields.FindIndex(kv => kv.Key == "keyOriginIndex");
            fields.Insert(at < 0 ? fields.Count : at, new("keyShapeInvalidated", new BoolValue(true)));
            return new ObjectValue(new Descriptor { Name = shape.Name, ClassId = shape.ClassId, Items = fields });
        }).ToList();
        if (!changed) return data;
        var top = new Descriptor
        {
            Name = d.Name,
            ClassId = d.ClassId,
            Items = d.Items.Select(kv => kv.Key == "keyDescriptorList" ? new KeyValuePair<string, DescriptorValue>(kv.Key, new ListValue(items)) : kv).ToList(),
        };
        return [.. data.AsSpan(0, 4), .. DescriptorWriter.WriteVersioned(top)];
    }

    private static double Read(byte[] o, int at) => BinaryPrimitives.ReadDoubleBigEndian(o.AsSpan(at));
    private static void Write(byte[] o, int at, double v) => BinaryPrimitives.WriteDoubleBigEndian(o.AsSpan(at), v);
    private static void Scale(byte[] o, int at, double k) => Write(o, at, Read(o, at) * k);

    // ---- Smart objects ---------------------------------------------------------------------------------

    /// <summary>Reads the placed-layer description of a smart object layer, or null if it has none (or it is unreadable).</summary>
    public static PsdSmartObject? ReadSmartObject(PsdLayerRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if ((record.FindBlock("SoLd") ?? record.FindBlock("SoLE"))?.Data is not { Length: > 12 } data) return null;
        Descriptor d;
        try
        {
            d = DescriptorReader.ReadVersioned(data, 8);
        }
        catch (PsdFormatException)
        {
            return null;
        }
        var corners = Corners(d.List("nonAffineTransform")) ?? Corners(d.List("Trnf"));
        if (corners is null || d.Text("Idnt") is not { } id || d.Object("Sz  ") is not { } size) return null;
        double w = size.Number("Wdth") ?? 0, h = size.Number("Hght") ?? 0;
        if (w <= 0 || h <= 0) return null;
        var warp = ReadWarp(d.Object("quiltWarp")) is { IsNone: false } quilt ? quilt : ReadWarp(d.Object("warp"));
        if (warp is { IsNone: true }) warp = null;
        return new PsdSmartObject(id, corners, w, h, warp is not null, d.Has("filterFX")) { Warp = warp, Descriptor = d };
    }

    /// <summary>
    /// Reads a warp descriptor ("warp" / "quiltWarp" in 'SoLd', or the one after 'PlLd' and type data): "warpStyle",
    /// "warpValue", "warpPerspective", "warpPerspectiveOther", "warpRotate" (Ornt), "bounds" (Top/Left/Btom/Rght),
    /// "uOrder"/"vOrder", for split meshes "deformNumRows"/"deformNumCols", "warpValues" (newer styles such as the
    /// cylinder), and for custom warps "customEnvelopeWarp" with "meshPoints" (an object array of Hrzn/Vrtc) and,
    /// when split, "quiltSliceX"/"quiltSliceY".
    /// </summary>
    public static Core.WarpSpec? ReadWarp(Descriptor? warp)
    {
        if (warp is null) return null;
        var b = warp.Object("bounds");
        var bounds = (b?.Number("Left") ?? 0, b?.Number("Top ") ?? 0, b?.Number("Rght") ?? 0, b?.Number("Btom") ?? 0);
        int rows = (int)(warp.Number("deformNumRows") ?? 4), cols = (int)(warp.Number("deformNumCols") ?? 4);
        List<(double, double)>? mesh = null;
        double[]? slicesX = null, slicesY = null;
        if (warp.Object("customEnvelopeWarp") is { } env)
        {
            if (env["meshPoints"] is ObjectArrayValue points && points.Columns["Hrzn"] is UnitFloatsValue hx && points.Columns["Vrtc"] is UnitFloatsValue vy
                && hx.Values.Count == vy.Values.Count)
                mesh = hx.Values.Zip(vy.Values).ToList();
            slicesX = Slices(env["quiltSliceX"], "quiltSliceX");
            slicesY = Slices(env["quiltSliceY"], "quiltSliceY");
        }
        if (mesh is not null && mesh.Count != rows * cols)
        {
            if (mesh.Count == 16) (rows, cols) = (4, 4);
            else mesh = null;
        }
        var values = warp.List("warpValues")?.Select(v => v switch { DoubleValue d => d.Value, UnitFloatValue u => u.Value, IntegerValue i => i.Value, _ => 0 }).ToArray();
        return new Core.WarpSpec
        {
            Style = warp.Enum("warpStyle") ?? "warpNone",
            Value = warp.Number("warpValue") ?? 0,
            Perspective = warp.Number("warpPerspective") ?? 0,
            PerspectiveOther = warp.Number("warpPerspectiveOther") ?? 0,
            Vertical = warp.Enum("warpRotate") == "Vrtc",
            Bounds = bounds,
            Rows = rows,
            Columns = cols,
            Mesh = mesh,
            SlicesX = slicesX,
            SlicesY = slicesY,
            Values = values,
        };
    }

    private static double[]? Slices(DescriptorValue? value, string key) =>
        value is ObjectArrayValue { Columns: var c } && c[key] is UnitFloatsValue u ? u.Values.ToArray()
        : value is ListValue l ? l.Items.Select(i => i is UnitFloatValue f ? f.Value : i is DoubleValue d ? d.Value : 0).ToArray()
        : null;

    private static (double X, double Y)[]? Corners(IReadOnlyList<DescriptorValue>? list)
    {
        if (list is not { Count: 8 }) return null;
        var v = list.Select(i => i is DoubleValue dv ? dv.Value : i is UnitFloatValue u ? u.Value : double.NaN).ToArray();
        if (v.Any(double.IsNaN)) return null;
        return [(v[0], v[1]), (v[2], v[3]), (v[4], v[5]), (v[6], v[7])];
    }

    /// <summary>Finds the embedded file a smart object shows, among the document's 'lnk2' / 'lnk3' / 'lnkD' blocks.</summary>
    public static PsdEmbeddedFile? FindEmbeddedFile(PsdFile file, string uniqueId)
    {
        ArgumentNullException.ThrowIfNull(file);
        foreach (var block in file.GlobalBlocks)
        {
            if (block.Key is not ("lnk2" or "lnk3" or "lnkD") || block.Data is not { } data) continue;
            foreach (var entry in ReadLinkedFiles(data))
                if (entry.UniqueId == uniqueId) return entry;
        }
        return null;
    }

    /// <summary>
    /// The embedded ('liFD') entries of a linked-files block. Each entry: an 8-byte length, the kind ('liFD' embedded,
    /// 'liFE' external, 'liFA' alias), a 4-byte version, the unique ID (Pascal string), the original file name
    /// (Unicode string), file type and creator (4 bytes each), the data length (8 bytes), a flag and, when set, a
    /// versioned descriptor of open-file settings, then for 'liFD' the file itself. Entries are padded to 4 bytes.
    /// </summary>
    public static IEnumerable<PsdEmbeddedFile> ReadLinkedFiles(byte[] data)
    {
        long at = 0;
        while (at + 16 <= data.Length)
        {
            long length = BinaryPrimitives.ReadInt64BigEndian(data.AsSpan((int)at));
            long start = at + 8, end = start + length;
            if (length <= 0 || end > data.Length) yield break;
            var entry = ReadEntry(data, (int)start, (int)end);
            if (entry is not null) yield return entry;
            at = start + ((length + 3) & ~3L);
        }
    }

    private static PsdEmbeddedFile? ReadEntry(byte[] data, int at, int end)
    {
        try
        {
            string kind = Encoding.ASCII.GetString(data, at, 4);
            if (kind != "liFD") return null;
            at += 8; // kind, version
            int idLength = data[at];
            string id = Encoding.ASCII.GetString(data, at + 1, idLength);
            at += 1 + idLength;
            int nameChars = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(at));
            string name = Encoding.BigEndianUnicode.GetString(data, at + 4, nameChars * 2).TrimEnd('\0');
            at += 4 + nameChars * 2;
            string type = Encoding.ASCII.GetString(data, at, 4);
            at += 8; // type, creator
            long size = BinaryPrimitives.ReadInt64BigEndian(data.AsSpan(at));
            at += 8;
            bool hasDescriptor = data[at++] != 0;
            if (hasDescriptor)
            {
                var reader = new DescriptorReader(data, at + 4);
                reader.ReadDescriptor();
                at = reader.Position;
            }
            if (size < 0 || at + size > end) return null;
            return new PsdEmbeddedFile(id, name, type, data.AsSpan(at, (int)size).ToArray());
        }
        catch (Exception e) when (e is PsdFormatException or ArgumentException)
        {
            return null;
        }
    }
}
