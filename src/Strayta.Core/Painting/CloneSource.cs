using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Strayta.Core.Painting;

/// <summary>
/// The Clone Source panel's transform: how the sampled area is scaled and turned where it is painted. Scales are
/// ratios (1 = 100%, negative flips); the angle is in degrees, counter-clockwise on screen.
/// </summary>
public readonly record struct SourceTransform(float ScaleX = 1f, float ScaleY = 1f, float Angle = 0f)
{
    public static SourceTransform Identity => new(1f, 1f, 0f);

    public bool IsIdentity => ScaleX == 1f && ScaleY == 1f && Angle % 360f == 0f;

    /// <summary>
    /// Maps a painted offset (destination − the destination anchor) back to the source: M⁻¹·v, with M = rotation × scale.
    /// </summary>
    public (float X, float Y) Inverse(float vx, float vy)
    {
        float a = Angle * MathF.PI / 180f;
        float cos = MathF.Cos(a), sin = MathF.Sin(a);
        // Screen y points down, so a counter-clockwise turn on screen is rotation by −a in these coordinates; undo it.
        float rx = cos * vx - sin * vy, ry = sin * vx + cos * vy;
        return (rx / NonZero(ScaleX), ry / NonZero(ScaleY));
    }

    /// <summary>M·v: a source offset from the source anchor to where it is painted, relative to the destination anchor.</summary>
    public (float X, float Y) Forward(float vx, float vy)
    {
        float a = Angle * MathF.PI / 180f;
        float cos = MathF.Cos(a), sin = MathF.Sin(a);
        float sx = vx * ScaleX, sy = vy * ScaleY;
        return (cos * sx + sin * sy, -sin * sx + cos * sy);
    }

    private static float NonZero(float v) => MathF.Abs(v) < 1e-3f ? MathF.CopySign(1e-3f, v) : v;
}

/// <summary>
/// Where a cloning stroke takes its colors. Without a transform, the pixel at <c>(x − Dx, y − Dy)</c> of
/// <see cref="Image"/> for each painted pixel (x, y): the offset is the distance from the source point to where the
/// stroke is painted, as in Photoshop. With a <see cref="SourceTransform"/> (the Clone Source panel's W, H and angle),
/// the source is scaled and turned about its anchor: painted point p samples <c>anchor + M⁻¹(p − (anchor + d))</c>,
/// with bilinear filtering (and box-filtered reductions when the source is shrunk).
/// </summary>
/// <remarks>
/// <see cref="Image"/> may arrive after the stroke starts (a flattened copy of the document is rendered in the
/// background); until then nothing is painted, and committing waits for it. It is set once, from any thread.
/// </remarks>
public sealed class CloneSource
{
    private volatile PixelSource? _image;

    public CloneSource(int dx, int dy, PixelSource? image = null)
        : this(dx, dy, image, SourceTransform.Identity, 0f, 0f)
    {
    }

    /// <param name="dx">Destination anchor minus source anchor, x.</param>
    /// <param name="dy">Destination anchor minus source anchor, y.</param>
    /// <param name="image">The sampled pixels (null while they are prepared).</param>
    /// <param name="transform">Scale and angle of the source where it is painted.</param>
    /// <param name="anchorX">The source anchor (normally the Option-clicked source point's center), x.</param>
    /// <param name="anchorY">The source anchor, y.</param>
    public CloneSource(int dx, int dy, PixelSource? image, SourceTransform transform, float anchorX, float anchorY)
    {
        Dx = dx;
        Dy = dy;
        _image = image;
        Transform = transform;
        AnchorX = anchorX;
        AnchorY = anchorY;
    }

    public int Dx { get; }
    public int Dy { get; }
    public SourceTransform Transform { get; }
    public float AnchorX { get; }
    public float AnchorY { get; }

    /// <summary>The sampled image, or null while it is still being prepared.</summary>
    public PixelSource? Image
    {
        get => _image;
        set => _image = value;
    }

    /// <summary>The (continuous) source position sampled for the document point (<paramref name="px"/>, <paramref name="py"/>).</summary>
    public (float X, float Y) SourcePoint(float px, float py)
    {
        if (Transform.IsIdentity) return (px - Dx, py - Dy);
        var (vx, vy) = Transform.Inverse(px - AnchorX - Dx, py - AnchorY - Dy);
        return (AnchorX + vx, AnchorY + vy);
    }

    /// <summary>
    /// The source color for document pixel (<paramref name="x"/>, <paramref name="y"/>) into <paramref name="color"/>
    /// (<paramref name="channels"/> entries, converting between gray and RGB as needed); returns its alpha, 0 while the
    /// image is not ready.
    /// </summary>
    public float Read(int x, int y, Span<float> color, int channels)
    {
        if (_image is not { } image)
        {
            color[..channels].Clear();
            return 0f;
        }
        Span<float> c = stackalloc float[3];
        float a;
        if (Transform.IsIdentity) a = image.Read(x - Dx, y - Dy, c);
        else
        {
            var (sx, sy) = SourcePoint(x + 0.5f, y + 0.5f);
            // A shrunk source covers several source pixels per painted one: sample the matching box-filtered reduction.
            float footprint = 1f / MathF.Max(1e-3f, MathF.Min(MathF.Abs(Transform.ScaleX), MathF.Abs(Transform.ScaleY)));
            a = SourceSampler.Sample(image, sx, sy, footprint, c);
        }
        Convert(image.ColorChannels, c, color, channels);
        return a;
    }

    /// <summary>
    /// The source color averaged over the <paramref name="factor"/>×<paramref name="factor"/> block of document pixels
    /// starting at (<paramref name="x0"/>, <paramref name="y0"/>), for downscaled previews: a box-filtered reduction of
    /// the source sampled bilinearly, so a preview of cloned detail does not alias the way point samples do.
    /// </summary>
    public float ReadArea(int x0, int y0, int factor, Span<float> color, int channels)
    {
        if (factor <= 1) return Read(x0, y0, color, channels);
        if (_image is not { } image)
        {
            color[..channels].Clear();
            return 0f;
        }
        Span<float> c = stackalloc float[3];
        var (sx, sy) = SourcePoint(x0 + factor * 0.5f, y0 + factor * 0.5f);
        float scale = Transform.IsIdentity ? 1f : MathF.Max(1e-3f, MathF.Min(MathF.Abs(Transform.ScaleX), MathF.Abs(Transform.ScaleY)));
        float a = SourceSampler.Sample(image, sx, sy, factor / scale, c);
        Convert(image.ColorChannels, c, color, channels);
        return a;
    }

    /// <summary>
    /// The source as it lands in document space (what a stroke would paint at each pixel), for the healing tools, which
    /// take their texture from it.
    /// </summary>
    public PixelSource Placed() => new PlacedPixels(this);

    private static void Convert(int have, ReadOnlySpan<float> c, Span<float> color, int channels)
    {
        if (have == channels) c[..channels].CopyTo(color);
        else if (channels == 1) color[0] = 0.299f * c[0] + 0.587f * c[1] + 0.114f * c[2];
        else color[0] = color[1] = color[2] = c[0];
    }

    private sealed class PlacedPixels(CloneSource source) : PixelSource
    {
        public override int ColorChannels => source.Image?.ColorChannels ?? 3;

        public override PixelRect Bounds
        {
            get
            {
                if (source.Image is not { } image || image.Bounds.IsEmpty) return PixelRect.Empty;
                var b = image.Bounds;
                if (source.Transform.IsIdentity) return new PixelRect(b.Left + source.Dx, b.Top + source.Dy, b.Right + source.Dx, b.Bottom + source.Dy);
                float l = float.MaxValue, t = float.MaxValue, r = float.MinValue, bo = float.MinValue;
                foreach (var (x, y) in new[] { (b.Left, b.Top), (b.Right, b.Top), (b.Left, b.Bottom), (b.Right, b.Bottom) })
                {
                    var (fx, fy) = source.Transform.Forward(x - source.AnchorX, y - source.AnchorY);
                    fx += source.AnchorX + source.Dx;
                    fy += source.AnchorY + source.Dy;
                    (l, t, r, bo) = (MathF.Min(l, fx), MathF.Min(t, fy), MathF.Max(r, fx), MathF.Max(bo, fy));
                }
                return new PixelRect((int)MathF.Floor(l), (int)MathF.Floor(t), (int)MathF.Ceiling(r), (int)MathF.Ceiling(bo));
            }
        }

        public override float Read(int x, int y, Span<float> color) => source.Read(x, y, color, ColorChannels);
    }
}

/// <summary>
/// Filtered sampling of a <see cref="PixelSource"/> at continuous positions: bilinear on the pixels themselves, or on a
/// box-filtered reduction (2×, 4×, … averaged premultiplied) when one sample stands for many source pixels. Reductions
/// are built lazily in tiles and cached with the source (sources are immutable snapshots), so previews pay only for the
/// area they show.
/// </summary>
public static class SourceSampler
{
    private const int Tile = 64;
    private static readonly ConditionalWeakTable<PixelSource, ConcurrentDictionary<(int Level, int Tx, int Ty), float[]>> Reductions = new();

    /// <summary>
    /// The straight color (into <paramref name="color"/>, <see cref="PixelSource.ColorChannels"/> entries) and alpha of
    /// <paramref name="source"/> around the continuous point (<paramref name="x"/>, <paramref name="y"/>) (pixel centers at
    /// .5), filtered for a sample spacing of <paramref name="footprint"/> source pixels.
    /// </summary>
    public static float Sample(PixelSource source, float x, float y, float footprint, Span<float> color)
    {
        int level = 1;
        while (level * 2 <= footprint && level < 64) level *= 2;
        int colors = source.ColorChannels, c = colors + 1;
        // Bilinear on premultiplied values of the chosen level.
        float u = x / level - 0.5f, v = y / level - 0.5f;
        int u0 = (int)MathF.Floor(u), v0 = (int)MathF.Floor(v);
        float fu = u - u0, fv = v - v0;
        Span<float> acc = stackalloc float[4];
        Span<float> px = stackalloc float[4];
        acc.Clear();
        for (int j = 0; j < 2; j++)
            for (int i = 0; i < 2; i++)
            {
                float w = (i == 0 ? 1 - fu : fu) * (j == 0 ? 1 - fv : fv);
                if (w <= 0f) continue;
                Premultiplied(source, level, u0 + i, v0 + j, px, colors);
                for (int k = 0; k < c; k++) acc[k] += px[k] * w;
            }
        float a = acc[colors];
        for (int k = 0; k < colors; k++) color[k] = a > 1e-6f ? Math.Clamp(acc[k] / a, 0f, 1f) : 0f;
        return Math.Clamp(a, 0f, 1f);
    }

    /// <summary>Premultiplied color then alpha of reduced pixel (<paramref name="u"/>, <paramref name="v"/>) at <paramref name="level"/>.</summary>
    private static void Premultiplied(PixelSource source, int level, int u, int v, Span<float> px, int colors)
    {
        if (level == 1)
        {
            Span<float> col = stackalloc float[3];
            float a = source.Read(u, v, col);
            for (int k = 0; k < colors; k++) px[k] = col[k] * a;
            px[colors] = a;
            return;
        }
        int c = colors + 1;
        int tx = FloorDiv(u, Tile), ty = FloorDiv(v, Tile);
        var cache = Reductions.GetValue(source, static _ => new());
        var tile = cache.GetOrAdd((level, tx, ty), key => Reduce(source, key.Level, key.Tx, key.Ty, colors));
        int o = ((v - ty * Tile) * Tile + (u - tx * Tile)) * c;
        for (int k = 0; k < c; k++) px[k] = tile[o + k];
    }

    private static float[] Reduce(PixelSource source, int level, int tx, int ty, int colors)
    {
        int c = colors + 1;
        var tile = new float[Tile * Tile * c];
        Span<float> col = stackalloc float[3];
        float inv = 1f / (level * level);
        for (int j = 0; j < Tile; j++)
            for (int i = 0; i < Tile; i++)
            {
                int sx = (tx * Tile + i) * level, sy = (ty * Tile + j) * level, o = (j * Tile + i) * c;
                for (int yy = sy; yy < sy + level; yy++)
                    for (int xx = sx; xx < sx + level; xx++)
                    {
                        float a = source.Read(xx, yy, col);
                        for (int k = 0; k < colors; k++) tile[o + k] += col[k] * a;
                        tile[o + colors] += a;
                    }
                for (int k = 0; k < c; k++) tile[o + k] *= inv;
            }
        return tile;
    }

    private static int FloorDiv(int a, int b) => a >= 0 ? a / b : (a - b + 1) / b;
}
