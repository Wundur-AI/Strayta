using System.Buffers.Binary;
using System.Text;
using Strayta.Core;
using Strayta.Psd.Descriptors;

namespace Strayta.Psd;

/// <summary>One smart filter of a smart object ("filterFXList" entry of the placed layer's "filterFX").</summary>
/// <param name="Name">What Photoshop shows, e.g. "Gaussian Blur" (may end in "…").</param>
/// <param name="FilterClass">The filter settings' class ID: "GsnB", "MtnB", "boxblur", "UnsM", "AdNs", "HghP", ...</param>
/// <param name="Settings">The filter's settings ("Fltr"), or null.</param>
/// <param name="Source">The whole entry as read or built; edits start from it so unknown keys stay.</param>
public sealed record PsdSmartFilter(string Name, string FilterClass, bool Enabled, BlendMode BlendMode, double Opacity, Descriptor? Settings, Descriptor Source);

/// <summary>A smart object's smart filters ("filterFX"), bottom (applied first) to top.</summary>
public sealed record PsdSmartFilterStack(bool Enabled, bool MaskEnabled, bool MaskLinked, bool MaskExtendWithWhite, IReadOnlyList<PsdSmartFilter> Filters, Descriptor Source);

/// <summary>
/// Editing smart objects in a file: the placed-layer blocks ('SoLd' / 'SoLE' and the legacy 'PlLd', see
/// <see cref="PsdLiveContent"/>), their smart filters, and the document's linked-files block
/// (<see cref="PsdLinkedFiles"/>). Everything returns new records and files; unchanged blocks keep their bytes.
/// <para>
/// Smart filters, as Photoshop writes them in the placed-layer descriptor: "filterFX" (class "filterFXStyle") holds
/// "enab", "validAtPosition", "filterMaskEnable", "filterMaskLinked", "filterMaskExtendWithWhite" and "filterFXList",
/// whose entries (class "filterFX") are "Nm  " (name), "blendOptions" ("Opct" percent, "Md  " BlnM), "enab",
/// "hasoptions", "FrgC" / "BckC" (the colors when applied), "Fltr" (the settings, whose class ID names the filter) and
/// "filterID" (the filter's event ID). Settings of the filters Strayta draws: Gaussian Blur "GsnB" {"Rds " #Pxl},
/// Motion Blur "MtnB" {"Angl" long, "Dstn" #Pxl}, Box Blur "boxblur" {"Rds " #Pxl}, Unsharp Mask "UnsM" {"Amnt" #Prc,
/// "Rds " #Pxl, "Thsh" long}, Add Noise "AdNs" {"Dstr" Dstr Unfr/Gsn, "Nose" #Prc, "Mnch" bool, "FlRs" long seed},
/// High Pass "HghP" {"Rds " #Pxl}. (Structure as documented by the MIT-licensed ag-psd project.)
/// </para>
/// </summary>
public static class PsdSmartObjects
{
    /// <summary>Photoshop's event IDs ("filterID") of the filters Strayta draws.</summary>
    public static IReadOnlyDictionary<string, int> FilterIds { get; } = new Dictionary<string, int>
    {
        ["GsnB"] = 1198747202, ["MtnB"] = 1299476034, ["boxblur"] = 697, ["UnsM"] = 1433301837, ["AdNs"] = 1097092723, ["HghP"] = 1214736464,
    };

    // ---- Placed-layer descriptor ------------------------------------------------------------------------

    /// <summary>The placed-layer descriptor ('SoLd', else 'SoLE'), or null.</summary>
    public static Descriptor? ReadPlaced(PsdLayerRecord record)
    {
        if ((record.FindBlock("SoLd") ?? record.FindBlock("SoLE"))?.Data is not { Length: > 12 } data) return null;
        try
        {
            return DescriptorReader.ReadVersioned(data, 8);
        }
        catch (PsdFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// A copy of <paramref name="record"/> whose placed-layer block holds <paramref name="placed"/>; the legacy 'PlLd'
    /// block (when present) is rebuilt from it so both agree.
    /// </summary>
    public static PsdLayerRecord WithPlaced(PsdLayerRecord record, Descriptor placed)
    {
        var key = record.FindBlock("SoLd") is not null ? "SoLd" : record.FindBlock("SoLE") is not null ? "SoLE" : "SoLd";
        byte[] head = record.FindBlock(key)?.Data is { Length: >= 8 } old ? old[..8] : [.. "soLD"u8, 0, 0, 0, 4];
        byte[] soLd = [.. head, .. DescriptorWriter.WriteVersioned(placed)];
        var blocks = record.Blocks.ToList();
        Replace(blocks, key, soLd);
        if (blocks.Any(b => b.Key == "PlLd")) Replace(blocks, "PlLd", Legacy(placed));
        return record.WithBlocks(blocks, record.Mask);
    }

    private static void Replace(List<TaggedBlock> blocks, string key, byte[] data)
    {
        int at = blocks.FindIndex(b => b.Key == key);
        var block = new TaggedBlock("8BIM", key, 0, data.Length, data);
        if (at < 0) blocks.Add(block);
        else blocks[at] = blocks[at] with { Data = data, Length = data.Length };
    }

    /// <summary>
    /// The legacy 'PlLd' block for a placed-layer descriptor: 'plcL', version 3, the unique ID (Pascal string, no
    /// padding), page number, total pages, anti-alias policy (16) and layer type (2: raster), the eight corner doubles,
    /// a warp version (0) and the versioned warp descriptor.
    /// </summary>
    public static byte[] Legacy(Descriptor placed)
    {
        var o = new MemoryStream();
        void U32(int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); o.Write(b); }
        void F64(double v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteDoubleBigEndian(b, v); o.Write(b); }
        o.Write("plcL"u8);
        U32(3);
        var id = Encoding.ASCII.GetBytes(placed.Text("Idnt") ?? "");
        o.WriteByte((byte)Math.Min(255, id.Length));
        o.Write(id, 0, Math.Min(255, id.Length));
        U32((int)(placed.Number("PgNm") ?? 1));
        U32((int)(placed.Number("totalPages") ?? 1));
        U32((int)(placed.Number("Annt") ?? 16));
        U32((int)(placed.Number("Type") ?? 2));
        var corners = placed.List("nonAffineTransform") ?? placed.List("Trnf") ?? [];
        for (int i = 0; i < 8; i++) F64(i < corners.Count && corners[i] is DoubleValue d ? d.Value : 0);
        U32(0);
        var warp = placed.Object("quiltWarp") ?? placed.Object("warp") ?? WarpDescriptor(new WarpSpec { Bounds = (0, 0, placed.Object("Sz  ")?.Number("Wdth") ?? 0, placed.Object("Sz  ")?.Number("Hght") ?? 0) });
        o.Write(DescriptorWriter.WriteVersioned(warp));
        return o.ToArray();
    }

    /// <summary>
    /// A new placed-layer descriptor like Photoshop's for embedded raster content: the unique ID of its linked-files
    /// entry, a placed-layer ID, the content size ("Sz"), the corners (both "Trnf" and "nonAffineTransform") and no warp.
    /// </summary>
    public static Descriptor NewPlaced(string uniqueId, string placedId, double width, double height, IReadOnlyList<(double X, double Y)> corners, double resolution)
    {
        static DescriptorValue Int(int v) => new IntegerValue(v);
        static Descriptor Obj(string cls, params (string Key, DescriptorValue Value)[] items) =>
            new() { ClassId = cls, Items = items.Select(i => new KeyValuePair<string, DescriptorValue>(i.Key, i.Value)).ToList() };
        var list = new ListValue(corners.SelectMany(c => new DescriptorValue[] { new DoubleValue(c.X), new DoubleValue(c.Y) }).ToList());
        return Obj("null",
            ("Idnt", new TextValue(uniqueId)),
            ("placed", new TextValue(placedId)),
            ("PgNm", Int(1)),
            ("totalPages", Int(1)),
            ("Crop", Int(1)),
            ("frameStep", new ObjectValue(Obj("null", ("numerator", Int(0)), ("denominator", Int(600))))),
            ("duration", new ObjectValue(Obj("null", ("numerator", Int(0)), ("denominator", Int(600))))),
            ("frameCount", Int(1)),
            ("Annt", Int(16)),
            ("Type", Int(2)),
            ("Trnf", list),
            ("nonAffineTransform", list),
            ("warp", new ObjectValue(WarpDescriptor(new WarpSpec { Bounds = (0, 0, width, height) }))),
            ("Sz  ", new ObjectValue(Obj("Pnt ", ("Wdth", new DoubleValue(width)), ("Hght", new DoubleValue(height))))),
            ("Rslt", new UnitFloatValue("#Rsl", resolution)),
            ("comp", Int(-1)),
            ("compInfo", new ObjectValue(Obj("null", ("compID", Int(-1)), ("originalCompID", Int(-1))))));
    }

    /// <summary>The "warp" descriptor of a warp spec (styles and 4×4 custom meshes; split meshes keep their slices).</summary>
    public static Descriptor WarpDescriptor(WarpSpec warp)
    {
        var items = new List<KeyValuePair<string, DescriptorValue>>
        {
            new("warpStyle", new EnumValue("warpStyle", warp.Style)),
            new("warpValue", new DoubleValue(warp.Value)),
            new("warpPerspective", new DoubleValue(warp.Perspective)),
            new("warpPerspectiveOther", new DoubleValue(warp.PerspectiveOther)),
            new("warpRotate", new EnumValue("Ornt", warp.Vertical ? "Vrtc" : "Hrzn")),
            new("bounds", new ObjectValue(new Descriptor
            {
                ClassId = "classFloatRect",
                Items =
                [
                    new("Top ", new DoubleValue(warp.Bounds.Top)), new("Left", new DoubleValue(warp.Bounds.Left)),
                    new("Btom", new DoubleValue(warp.Bounds.Bottom)), new("Rght", new DoubleValue(warp.Bounds.Right)),
                ],
            })),
            new("uOrder", new IntegerValue(4)),
            new("vOrder", new IntegerValue(4)),
        };
        if (warp.Style == "warpCustom" && warp.Mesh is { } mesh)
        {
            var envelope = new List<KeyValuePair<string, DescriptorValue>>();
            if (warp.Rows != 4 || warp.Columns != 4)
            {
                items.Add(new("deformNumRows", new IntegerValue(warp.Rows)));
                items.Add(new("deformNumCols", new IntegerValue(warp.Columns)));
            }
            if (warp.SlicesX is { } sx) envelope.Add(new("quiltSliceX", Slices("quiltSliceX", sx)));
            if (warp.SlicesY is { } sy) envelope.Add(new("quiltSliceY", Slices("quiltSliceY", sy)));
            envelope.Add(new("meshPoints", new ObjectArrayValue(mesh.Count, new Descriptor
            {
                ClassId = "rationalPoint",
                Items = [new("Hrzn", new UnitFloatsValue("#Pxl", mesh.Select(p => p.X).ToArray())), new("Vrtc", new UnitFloatsValue("#Pxl", mesh.Select(p => p.Y).ToArray()))],
            })));
            items.Add(new("customEnvelopeWarp", new ObjectValue(new Descriptor { ClassId = "customEnvelopeWarp", Items = envelope })));
        }
        return new Descriptor { ClassId = "warp", Items = items };

        static ObjectArrayValue Slices(string key, IReadOnlyList<double> values) =>
            new(values.Count, new Descriptor { ClassId = "UntF", Items = [new(key, new UnitFloatsValue("#Pxl", values.ToArray()))] });
    }

    /// <summary>A copy of a descriptor with <paramref name="key"/> set (replaced in place, or appended).</summary>
    public static Descriptor With(this Descriptor d, string key, DescriptorValue value)
    {
        var items = d.Items.ToList();
        int at = items.FindIndex(kv => kv.Key == key);
        if (at >= 0) items[at] = new(key, value);
        else items.Add(new(key, value));
        return new Descriptor { Name = d.Name, ClassId = d.ClassId, Items = items };
    }

    /// <summary>A copy of a descriptor without <paramref name="key"/>.</summary>
    public static Descriptor Without(this Descriptor d, string key) =>
        new() { Name = d.Name, ClassId = d.ClassId, Items = d.Items.Where(kv => kv.Key != key).ToList() };

    /// <summary>A copy with other unique and placed-layer IDs (New Smart Object via Copy).</summary>
    public static PsdLayerRecord WithIds(PsdLayerRecord record, string uniqueId, string placedId)
    {
        var placed = ReadPlaced(record) ?? throw new ArgumentException("The layer is not a smart object.", nameof(record));
        return WithPlaced(record, placed.With("Idnt", new TextValue(uniqueId)).With("placed", new TextValue(placedId)));
    }

    /// <summary>
    /// A copy for content of another size (Edit Contents or Replace Contents changed it): like Photoshop, the content
    /// keeps its scale and stays centered where the old content was, and a warp is scaled to the new size.
    /// </summary>
    public static PsdLayerRecord WithContentSize(PsdLayerRecord record, double width, double height)
    {
        var placed = ReadPlaced(record) ?? throw new ArgumentException("The layer is not a smart object.", nameof(record));
        double oldW = placed.Object("Sz  ")?.Number("Wdth") ?? width, oldH = placed.Object("Sz  ")?.Number("Hght") ?? height;
        if (Math.Abs(oldW - width) < 1e-9 && Math.Abs(oldH - height) < 1e-9) return record;
        // Corners: the old placement extended to the new rectangle centered on the old one.
        double x0 = (oldW - width) / 2, y0 = (oldH - height) / 2;
        (double, double)[] rect = [(x0, y0), (x0 + width, y0), (x0 + width, y0 + height), (x0, y0 + height)];
        DescriptorValue Map(string key)
        {
            if (placed.List(key) is not { Count: 8 } list) return placed[key]!;
            var quad = Enumerable.Range(0, 4).Select(i => (X: Num(list[2 * i]), Y: Num(list[2 * i + 1]))).ToArray();
            var points = rect.Select(p => Bilinear(quad, oldW, oldH, p.Item1, p.Item2));
            return new ListValue(points.SelectMany(p => new DescriptorValue[] { new DoubleValue(p.X), new DoubleValue(p.Y) }).ToList());
        }
        var updated = placed;
        foreach (var key in new[] { "Trnf", "nonAffineTransform" })
            if (placed.Has(key)) updated = updated.With(key, Map(key));
        updated = updated.With("Sz  ", new ObjectValue(new Descriptor
        {
            Name = placed.Object("Sz  ")?.Name ?? "",
            ClassId = "Pnt ",
            Items = [new("Wdth", new DoubleValue(width)), new("Hght", new DoubleValue(height))],
        }));
        double kx = width / oldW, ky = height / oldH;
        foreach (var key in new[] { "warp", "quiltWarp" })
            if (PsdLiveContent.ReadWarp(placed.Object(key)) is { } w)
            {
                var scaled = w with
                {
                    Bounds = (w.Bounds.Left * kx, w.Bounds.Top * ky, w.Bounds.Right * kx, w.Bounds.Bottom * ky),
                    Mesh = w.Mesh?.Select(p => (p.X * kx, p.Y * ky)).ToArray(),
                    SlicesX = w.SlicesX?.Select(v => v * kx).ToArray(),
                    SlicesY = w.SlicesY?.Select(v => v * ky).ToArray(),
                };
                // Styles only need their bounds; a custom mesh is rewritten. Unknown keys of the old warp stay.
                var old = placed.Object(key)!;
                var fresh = WarpDescriptor(scaled);
                var merged = old;
                foreach (var (k, v) in fresh.Items) if (old.Has(k)) merged = merged.With(k, v);
                if (w.Values is { Count: >= 4 } values && old.Has("warpValues"))
                {
                    var vs = values.ToArray();
                    vs[0] *= kx; vs[2] *= kx; vs[1] *= ky; vs[3] *= ky;
                    merged = merged.With("warpValues", new ListValue(vs.Select(v => (DescriptorValue)new DoubleValue(v)).ToList()));
                }
                updated = updated.With(key, new ObjectValue(merged));
            }
        return WithPlaced(record, updated);
    }

    private static double Num(DescriptorValue v) => v switch { DoubleValue d => d.Value, UnitFloatValue u => u.Value, _ => 0 };

    /// <summary>The perspective map of the quad, extended to a point outside the content rectangle.</summary>
    private static (double X, double Y) Bilinear((double X, double Y)[] quad, double w, double h, double x, double y)
    {
        // Heckbert's unit square to quad, as in Rendering's Projective.RectToQuad.
        var (x0, y0) = quad[0]; var (x1, y1) = quad[1]; var (x2, y2) = quad[2]; var (x3, y3) = quad[3];
        double sx = x0 - x1 + x2 - x3, sy = y0 - y1 + y2 - y3, g = 0, hh = 0;
        if (Math.Abs(sx) > 1e-12 || Math.Abs(sy) > 1e-12)
        {
            double dx1 = x1 - x2, dx2 = x3 - x2, dy1 = y1 - y2, dy2 = y3 - y2, den = dx1 * dy2 - dx2 * dy1;
            if (Math.Abs(den) > 1e-18) { g = (sx * dy2 - dx2 * sy) / den; hh = (dx1 * sy - sx * dy1) / den; }
        }
        double u = x / w, v = y / h;
        double wq = g * u + hh * v + 1;
        return (((x1 - x0 + g * x1) * u + (x3 - x0 + hh * x3) * v + x0) / wq, ((y1 - y0 + g * y1) * u + (y3 - y0 + hh * y3) * v + y0) / wq);
    }

    // ---- Smart filters ---------------------------------------------------------------------------------

    /// <summary>The smart filters of a placed-layer descriptor, or null when it has none.</summary>
    public static PsdSmartFilterStack? ReadFilters(Descriptor placed)
    {
        if (placed.Object("filterFX") is not { } fx) return null;
        var filters = new List<PsdSmartFilter>();
        foreach (var item in fx.List("filterFXList") ?? [])
        {
            if (item is not ObjectValue { Value: var f }) continue;
            var blend = f.Object("blendOptions");
            var settings = f.Object("Fltr");
            filters.Add(new PsdSmartFilter(
                f.Text("Nm  ") ?? settings?.Name ?? "Filter",
                settings?.ClassId.TrimEnd() ?? "",
                f.Bool("enab") ?? true,
                PsdEffects.BlendModeOf(blend?.Enum("Md  ")),
                (blend?.Number("Opct") ?? 100) / 100,
                settings,
                f));
        }
        return new PsdSmartFilterStack(fx.Bool("enab") ?? true, fx.Bool("filterMaskEnable") ?? false, fx.Bool("filterMaskLinked") ?? true,
            fx.Bool("filterMaskExtendWithWhite") ?? true, filters, fx);
    }

    /// <summary>The placed-layer descriptor with <paramref name="stack"/> as its smart filters (null removes them).</summary>
    public static Descriptor WithFilters(Descriptor placed, PsdSmartFilterStack? stack)
    {
        if (stack is null || stack.Filters.Count == 0) return placed.Without("filterFX");
        var fx = stack.Source
            .With("enab", new BoolValue(stack.Enabled))
            .With("filterMaskEnable", new BoolValue(stack.MaskEnabled))
            .With("filterMaskLinked", new BoolValue(stack.MaskLinked))
            .With("filterMaskExtendWithWhite", new BoolValue(stack.MaskExtendWithWhite))
            .With("filterFXList", new ListValue(stack.Filters.Select(f => (DescriptorValue)new ObjectValue(Entry(f))).ToList()));
        if (placed.Has("filterFX")) return placed.With("filterFX", new ObjectValue(fx));
        // Photoshop keeps it after the warp, before "Sz  ".
        var items = placed.Items.ToList();
        int at = items.FindIndex(kv => kv.Key == "Sz  ");
        items.Insert(at < 0 ? items.Count : at, new("filterFX", new ObjectValue(fx)));
        return new Descriptor { Name = placed.Name, ClassId = placed.ClassId, Items = items };
    }

    /// <summary>A new, empty smart-filter stack (as Photoshop starts one).</summary>
    public static PsdSmartFilterStack NewStack() => new(true, false, true, true, [], new Descriptor
    {
        ClassId = "filterFXStyle",
        Items =
        [
            new("enab", new BoolValue(true)), new("validAtPosition", new BoolValue(true)), new("filterMaskEnable", new BoolValue(false)),
            new("filterMaskLinked", new BoolValue(true)), new("filterMaskExtendWithWhite", new BoolValue(true)), new("filterFXList", new ListValue([])),
        ],
    });

    /// <summary>A new smart filter entry with the given settings (class ID = filter), black and white as the colors.</summary>
    public static PsdSmartFilter NewFilter(string name, Descriptor settings)
    {
        var source = new Descriptor
        {
            ClassId = "filterFX",
            Items =
            [
                new("Nm  ", new TextValue(name)),
                new("blendOptions", new ObjectValue(new Descriptor
                {
                    ClassId = "blendOptions",
                    Items = [new("Opct", new UnitFloatValue("#Prc", 100)), new("Md  ", new EnumValue("BlnM", "Nrml"))],
                })),
                new("enab", new BoolValue(true)),
                new("hasoptions", new BoolValue(true)),
                new("FrgC", new ObjectValue(Rgb(0, 0, 0))),
                new("BckC", new ObjectValue(Rgb(255, 255, 255))),
                new("Fltr", new ObjectValue(settings)),
                new("filterID", new IntegerValue(FilterIds.GetValueOrDefault(settings.ClassId, 0))),
            ],
        };
        return new PsdSmartFilter(name, settings.ClassId, true, BlendMode.Normal, 1, settings, source);
    }

    private static Descriptor Rgb(double r, double g, double b) => new()
    {
        ClassId = "RGBC",
        Items = [new("Rd  ", new DoubleValue(r)), new("Grn ", new DoubleValue(g)), new("Bl  ", new DoubleValue(b))],
    };

    /// <summary>The entry's descriptor with the modeled fields written back.</summary>
    private static Descriptor Entry(PsdSmartFilter f)
    {
        var blend = (f.Source.Object("blendOptions") ?? new Descriptor { ClassId = "blendOptions" })
            .With("Opct", new UnitFloatValue("#Prc", Math.Round(f.Opacity * 100, 6)))
            .With("Md  ", new EnumValue("BlnM", PsdEffects.DescriptorKeyOf(f.BlendMode)));
        var d = f.Source.With("Nm  ", new TextValue(f.Name)).With("blendOptions", new ObjectValue(blend)).With("enab", new BoolValue(f.Enabled));
        if (f.Settings is { } s) d = d.With("Fltr", new ObjectValue(s));
        return d;
    }

    // ---- Linked files -----------------------------------------------------------------------------------

    /// <summary>The linked-files entry with this unique ID (embedded or external), or null.</summary>
    public static PsdLinkedFile? FindLinkedFile(PsdFile file, string uniqueId)
    {
        foreach (var block in file.GlobalBlocks)
            if (block.Key is "lnk2" or "lnk3" or "lnkD" && block.Data is { } data)
                foreach (var e in PsdLinkedFiles.Read(data))
                    if (e.UniqueId == uniqueId) return e;
        return null;
    }

    /// <summary>
    /// A copy of the file whose linked-files entry with <paramref name="entry"/>'s unique ID is replaced by it (or, when
    /// there is none, added to the 'lnk2' block, created if needed). Other blocks keep their bytes.
    /// </summary>
    public static PsdFile WithLinkedFile(PsdFile file, PsdLinkedFile entry)
    {
        var blocks = file.GlobalBlocks.ToList();
        for (int i = 0; i < blocks.Count; i++)
        {
            if (blocks[i].Key is not ("lnk2" or "lnk3" or "lnkD") || blocks[i].Data is not { } data) continue;
            var entries = PsdLinkedFiles.Read(data);
            int at = entries.ToList().FindIndex(e => e.UniqueId == entry.UniqueId);
            if (at < 0) continue;
            if (!PsdLinkedFiles.IsFullyReadable(data)) throw new InvalidOperationException("The document's linked-files block cannot be rewritten safely.");
            var list = entries.ToList();
            list[at] = entry;
            var bytes = PsdLinkedFiles.Write(list);
            blocks[i] = blocks[i] with { Data = bytes, Length = bytes.Length };
            return file.WithGlobalBlocks(blocks);
        }
        // Not present: embedded files go to 'lnk2' (external ones to 'lnkE'? Photoshop keeps both kinds in lnk2 too).
        int target = blocks.FindIndex(b => b.Key == "lnk2" && b.Data is not null);
        if (target >= 0)
        {
            var bytes = PsdLinkedFiles.Write([.. PsdLinkedFiles.Read(blocks[target].Data!), entry]);
            if (!PsdLinkedFiles.IsFullyReadable(blocks[target].Data!)) throw new InvalidOperationException("The document's linked-files block cannot be rewritten safely.");
            blocks[target] = blocks[target] with { Data = bytes, Length = bytes.Length };
        }
        else
        {
            var bytes = PsdLinkedFiles.Write([entry]);
            // Photoshop writes the linked files before the pattern and text engine blocks; order is not significant to readers.
            blocks.Insert(0, new TaggedBlock("8BIM", "lnk2", 0, bytes.Length, bytes));
        }
        return file.WithGlobalBlocks(blocks);
    }

    /// <summary>A copy of the file without the linked-files entries no layer refers to any more.</summary>
    public static PsdFile WithoutUnusedLinkedFiles(PsdFile file, ISet<string> used)
    {
        var blocks = file.GlobalBlocks.ToList();
        bool changed = false;
        for (int i = 0; i < blocks.Count; i++)
        {
            if (blocks[i].Key is not ("lnk2" or "lnk3" or "lnkD") || blocks[i].Data is not { } data || !PsdLinkedFiles.IsFullyReadable(data)) continue;
            var entries = PsdLinkedFiles.Read(data);
            var kept = entries.Where(e => used.Contains(e.UniqueId)).ToList();
            if (kept.Count == entries.Count) continue;
            var bytes = PsdLinkedFiles.Write(kept);
            blocks[i] = blocks[i] with { Data = bytes, Length = bytes.Length };
            changed = true;
        }
        return changed ? file.WithGlobalBlocks(blocks) : file;
    }

    /// <summary>A new embedded entry (version 7, as Photoshop CC writes when there is no content ID).</summary>
    public static PsdLinkedFile NewEmbedded(string uniqueId, string name, string fileType, byte[] data) => new()
    {
        Kind = "liFD",
        Version = 7,
        UniqueId = uniqueId,
        Name = name,
        FileType = fileType,
        Creator = "8BIM",
        Data = data,
        OpenDescriptor = new Descriptor
        {
            ClassId = "null",
            Items = [new("compInfo", new ObjectValue(new Descriptor { ClassId = "null", Items = [new("compID", new IntegerValue(-1)), new("originalCompID", new IntegerValue(-1))] }))],
        },
    };

    /// <summary>The Mac file type Photoshop records for a file name's extension.</summary>
    public static string FileTypeFor(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".psb" => "8BPB",
        ".psd" => "8BPS",
        ".png" => "png ",
        ".jpg" or ".jpeg" => "JPEG",
        ".tif" or ".tiff" => "TIFF",
        ".gif" => "GIFf",
        _ => "    ",
    };

    /// <summary>A fresh ID in Photoshop's form (a lowercase UUID).</summary>
    public static string NewId() => Guid.NewGuid().ToString();
}
