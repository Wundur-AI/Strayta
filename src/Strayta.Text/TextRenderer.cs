using SkiaSharp;
using Strayta.Core;
using Strayta.Core.Text;

namespace Strayta.Text;

/// <summary>Settings for <see cref="TextRenderer.Render(TextLayerData, TextRenderOptions?)"/>.</summary>
public sealed class TextRenderOptions
{
    /// <summary>Where fonts come from; the installed fonts by default.</summary>
    public FontCatalog? Fonts { get; init; }

    /// <summary>Color mode of the result (RGB or grayscale).</summary>
    public ColorMode ColorMode { get; init; } = ColorMode.Rgb;

    /// <summary>Bit depth of the result (8, 16 or 32).</summary>
    public int BitDepth { get; init; } = 8;
}

/// <summary>A rendered type layer.</summary>
/// <param name="Pixels">The layer's pixels (fill color with coverage as transparency), or null when nothing is drawn.</param>
/// <param name="Bounds">Where <paramref name="Pixels"/> sits in the document (tight around the drawn pixels).</param>
public sealed record TextRenderResult(Raster? Pixels, PixelRect Bounds, TextLayout Layout)
{
    public IReadOnlyList<string> MissingFonts => Layout.MissingFonts;
}

/// <summary>
/// Draws laid-out text as layer pixels: glyph outlines (unhinted) filled through the layer's transform, so the result
/// is the same on every platform. Photoshop's anti-aliasing methods are approximated (see <see cref="Tuning"/>):
/// None is aliased; Smooth is exact area coverage; Strong grows the outlines by a fraction of a pixel and counts
/// 4×4 point samples, as Photoshop's stored Strong pixels show; Sharp and Crisp sit in between. Photoshop also
/// hints small Sharp/Smooth type, which is not reproduced.
/// </summary>
public static class TextRenderer
{
    /// <summary>Lays out and renders <paramref name="data"/>.</summary>
    public static TextRenderResult Render(TextLayerData data, TextRenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        options ??= new TextRenderOptions();
        return Render(TextLayout.Create(data, options.Fonts), options);
    }

    /// <summary>Renders an existing layout.</summary>
    public static TextRenderResult Render(TextLayout layout, TextRenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        options ??= new TextRenderOptions();
        var data = layout.Data;
        var t = data.Transform;

        // Document-space area of the ink, with a margin for anti-aliasing and faux bold.
        var ink = layout.InkBounds;
        if (ink.Width <= 0 || ink.Height <= 0) return new TextRenderResult(null, PixelRect.Empty, layout);
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (x, y) in new[] { (ink.Left, ink.Top), (ink.Right, ink.Top), (ink.Right, ink.Bottom), (ink.Left, ink.Bottom) })
        {
            var (dx, dy) = t.Apply(x, y);
            minX = Math.Min(minX, dx); maxX = Math.Max(maxX, dx);
            minY = Math.Min(minY, dy); maxY = Math.Max(maxY, dy);
        }
        const int margin = 2;
        int left = (int)Math.Floor(minX) - margin, top = (int)Math.Floor(minY) - margin;
        int width = (int)Math.Ceiling(maxX) + margin - left, height = (int)Math.Ceiling(maxY) + margin - top;
        if (width <= 0 || height <= 0 || (long)width * height > 400_000_000L) return new TextRenderResult(null, PixelRect.Empty, layout);

        var colors = layout.Glyphs.Select(g => g.Color).Concat(layout.Decorations.Select(d => d.Color)).Distinct().ToList();
        var coverage = new List<(TextColor Color, byte[] Mask)>();
        foreach (var color in colors)
            coverage.Add((color, DrawCoverage(layout, color, left, top, width, height)));

        var shaped = coverage.Select(c => (c.Color, Mask: Shape(c.Mask, data.AntiAlias))).ToList();
        var raster = Compose(shaped, width, height, options, out var tight);
        if (raster is null) return new TextRenderResult(null, PixelRect.Empty, layout);
        var bounds = new PixelRect(left + tight.Left, top + tight.Top, left + tight.Right, top + tight.Bottom);
        return new TextRenderResult(raster, bounds, layout);
    }

    /// <summary>The coverage (0..255) of every glyph and decoration of one color.</summary>
    private static byte[] DrawCoverage(TextLayout layout, TextColor color, int left, int top, int width, int height)
    {
        var t = layout.Data.Transform;
        var tuning = Tuning(layout.Data.AntiAlias);
        // Point-sampled methods are drawn aliased at 4× and counted per pixel (4×4 samples).
        int k = tuning.Levels > 0 ? 4 : 1;
        var info = new SKImageInfo(width * k, height * k, SKColorType.Alpha8, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            var docMatrix = new SKMatrix((float)t.XX, (float)t.YX, (float)(t.TX - left), (float)t.XY, (float)t.YY, (float)(t.TY - top), 0, 0, 1);
            bool aliased = layout.Data.AntiAlias == TextAntiAlias.None || k > 1;
            using var fill = new SKPaint { IsAntialias = !aliased, Color = SKColors.Black, Style = SKPaintStyle.Fill };
            using var path = new SKPath { FillType = SKPathFillType.Winding };
            using var bold = new SKPath { FillType = SKPathFillType.Winding };
            double boldWidth = 0;
            foreach (var g in layout.Glyphs)
            {
                if (g.Color != color || g.Face.Outline(g.Glyph) is not { } outline) continue;
                double sx = g.Size * g.ScaleX / 1000, sy = g.Size * g.ScaleY / 1000;
                double skew = g.FauxItalic ? -TextLayoutEngine.FauxItalicSkew * sy : 0;
                // Glyph space (1000 units, y down) → text space: scale, slant, then move to the glyph's pen position.
                var glyphMatrix = new SKMatrix((float)sx, (float)skew, (float)g.X, 0, (float)sy, (float)g.Y, 0, 0, 1);
                (g.FauxBold ? bold : path).AddPath(outline, in glyphMatrix);
                if (g.FauxBold) boldWidth = Math.Max(boldWidth, TextLayoutEngine.FauxBoldWidth(g.Size));
            }
            foreach (var d in layout.Decorations)
                if (d.Color == color) path.AddRect(new SKRect((float)d.Rect.Left, (float)d.Rect.Top, (float)d.Rect.Right, (float)d.Rect.Bottom));

            if (!bold.IsEmpty)
            {
                // Faux bold: the outline stroked, merged into the glyphs.
                using var stroke = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = (float)boldWidth, StrokeJoin = SKStrokeJoin.Miter };
                using var stroked = new SKPath();
                stroke.GetFillPath(bold, stroked);
                using var union = bold.Op(stroked, SKPathOp.Union) ?? bold;
                path.AddPath(union);
            }
            using var device = new SKPath { FillType = SKPathFillType.Winding };
            path.Transform(docMatrix, device);
            // Heavier anti-aliasing methods thicken the glyphs by a fraction of a device pixel.
            if (tuning.GrowX > 0 || tuning.GrowY > 0)
            {
                using var grown = new SKPath { FillType = SKPathFillType.Winding };
                foreach (var (dx, dy) in new[] { (-tuning.GrowX, 0.0), (tuning.GrowX, 0.0), (0.0, -tuning.GrowY), (0.0, tuning.GrowY) })
                {
                    if (dx == 0 && dy == 0) continue;
                    var shift = SKMatrix.CreateTranslation((float)dx, (float)dy);
                    grown.AddPath(device, in shift);
                }
                device.AddPath(grown);
            }
            if (k > 1) device.Transform(SKMatrix.CreateScale(k, k));
            canvas.DrawPath(device, fill);
        }
        var mask = new byte[width * height];
        var span = bitmap.GetPixelSpan();
        int stride = bitmap.RowBytes;
        if (k == 1)
        {
            for (int y = 0; y < height; y++) span.Slice(y * stride, width).CopyTo(mask.AsSpan(y * width, width));
            return mask;
        }
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int count = 0;
                for (int sy = 0; sy < k; sy++)
                {
                    var row = span.Slice((y * k + sy) * stride + x * k, k);
                    for (int sx = 0; sx < k; sx++) if (row[sx] >= 128) count++;
                }
                mask[y * width + x] = tuning.Levels == 255 ? (byte)(count * 255 / 16) : (byte)Math.Min(255, count * tuning.Levels);
            }
        return mask;
    }

    /// <summary>
    /// How an anti-aliasing method is approximated, fitted to the pixels Photoshop stores for type layers: outlines
    /// grown by <c>GrowX</c>/<c>GrowY</c> device pixels, coverage from 4×4 point samples scaled by <c>Levels</c> per
    /// sample (Photoshop's Strong stores multiples of 16 and Sharp multiples of 17; 0 means exact area coverage),
    /// then raised to <c>Gamma</c>.
    /// </summary>
    internal readonly record struct AaTuning(double GrowX, double GrowY, double Gamma, int Levels);

    internal static AaTuning Tuning(TextAntiAlias method)
    {
        if (TuningOverride.Value.TryGetValue(method, out var t)) return t;
        return method switch
        {
            TextAntiAlias.Strong => new(0.13, 0.15, 1.0, 16),
            TextAntiAlias.Crisp => new(0.05, 0.05, 1.0, 16), // no sample yet: between Sharp and Strong
            TextAntiAlias.Sharp => new(0.1, 0.1, 1.0, 0), // Photoshop hints Sharp type, which this does not do
            _ => new(0.0, 0.0, 1.0, 0),
        };
    }

    /// <summary>STRAYTA_TEXT_AA="Strong=0.13/0.15/1/16;Sharp=0/0/1/17" overrides the tuning (for fitting).</summary>
    private static readonly Lazy<Dictionary<TextAntiAlias, AaTuning>> TuningOverride = new(() =>
    {
        var d = new Dictionary<TextAntiAlias, AaTuning>();
        if (Environment.GetEnvironmentVariable("STRAYTA_TEXT_AA") is not { Length: > 0 } s) return d;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var part in s.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=');
            var v = kv[1].Split('/');
            if (Enum.TryParse<TextAntiAlias>(kv[0], out var m))
                d[m] = new(double.Parse(v[0], inv), double.Parse(v[1], inv), double.Parse(v[2], inv), int.Parse(v[3], inv));
        }
        return d;
    });

    /// <summary>Approximates Photoshop's anti-aliasing methods by reshaping coverage.</summary>
    private static byte[] Shape(byte[] mask, TextAntiAlias method)
    {
        double gamma = Tuning(method).Gamma;
        if (gamma == 1.0) return mask;
        var lut = new byte[256];
        for (int i = 0; i < 256; i++) lut[i] = (byte)Math.Round(255 * Math.Pow(i / 255.0, gamma));
        var o = new byte[mask.Length];
        for (int i = 0; i < mask.Length; i++) o[i] = lut[mask[i]];
        return o;
    }

    /// <summary>Colors the coverage masks into layer pixels (straight alpha) and crops to what is drawn.</summary>
    private static Raster? Compose(List<(TextColor Color, byte[] Mask)> layers, int width, int height, TextRenderOptions options, out PixelRect tight)
    {
        // Straight RGBA in doubles, each color composited over the ones before it.
        int n = width * height;
        var r = new float[n];
        var g = new float[n];
        var b = new float[n];
        var a = new float[n];
        foreach (var (color, mask) in layers)
        {
            float cr = (float)color.R, cg = (float)color.G, cb = (float)color.B, ca = (float)color.A;
            for (int i = 0; i < n; i++)
            {
                if (mask[i] == 0) continue;
                float sa = mask[i] / 255f * ca;
                float da = a[i];
                float oa = sa + da * (1 - sa);
                if (oa <= 0) continue;
                r[i] = (cr * sa + r[i] * da * (1 - sa)) / oa;
                g[i] = (cg * sa + g[i] * da * (1 - sa)) / oa;
                b[i] = (cb * sa + b[i] * da * (1 - sa)) / oa;
                a[i] = oa;
            }
        }
        // Where nothing is drawn, carry the first color so edges never blend toward black.
        if (layers.Count > 0)
        {
            var first = layers[0].Color;
            for (int i = 0; i < n; i++)
                if (a[i] <= 0) { r[i] = (float)first.R; g[i] = (float)first.G; b[i] = (float)first.B; }
        }

        int x0 = width, y0 = height, x1 = -1, y1 = -1;
        int depth = options.BitDepth;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                if (Quantize(a[y * width + x], depth) > 0)
                {
                    x0 = Math.Min(x0, x); x1 = Math.Max(x1, x);
                    y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
                }
        tight = x1 < 0 ? default : new PixelRect(x0, y0, x1 + 1, y1 + 1);
        if (x1 < 0) return null;

        int w = tight.Width, h = tight.Height;
        bool gray = options.ColorMode == ColorMode.Grayscale;
        var planes = new List<Plane>();
        var channels = gray
            ? new[] { (Func<int, float>)(i => (float)(0.299 * r[i] + 0.587 * g[i] + 0.114 * b[i])) }
            : [i => r[i], i => g[i], i => b[i]];
        foreach (var channel in channels) planes.Add(Fill(w, h, depth, (x, y) => channel((y + y0) * width + x + x0)));
        var alpha = Fill(w, h, depth, (x, y) => a[(y + y0) * width + x + x0]);
        return new Raster(gray ? ColorMode.Grayscale : ColorMode.Rgb, planes, alpha);
    }

    private static int Quantize(float v, int depth) => depth switch
    {
        8 => (int)MathF.Round(v * 255),
        16 => (int)MathF.Round(v * 65535),
        _ => v > 0 ? 1 : 0,
    };

    private static Plane Fill(int w, int h, int depth, Func<int, int, float> value)
    {
        var p = Plane.Create(w, h, depth);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float v = Math.Clamp(value(x, y), 0f, 1f);
                int i = y * w + x;
                switch (depth)
                {
                    case 8: p.Data[i] = (byte)MathF.Round(v * 255); break;
                    case 16: p.AsUInt16()[i] = (ushort)MathF.Round(v * 65535); break;
                    default: p.AsSingle()[i] = v; break;
                }
            }
        return p;
    }
}
