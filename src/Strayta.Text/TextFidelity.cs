using Strayta.Core;

namespace Strayta.Text;

/// <summary>How closely rendered type matches a reference (normally the pixels Photoshop stored for the layer).</summary>
/// <param name="MatchPercent">Share of the pixels drawn in either image whose coverage differs by at most
/// <see cref="TextFidelity.Tolerance"/> (of 255), at the positions as rendered.</param>
/// <param name="Similarity">1 − Σ|Δcoverage| / Σ max(coverage): 1 for identical, 0 for disjoint.</param>
/// <param name="OffsetX">The whole-pixel shift of the render (within ±<see cref="TextFidelity.SearchRadius"/>) that matches best.</param>
/// <param name="BestMatchPercent"><see cref="MatchPercent"/> after that shift.</param>
/// <param name="ColorError">Mean color difference (0..255) where both are drawn, weighted by coverage.</param>
public sealed record TextFidelityResult(double MatchPercent, double Similarity, int OffsetX, int OffsetY, double BestMatchPercent,
    double BestSimilarity, double ColorError, PixelRect Rendered, PixelRect Reference);

public static class TextFidelity
{
    /// <summary>Coverage difference (of 255) at or below which a pixel counts as matching.</summary>
    public const int Tolerance = 16;

    public const int SearchRadius = 4;

    public static TextFidelityResult Compare(Raster rendered, PixelRect renderedAt, Raster reference, PixelRect referenceAt)
    {
        ArgumentNullException.ThrowIfNull(rendered);
        ArgumentNullException.ThrowIfNull(reference);
        var a = Coverage(rendered);
        var b = Coverage(reference);
        var (match, sim) = Score(a, renderedAt, b, referenceAt, 0, 0);
        int bx = 0, by = 0;
        double bestMatch = match, bestSim = sim;
        for (int dy = -SearchRadius; dy <= SearchRadius; dy++)
            for (int dx = -SearchRadius; dx <= SearchRadius; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                var (m, s) = Score(a, renderedAt, b, referenceAt, dx, dy);
                if (s > bestSim + 1e-9)
                {
                    (bestSim, bestMatch, bx, by) = (s, m, dx, dy);
                }
            }
        return new TextFidelityResult(match, sim, bx, by, bestMatch, bestSim, ColorError(rendered, renderedAt, reference, referenceAt),
            renderedAt, referenceAt);
    }

    private static byte[] Coverage(Raster r)
    {
        int n = r.Width * r.Height;
        var o = new byte[n];
        if (r.Alpha is not { } alpha)
        {
            Array.Fill(o, (byte)255);
            return o;
        }
        for (int i = 0; i < n; i++) o[i] = (byte)Math.Round(alpha.GetNormalized(i) * 255);
        return o;
    }

    private static (double Match, double Similarity) Score(byte[] a, PixelRect ar, byte[] b, PixelRect br, int dx, int dy)
    {
        var ra = new PixelRect(ar.Left + dx, ar.Top + dy, ar.Right + dx, ar.Bottom + dy);
        int left = Math.Min(ra.Left, br.Left), top = Math.Min(ra.Top, br.Top);
        int right = Math.Max(ra.Right, br.Right), bottom = Math.Max(ra.Bottom, br.Bottom);
        long drawn = 0, matches = 0;
        double diff = 0, total = 0;
        for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++)
            {
                int va = At(a, ra, x, y), vb = At(b, br, x, y);
                if (va == 0 && vb == 0) continue;
                drawn++;
                int d = Math.Abs(va - vb);
                if (d <= Tolerance) matches++;
                diff += d;
                total += Math.Max(va, vb);
            }
        return drawn == 0 ? (100, 1) : (100.0 * matches / drawn, total == 0 ? 1 : 1 - diff / total);
    }

    private static int At(byte[] m, PixelRect r, int x, int y) =>
        x < r.Left || y < r.Top || x >= r.Right || y >= r.Bottom ? 0 : m[(y - r.Top) * r.Width + x - r.Left];

    private static double ColorError(Raster a, PixelRect ar, Raster b, PixelRect br)
    {
        if (a.ColorPlanes.Count == 0 || b.ColorPlanes.Count == 0) return 0;
        var r = ar.Intersect(br);
        double sum = 0, weight = 0;
        int channels = Math.Min(a.ColorPlanes.Count, b.ColorPlanes.Count);
        for (int y = r.Top; y < r.Bottom; y++)
            for (int x = r.Left; x < r.Right; x++)
            {
                int ia = (y - ar.Top) * ar.Width + x - ar.Left, ib = (y - br.Top) * br.Width + x - br.Left;
                double wa = a.Alpha?.GetNormalized(ia) ?? 1, wb = b.Alpha?.GetNormalized(ib) ?? 1;
                double w = Math.Min(wa, wb);
                if (w <= 0) continue;
                double e = 0;
                for (int c = 0; c < channels; c++) e = Math.Max(e, Math.Abs(a.ColorPlanes[c].GetNormalized(ia) - b.ColorPlanes[c].GetNormalized(ib)));
                sum += e * 255 * w;
                weight += w;
            }
        return weight == 0 ? 0 : sum / weight;
    }
}
