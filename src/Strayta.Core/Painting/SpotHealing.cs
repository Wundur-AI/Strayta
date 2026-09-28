namespace Strayta.Core.Painting;

/// <summary>
/// The Spot Healing Brush's Proximity Match: finds a nearby area whose surroundings look like the surroundings of the
/// painted blemish, to heal from (see <see cref="Healing"/>).
/// </summary>
/// <remarks>
/// <para>
/// For every candidate offset d, the ring of pixels just outside the painted area Ω is compared with the same ring
/// moved by d (mean squared difference over the colour channels and alpha, premultiplied). The painted pixels
/// themselves are never compared: they are the blemish. Candidates whose source area would reuse painted pixels, or
/// reach outside the image, are skipped. A small penalty for distance prefers nearby matches when several are about as
/// good, as Photoshop's "proximity" suggests.
/// </para>
/// <para>
/// Search: coarse to fine over an image pyramid, in the spirit of PatchMatch's coarse-to-fine refinement (Barnes et al.,
/// SIGGRAPH 2009) but with an exhaustive search at the coarsest level, which is small. The best few offsets there are
/// refined at each finer level within ±2 pixels of twice their coarse value.
/// </para>
/// </remarks>
public static class SpotHealing
{
    private const int Keep = 8;

    /// <summary>The width of the compared ring outside the painted area, for a brush of <paramref name="brushSize"/> pixels.</summary>
    public static int RingWidth(float brushSize) => Math.Clamp((int)MathF.Round(brushSize * 0.25f), 3, 24);

    /// <summary>
    /// The offset (destination − source, as <see cref="CloneSource"/>) to heal the finished <paramref name="stroke"/>
    /// from, or null when no area of the image fits.
    /// </summary>
    public static (int Dx, int Dy)? FindSource(PixelSource image, PaintStroke stroke, PixelRect canvas) =>
        FindSource(image, Healing.Coverage(stroke), stroke.Bounds, canvas, stroke.Brush.Size);

    /// <summary>As <see cref="FindSource(PixelSource, PaintStroke, PixelRect)"/> for an explicit coverage over <paramref name="area"/>.</summary>
    public static (int Dx, int Dy)? FindSource(PixelSource image, float[] coverage, PixelRect area, PixelRect canvas, float brushSize)
    {
        int ring = RingWidth(brushSize);
        int extent = Math.Max(area.Width, area.Height);
        int radius = Math.Clamp(extent * 2 + (int)brushSize, 48, 1200);
        var window = new PixelRect(area.Left - ring - radius, area.Top - ring - radius, area.Right + ring + radius, area.Bottom + ring + radius).Intersect(canvas);
        if (window.IsEmpty) return null;

        // Level 0: the window's pixels (premultiplied colors, then alpha) and the painted area.
        int colors = image.ColorChannels, c = colors + 1;
        var level0 = new Level(window.Width, window.Height, c);
        Parallel.For(0, window.Height, row =>
        {
            int y = window.Top + row;
            Span<float> color = stackalloc float[3];
            for (int col = 0; col < window.Width; col++)
            {
                int x = window.Left + col, o = (row * window.Width + col) * c;
                float a = image.Read(x, y, color);
                for (int k = 0; k < colors; k++) level0.Pixels[o + k] = color[k] * a;
                level0.Pixels[o + colors] = a;
                if (x >= area.Left && x < area.Right && y >= area.Top && y < area.Bottom
                    && coverage[(y - area.Top) * area.Width + (x - area.Left)] > 0f)
                    level0.Painted[row * window.Width + col] = true;
            }
        });
        level0.MarkRing(ring);

        // Coarser levels until the painted area is about 16 pixels across (at most four halvings).
        var levels = new List<Level> { level0 };
        while (levels.Count < 5 && (extent >> levels.Count) >= 16 && levels[^1].Width > 32 && levels[^1].Height > 32)
            levels.Add(levels[^1].Half());

        // Exhaustive search at the coarsest level.
        var top = levels[^1];
        int r = radius >> (levels.Count - 1);
        var best = new List<(int Dx, int Dy, double Cost)>();
        var sync = new object();
        Parallel.For(-r, r + 1, () => new List<(int, int, double)>(), (dy, _, local) =>
        {
            for (int dx = -r; dx <= r; dx++)
                if (top.Cost(dx, dy, radius >> (levels.Count - 1)) is { } cost) Insert(local, (dx, dy, cost));
            return local;
        }, local =>
        {
            lock (sync) foreach (var e in local) Insert(best, e);
        });

        // Refine through the finer levels.
        for (int l = levels.Count - 2; l >= 0; l--)
        {
            var level = levels[l];
            int levelRadius = radius >> l;
            var next = new List<(int, int, double)>();
            var tried = new HashSet<(int, int)>();
            foreach (var (cx, cy, _) in best)
                for (int ey = -2; ey <= 2; ey++)
                    for (int ex = -2; ex <= 2; ex++)
                    {
                        int dx = cx * 2 + ex, dy = cy * 2 + ey;
                        if (!tried.Add((dx, dy))) continue;
                        if (level.Cost(dx, dy, levelRadius) is { } cost) Insert(next, (dx, dy, cost));
                    }
            best = next;
        }
        return best.Count == 0 ? null : (best[0].Dx, best[0].Dy);
    }

    private static void Insert(List<(int Dx, int Dy, double Cost)> list, (int Dx, int Dy, double Cost) e)
    {
        int at = list.FindIndex(x => x.Cost > e.Cost);
        if (at < 0)
        {
            if (list.Count < Keep) list.Add(e);
            return;
        }
        list.Insert(at, e);
        if (list.Count > Keep) list.RemoveAt(list.Count - 1);
    }

    /// <summary>One pyramid level of the search window.</summary>
    private sealed class Level
    {
        public Level(int width, int height, int channels)
        {
            Width = width;
            Height = height;
            Channels = channels;
            Pixels = new float[width * height * channels];
            Painted = new bool[width * height];
            Ring = new bool[width * height];
        }

        public int Width { get; }
        public int Height { get; }
        public int Channels { get; }
        public float[] Pixels { get; }
        public bool[] Painted { get; }
        public bool[] Ring { get; }

        private (int X, int Y)[] _ringPixels = [];
        private (int X, int Y)[] _paintedPixels = [];
        private PixelRect _box; // painted area and ring

        /// <summary>Marks the pixels within <paramref name="width"/> (square distance) of the painted area, outside it.</summary>
        public void MarkRing(int width)
        {
            // Separable dilation: horizontally, then vertically.
            var horizontal = new bool[Width * Height];
            for (int y = 0; y < Height; y++)
            {
                int last = -1_000_000;
                for (int x = 0; x < Width; x++)
                {
                    if (Painted[y * Width + x]) last = x;
                    if (x - last <= width) horizontal[y * Width + x] = true;
                }
                last = 1_000_000;
                for (int x = Width - 1; x >= 0; x--)
                {
                    if (Painted[y * Width + x]) last = x;
                    if (last - x <= width) horizontal[y * Width + x] = true;
                }
            }
            for (int x = 0; x < Width; x++)
            {
                int last = -1_000_000;
                for (int y = 0; y < Height; y++)
                {
                    if (horizontal[y * Width + x]) last = y;
                    if (y - last <= width) Ring[y * Width + x] = true;
                }
                last = 1_000_000;
                for (int y = Height - 1; y >= 0; y--)
                {
                    if (horizontal[y * Width + x]) last = y;
                    if (last - y <= width) Ring[y * Width + x] = true;
                }
            }
            for (int i = 0; i < Ring.Length; i++) Ring[i] &= !Painted[i];
            Index();
        }

        private void Index()
        {
            var ring = new List<(int, int)>();
            var painted = new List<(int, int)>();
            int l = Width, t = Height, r = 0, b = 0;
            for (int y = 0; y < Height; y++)
                for (int x = 0; x < Width; x++)
                {
                    int i = y * Width + x;
                    if (!Ring[i] && !Painted[i]) continue;
                    if (Ring[i]) ring.Add((x, y));
                    else painted.Add((x, y));
                    (l, t, r, b) = (Math.Min(l, x), Math.Min(t, y), Math.Max(r, x + 1), Math.Max(b, y + 1));
                }
            _ringPixels = [.. ring];
            _paintedPixels = [.. painted];
            _box = r > l ? new PixelRect(l, t, r, b) : PixelRect.Empty;
        }

        /// <summary>The next coarser level: 2×2 averages; a cell is painted if any of its pixels is, ring if any is and it is not painted.</summary>
        public Level Half()
        {
            int w = (Width + 1) / 2, h = (Height + 1) / 2, c = Channels;
            var o = new Level(w, h, c);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int n = 0, oi = y * w + x;
                    bool painted = false, ring = false;
                    for (int sy = 2 * y; sy < Math.Min(Height, 2 * y + 2); sy++)
                        for (int sx = 2 * x; sx < Math.Min(Width, 2 * x + 2); sx++)
                        {
                            int i = sy * Width + sx;
                            n++;
                            painted |= Painted[i];
                            ring |= Ring[i];
                            for (int k = 0; k < c; k++) o.Pixels[oi * c + k] += Pixels[i * c + k];
                        }
                    for (int k = 0; k < c; k++) o.Pixels[oi * c + k] /= n;
                    o.Painted[oi] = painted;
                    o.Ring[oi] = ring && !painted;
                }
            o.Index();
            return o;
        }

        /// <summary>
        /// Mean squared difference between the ring and the ring moved by (−<paramref name="dx"/>, −<paramref name="dy"/>),
        /// with a slight preference for short offsets; null when the moved area leaves the window or reuses painted pixels.
        /// </summary>
        public double? Cost(int dx, int dy, int radius)
        {
            if (_ringPixels.Length == 0 || (dx == 0 && dy == 0)) return null;
            var b = _box;
            if (b.Left - dx < 0 || b.Top - dy < 0 || b.Right - dx > Width || b.Bottom - dy > Height) return null;
            // The moved area must not overlap the painted pixels (which only matters when the boxes overlap).
            bool overlap = Math.Abs(dx) < b.Width && Math.Abs(dy) < b.Height;
            if (overlap)
            {
                foreach (var (x, y) in _paintedPixels)
                    if (Painted[(y - dy) * Width + (x - dx)]) return null;
                foreach (var (x, y) in _ringPixels)
                    if (Painted[(y - dy) * Width + (x - dx)]) return null;
            }

            int c = Channels;
            double sum = 0;
            var px = Pixels;
            foreach (var (x, y) in _ringPixels)
            {
                int a = (y * Width + x) * c, s = ((y - dy) * Width + (x - dx)) * c;
                for (int k = 0; k < c; k++)
                {
                    float d = px[a + k] - px[s + k];
                    sum += d * d;
                }
            }
            double mse = sum / (_ringPixels.Length * c);
            double distance = Math.Sqrt((double)dx * dx + (double)dy * dy) / Math.Max(1, radius);
            return mse * (1 + 0.1 * distance) + 1e-7 * distance;
        }
    }
}
