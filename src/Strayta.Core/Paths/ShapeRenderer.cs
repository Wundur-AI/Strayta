using Strayta.Core.Painting;

namespace Strayta.Core.Paths;

/// <summary>What <see cref="ShapeRenderer.Render"/> produced: straight-alpha pixels over <see cref="Bounds"/>, or null when nothing shows.</summary>
public sealed record ShapeRender(Raster? Pixels, PixelRect Bounds);

/// <summary>
/// Draws a shape layer: the fill through the outline's coverage, the stroke over it, as Photoshop stores a shape
/// layer's pixels. An inside stroke is a stroke twice as wide kept to the fill's coverage, an outside one the same kept
/// outside it, so anti-aliased edges meet without gaps. Gradients follow the gradient fill layer's settings (aligned
/// with the shape's bounds or the canvas), patterns tile from the document origin.
/// </summary>
public static class ShapeRenderer
{
    /// <param name="clip">Where pixels may go: the canvas, or more for content that should survive being moved in later.</param>
    public static ShapeRender Render(ShapeLayerData data, PixelRect canvas, PixelRect clip, ColorMode mode, int bitDepth, CancellationToken cancel = default)
    {
        if (mode is not (ColorMode.Rgb or ColorMode.Grayscale)) throw new NotSupportedException($"Drawing shapes in {mode} documents is not supported yet.");
        var path = data.Path;
        var stroke = data.Stroke;
        bool strokeOn = stroke.Enabled && stroke.Width > 0 && stroke.Opacity > 0;
        if (!data.FillEnabled && !strokeOn) return new ShapeRender(null, PixelRect.Empty);
        // The area that can show: the outline's bounds, grown by the stroke.
        PixelRect area;
        double grow = strokeOn ? stroke.Alignment switch { StrokeAlignment.Inside => 0, StrokeAlignment.Center => stroke.Width / 2, _ => stroke.Width } : 0;
        if (strokeOn && stroke.Alignment != StrokeAlignment.Inside && (stroke.Cap != LineCap.Butt || stroke.Join == LineJoin.Miter)) grow = Math.Max(grow, stroke.Width * Math.Min(stroke.MiterLimit, 4));
        if (data.Disabled || data.Inverted || path.InitialFillAll == true) area = clip;
        else if (path.CurveBounds() is var (l, t, r, b))
            area = new PixelRect((int)Math.Floor(l - grow) - 1, (int)Math.Floor(t - grow) - 1, (int)Math.Ceiling(r + grow) + 1, (int)Math.Ceiling(b + grow) + 1).Intersect(clip);
        else return new ShapeRender(null, PixelRect.Empty);
        if (area.IsEmpty) return new ShapeRender(null, PixelRect.Empty);

        int w = area.Width, h = area.Height;
        // Fill coverage (the vector mask).
        var inside = new byte[(long)w * h];
        if (data.Disabled) Array.Fill(inside, (byte)255);
        else
        {
            PathRasterizer.Rasterize(path, area, inside, cancel);
            if (data.Inverted) for (int i = 0; i < inside.Length; i++) inside[i] = (byte)(255 - inside[i]);
        }
        byte[]? strokeCover = null;
        if (strokeOn)
        {
            double width = stroke.Alignment == StrokeAlignment.Center ? stroke.Width : stroke.Width * 2;
            var polys = PathStroker.Outline(path, stroke.Geometry(width));
            strokeCover = new byte[(long)w * h];
            PathRasterizer.Rasterize([new FillGroup(polys, FillRule.NonZero, PathOperation.Combine)], false, area, strokeCover, cancel);
        }

        int colors = mode.ColorChannelCount();
        var planes = Enumerable.Range(0, colors).Select(_ => Plane.Create(w, h, bitDepth)).ToArray();
        var alpha = Plane.Create(w, h, bitDepth);
        bool gray = mode == ColorMode.Grayscale;
        var shapeBox = ShapeBox(path, canvas);
        var fillPaint = data.FillEnabled ? Painter.For(data.Fill, shapeBox, canvas) : null;
        var strokePaint = strokeOn ? Painter.For(stroke.Content, shapeBox, canvas) : null;
        float strokeOpacity = Math.Clamp(stroke.Opacity, 0f, 1f);
        var align = stroke.Alignment;
        // Solid fills in 8-bit documents (the common case) write bytes without per-pixel color lookups where only the
        // fill shows: the inside of the shape is one color at the fill's coverage.
        byte[]? flatFill = bitDepth == 8 && fillPaint is Flat ff ? Bytes(ff.Color, gray) : null;
        bool any = false;
        Parallel.For(0, h, new ParallelOptions { CancellationToken = cancel }, row =>
        {
            int y = area.Top + row;
            bool rowAny = false;
            for (int col = 0; col < w; col++)
            {
                int i = row * w + col;
                byte inCov = inside[i];
                byte stCov = strokeCover is null ? (byte)0 : strokeCover[i];
                if (stCov != 0 && align != StrokeAlignment.Center)
                    stCov = (byte)((align == StrokeAlignment.Inside ? stCov * inCov : stCov * (255 - inCov)) / 255);
                byte fillCov = fillPaint is null ? (byte)0 : inCov;
                if (fillCov == 0 && stCov == 0) continue;
                rowAny = true;
                if (stCov == 0 && flatFill is not null)
                {
                    for (int k = 0; k < colors; k++) planes[k].Data[i] = flatFill[k];
                    alpha.Data[i] = fillCov;
                    continue;
                }
                int x = area.Left + col;
                float r = 0, g = 0, b = 0, a = 0;
                if (fillCov > 0)
                {
                    var (pr, pg, pb, pa) = fillPaint!.At(x, y);
                    a = fillCov * (1f / 255f) * pa;
                    (r, g, b) = (pr, pg, pb);
                }
                if (stCov > 0)
                {
                    var (sr, sg, sb, spa) = strokePaint!.At(x, y);
                    float st = stCov * (1f / 255f) * strokeOpacity * spa;
                    float outA = st + a * (1f - st);
                    if (outA > 0f)
                    {
                        float k = a * (1f - st);
                        r = (sr * st + r * k) / outA;
                        g = (sg * st + g * k) / outA;
                        b = (sb * st + b * k) / outA;
                    }
                    a = outA;
                }
                if (a <= 0f) continue;
                if (gray) Set(planes[0], i, 0.299f * r + 0.587f * g + 0.114f * b);
                else
                {
                    Set(planes[0], i, r);
                    Set(planes[1], i, g);
                    Set(planes[2], i, b);
                }
                Set(alpha, i, a);
            }
            if (rowAny) any = true;
        });
        return any ? new ShapeRender(new Raster(mode, planes, alpha), area) : new ShapeRender(null, PixelRect.Empty);
    }

    /// <summary>The coverage of a path's fill (its vector mask) over <paramref name="area"/>, 0..255.</summary>
    public static byte[] MaskCoverage(ShapeLayerData data, PixelRect area)
    {
        var o = new byte[(long)area.Width * area.Height];
        if (data.Disabled) Array.Fill(o, (byte)255);
        else
        {
            PathRasterizer.Rasterize(data.Path, area, o);
            if (data.Inverted) for (int i = 0; i < o.Length; i++) o[i] = (byte)(255 - o[i]);
        }
        return o;
    }

    /// <summary>The box gradients align with: the outline's bounds, or the canvas when the path is empty.</summary>
    private static (double L, double T, double R, double B) ShapeBox(VectorPath path, PixelRect canvas) =>
        path.CurveBounds() ?? (canvas.Left, canvas.Top, canvas.Right, canvas.Bottom);

    private static void Set(Plane p, int i, float v)
    {
        v = Math.Clamp(v, 0f, 1f);
        switch (p.BitDepth)
        {
            case 8: p.Data[i] = (byte)(v * 255f + 0.5f); break;
            case 16: p.AsUInt16()[i] = (ushort)(v * 65535f + 0.5f); break;
            default: p.AsSingle()[i] = v; break;
        }
    }

    /// <summary>Colors of a fill or stroke content per document pixel (straight RGBA, 0..1).</summary>
    private abstract class Painter
    {
        public abstract (float R, float G, float B, float A) At(int x, int y);

        public static Painter For(ShapeContent content, (double L, double T, double R, double B) box, PixelRect canvas) => content switch
        {
            ShapeContent.SolidColor s => new Flat(s.Color),
            ShapeContent.GradientContent g => new Gradient(g.Fill, box, canvas),
            ShapeContent.PatternContent p when p.Fill.Pattern.Resolved is { } tile => new Tiled(tile, p.Fill),
            _ => new Flat(new RgbColor(0.5f, 0.5f, 0.5f)),
        };
    }

    private sealed class Flat(RgbColor c) : Painter
    {
        public RgbColor Color => c;

        public override (float, float, float, float) At(int x, int y) => (c.R, c.G, c.B, 1f);
    }

    private static byte[] Bytes(RgbColor c, bool gray)
    {
        static byte B(float v) => (byte)(Math.Clamp(v, 0f, 1f) * 255f + 0.5f);
        return gray ? [B(0.299f * c.R + 0.587f * c.G + 0.114f * c.B)] : [B(c.R), B(c.G), B(c.B)];
    }

    private sealed class Tiled(Pattern pattern, PatternFill fill) : Painter
    {
        private readonly float _scale = fill.Scale > 0 ? fill.Scale : 1f;

        public override (float, float, float, float) At(int x, int y)
        {
            var (r, g, b, a) = pattern.At((int)Math.Floor((x - fill.PhaseX) / _scale), (int)Math.Floor((y - fill.PhaseY) / _scale));
            return (r, g, b, a);
        }
    }

    /// <summary>
    /// A gradient fill's geometry, as Photoshop's gradient fill layers and Gradient Overlay lay it out: centred on the
    /// box (plus the offset), along the angle, spanning the box's extent in that direction times the scale.
    /// </summary>
    private sealed class Gradient : Painter
    {
        private readonly GradientFill _g;
        private readonly GradientLut _lut;
        private readonly float _cx, _cy, _dirX, _dirY, _length, _radius;

        public Gradient(GradientFill g, (double L, double T, double R, double B) shape, PixelRect canvas)
        {
            _g = g;
            _lut = GradientLut.Build(g.Gradient, GradientMethod.Classic, transparency: true);
            var (l, t, r, b) = g.AlignWithLayer ? shape : (canvas.Left, canvas.Top, canvas.Right, canvas.Bottom);
            float bw = (float)(r - l), bh = (float)(b - t);
            _cx = (float)l + bw / 2f + g.OffsetX * bw;
            _cy = (float)t + bh / 2f + g.OffsetY * bh;
            double a = g.Angle * Math.PI / 180.0;
            _dirX = (float)Math.Cos(a);
            _dirY = (float)-Math.Sin(a);
            _length = MathF.Max(1f, (MathF.Abs(bw * _dirX) + MathF.Abs(bh * _dirY)) * g.Scale);
            _radius = MathF.Max(1f, MathF.Sqrt(bw * bw + bh * bh) / 2f * g.Scale);
        }

        public override (float, float, float, float) At(int x, int y)
        {
            float px = x + 0.5f - _cx, py = y + 0.5f - _cy;
            float along = px * _dirX + py * _dirY, across = -px * _dirY + py * _dirX;
            float t = _g.Style switch
            {
                GradientStyle.Radial => MathF.Sqrt(px * px + py * py) / _radius,
                GradientStyle.Reflected => MathF.Abs(along) / (_length / 2f),
                GradientStyle.Diamond => (MathF.Abs(along) + MathF.Abs(across)) / (_length / 2f),
                GradientStyle.Angle => (float)((Math.Atan2(-across, along) / (2 * Math.PI) + 1) % 1),
                _ => along / _length + 0.5f,
            };
            if (_g.Reverse) t = 1f - t;
            return _lut.At(Math.Clamp(t, 0f, 1f));
        }
    }
}
