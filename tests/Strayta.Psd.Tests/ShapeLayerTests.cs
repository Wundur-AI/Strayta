using System.Buffers.Binary;
using Strayta.Core;
using Strayta.Core.Paths;
using Strayta.Psd.Descriptors;

namespace Strayta.Psd.Tests;

/// <summary>
/// Shape layers and paths in PSD: path records, vector masks, live shape origination ('vogk'), vector strokes ('vstk')
/// and fill content ('vscg' / 'SoCo' / 'GdFl' / 'PtFl'); unedited data stays byte for byte, edits read back as written,
/// and new shapes are stored the way Photoshop CC stores its own.
/// </summary>
public class ShapeLayerTests
{
    private const int W = 300, H = 200;

    private static ShapeLayerData Sample(ShapeContent? fill = null) => new()
    {
        Path = new VectorPath([ShapeGeometry.RoundedRectangle(20, 30, 180, 150, new CornerRadii(4, 8, 12, 16))]) { InitialFillAll = false },
        Fill = fill ?? ShapeContent.Solid(new RgbColor(1, 0.5f, 0)),
        Stroke = new ShapeStroke { Enabled = true, Width = 6, Alignment = StrokeAlignment.Outside, Cap = LineCap.Round, Join = LineJoin.Bevel, Dashes = [4, 2],
            Content = ShapeContent.Solid(new RgbColor(0, 0, 1)), Opacity = 0.75f },
        LiveShapes = [new LiveShape(LiveShapeKind.RoundedRectangle, 20, 30, 180, 150) { Radii = new CornerRadii(4, 8, 12, 16) }],
    };

    [Fact]
    public void Path_records_encode_and_decode_exactly()
    {
        var path = new VectorPath(
        [
            ShapeGeometry.Ellipse(10.25, 20.5, 110.75, 90),
            new Subpath([PathKnot.Corner(5, 5), PathKnot.Smooth(new(50, 40), new(70, 40)), PathKnot.Corner(90, 5)], Closed: false, PathOperation.Subtract),
        ]) { InitialFillAll = false };
        var bytes = PsdPaths.Encode(path, W, H);
        Assert.Equal(26 * (2 + 1 + 4 + 1 + 3), bytes.Length); // fill rule, initial fill, two length records and their knots
        var back = PsdPaths.Decode(bytes, 0, W, H);
        Assert.Equal(2, back.Subpaths.Count);
        Assert.Equal(PathOperation.Subtract, back.Subpaths[1].Operation);
        Assert.False(back.Subpaths[1].Closed);
        Assert.True(back.Subpaths[1].Knots[1].Linked);
        for (int s = 0; s < 2; s++)
            for (int k = 0; k < path.Subpaths[s].Knots.Count; k++)
            {
                Assert.Equal(path.Subpaths[s].Knots[k].Anchor.X, back.Subpaths[s].Knots[k].Anchor.X, 4);
                Assert.Equal(path.Subpaths[s].Knots[k].In.Y, back.Subpaths[s].Knots[k].In.Y, 4);
            }
        Assert.Equal(bytes, PsdPaths.Encode(back, W, H)); // decoding and encoding again changes nothing
    }

    [Fact]
    public void A_legacy_operation_is_kept_and_a_changed_one_written()
    {
        var tail = ShapeGeometry.ContinuationTail;
        var path = new VectorPath([ShapeGeometry.Rectangle(0, 0, 10, 10) with { RecordTail = tail }]);
        var bytes = PsdPaths.Encode(path, W, H);
        Assert.Equal(-1, BinaryPrimitives.ReadInt16BigEndian(bytes.AsSpan(26 * 2 + 4)));
        var changed = PsdPaths.Encode(new VectorPath([path.Subpaths[0] with { Operation = PathOperation.Intersect }]), W, H);
        Assert.Equal(3, BinaryPrimitives.ReadInt16BigEndian(changed.AsSpan(26 * 2 + 4)));
    }

    [Fact]
    public void A_new_shape_layer_reads_back_as_written()
    {
        var data = Sample();
        var record = PsdShapeLayer.Create(data, W, H, resolution: 144);
        Assert.Equal(["vscg", "vsms", "vogk", "vstk"], record.Blocks.Select(b => b.Key));
        Assert.Equal(0, record.FindBlock("vsms")!.Data!.Length % 4);
        var back = PsdShapeLayer.Read(record, W, H, resolution: 144)!;
        Assert.Equal(data.Fill, back.Fill);
        Assert.Equal(data.Stroke, back.Stroke);
        Assert.Equal(data.LiveShapes.Single() with { SourceData = null }, back.LiveShapes.Single() with { SourceData = null });
        Assert.Equal(data.Path.Subpaths[0].Knots.Count, back.Path.Subpaths[0].Knots.Count);
        // The stroke width is stored in points at the document resolution.
        var vstk = DescriptorReader.ReadVersioned(record.FindBlock("vstk")!.Data!);
        Assert.Equal(3.0, vstk.Number("strokeStyleLineWidth")!.Value, 6);
        Assert.Equal("strokeStyleAlignOutside", vstk.Enum("strokeStyleLineAlignment"));
        var vogk = DescriptorReader.ReadVersioned(record.FindBlock("vogk")!.Data!, 4);
        var shape = ((ObjectValue)vogk.List("keyDescriptorList")![0]).Value;
        Assert.Equal(2, shape.Number("keyOriginType"));
        Assert.Equal(8, shape.Object("keyOriginRRectRadii")!.Number("topRight"));
    }

    [Fact]
    public void Unchanged_shapes_keep_their_bytes_and_changes_touch_only_their_block()
    {
        var data = Sample();
        var record = PsdShapeLayer.Create(data, W, H);
        var read = PsdShapeLayer.Read(record, W, H)!;
        var same = PsdShapeLayer.Apply(record, read, W, H);
        for (int i = 0; i < record.Blocks.Count; i++) Assert.Same(record.Blocks[i].Data, same.Blocks[i].Data);

        var recolored = PsdShapeLayer.Apply(record, read with { Fill = ShapeContent.Solid(new RgbColor(0, 1, 0)) }, W, H);
        Assert.NotSame(record.FindBlock("vscg")!.Data, recolored.FindBlock("vscg")!.Data);
        Assert.Same(record.FindBlock("vsms")!.Data, recolored.FindBlock("vsms")!.Data);
        Assert.Same(record.FindBlock("vstk")!.Data, recolored.FindBlock("vstk")!.Data);
        Assert.Equal(new RgbColor(0, 1, 0), ((ShapeContent.SolidColor)PsdShapeLayer.Read(recolored, W, H)!.Fill).Color);

        // Moving the outline rewrites the mask and the live shape box, nothing else.
        var moved = PsdShapeLayer.Apply(record, read.Offset(10, 5), W, H);
        Assert.Same(record.FindBlock("vscg")!.Data, moved.FindBlock("vscg")!.Data);
        var again = PsdShapeLayer.Read(moved, W, H)!;
        Assert.Equal(30, again.LiveShapes[0].Left, 6);
        Assert.Equal(20 + 10 + 4 * 0, again.Path.Subpaths[0].Knots.Min(k => k.Anchor.X), 4);
    }

    [Fact]
    public void Gradient_and_pattern_fills_round_trip()
    {
        var gradient = new GradientFill(GradientModel.TwoColor("Black, White", RgbColor.Black, new RgbColor(1, 1, 1))) { Angle = 30, Style = GradientStyle.Radial, Scale = 0.5f };
        var g = Sample(new ShapeContent.GradientContent(gradient));
        Assert.Equal(g.Fill, PsdShapeLayer.Read(PsdShapeLayer.Create(g, W, H), W, H)!.Fill);
        var p = Sample(new ShapeContent.PatternContent(new PatternFill(new PatternReference("abc-123", "Dots")) { Scale = 2f }));
        var record = PsdShapeLayer.Create(p, W, H);
        Assert.Equal("PtFl", System.Text.Encoding.ASCII.GetString(record.FindBlock("vscg")!.Data!, 0, 4));
        Assert.Equal(p.Fill, PsdShapeLayer.Read(record, W, H)!.Fill);
    }

    [Fact]
    public void A_saved_file_opens_with_its_shape_and_its_paths()
    {
        var doc = new Document(W, H, ColorMode.Rgb, 8);
        var data = Sample();
        var render = ShapeRenderer.Render(data, doc.Bounds, doc.Bounds, doc.ColorMode, doc.BitDepth);
        Assert.NotNull(render.Pixels);
        var layer = new PixelLayer { Name = "Rounded Rectangle 1", Pixels = render.Pixels, Bounds = render.Bounds, SourceData = PsdShapeLayer.Create(data, W, H) };
        doc.Root.Add(layer);
        var paths = new List<DocumentPath>
        {
            new("Work Path", new VectorPath([ShapeGeometry.Ellipse(1, 2, 50, 60)]) { InitialFillAll = false }, DocumentPathKind.Work),
            new("Outline", new VectorPath([ShapeGeometry.Rectangle(5, 5, 25, 25)]) { InitialFillAll = false }, DocumentPathKind.Saved),
        };
        doc.SourceData = PsdPathResources.WithPaths(PsdPathResources.Empty(doc), paths);
        var ms = new MemoryStream();
        PsdWriter.Write(doc, ms);
        ms.Position = 0;
        var file = PsdFile.Read(ms);
        var record = file.Layers.Single();
        Assert.Equal(PsdLayerKind.Shape, PsdLayerKinds.Classify(record));
        var back = PsdShapeLayer.Read(record, W, H)!;
        Assert.Equal(data.Stroke, back.Stroke);
        var read = PsdPathResources.Read(file);
        Assert.Equal(["Work Path", "Outline"], read.Select(p => p.Name));
        Assert.Equal([1025, 2000], read.Select(p => p.Id));
        Assert.Equal(50, read[0].Path.Subpaths[0].Knots[1].Anchor.X, 4);
        // Writing the same paths again keeps the resources as they were.
        var kept = PsdPathResources.WithPaths(file, read);
        Assert.Same(file.FindResource(2000), kept.FindResource(2000));
    }

    [Fact]
    public void The_renderer_draws_fill_and_stroke_where_they_belong()
    {
        var data = new ShapeLayerData
        {
            Path = new VectorPath([ShapeGeometry.Rectangle(20, 20, 60, 60)]),
            Fill = ShapeContent.Solid(new RgbColor(1, 0, 0)),
            Stroke = new ShapeStroke { Enabled = true, Width = 4, Alignment = StrokeAlignment.Outside, Content = ShapeContent.Solid(new RgbColor(0, 0, 1)) },
        };
        var canvas = new PixelRect(0, 0, 100, 100);
        var r = ShapeRenderer.Render(data, canvas, canvas, ColorMode.Rgb, 8);
        var px = r.Pixels!;
        byte At(int plane, int x, int y) => (plane < 0 ? px.Alpha! : px.ColorPlanes[plane]).Data[(y - r.Bounds.Top) * r.Bounds.Width + x - r.Bounds.Left];
        Assert.Equal((255, 0, 255), (At(0, 40, 40), At(2, 40, 40), At(-1, 40, 40))); // red fill inside
        Assert.Equal((0, 255, 255), (At(0, 17, 40), At(2, 17, 40), At(-1, 17, 40))); // blue stroke outside
        Assert.Equal(0, At(-1, 15, 40)); // beyond the stroke
        var inside = ShapeRenderer.Render(data with { Stroke = data.Stroke with { Alignment = StrokeAlignment.Inside } }, canvas, canvas, ColorMode.Rgb, 8);
        Assert.Equal(new PixelRect(19, 19, 61, 61), inside.Bounds);
        var none = ShapeRenderer.Render(data with { FillEnabled = false }, canvas, canvas, ColorMode.Rgb, 8);
        byte A(ShapeRender s, int x, int y) => s.Pixels!.Alpha!.Data[(y - s.Bounds.Top) * s.Bounds.Width + x - s.Bounds.Left];
        Assert.Equal(0, A(none, 40, 40));
        Assert.Equal(255, A(none, 18, 40));
    }

    // ---- Real files ----------------------------------------------------------------------------------

    private static readonly string? CorpusDir = Environment.GetEnvironmentVariable("STRAYTA_CORPUS");

    [Fact]
    public void Real_vector_data_round_trips_and_matches_photoshops_rasterization()
    {
        if (CorpusDir is null || !Directory.Exists(CorpusDir)) Assert.Skip("Set STRAYTA_CORPUS to run corpus tests.");
        int masks = 0, shapes = 0;
        double matchSum = 0, matched = 0, pixels = 0;
        foreach (var f in Directory.EnumerateFiles(CorpusDir, "*.ps?", SearchOption.AllDirectories).Where(f => !Path.GetFileName(f).StartsWith("._")))
        {
            var file = PsdFile.Open(f, new PsdReadOptions { SkipComposite = true, MaxRawBlockBytes = long.MaxValue });
            var (w, h) = (file.Header.Width, file.Header.Height);
            foreach (var r in file.Layers)
            {
                if ((r.FindBlock("vmsk") ?? r.FindBlock("vsms"))?.Data is not { Length: >= 8 } data) continue;
                var path = PsdPaths.Decode(data, 8, w, h);
                // Records re-encode exactly (the block pads them to 4 bytes).
                var records = PsdPaths.Encode(path, w, h);
                Assert.Equal(data.AsSpan(8, records.Length).ToArray(), records);
                Assert.All(data.AsSpan(8 + records.Length).ToArray(), b => Assert.Equal(0, b));
                if (PsdShapeLayer.Read(r, w, h) is { } shape)
                {
                    shapes++;
                    var same = PsdShapeLayer.Apply(r, shape, w, h);
                    Assert.Equal(r.Blocks.Select(b => b.Key), same.Blocks.Select(b => b.Key));
                    for (int i = 0; i < r.Blocks.Count; i++) Assert.Same(r.Blocks[i].Data, same.Blocks[i].Data);
                }
                if (r.Mask is { } m && (m.Flags & 0x08) != 0 && r.ChannelData.TryGetValue(PsdChannelId.UserMask, out var stored) && !m.Rect.IsEmpty)
                {
                    var ours = PathRasterizer.Rasterize(path, m.Rect);
                    if ((BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(4)) & 1) != 0) for (int i = 0; i < ours.Length; i++) ours[i] = (byte)(255 - ours[i]);
                    int same = ours.Zip(stored.Data).Count(p => Math.Abs(p.First - p.Second) <= 1);
                    matchSum += same / (double)ours.Length;
                    matched += same;
                    pixels += ours.Length;
                    masks++;
                }
            }
        }
        // Photoshop CC files match on 99.9% of pixels (docs: README); some older files' cached masks sit a fraction of a
        // pixel off their own paths, so the bound is on all mask pixels together.
        if (masks > 0) Assert.True(matched / pixels > 0.97, $"vector masks match Photoshop's on {matched / pixels:P2} of their pixels");
        TestContext.Current.SendDiagnosticMessage($"{shapes} shape layers, {masks} rasterized masks, mean match {(masks > 0 ? matchSum / masks : 0):P3}");
    }
}
