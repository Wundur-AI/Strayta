namespace Strayta.Core.Painting;

/// <summary>Brush tip settings. Size is the diameter in pixels; hardness, opacity and flow are 0..1.</summary>
public readonly record struct BrushSettings(float Size, float Hardness, float Opacity)
{
    public static BrushSettings Default => new(30f, 0.8f, 1f);

    /// <summary>Distance between dabs, as a fraction of the diameter (Photoshop's default spacing is 25%).</summary>
    public float Spacing => MathF.Max(0.5f, Size * 0.15f);
}

/// <summary>
/// A stroke in progress: dab coverage accumulated in document space. Coverage takes the maximum of
/// overlapping dabs, so one stroke never exceeds the brush opacity (Photoshop's behavior at 100% flow).
/// The target layer is not modified until <see cref="StrokeBaker.Bake"/>.
/// </summary>
public sealed class PaintStroke
{
    private const int TileSize = 128;
    private readonly Dictionary<(int, int), float[]> _tiles = [];
    private readonly PixelRect _limit;
    private float _lastX, _lastY, _carry;
    private bool _started;

    public PaintStroke(PixelLayer target, BrushSettings brush, RgbColor color, bool erase, PixelRect limit)
    {
        Target = target;
        Brush = brush;
        Color = color;
        Erase = erase;
        _limit = limit;
    }

    public PixelLayer Target { get; }
    public BrushSettings Brush { get; }
    public RgbColor Color { get; }
    public bool Erase { get; }

    /// <summary>Area touched so far (document coordinates).</summary>
    public PixelRect Bounds { get; private set; } = PixelRect.Empty;

    /// <summary>Increments whenever coverage changes, so renderers can tell the stroke moved on.</summary>
    public int Version { get; private set; }

    /// <summary>Stroke coverage at a document pixel, 0..1 (before brush opacity).</summary>
    public float CoverageAt(int x, int y)
    {
        if (x < Bounds.Left || x >= Bounds.Right || y < Bounds.Top || y >= Bounds.Bottom) return 0f;
        int tx = FloorDiv(x, TileSize), ty = FloorDiv(y, TileSize);
        return _tiles.TryGetValue((tx, ty), out var tile) ? tile[(y - ty * TileSize) * TileSize + (x - tx * TileSize)] : 0f;
    }

    /// <summary>Adds dabs from the previous point to (x, y), spaced along the path; the first call places one dab.</summary>
    public void StrokeTo(float x, float y)
    {
        if (!_started)
        {
            _started = true;
            Dab(x, y);
            (_lastX, _lastY) = (x, y);
            return;
        }

        float dx = x - _lastX, dy = y - _lastY;
        float length = MathF.Sqrt(dx * dx + dy * dy);
        float spacing = Brush.Spacing;
        float travelled = spacing - _carry;
        while (travelled <= length)
        {
            float t = travelled / length;
            Dab(_lastX + dx * t, _lastY + dy * t);
            travelled += spacing;
        }
        _carry = length - (travelled - spacing);
        (_lastX, _lastY) = (x, y);
    }

    /// <summary>
    /// One round dab. Inside radius × hardness coverage is full; beyond it falls off smoothly to zero at the
    /// radius. A fully hard brush gets a one-pixel anti-aliased edge.
    /// </summary>
    private void Dab(float cx, float cy)
    {
        float r = MathF.Max(0.5f, Brush.Size / 2f);
        float hard = Math.Clamp(Brush.Hardness, 0f, 1f);
        var rect = new PixelRect((int)MathF.Floor(cx - r - 1), (int)MathF.Floor(cy - r - 1),
            (int)MathF.Ceiling(cx + r + 1), (int)MathF.Ceiling(cy + r + 1)).Intersect(_limit);
        if (rect.IsEmpty) return;

        for (int y = rect.Top; y < rect.Bottom; y++)
        {
            for (int x = rect.Left; x < rect.Right; x++)
            {
                float px = x + 0.5f - cx, py = y + 0.5f - cy;
                float d = MathF.Sqrt(px * px + py * py);
                float a;
                if (hard >= 0.99f) a = Math.Clamp(r - d + 0.5f, 0f, 1f);
                else
                {
                    float inner = r * hard;
                    if (d <= inner) a = 1f;
                    else if (d >= r) a = 0f;
                    else
                    {
                        float t = (d - inner) / (r - inner);
                        a = 1f - t * t * (3f - 2f * t); // smoothstep falloff
                    }
                }
                if (a <= 0f) continue;

                int tx = FloorDiv(x, TileSize), ty = FloorDiv(y, TileSize);
                if (!_tiles.TryGetValue((tx, ty), out var tile)) _tiles[(tx, ty)] = tile = new float[TileSize * TileSize];
                ref float cell = ref tile[(y - ty * TileSize) * TileSize + (x - tx * TileSize)];
                if (a > cell) cell = a;
            }
        }

        Bounds = Bounds.IsEmpty ? rect : new PixelRect(Math.Min(Bounds.Left, rect.Left), Math.Min(Bounds.Top, rect.Top),
            Math.Max(Bounds.Right, rect.Right), Math.Max(Bounds.Bottom, rect.Bottom));
        Version++;
    }

    private static int FloorDiv(int a, int b) => a >= 0 ? a / b : (a - b + 1) / b;
}
