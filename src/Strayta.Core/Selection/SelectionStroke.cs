namespace Strayta.Core.Selection;

/// <summary>Where Edit › Stroke puts its band relative to the selection's edge.</summary>
public enum StrokeLocation
{
    Inside,
    Center,
    Outside,
}

/// <summary>
/// The area Edit › Stroke paints: a band of a given width along the selection's outline, as coverage.
/// </summary>
/// <remarks>
/// The outline is where coverage crosses 50%. Two exact Euclidean distance transforms (Felzenszwalb and
/// Huttenlocher's lower-envelope algorithm, 2012) give every pixel its distance to the nearest pixel on the other
/// side; half a pixel less is the distance to the outline between them, signed negative inside. The band is the range
/// of signed distances the location asks for, with a one-pixel anti-aliased ramp at its far side. At the outline
/// itself the band uses the selection's own coverage (its complement outside), so an inside stroke meets an outside
/// one, or a fill of the selection, without a seam. Beyond the canvas counts as unselected, so a stroke of the whole
/// canvas runs along its edges.
/// </remarks>
public static class SelectionStroke
{
    /// <summary>Photoshop's largest stroke width.</summary>
    public const int MaxWidth = 250;

    /// <summary>The stroke's coverage within <paramref name="canvas"/>, or null when it covers nothing.</summary>
    public static SelectionMask? Band(SelectionMask selection, int width, StrokeLocation location, PixelRect canvas)
    {
        width = Math.Clamp(width, 1, MaxWidth);
        var region = MaskFilters.Inflate(selection.Bounds, width + 2);
        int w = region.Width, h = region.Height;
        var coverage = MaskFilters.Extract(selection, region);
        // Outside the canvas nothing is selected.
        if (canvas.Intersect(region) != region)
            Parallel.For(0, h, row =>
            {
                int y = region.Top + row;
                for (int col = 0; col < w; col++)
                {
                    int x = region.Left + col;
                    if (x < canvas.Left || x >= canvas.Right || y < canvas.Top || y >= canvas.Bottom) coverage[row * w + col] = 0;
                }
            });

        var toInside = DistanceSquared(coverage, w, h, inside: true);   // for outside pixels
        var toOutside = DistanceSquared(coverage, w, h, inside: false); // for inside pixels
        var band = new byte[coverage.Length];
        float half = width / 2f;
        Parallel.For(0, h, row =>
        {
            for (int i = row * w, end = i + w; i < end; i++)
            {
                float sel = coverage[i] * (1f / 255f);
                bool isInside = coverage[i] >= 128;
                float sd = isInside ? -(MathF.Sqrt(toOutside[i]) - 0.5f) : MathF.Sqrt(toInside[i]) - 0.5f;
                float v = location switch
                {
                    StrokeLocation.Outside => MathF.Min(1f - sel, Ramp(width - sd)),
                    StrokeLocation.Inside => MathF.Min(sel, Ramp(width + sd)),
                    _ => Ramp(half - MathF.Abs(sd)),
                };
                band[i] = MaskFilters.ClampByte(v * 255f);
            }
        });
        return SelectionMask.FromCoverage(region, band, canvas);
    }

    private static float Ramp(float v) => Math.Clamp(v + 0.5f, 0f, 1f);

    /// <summary>
    /// Squared distance from each pixel to the nearest pixel that is inside (coverage ≥ 50%) when
    /// <paramref name="inside"/> is true, or outside otherwise; pixels in that set get 0.
    /// </summary>
    private static float[] DistanceSquared(byte[] coverage, int w, int h, bool inside)
    {
        const float Far = 1e10f;
        var d = new float[coverage.Length];
        for (int i = 0; i < d.Length; i++) d[i] = (coverage[i] >= 128) == inside ? 0f : Far;

        // Columns, then rows: the 2-D transform is two 1-D passes.
        Parallel.For(0, w, () => new Scratch(Math.Max(w, h)), (x, _, s) =>
        {
            for (int y = 0; y < h; y++) s.F[y] = d[y * w + x];
            Transform1D(s, h);
            for (int y = 0; y < h; y++) d[y * w + x] = s.D[y];
            return s;
        }, _ => { });
        Parallel.For(0, h, () => new Scratch(Math.Max(w, h)), (y, _, s) =>
        {
            Array.Copy(d, y * w, s.F, 0, w);
            Transform1D(s, w);
            Array.Copy(s.D, 0, d, y * w, w);
            return s;
        }, _ => { });
        return d;
    }

    private sealed class Scratch(int n)
    {
        public readonly float[] F = new float[n], D = new float[n], Z = new float[n + 1];
        public readonly int[] V = new int[n];
    }

    /// <summary>The lower envelope of parabolas rooted at (q, F[q]): D[p] = min over q of (p − q)² + F[q].</summary>
    private static void Transform1D(Scratch s, int n)
    {
        var f = s.F;
        int k = 0;
        s.V[0] = 0;
        s.Z[0] = float.NegativeInfinity;
        s.Z[1] = float.PositiveInfinity;
        for (int q = 1; q < n; q++)
        {
            float sv;
            while (true)
            {
                int v = s.V[k];
                sv = ((f[q] + (float)q * q) - (f[v] + (float)v * v)) / (2f * (q - v));
                if (sv <= s.Z[k] && k > 0) k--; // Z[0] is −∞, so this stops at the first parabola
                else break;
            }
            k++;
            s.V[k] = q;
            s.Z[k] = sv;
            s.Z[k + 1] = float.PositiveInfinity;
        }
        k = 0;
        for (int q = 0; q < n; q++)
        {
            while (s.Z[k + 1] < q) k++;
            int v = s.V[k];
            s.D[q] = (q - v) * (float)(q - v) + f[v];
        }
    }
}
