using System.Buffers.Binary;
using System.Text;
using Strayta.Core;
using Strayta.Psd.Descriptors;
using LibWriter = Strayta.Psd.Descriptors.DescriptorWriter;

namespace Strayta.Psd.Tests;

/// <summary>
/// Live content (smart objects, type, vector shapes, guides) follows affine changes of the document in the file
/// data itself, so Photoshop opens transformed, cropped or resized layers still editable, in the right place.
/// </summary>
public class LiveContentTests
{
    private static readonly (double X, double Y)[] Corners = [(10, 20), (110, 20), (110, 70), (10, 70)];

    private static ListValue Doubles(IEnumerable<double> values) => new(values.Select(v => (DescriptorValue)new DoubleValue(v)).ToList());

    private static Descriptor PlacedDescriptor(bool warped = false) => new()
    {
        ClassId = "null",
        Items =
        [
            new("Idnt", new TextValue("id-1")),
            new("placed", new TextValue("id-2")),
            new("Trnf", Doubles(Corners.SelectMany(c => new[] { c.X, c.Y }))),
            new("nonAffineTransform", Doubles(Corners.SelectMany(c => new[] { c.X, c.Y }))),
            new("warp", new ObjectValue(new Descriptor
            {
                ClassId = "warp",
                Items =
                [
                    new("warpStyle", new EnumValue("warpStyle", warped ? "warpArc" : "warpNone")),
                    new("warpValue", new DoubleValue(0)),
                    new("bounds", new ObjectValue(new Descriptor
                    {
                        ClassId = "classFloatRect",
                        Items = [new("Top ", new DoubleValue(0)), new("Left", new DoubleValue(0)), new("Btom", new DoubleValue(50)), new("Rght", new DoubleValue(100))],
                    })),
                ],
            })),
            new("Sz  ", new ObjectValue(new Descriptor { ClassId = "Pnt ", Items = [new("Wdth", new DoubleValue(100)), new("Hght", new DoubleValue(50))] })),
            new("Rslt", new UnitFloatValue("#Rsl", 72)),
        ],
    };

    private static byte[] SoLd(bool warped = false) => [.. "soLD"u8, 0, 0, 0, 4, .. LibWriter.WriteVersioned(PlacedDescriptor(warped))];

    private static byte[] PlLd()
    {
        var o = new MemoryStream();
        o.Write("plcL"u8);
        o.Write([0, 0, 0, 3]);
        o.WriteByte(4);
        o.Write("id-1"u8);
        o.Write([0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 16, 0, 0, 0, 2]);
        Span<byte> d = stackalloc byte[8];
        foreach (var (x, y) in Corners)
        {
            BinaryPrimitives.WriteDoubleBigEndian(d, x);
            o.Write(d);
            BinaryPrimitives.WriteDoubleBigEndian(d, y);
            o.Write(d);
        }
        o.Write([0, 0, 0, 0, 0, 0, 0, 16]);
        o.Write(LibWriter.Write(new Descriptor { ClassId = "warp", Items = [new("warpStyle", new EnumValue("warpStyle", "warpNone"))] }));
        return o.ToArray();
    }

    private static TaggedBlock Block(string key, byte[] data) => new("8BIM", key, 0, data.Length, data);

    private static readonly CanvasMap Turn = Rotation(10 * Math.PI / 180, 5, -3);

    private static CanvasMap Rotation(double radians, double dx, double dy)
    {
        var (sin, cos) = Math.SinCos(radians);
        return new CanvasMap(cos, -sin, sin, cos, dx, dy);
    }

    [Fact]
    public void Smart_object_corners_follow_a_turn_and_nothing_else_changes()
    {
        var record = new PsdLayerRecord { Blocks = [Block("SoLd", SoLd()), Block("PlLd", PlLd())] };
        var moved = PsdCanvas.WithCanvas(record, 200, 100, 180, 90, Turn);

        var so = PsdLiveContent.ReadSmartObject(moved)!;
        Assert.False(so.Warped);
        Assert.Equal("id-1", so.UniqueId);
        for (int i = 0; i < 4; i++)
        {
            var (x, y) = Turn.Apply(Corners[i].X, Corners[i].Y);
            Assert.Equal(x, so.Corners[i].X, 9);
            Assert.Equal(y, so.Corners[i].Y, 9);
        }
        // Trnf too, and the descriptor is otherwise untouched (same length, same warp and size).
        var d = DescriptorReader.ReadVersioned(moved.FindBlock("SoLd")!.Data!, 8);
        Assert.Equal(Turn.Apply(110, 70).X, ((DoubleValue)d.List("Trnf")![4]).Value, 9);
        Assert.Equal(SoLd().Length, moved.FindBlock("SoLd")!.Data!.Length);
        Assert.Equal(100, d.Object("warp")!.Object("bounds")!.Number("Rght"));

        var legacy = moved.FindBlock("PlLd")!.Data!;
        Assert.Equal(PlLd().Length, legacy.Length);
        int at = 8 + 1 + 4 + 16;
        Assert.Equal(Turn.Apply(10, 20).X, BinaryPrimitives.ReadDoubleBigEndian(legacy.AsSpan(at)), 9);
        Assert.Equal(Turn.Apply(10, 70).Y, BinaryPrimitives.ReadDoubleBigEndian(legacy.AsSpan(at + 56)), 9);
    }

    [Fact]
    public void Warped_smart_objects_are_recognized()
    {
        var record = new PsdLayerRecord { Blocks = [Block("SoLd", SoLd(warped: true))] };
        Assert.True(PsdLiveContent.ReadSmartObject(record)!.Warped);
    }

    [Fact]
    public void Object_arrays_in_warp_meshes_are_read_and_written()
    {
        var mesh = new Descriptor
        {
            ClassId = "null",
            Items =
            [
                new("meshPoints", new ObjectArrayValue(2, new Descriptor
                {
                    ClassId = "rationalPoint",
                    Items = [new("Hrzn", new UnitFloatsValue("#Pxl", [1, 2])), new("Vrtc", new UnitFloatsValue("#Pxl", [3, 4]))],
                })),
                new("after", new DoubleValue(7)),
            ],
        };
        var bytes = LibWriter.WriteVersioned(mesh);
        var offsets = new List<(string Path, int Offset)>();
        var reader = new DescriptorReader(bytes, 4) { NumberOffsets = offsets };
        var again = reader.ReadDescriptor();
        var array = (ObjectArrayValue)again["meshPoints"]!;
        Assert.Equal(2, array.Count);
        Assert.Equal([3.0, 4.0], ((UnitFloatsValue)array.Columns["Vrtc"]!).Values);
        Assert.Equal(7, again.Number("after"));
        var (path, offset) = Assert.Single(offsets);
        Assert.Equal("after", path);
        Assert.Equal(7, BinaryPrimitives.ReadDoubleBigEndian(bytes.AsSpan(offset)));
        Assert.Equal(bytes, LibWriter.WriteVersioned(again));
    }

    private static byte[] Vogk(bool radii) => [0, 0, 0, 1, .. LibWriter.WriteVersioned(new Descriptor
    {
        ClassId = "null",
        Items =
        [
            new("keyDescriptorList", new ListValue(
            [
                new ObjectValue(new Descriptor
                {
                    ClassId = "null",
                    Items =
                    [
                        new("keyOriginType", new IntegerValue(radii ? 2 : 1)),
                        .. radii
                            ? new KeyValuePair<string, DescriptorValue>[]
                            {
                                new("keyOriginRRectRadii", new ObjectValue(new Descriptor
                                {
                                    ClassId = "radii",
                                    Items =
                                    [
                                        new("unitValueQuadVersion", new IntegerValue(1)),
                                        new("topRight", new UnitFloatValue("#Pxl", 4)), new("topLeft", new UnitFloatValue("#Pxl", 4)),
                                        new("bottomLeft", new UnitFloatValue("#Pxl", 4)), new("bottomRight", new UnitFloatValue("#Pxl", 4)),
                                    ],
                                })),
                            }
                            : [],
                        new("keyOriginShapeBBox", new ObjectValue(new Descriptor
                        {
                            ClassId = "unitRect",
                            Items =
                            [
                                new("unitValueQuadVersion", new IntegerValue(1)),
                                new("Top ", new UnitFloatValue("#Pxl", 10)), new("Left", new UnitFloatValue("#Pxl", 20)),
                                new("Btom", new UnitFloatValue("#Pxl", 30)), new("Rght", new UnitFloatValue("#Pxl", 60)),
                            ],
                        })),
                        new("keyOriginIndex", new IntegerValue(0)),
                    ],
                }),
            ])),
        ],
    })];

    private static Descriptor Shape(byte[] vogk) =>
        ((ObjectValue)DescriptorReader.ReadVersioned(vogk, 4).List("keyDescriptorList")![0]).Value;

    [Fact]
    public void Live_shapes_move_and_scale_and_become_paths_when_turned()
    {
        var scaled = PsdLiveContent.TransformOrigination(Vogk(radii: true), new CanvasMap(2, 0, 0, 2, 5, 1));
        var shape = Shape(scaled);
        var box = shape.Object("keyOriginShapeBBox")!;
        Assert.Equal((21.0, 45.0, 61.0, 125.0), (box.Number("Top ")!.Value, box.Number("Left")!.Value, box.Number("Btom")!.Value, box.Number("Rght")!.Value));
        Assert.Equal(8, shape.Object("keyOriginRRectRadii")!.Number("topLeft"));
        Assert.Null(shape.Bool("keyShapeInvalidated"));

        // A stretch keeps a plain rectangle live but not rounded corners, which would turn elliptical.
        Assert.Equal(60, Shape(PsdLiveContent.TransformOrigination(Vogk(radii: false), new CanvasMap(1, 0, 0, 2, 0, 0))).Object("keyOriginShapeBBox")!.Number("Btom"));
        Assert.True(Shape(PsdLiveContent.TransformOrigination(Vogk(radii: true), new CanvasMap(1, 0, 0, 2, 0, 0))).Bool("keyShapeInvalidated"));

        var turned = Shape(PsdLiveContent.TransformOrigination(Vogk(radii: false), Turn));
        Assert.True(turned.Bool("keyShapeInvalidated"));
        Assert.Equal(20, turned.Object("keyOriginShapeBBox")!.Number("Left")); // kept as it was: the path now rules
    }

    private static byte[] Guides(params (double Location, bool Horizontal)[] guides)
    {
        var data = new byte[16 + guides.Length * 5];
        BinaryPrimitives.WriteInt32BigEndian(data, 1);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(12), guides.Length);
        for (int i = 0; i < guides.Length; i++)
        {
            BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(16 + i * 5), (int)(guides[i].Location * 32));
            data[20 + i * 5] = guides[i].Horizontal ? (byte)1 : (byte)0;
        }
        return data;
    }

    private static (double Location, bool Horizontal) Guide(byte[] data, int i) =>
        (BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(16 + i * 5)) / 32.0, data[20 + i * 5] != 0);

    [Fact]
    public void Guides_turn_exactly_by_quarter_turns_and_stay_right_at_the_center_otherwise()
    {
        // A quarter turn clockwise of a 200×100 canvas onto 100×200: (x, y) → (100 - y, x).
        var quarter = new CanvasMap(0, -1, 1, 0, 100, 0);
        var turned = PsdCanvas.RemapGuides(Guides((50, false), (20, true)), 100, 200, quarter);
        Assert.Equal((50.0, true), Guide(turned, 0)); // vertical x = 50 becomes horizontal y = 50
        Assert.Equal((80.0, false), Guide(turned, 1)); // horizontal y = 20 becomes vertical x = 80

        // A slight turn: the guide passes through where the turned line crosses the middle of the new canvas.
        var slight = Turn;
        var result = PsdCanvas.RemapGuides(Guides((50, false)), 180, 90, slight);
        var (location, horizontal) = Guide(result, 0);
        Assert.False(horizontal);
        // Point on the old guide that lands at y = 45: solve through the inverse map.
        var (sin, cos) = Math.SinCos(10 * Math.PI / 180);
        double t = (45 - (sin * 50 + 3 * -1)) / cos; // y = sin·x + cos·y + dy with dy = -3
        var (x, y) = slight.Apply(50, t);
        Assert.Equal(45, y, 6);
        Assert.Equal(x, location, 1); // stored in 1/32 px
    }

    [Fact]
    public void Embedded_files_are_found_by_unique_id()
    {
        byte[] payload = [1, 2, 3, 4, 5];
        var entry = new MemoryStream();
        entry.Write("liFD"u8);
        entry.Write([0, 0, 0, 7]);
        entry.WriteByte(4);
        entry.Write("id-1"u8);
        var name = Encoding.BigEndianUnicode.GetBytes("a.png\0");
        entry.Write([0, 0, 0, (byte)(name.Length / 2)]);
        entry.Write(name);
        entry.Write("png "u8);
        entry.Write("8BIM"u8);
        entry.Write([0, 0, 0, 0, 0, 0, 0, (byte)payload.Length]);
        entry.WriteByte(1); // has an open-file descriptor
        entry.Write(LibWriter.WriteVersioned(new Descriptor { ClassId = "null", Items = [new("compInfo", new IntegerValue(0))] }));
        entry.Write(payload);
        var body = entry.ToArray();
        var block = new byte[8 + ((body.Length + 3) & ~3)];
        BinaryPrimitives.WriteInt64BigEndian(block, body.Length);
        body.CopyTo(block, 8);

        var file = new PsdFile { Header = new PsdHeader(1, 3, 10, 10, 8, ColorMode.Rgb), GlobalBlocks = [Block("lnk2", block)] };
        var found = PsdLiveContent.FindEmbeddedFile(file, "id-1")!;
        Assert.Equal(("a.png", "png "), (found.Name, found.FileType));
        Assert.Equal(payload, found.Data);
        Assert.Null(PsdLiveContent.FindEmbeddedFile(file, "other"));
    }

    [Fact]
    public void Scale_styles_scales_modeled_and_unmodeled_pixel_settings_and_reaches_the_file()
    {
        var bevelSource = new Descriptor
        {
            ClassId = "ebbl",
            Items = [new("enab", new BoolValue(true)), new("present", new BoolValue(true)), new("srgR", new UnitFloatValue("#Prc", 100)),
                new("blur", new UnitFloatValue("#Pxl", 5)), new("Sftn", new UnitFloatValue("#Pxl", 2))],
        };
        var effects = new LayerEffects(
        [
            new DropShadowEffect { Distance = 10, Size = 4, Spread = 0.2f },
            new StrokeEffect { Size = 3 },
            new UnsupportedEffect("Bevel & Emboss") { SourceData = new PsdEffectSource("ebbl", bevelSource) },
        ]);
        var scaled = PsdEffectScaling.Scale(effects, 0.5)!;
        var shadow = (DropShadowEffect)scaled.Items[0];
        Assert.Equal((5f, 2f, 0.2f), (shadow.Distance, shadow.Size, shadow.Spread));
        Assert.Equal(1.5f, ((StrokeEffect)scaled.Items[1]).Size);
        var bevel = ((PsdEffectSource)scaled.Items[2].SourceData!).Descriptor;
        Assert.Equal((2.5, 1.0, 100.0), (bevel.Number("blur")!.Value, bevel.Number("Sftn")!.Value, bevel.Number("srgR")!.Value));

        // Written through the record, the scaled bevel survives saving even though the model cannot tell it changed.
        var doc = new Document(20, 20, ColorMode.Rgb, 8);
        var layer = new PixelLayer { Name = "L", Bounds = new PixelRect(0, 0, 4, 4), Pixels = new Raster(ColorMode.Rgb, [Plane.Create(4, 4, 8), Plane.Create(4, 4, 8), Plane.Create(4, 4, 8)], Plane.Create(4, 4, 8)) };
        layer.Effects = scaled;
        layer.SourceData = PsdEffectScaling.WithEffects(new PsdLayerRecord { Blocks = [Block("lfx2", PsdEffectsWriter.Encode(effects))] }, scaled);
        doc.Root.Add(layer);
        var ms = new MemoryStream();
        PsdWriter.Write(doc, ms);
        ms.Position = 0;
        var saved = PsdFile.Read(ms).Layers.Single();
        var read = PsdEffects.Read(saved, 120)!;
        Assert.Equal(5f, ((DropShadowEffect)read.Items.OfType<DropShadowEffect>().Single()).Distance);
        var savedBevel = DescriptorReader.ReadVersioned(saved.FindBlock("lfx2")!.Data!, 4).Object("ebbl")!;
        Assert.Equal(2.5, savedBevel.Number("blur"));
    }

    // ---- Real files ------------------------------------------------------------------------------

    private static readonly string? CorpusDir = Environment.GetEnvironmentVariable("STRAYTA_CORPUS");

    public static TheoryData<string> LiveFiles()
    {
        var data = new TheoryData<string>();
        if (CorpusDir is null || !Directory.Exists(CorpusDir))
        {
            data.Add("");
            return data;
        }
        foreach (var f in Directory.EnumerateFiles(CorpusDir, "*.ps?", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(f).StartsWith("._")) continue;
            var file = PsdFile.Open(f, new PsdReadOptions { SkipLayerPixels = true, SkipComposite = true });
            if (file.Layers.Any(r => r.Blocks.Any(b => b.Key is "SoLd" or "PlLd" or "TySh" or "vogk" or "vmsk" or "vsms")))
                data.Add(Path.GetRelativePath(CorpusDir, f));
        }
        if (data.Count == 0) data.Add("");
        return data;
    }

    [Theory]
    [MemberData(nameof(LiveFiles))]
    public void Real_live_layers_follow_a_turned_crop_through_saving(string relativePath)
    {
        if (relativePath.Length == 0) Assert.Skip("Set STRAYTA_CORPUS to a folder of PSD files with live layers to run this test.");
        var options = new PsdReadOptions { MaxRawBlockBytes = long.MaxValue };
        var original = PsdFile.Open(Path.Combine(CorpusDir!, relativePath), options);
        int w = original.Header.Width, h = original.Header.Height;
        var map = Rotation(7 * Math.PI / 180, -w * 0.05, h * 0.08);
        var doc = original.ToDocument();
        foreach (var node in doc.Root.Descendants())
            node.SourceData = node.SourceData switch
            {
                PsdLayerRecord r => PsdCanvas.WithCanvas(r, w, h, w, h, map),
                PsdGroupRecords g => g with { Folder = PsdCanvas.WithCanvas(g.Folder, w, h, w, h, map) },
                var s => s,
            };
        var ms = new MemoryStream();
        PsdWriter.Write(doc, ms);
        ms.Position = 0;
        var again = PsdFile.Read(ms, options);

        Assert.Equal(original.Layers.Count, again.Layers.Count);
        for (int i = 0; i < original.Layers.Count; i++)
        {
            var (a, b) = (original.Layers[i], again.Layers[i]);
            Assert.Equal(a.Blocks.Select(k => k.Key).Where(k => k is not ("luni" or "lsct" or "lsdk" or "iOpa")),
                b.Blocks.Select(k => k.Key).Where(k => k is not ("luni" or "lsct" or "lsdk" or "iOpa")));
            if (PsdLiveContent.ReadSmartObject(a) is { } before)
            {
                var after = PsdLiveContent.ReadSmartObject(b)!;
                for (int c = 0; c < 4; c++)
                {
                    var (x, y) = map.Apply(before.Corners[c].X, before.Corners[c].Y);
                    Assert.Equal(x, after.Corners[c].X, 6);
                    Assert.Equal(y, after.Corners[c].Y, 6);
                }
                Assert.Equal(before.Warped, after.Warped);
                Assert.Equal(a.FindBlock("SoLd")!.Data!.Length, b.FindBlock("SoLd")!.Data!.Length);
            }
            if (a.FindBlock("TySh")?.Data is { } ta)
            {
                var tb = b.FindBlock("TySh")!.Data!;
                double tx = BinaryPrimitives.ReadDoubleBigEndian(ta.AsSpan(34)), ty = BinaryPrimitives.ReadDoubleBigEndian(ta.AsSpan(42));
                var (ex, ey) = map.Apply(tx, ty);
                Assert.Equal(ex, BinaryPrimitives.ReadDoubleBigEndian(tb.AsSpan(34)), 6);
                Assert.Equal(ey, BinaryPrimitives.ReadDoubleBigEndian(tb.AsSpan(42)), 6);
                Assert.Equal(ta.AsSpan(50).ToArray(), tb.AsSpan(50).ToArray()); // text and warp untouched
            }
            if (a.FindBlock("vogk")?.Data is { } va)
            {
                var shapes = DescriptorReader.ReadVersioned(b.FindBlock("vogk")!.Data!, 4).List("keyDescriptorList")!;
                Assert.All(shapes.OfType<ObjectValue>(), s => Assert.True(s.Value.Bool("keyShapeInvalidated") == true || !s.Value.Has("keyOriginType")));
                Assert.Equal(DescriptorReader.ReadVersioned(va, 4).List("keyDescriptorList")!.Count, shapes.Count);
            }
        }
    }
}
