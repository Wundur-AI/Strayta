using System.Buffers.Binary;
using Strayta.Core;

namespace Strayta.Psd.Tests;

public class PsdCanvasTests
{
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

    /// <summary>A path: one closed-subpath length record and one linked knot with all three points at (x, y) fractions.</summary>
    private static byte[] Path(int prefix, double x, double y)
    {
        var data = new byte[prefix + 52];
        int at = prefix;
        BinaryPrimitives.WriteInt16BigEndian(data.AsSpan(at), 0);
        BinaryPrimitives.WriteInt16BigEndian(data.AsSpan(at + 2), 1);
        at += 26;
        BinaryPrimitives.WriteInt16BigEndian(data.AsSpan(at), 1);
        for (int p = 0; p < 3; p++)
        {
            BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(at + 2 + p * 8), (int)(y * (1 << 24)));
            BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(at + 6 + p * 8), (int)(x * (1 << 24)));
        }
        return data;
    }

    private static (double X, double Y) FirstKnot(byte[] data, int prefix) =>
        (BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(prefix + 26 + 6)) / (double)(1 << 24),
         BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(prefix + 26 + 2)) / (double)(1 << 24));

    private static Plane Filled(int w, int h, byte v) => new(w, h, 8, Enumerable.Repeat(v, w * h).ToArray());

    private static PsdFile Sample() => new()
    {
        Header = new PsdHeader(1, 4, 200, 100, 8, ColorMode.Rgb),
        Resources =
        [
            new ImageResource("8BIM", 1032, "", Guides((50, false), (20, true))),
            new ImageResource("8BIM", 1050, "", [0, 0, 0, 6]),
            new ImageResource("8BIM", 2000, "Path 1", Path(0, 0.25, 0.5)),
            new ImageResource("8BIM", 1005, "", [0, 72, 0, 0, 0, 1, 0, 2, 0, 72, 0, 0, 0, 1, 0, 2]),
        ],
        // Three color channels and one saved selection.
        CompositeChannels = [Filled(200, 100, 10), Filled(200, 100, 20), Filled(200, 100, 30), Filled(200, 100, 255)],
    };

    private static Plane Crop(Plane p, int left, int top, int w, int h)
    {
        var o = Plane.Create(w, h, 8);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                o.Data[y * w + x] = p.Data[(y + top) * p.Width + x + left];
        return o;
    }

    [Fact]
    public void Canvas_change_moves_guides_and_paths_drops_slices_and_maps_channels()
    {
        var file = PsdCanvas.WithCanvas(Sample(), 100, 50, CanvasMap.Translation(-10, -5), p => Crop(p, 10, 5, 100, 50));

        Assert.Equal((100, 50), (file.Header.Width, file.Header.Height));
        Assert.Null(file.FindResource(1050));
        var guides = file.FindResource(1032)!.Data;
        Assert.Equal(40 * 32, BinaryPrimitives.ReadInt32BigEndian(guides.AsSpan(16)));
        Assert.Equal(15 * 32, BinaryPrimitives.ReadInt32BigEndian(guides.AsSpan(21)));
        // (0.25, 0.5) of 200×100 is (50, 50) px; moved by (-10, -5) on a 100×50 canvas that is (0.4, 0.9).
        var (x, y) = FirstKnot(file.FindResource(2000)!.Data, 0);
        Assert.Equal(0.4, x, 5);
        Assert.Equal(0.9, y, 5);
        Assert.All(file.CompositeChannels, c => Assert.Equal((100, 50), (c.Width, c.Height)));
        Assert.Equal("Path 1", file.FindResource(2000)!.Name);
    }

    [Fact]
    public void Saved_selections_survive_saving_after_a_canvas_change()
    {
        var file = PsdCanvas.WithCanvas(Sample(), 100, 50, CanvasMap.Translation(-10, -5), p => Crop(p, 10, 5, 100, 50));
        var doc = new Document(100, 50, ColorMode.Rgb, 8) { SourceData = file, Resolution = 150 };
        doc.Root.Add(new PixelLayer { Name = "Layer", Bounds = doc.Bounds, Pixels = new Raster(ColorMode.Rgb, [Filled(100, 50, 1), Filled(100, 50, 2), Filled(100, 50, 3)], null) });
        var ms = new MemoryStream();
        PsdWriter.Write(doc, ms, new PsdWriteOptions { Composite = new Raster(ColorMode.Rgb, [Filled(100, 50, 1), Filled(100, 50, 2), Filled(100, 50, 3)], null) });
        ms.Position = 0;
        var again = PsdFile.Read(ms);
        Assert.Equal(4, again.CompositeChannels.Count);
        Assert.All(again.CompositeChannels[3].Data, v => Assert.Equal(255, v));
        var resolution = again.FindResource(1005)!.Data;
        Assert.Equal(150 * 65536, BinaryPrimitives.ReadInt32BigEndian(resolution));
        Assert.Equal(2, BinaryPrimitives.ReadInt16BigEndian(resolution.AsSpan(6))); // display unit kept
        Assert.Equal(150, again.ToDocument().Resolution);
    }

    [Fact]
    public void Layer_vector_masks_and_type_move_with_the_canvas()
    {
        var tysh = new byte[2 + 48 + 10];
        BinaryPrimitives.WriteInt16BigEndian(tysh, 1);
        double[] transform = [1, 0, 0, 1, 120.5, 40];
        for (int i = 0; i < 6; i++) BinaryPrimitives.WriteDoubleBigEndian(tysh.AsSpan(2 + i * 8), transform[i]);
        var record = new PsdLayerRecord
        {
            Rect = new PixelRect(10, 10, 20, 20),
            Blocks =
            [
                new TaggedBlock("8BIM", "vmsk", 0, 60, Path(8, 0.5, 0.5)),
                new TaggedBlock("8BIM", "TySh", 0, tysh.Length, tysh),
                new TaggedBlock("8BIM", "zzzz", 0, 3, [1, 2, 3]),
            ],
        };
        var moved = PsdCanvas.WithCanvas(record, 200, 100, 100, 50, CanvasMap.Translation(-10, -5));
        var (x, y) = FirstKnot(moved.FindBlock("vmsk")!.Data!, 8);
        Assert.Equal(0.9, x, 5); // 100 px - 10 on a 100-wide canvas
        Assert.Equal(0.9, y, 5); // 50 px - 5 on a 50-high canvas
        var t = moved.FindBlock("TySh")!.Data!;
        Assert.Equal(110.5, BinaryPrimitives.ReadDoubleBigEndian(t.AsSpan(2 + 32)));
        Assert.Equal(35, BinaryPrimitives.ReadDoubleBigEndian(t.AsSpan(2 + 40)));
        Assert.Equal([1, 2, 3], moved.FindBlock("zzzz")!.Data);

        var plain = new PsdLayerRecord { Blocks = [new TaggedBlock("8BIM", "zzzz", 0, 3, [1, 2, 3])] };
        Assert.Same(plain, PsdCanvas.WithCanvas(plain, 200, 100, 100, 50, CanvasMap.Translation(-10, -5)));
    }
}
