using System.Numerics;
using System.Runtime.InteropServices;

namespace Strayta.Core.Selection;

/// <summary>Magic Wand settings, as in Photoshop's options bar.</summary>
/// <param name="Tolerance">0..255: a pixel matches when every channel is within this many levels of the clicked pixel.</param>
/// <param name="AntiAlias">Soften the stair-stepped edge of the selection.</param>
/// <param name="Contiguous">Only pixels connected to the clicked one; otherwise every matching pixel in the image.</param>
public readonly record struct MagicWandOptions(int Tolerance = 32, bool AntiAlias = true, bool Contiguous = true);

/// <summary>
/// Photoshop's Magic Wand: selects the pixels whose color is within a tolerance of the clicked pixel's.
/// </summary>
/// <remarks>
/// Colors are compared channel by channel on premultiplied RGBA (see <see cref="SampleImage"/>), so transparency
/// counts as a difference and all empty pixels match each other. Every pixel's match is computed first, in parallel;
/// the contiguous fill then works on that byte map (4-connected, a whole run at a time, the map doubling as the
/// visited set), so it only compares bytes and finds run ends with vectorized searches.
/// <para>Anti-aliasing blurs the hard result with a 3×3 tent filter: pixels on either side of the boundary become
/// partly selected, which removes the stair steps without moving the 50% outline or changing the selected area. The
/// alternative, deriving sub-pixel coverage from how far a pixel falls outside the tolerance, depends on the colors
/// beyond the edge and on the tolerance itself, so the same click would give edges of varying softness.</para>
/// </remarks>
public static class MagicWand
{
    public static SelectionMask? Select(SampleImage image, int x, int y, MagicWandOptions options)
    {
        int w = image.Width, h = image.Height;
        if (x < 0 || y < 0 || x >= w || y >= h) return null;
        var pixels = MemoryMarshal.Cast<byte, uint>(image.Rgba.AsSpan());
        uint seed = pixels[y * w + x];
        int tolerance = Math.Clamp(options.Tolerance, 0, 255);

        // Every pixel's match first (in parallel, cheap), then the fill only compares bytes: 1 = matches, 255 = filled.
        var mask = new byte[w * h];
        MatchAll(image, mask, seed, tolerance);
        if (options.Contiguous)
        {
            Flood(mask, w, h, x, y);
            mask.AsSpan().Replace((byte)1, (byte)0); // matches the fill did not reach
        }
        else mask.AsSpan().Replace((byte)1, (byte)255);
        if (options.AntiAlias) mask = SmoothEdges(mask, w, h);
        return SelectionMask.FromCoverage(image.Bounds, mask);
    }

    private static bool Matches(uint p, uint seed, int tolerance) =>
        Math.Abs((int)(p & 0xFF) - (int)(seed & 0xFF)) <= tolerance &&
        Math.Abs((int)((p >> 8) & 0xFF) - (int)((seed >> 8) & 0xFF)) <= tolerance &&
        Math.Abs((int)((p >> 16) & 0xFF) - (int)((seed >> 16) & 0xFF)) <= tolerance &&
        Math.Abs((int)(p >> 24) - (int)(seed >> 24)) <= tolerance;

    private static void MatchAll(SampleImage image, byte[] mask, uint seed, int tolerance)
    {
        int w = image.Width;
        var rgba = image.Rgba;
        Parallel.For(0, image.Height, y =>
        {
            var pixels = MemoryMarshal.Cast<byte, uint>(rgba.AsSpan(y * w * 4, w * 4));
            var dst = mask.AsSpan(y * w, w);
            for (int x = 0; x < w; x++)
                dst[x] = Matches(pixels[x], seed, tolerance) ? (byte)1 : (byte)0;
        });
    }

    /// <summary>
    /// Scanline flood fill over the match map: extends the seed to its whole run of matches, fills it, then queues
    /// one seed per run of unfilled matches in the rows above and below. Runs are found with vectorized searches.
    /// </summary>
    private static void Flood(byte[] mask, int w, int h, int x0, int y0)
    {
        var stack = new Stack<int>();
        stack.Push(y0 * w + x0);
        while (stack.Count > 0)
        {
            int at = stack.Pop();
            if (mask[at] != 1) continue;
            int y = at / w, row = y * w, x = at - row;
            var line = mask.AsSpan(row, w);
            int left = line[..x].LastIndexOfAnyExcept((byte)1) + 1;
            int end = line[x..].IndexOfAnyExcept((byte)1);
            int right = end < 0 ? w : x + end; // exclusive
            line[left..right].Fill(255);
            if (y > 0) QueueRuns(mask.AsSpan(row - w + left, right - left), row - w + left, stack);
            if (y < h - 1) QueueRuns(mask.AsSpan(row + w + left, right - left), row + w + left, stack);
        }
    }

    private static void QueueRuns(Span<byte> span, int offset, Stack<int> stack)
    {
        int x = 0;
        while (x < span.Length)
        {
            int start = span[x..].IndexOf((byte)1);
            if (start < 0) return;
            x += start;
            stack.Push(offset + x);
            int run = span[x..].IndexOfAnyExcept((byte)1);
            if (run < 0) return;
            x += run;
        }
    }

    /// <summary>
    /// Anti-aliasing for a hard mask: a 3×3 tent blur (1-2-1 in each direction), evaluated only where the
    /// neighborhood is mixed. Beyond the image the edge pixels repeat, so selections touching the border stay solid.
    /// </summary>
    internal static byte[] SmoothEdges(byte[] mask, int w, int h)
    {
        var result = (byte[])mask.Clone();
        int vc = Vector<byte>.Count;
        Parallel.For(0, h, y =>
        {
            var up = mask.AsSpan(Math.Max(y - 1, 0) * w, w);
            var mid = mask.AsSpan(y * w, w);
            var down = mask.AsSpan(Math.Min(y + 1, h - 1) * w, w);
            var dst = result.AsSpan(y * w, w);
            int x = 0;
            while (x < w)
            {
                // Skip whole vectors whose 3×3 neighborhoods are uniform: columns x-1 .. x+vc all equal in all three rows.
                if (x >= 1 && x + vc + 1 <= w)
                {
                    var m = new Vector<byte>(mid[x..]);
                    if (m == new Vector<byte>(mid[(x - 1)..]) && m == new Vector<byte>(mid[(x + 1)..]) &&
                        m == new Vector<byte>(up[(x - 1)..]) && m == new Vector<byte>(up[(x + 1)..]) &&
                        m == new Vector<byte>(down[(x - 1)..]) && m == new Vector<byte>(down[(x + 1)..]))
                    {
                        x += vc;
                        continue;
                    }
                }
                int end = Math.Min(x + vc, w);
                for (; x < end; x++)
                {
                    int xl = Math.Max(x - 1, 0), xr = Math.Min(x + 1, w - 1);
                    int c = mid[x];
                    if (up[x] == c && down[x] == c && mid[xl] == c && mid[xr] == c &&
                        up[xl] == c && up[xr] == c && down[xl] == c && down[xr] == c) continue;
                    int sum = up[xl] + 2 * up[x] + up[xr] + 2 * mid[xl] + 4 * c + 2 * mid[xr] + down[xl] + 2 * down[x] + down[xr];
                    dst[x] = (byte)((sum + 8) >> 4);
                }
            }
        });
        return result;
    }
}
