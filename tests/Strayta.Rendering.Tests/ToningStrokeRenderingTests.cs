using Strayta.Core;
using Strayta.Core.Painting;

namespace Strayta.Rendering.Tests;

/// <summary>The live overlay of Dodge / Burn / Sponge and Blur / Sharpen / Smudge strokes matches what is committed.</summary>
public class ToningStrokeRenderingTests
{
    private const int Size = 128;

    /// <summary>A colorful gradient photo on a white background, with a transparent-edged layer on top.</summary>
    private static (Document Doc, PixelLayer Photo) Scene(int depth)
    {
        var doc = new Document(Size, Size, ColorMode.Rgb, depth);
        var planes = Enumerable.Range(0, 3).Select(_ => Plane.Create(Size, Size, depth)).ToArray();
        var alpha = Plane.Create(Size, Size, depth);
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                int i = y * Size + x;
                Set(planes[0], i, x / (float)Size);
                Set(planes[1], i, y / (float)Size);
                Set(planes[2], i, (x + y) % 32 < 16 ? 0.8f : 0.2f);
                Set(alpha, i, x < 16 ? 0f : 1f);
            }
        var photo = new PixelLayer { Bounds = doc.Bounds, Pixels = new Raster(ColorMode.Rgb, planes, alpha) };
        doc.Root.Add(photo);
        return (doc, photo);
    }

    private static void Set(Plane p, int i, float v)
    {
        if (p.BitDepth == 8) p.Data[i] = (byte)MathF.Round(v * 255f);
        else p.AsUInt16()[i] = (ushort)MathF.Round(v * 65535f);
    }

    public static TheoryData<string, int, int> Cases => new()
    {
        { "dodge", 8, 1 }, { "burn", 16, 1 }, { "sponge", 8, 1 }, { "blur", 8, 1 }, { "sharpen", 16, 1 }, { "smudge", 8, 1 },
        { "dodge", 8, 4 }, { "blur", 8, 4 }, { "smudge", 8, 2 },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Live_tool_stroke_matches_the_commit(string tool, int depth, int factor)
    {
        var (doc, photo) = Scene(depth);
        var brush = new BrushSettings(30, 0.3f, 0.7f);
        PaintStroke stroke = tool switch
        {
            "dodge" => PaintStroke.Toning(photo, false, brush, new ToneSettings(ToneTool.Dodge) { Range = ToneRange.Highlights }, doc.Bounds),
            "burn" => PaintStroke.Toning(photo, false, brush, new ToneSettings(ToneTool.Burn) { ProtectTones = false }, doc.Bounds),
            "sponge" => PaintStroke.Toning(photo, false, brush with { Flow = 0.5f, Opacity = 1f }, new ToneSettings(ToneTool.Sponge) { Saturate = true }, doc.Bounds),
            _ => PaintStroke.Retouching(photo, false, brush, new LocalStroke(new LocalToolSettings(tool switch
            {
                "blur" => LocalTool.Blur,
                "sharpen" => LocalTool.Sharpen,
                _ => LocalTool.Smudge,
            }) { Strength = 0.8f }, PixelSource.FromRaster(photo.Pixels, photo.Bounds), doc.Bounds, brush.Size), doc.Bounds),
        };
        for (int pass = 0; pass < 2; pass++)
        {
            stroke.StrokeTo(4, 40 + pass * 30);
            stroke.StrokeTo(120, 70 + pass * 20);
        }

        byte[] before = Render(doc, factor, null);
        byte[] live = Render(doc, factor, stroke);
        var (px, bounds) = StrokeBaker.Bake(photo, stroke, doc.ColorMode, doc.BitDepth);
        (photo.Pixels, photo.Bounds) = (px, bounds);
        byte[] baked = Render(doc, factor, null);

        var report = FidelityReport.Compare(live, baked, Size / factor, Size / factor);
        if (factor == 1) Assert.True(report.MaxError <= 2, $"{tool}: live differs from the commit by up to {report.MaxError}");
        else Assert.True(report.MeanError < 2, $"{tool}: preview mean error {report.MeanError:F2}");
        Assert.True(FidelityReport.Compare(before, baked, Size / factor, Size / factor).MaxError > 10, $"{tool} changed something visible");
    }

    private static byte[] Render(Document doc, int factor, PaintStroke? stroke)
    {
        if (factor == 1)
            return Compositor.Render(doc, new RenderOptions { ActiveStroke = stroke is null ? null : new StrokeOverlay(stroke, stroke.Owner) }).ToRgba8();
        var preview = new PreviewDocument(doc, factor);
        var proxy = preview.Sync();
        return Compositor.Render(proxy, new RenderOptions { ActiveStroke = preview.MapStroke(stroke) }).ToRgba8();
    }
}
