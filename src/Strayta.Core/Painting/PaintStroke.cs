using Strayta.Core.Selection;

namespace Strayta.Core.Painting;

/// <summary>
/// Brush tip and painting settings. Size is the diameter in pixels; hardness, opacity, flow and roundness are 0..1.
/// </summary>
/// <remarks>
/// Opacity and Flow follow Photoshop: within one stroke each dab adds <see cref="Flow"/> × its tip coverage of the
/// remaining distance to full coverage (so overlapping dabs build up), and the whole stroke is then applied at
/// <see cref="Opacity"/>, the most one stroke can cover. At 100% flow a hard brush lays down its full opacity at once;
/// at 10% dabs build up gradually where they overlap.
/// </remarks>
public readonly record struct BrushSettings(float Size, float Hardness, float Opacity)
{
    public static BrushSettings Default => new(30f, 0.8f, 1f);

    /// <summary>How much each dab adds, 0..1 (Photoshop's Flow).</summary>
    public float Flow { get; init; } = 1f;

    /// <summary>Distance between dabs in percent of the diameter (Photoshop's default is 25%).</summary>
    public float SpacingPercent { get; init; } = 25f;

    /// <summary>Tip angle in degrees, counter-clockwise from the x axis (matters when <see cref="Roundness"/> is below 1).</summary>
    public float Angle { get; init; }

    /// <summary>Ratio of the tip's short axis to its long axis, 0.01..1 (1 is round).</summary>
    public float Roundness { get; init; } = 1f;

    /// <summary>How the paint combines with the layer (Normal, Behind, Clear, the blend modes).</summary>
    public PaintMode Mode { get; init; }

    /// <summary>Pen pressure scales the dab size (Photoshop's pressure-for-size button).</summary>
    public bool PressureSize { get; init; }

    /// <summary>Pen pressure scales the stroke's opacity where it is painted (pressure-for-opacity).</summary>
    public bool PressureOpacity { get; init; }

    /// <summary>Distance between dabs in pixels at full size.</summary>
    public float Spacing => MathF.Max(0.5f, Size * Math.Clamp(SpacingPercent, 1f, 1000f) / 100f);
}

/// <summary>
/// A stroke in progress: dab coverage accumulated in document space. Each dab adds its flow toward full coverage (see
/// <see cref="BrushSettings"/>); the stroke is applied at the brush opacity, so one stroke never exceeds it.
/// The target layer is not modified until <see cref="StrokeBaker.Bake"/>.
/// A selection clips the coverage itself, so the live overlay, previews and the baked result all agree.
/// </summary>
public sealed class PaintStroke
{
    private const int TileSize = 128;
    // Read by render threads while dabs are added on the UI thread.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), float[]> _tiles = new();
    private readonly PixelRect _limit;
    private float _lastX, _lastY, _lastPressure = 1f, _carry;
    private bool _started;

    public PaintStroke(PixelLayer target, BrushSettings brush, RgbColor color, bool erase, PixelRect limit, SelectionMask? clip = null)
        : this(target, brush, color, erase, limit, clip, targetsMask: false)
    {
    }

    private PaintStroke(LayerNode owner, BrushSettings brush, RgbColor color, bool erase, PixelRect limit, SelectionMask? clip, bool targetsMask,
        CloneSource? source = null)
    {
        Owner = owner;
        Brush = brush;
        Color = color;
        Erase = erase;
        Clip = clip;
        _limit = clip is null ? limit : limit.Intersect(clip.Bounds);
        TargetsMask = targetsMask;
        Source = source;
    }

    /// <summary>
    /// A cloning stroke (Clone Stamp, and the healing tools' results): paints each pixel with <paramref name="source"/>'s
    /// color there instead of a single color, into <paramref name="owner"/>'s pixels, or its layer mask when
    /// <paramref name="targetsMask"/> (the source is then a mask too).
    /// </summary>
    public static PaintStroke Cloning(LayerNode owner, bool targetsMask, BrushSettings brush, CloneSource source, PixelRect limit, SelectionMask? clip = null) =>
        new(owner, brush, default, erase: false, limit, clip, targetsMask, source);

    /// <summary>
    /// The same painted area (coverage, bounds) with a different color source and opacity (and optionally mode): the
    /// healing tools paint their healed patch through the stroke the user drew. The coverage is shared, so the stroke
    /// must be finished.
    /// </summary>
    public PaintStroke WithSource(CloneSource source, float opacity, PaintMode mode = PaintMode.Normal)
    {
        var copy = new PaintStroke(Owner, Brush with { Opacity = opacity, Mode = mode }, Color, erase: false, _limit, Clip, TargetsMask, source)
        {
            Bounds = Bounds,
            Version = Version,
        };
        foreach (var (key, tile) in _tiles) copy._tiles[key] = tile;
        return copy;
    }

    /// <summary>
    /// A finished cloning stroke whose coverage is given rather than painted: <paramref name="coverage"/> (0..1, row-major
    /// over <paramref name="area"/>), for fills that paint a computed image through a selection (Edit › Content-Aware Fill).
    /// </summary>
    public static PaintStroke FromCoverage(LayerNode owner, bool targetsMask, float[] coverage, PixelRect area, CloneSource source, PixelRect limit)
    {
        var stroke = new PaintStroke(owner, new BrushSettings(1f, 1f, 1f), default, erase: false, limit, clip: null, targetsMask, source);
        var bounds = area.Intersect(limit);
        int l = int.MaxValue, t = int.MaxValue, r = int.MinValue, b = int.MinValue;
        for (int y = bounds.Top; y < bounds.Bottom; y++)
            for (int x = bounds.Left; x < bounds.Right; x++)
            {
                float c = coverage[(y - area.Top) * area.Width + (x - area.Left)];
                if (c <= 0f) continue;
                var tile = stroke.Tile(FloorDiv(x, TileSize), FloorDiv(y, TileSize));
                tile[(y - FloorDiv(y, TileSize) * TileSize) * TileSize + (x - FloorDiv(x, TileSize) * TileSize)] = Math.Min(1f, c);
                (l, t, r, b) = (Math.Min(l, x), Math.Min(t, y), Math.Max(r, x + 1), Math.Max(b, y + 1));
            }
        stroke._started = true;
        stroke.Bounds = r > l ? new PixelRect(l, t, r, b) : PixelRect.Empty;
        stroke.Version++;
        return stroke;
    }

    /// <summary>
    /// Where a cloning stroke takes its colors (see <see cref="Cloning"/>); null for strokes that paint <see cref="Color"/>.
    /// Transparent source pixels paint nothing, as with Photoshop's Clone Stamp.
    /// </summary>
    public CloneSource? Source { get; }

    /// <summary>
    /// A stroke that paints <paramref name="gray"/> (0 black hides, 1 white reveals) into the layer mask of
    /// <paramref name="owner"/>, which may be a pixel layer, group or adjustment layer. See <see cref="MaskBaker"/>.
    /// </summary>
    /// A selection confines mask painting too, as it does in Photoshop.
    public static PaintStroke ForMask(LayerNode owner, BrushSettings brush, float gray, PixelRect limit, SelectionMask? clip = null) =>
        new(owner, brush, new RgbColor(gray, gray, gray), erase: false, limit, clip, targetsMask: true);

    /// <summary>The layer whose pixels (or, for <see cref="TargetsMask"/>, whose mask) the stroke paints.</summary>
    public LayerNode Owner { get; }

    /// <summary>True when the stroke paints into <see cref="Owner"/>'s layer mask rather than its pixels.</summary>
    public bool TargetsMask { get; }

    /// <summary>The pixel layer being painted; mask strokes have none.</summary>
    public PixelLayer Target => !TargetsMask && Owner is PixelLayer p ? p : throw new InvalidOperationException("A mask stroke paints a mask, not layer pixels.");
    public BrushSettings Brush { get; }
    public RgbColor Color { get; }
    public bool Erase { get; }

    /// <summary>How the stroke combines with the layer's pixels (ignored by the eraser and by mask strokes).</summary>
    public PaintMode Mode => Erase || TargetsMask ? PaintMode.Normal : Brush.Mode;

    /// <summary>The selection painting is confined to, or null to paint anywhere.</summary>
    public SelectionMask? Clip { get; }

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

    /// <summary>
    /// Adds dabs from the previous point to (x, y), spaced along the path; the first call places one dab.
    /// <paramref name="pressure"/> (0..1, pen pressure; 1 for a mouse) is interpolated between points and scales the
    /// dab size and/or opacity when <see cref="BrushSettings.PressureSize"/> / <see cref="BrushSettings.PressureOpacity"/> are on.
    /// </summary>
    public void StrokeTo(float x, float y, float pressure = 1f)
    {
        pressure = Math.Clamp(pressure, 0f, 1f);
        if (!_started)
        {
            _started = true;
            Dab(x, y, pressure);
            (_lastX, _lastY, _lastPressure) = (x, y, pressure);
            return;
        }

        float dx = x - _lastX, dy = y - _lastY;
        float length = MathF.Sqrt(dx * dx + dy * dy);
        // Spacing follows the dab size, so it tightens with pressure-for-size as Photoshop's does.
        float spacing = SpacingAt(_lastPressure);
        float travelled = spacing - _carry;
        while (travelled <= length)
        {
            float t = travelled / length;
            float p = _lastPressure + (pressure - _lastPressure) * t;
            Dab(_lastX + dx * t, _lastY + dy * t, p);
            spacing = SpacingAt(p);
            travelled += spacing;
        }
        _carry = length - (travelled - spacing);
        (_lastX, _lastY, _lastPressure) = (x, y, pressure);
    }

    /// <summary>
    /// Lifts the brush: the next <see cref="StrokeTo"/> starts a new line with a dab of its own instead of continuing
    /// from the last point (Stroke Path paints every subpath as one stroke this way).
    /// </summary>
    public void Lift()
    {
        _started = false;
        _carry = 0;
    }

    /// <summary>
    /// Airbrush build-up: one more dab where the stroke is now, for while the pointer rests (called at a steady rate
    /// while the button is held). Each dab adds <see cref="BrushSettings.Flow"/>, up to the stroke's opacity.
    /// </summary>
    public void Airbrush()
    {
        if (_started) Dab(_lastX, _lastY, _lastPressure);
    }

    /// <summary>Where the stroke is now (the last point it reached), or null before it starts.</summary>
    public (float X, float Y)? Position => _started ? (_lastX, _lastY) : null;

    private float SpacingAt(float pressure) =>
        Brush.PressureSize ? MathF.Max(0.5f, Brush.Spacing * MathF.Max(pressure, 0.02f)) : Brush.Spacing;

    /// <summary>
    /// One dab of a round or elliptical tip. Inside radius × hardness coverage is full; beyond it falls off smoothly to
    /// zero at the radius. A fully hard tip gets a one-pixel anti-aliased edge. The dab moves each covered cell toward
    /// its cap (1, or the pen pressure with pressure-for-opacity; times the selection) by flow × tip coverage.
    /// </summary>
    private void Dab(float cx, float cy, float pressure)
    {
        var brush = Brush;
        float size = brush.PressureSize ? brush.Size * pressure : brush.Size;
        float r = MathF.Max(0.5f, size / 2f);
        float hard = Math.Clamp(brush.Hardness, 0f, 1f);
        float round = Math.Clamp(brush.Roundness, 0.01f, 1f);
        float cap = brush.PressureOpacity ? pressure : 1f;
        float flow = Math.Clamp(brush.Flow, 0f, 1f);
        if (cap <= 0f || flow <= 0f) return;

        // Ellipse axes: r along the angle, r × roundness across it (y points down, so a positive angle turns counter-clockwise on screen).
        float angle = -brush.Angle * MathF.PI / 180f;
        float cos = MathF.Cos(angle), sin = MathF.Sin(angle);
        bool elliptical = round < 0.999f;
        float ex = elliptical ? r * MathF.Sqrt(cos * cos + round * round * sin * sin) : r;
        float ey = elliptical ? r * MathF.Sqrt(sin * sin + round * round * cos * cos) : r;
        var rect = new PixelRect((int)MathF.Floor(cx - ex - 1), (int)MathF.Floor(cy - ey - 1),
            (int)MathF.Ceiling(cx + ex + 1), (int)MathF.Ceiling(cy + ey + 1)).Intersect(_limit);
        if (rect.IsEmpty) return;
        var clip = Clip;
        float inner = r * hard, invSoft = hard < 0.99f ? 1f / (r - inner) : 0f, invRound2 = 1f / (round * round);

        // Walk the dab tile by tile, so each tile is looked up once rather than once per pixel.
        for (int ty = FloorDiv(rect.Top, TileSize); ty <= FloorDiv(rect.Bottom - 1, TileSize); ty++)
        {
            int y0 = Math.Max(rect.Top, ty * TileSize), y1 = Math.Min(rect.Bottom, (ty + 1) * TileSize);
            for (int tx = FloorDiv(rect.Left, TileSize); tx <= FloorDiv(rect.Right - 1, TileSize); tx++)
            {
                int x0 = Math.Max(rect.Left, tx * TileSize), x1 = Math.Min(rect.Right, (tx + 1) * TileSize);
                float[]? tile = null;
                for (int y = y0; y < y1; y++)
                {
                    float py = y + 0.5f - cy;
                    for (int x = x0; x < x1; x++)
                    {
                        float px = x + 0.5f - cx;
                        float d, edge;
                        if (elliptical)
                        {
                            float u = px * cos + py * sin, v = -px * sin + py * cos;
                            d = MathF.Sqrt(u * u + v * v * invRound2);
                            // Distance to the outline in pixels, for the anti-aliased edge: (r − d) / |∇d|.
                            float grad = d > 1e-4f ? MathF.Sqrt(u * u + v * v * invRound2 * invRound2) / d : 1f;
                            edge = (r - d) / MathF.Max(grad, 1e-4f);
                        }
                        else
                        {
                            d = MathF.Sqrt(px * px + py * py);
                            edge = r - d;
                        }
                        float a;
                        if (hard >= 0.99f) a = Math.Clamp(edge + 0.5f, 0f, 1f);
                        else if (d <= inner) a = 1f;
                        else if (d >= r) a = 0f;
                        else
                        {
                            float t = (d - inner) * invSoft;
                            a = 1f - t * t * (3f - 2f * t); // smoothstep falloff
                        }
                        if (a <= 0f) continue;
                        // Scaling the cap by the selection clips the whole stroke exactly, since each update is linear in the cap.
                        float target = cap;
                        if (clip is not null)
                        {
                            target *= clip.CoverageAt(x, y) * (1f / 255f);
                            if (target <= 0f) continue;
                        }
                        tile ??= Tile(tx, ty);
                        ref float cell = ref tile[(y - ty * TileSize) * TileSize + (x - tx * TileSize)];
                        if (cell < target) cell += (target - cell) * a * flow;
                    }
                }
            }
        }

        Bounds = Bounds.IsEmpty ? rect : new PixelRect(Math.Min(Bounds.Left, rect.Left), Math.Min(Bounds.Top, rect.Top),
            Math.Max(Bounds.Right, rect.Right), Math.Max(Bounds.Bottom, rect.Bottom));
        Version++;
    }

    private float[] Tile(int tx, int ty) => _tiles.GetOrAdd((tx, ty), static _ => new float[TileSize * TileSize]);

    private static int FloorDiv(int a, int b) => a >= 0 ? a / b : (a - b + 1) / b;
}
