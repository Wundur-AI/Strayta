namespace Strayta.Core.Painting;

/// <summary>
/// The healing step shared by the Healing Brush and the Spot Healing Brush: texture from a source area, color and
/// brightness from the destination's surroundings.
/// </summary>
/// <remarks>
/// <para>
/// Method: Poisson image editing with a guidance field taken from the source (Pérez, Gangnet and Blake, "Poisson Image
/// Editing", SIGGRAPH 2003). Inside the painted area Ω the result f must have the source's Laplacian, Δf = Δs, and on
/// Ω's boundary it must equal the destination d. Writing f = s + h turns this into a membrane problem: Δh = 0 in Ω with
/// h = d − s on the boundary. The correction h is smooth (harmonic), which is why healing keeps every detail of the
/// source and only shifts its colors, and it can be solved cheaply.
/// </para>
/// <para>
/// Solver: cascadic multigrid. The grid (the stroke's bounding box plus a one-pixel ring of fixed boundary pixels) is
/// halved until it is tiny; each coarse cell is unknown if any of its four children is, otherwise fixed at the mean of
/// their fixed values. The coarsest grid is relaxed to convergence, then each finer level starts from the bilinearly
/// interpolated coarser solution and is relaxed with red–black successive over-relaxation until the largest update
/// falls below a small tolerance. Pixels outside the canvas are left out of the grid (a zero-flux, Neumann edge), so
/// strokes touching the image border still heal. All channels, alpha included, are solved at once.
/// </para>
/// </remarks>
public static class Healing
{
    private const byte Fixed = 0, Unknown = 1;

    /// <summary>
    /// Heals the pixels a finished stroke covered in <paramref name="image"/>, with texture from the pixels
    /// (<paramref name="dx"/>, <paramref name="dy"/>) away (as <see cref="CloneSource"/>). Returns the healed pixels over
    /// the stroke's bounds plus one pixel, to be painted through the stroke (see <see cref="PaintStroke.WithSource"/>).
    /// </summary>
    public static PixelSource HealStroke(PixelSource image, int dx, int dy, PaintStroke stroke, PixelRect canvas) =>
        Heal(image, image, dx, dy, Coverage(stroke), stroke.Bounds, canvas);

    /// <summary>The stroke's coverage over its bounds, row-major.</summary>
    public static float[] Coverage(PaintStroke stroke)
    {
        var b = stroke.Bounds;
        var cov = new float[b.Width * b.Height];
        Parallel.For(b.Top, b.Bottom, y =>
        {
            int row = (y - b.Top) * b.Width - b.Left;
            for (int x = b.Left; x < b.Right; x++) cov[row + x] = stroke.CoverageAt(x, y);
        });
        return cov;
    }

    /// <summary>
    /// Heals the pixels of <paramref name="destination"/> where <paramref name="coverage"/> (over <paramref name="area"/>)
    /// is above zero, taking gradients from <paramref name="source"/> offset by (<paramref name="dx"/>,
    /// <paramref name="dy"/>). Outside that region the result is the destination exactly.
    /// </summary>
    public static PixelSource Heal(PixelSource destination, PixelSource source, int dx, int dy, float[] coverage, PixelRect area, PixelRect canvas)
    {
        var grid = new PixelRect(area.Left - 1, area.Top - 1, area.Right + 1, area.Bottom + 1).Intersect(canvas);
        int colors = destination.ColorChannels, c = colors + 1;
        if (grid.IsEmpty) return PixelSource.FromFloats([], colors, PixelRect.Empty);
        int w = grid.Width, h = grid.Height;
        var dst = new float[w * h * c];
        var src = new float[w * h * c];
        var state = new byte[w * h];

        Parallel.For(0, h, row =>
        {
            int y = grid.Top + row;
            Span<float> color = stackalloc float[3];
            for (int col = 0; col < w; col++)
            {
                int x = grid.Left + col, i = row * w + col, o = i * c;
                float a = destination.Read(x, y, color);
                for (int k = 0; k < colors; k++) dst[o + k] = color[k];
                dst[o + colors] = a;
                a = ReadAs(source, x - dx, y - dy, color, colors);
                for (int k = 0; k < colors; k++) src[o + k] = color[k];
                src[o + colors] = a;
                bool inside = x >= area.Left && x < area.Right && y >= area.Top && y < area.Bottom
                              && coverage[(y - area.Top) * area.Width + (x - area.Left)] > 0f;
                state[i] = inside ? Unknown : Fixed;
            }
        });

        // h = d − s on the boundary (and everywhere fixed); the membrane fills in the rest.
        var corr = new float[dst.Length];
        for (int i = 0; i < state.Length; i++)
            if (state[i] == Fixed)
                for (int k = 0; k < c; k++) corr[i * c + k] = dst[i * c + k] - src[i * c + k];
        SolveMembrane(corr, state, w, h, c);

        var result = dst; // fixed pixels keep the destination exactly
        Parallel.For(0, h, row =>
        {
            for (int i = row * w; i < (row + 1) * w; i++)
            {
                if (state[i] != Unknown) continue;
                for (int k = 0; k < c; k++) result[i * c + k] = Math.Clamp(src[i * c + k] + corr[i * c + k], 0f, 1f);
            }
        });
        return PixelSource.FromFloats(result, colors, grid);
    }

    private static float ReadAs(PixelSource image, int x, int y, Span<float> color, int channels)
    {
        Span<float> c = stackalloc float[3];
        float a = image.Read(x, y, c);
        if (image.ColorChannels == channels) c[..channels].CopyTo(color);
        else if (channels == 1) color[0] = 0.299f * c[0] + 0.587f * c[1] + 0.114f * c[2];
        else color[0] = color[1] = color[2] = c[0];
        return a;
    }

    /// <summary>
    /// Solves Δh = 0 for the <see cref="Unknown"/> cells of a <paramref name="w"/>×<paramref name="h"/> grid of
    /// <paramref name="c"/>-channel values, holding the other cells fixed (cascadic multigrid; see the class remarks).
    /// </summary>
    internal static void SolveMembrane(float[] values, byte[] state, int w, int h, int c)
    {
        bool any = false;
        foreach (byte s in state) any |= s == Unknown;
        if (!any) return;

        if (w > 8 && h > 8)
        {
            int cw = (w + 1) / 2, ch = (h + 1) / 2;
            var coarse = new float[cw * ch * c];
            var coarseState = new byte[cw * ch];
            Span<float> sum = stackalloc float[c];
            for (int y = 0; y < ch; y++)
                for (int x = 0; x < cw; x++)
                {
                    bool unknown = false;
                    int fixedCount = 0;
                    sum.Clear();
                    for (int sy = 2 * y; sy < Math.Min(h, 2 * y + 2); sy++)
                        for (int sx = 2 * x; sx < Math.Min(w, 2 * x + 2); sx++)
                        {
                            int i = sy * w + sx;
                            if (state[i] == Unknown) unknown = true;
                            else
                            {
                                fixedCount++;
                                for (int k = 0; k < c; k++) sum[k] += values[i * c + k];
                            }
                        }
                    int ci = y * cw + x;
                    coarseState[ci] = unknown ? Unknown : Fixed;
                    if (!unknown)
                        for (int k = 0; k < c; k++) coarse[ci * c + k] = sum[k] / fixedCount;
                }
            SolveMembrane(coarse, coarseState, cw, ch, c);

            // Bilinear prolongation: fine pixel x sits at coarse coordinate x/2 − 0.25.
            Parallel.For(0, h, y =>
            {
                float v = Math.Clamp(y * 0.5f - 0.25f, 0f, ch - 1);
                int y0 = (int)v, y1 = Math.Min(ch - 1, y0 + 1);
                float fy = v - y0;
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (state[i] != Unknown) continue;
                    float u = Math.Clamp(x * 0.5f - 0.25f, 0f, cw - 1);
                    int x0 = (int)u, x1 = Math.Min(cw - 1, x0 + 1);
                    float fx = u - x0;
                    for (int k = 0; k < c; k++)
                    {
                        float top = coarse[(y0 * cw + x0) * c + k] * (1 - fx) + coarse[(y0 * cw + x1) * c + k] * fx;
                        float bottom = coarse[(y1 * cw + x0) * c + k] * (1 - fx) + coarse[(y1 * cw + x1) * c + k] * fx;
                        values[i * c + k] = top * (1 - fy) + bottom * fy;
                    }
                }
            });
            Relax(values, state, w, h, c, maxSweeps: 400, tolerance: 1e-6f);
        }
        else
        {
            Relax(values, state, w, h, c, maxSweeps: 4000, tolerance: 1e-7f);
        }
    }

    /// <summary>Red–black successive over-relaxation until the largest update is below <paramref name="tolerance"/>.</summary>
    private static void Relax(float[] values, byte[] state, int w, int h, int c, int maxSweeps, float tolerance)
    {
        // Close to the optimal factor for a grid of this size; the start is already smooth, so this mostly removes the
        // remaining low-frequency error quickly without overshooting.
        float omega = Math.Clamp(2f / (1f + MathF.Sin(MathF.PI / Math.Max(w, h))), 1f, 1.9f);
        var rowMax = new float[h];
        bool parallel = w * h >= 16384;
        for (int sweep = 0; sweep < maxSweeps; sweep++)
        {
            for (int parity = 0; parity < 2; parity++)
            {
                int p = parity;
                if (parallel) Parallel.For(0, h, y => RelaxRow(values, state, w, h, c, y, p, omega, rowMax));
                else for (int y = 0; y < h; y++) RelaxRow(values, state, w, h, c, y, p, omega, rowMax);
            }
            float worst = 0f;
            foreach (float m in rowMax) worst = MathF.Max(worst, m);
            if (worst < tolerance) break;
        }
    }

    /// <summary>One colour of one row of a red–black sweep; records the row's largest update in <paramref name="rowMax"/>.</summary>
    private static void RelaxRow(float[] values, byte[] state, int w, int h, int c, int y, int parity, float omega, float[] rowMax)
    {
        float max = parity == 0 ? 0f : rowMax[y];
        Span<float> avg = stackalloc float[c];
        for (int x = (y + parity) & 1; x < w; x += 2)
        {
            int i = y * w + x;
            if (state[i] != Unknown) continue;
            avg.Clear();
            int n = 0;
            if (x > 0) { Add(avg, values, (i - 1) * c); n++; }
            if (x < w - 1) { Add(avg, values, (i + 1) * c); n++; }
            if (y > 0) { Add(avg, values, (i - w) * c); n++; }
            if (y < h - 1) { Add(avg, values, (i + w) * c); n++; }
            if (n == 0) continue;
            float inv = 1f / n;
            for (int k = 0; k < c; k++)
            {
                ref float v = ref values[i * c + k];
                float delta = omega * (avg[k] * inv - v);
                v += delta;
                max = MathF.Max(max, MathF.Abs(delta));
            }
        }
        rowMax[y] = max;
    }

    private static void Add(Span<float> sum, float[] values, int at)
    {
        for (int k = 0; k < sum.Length; k++) sum[k] += values[at + k];
    }
}
