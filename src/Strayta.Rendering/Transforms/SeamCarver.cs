using System.Numerics;
using Plane = Strayta.Core.Plane;
using Strayta.Core;

namespace Strayta.Rendering.Transforms;

/// <summary>
/// Content-aware scaling by seam carving, after Avidan and Shamir, "Seam Carving for Content-Aware Image Resizing"
/// (SIGGRAPH 2007). A seam is a connected path of one pixel per row (a vertical seam) through the pixels whose removal
/// costs least: the sum of an energy (here the luminance and alpha gradient magnitude, plus a large amount over protected
/// pixels) found by dynamic programming. Removing the cheapest seam again and again narrows the image while keeping what
/// stands out; widening duplicates the first seams that would have been removed (each averaged with its neighbor), as
/// the paper describes. Heights are done the same way on the transposed image.
/// <para>
/// Every pixel is labelled with the step at which its seam was removed (<see cref="RemovalOrder"/>), so any narrower or
/// wider version up to that many seams follows directly from one computation: that is what makes the preview
/// interactive.
/// </para>
/// </summary>
public static class SeamCarver
{
    /// <summary>Energy added over protected pixels (scaled by the protection, 0..1): far above any gradient.</summary>
    private const float ProtectEnergy = 1e4f;

    /// <summary>
    /// For an image of <paramref name="width"/> × <paramref name="height"/> premultiplied RGBA floats, the step (0-based)
    /// at which each pixel's vertical seam is removed, when up to <paramref name="seams"/> seams are removed one after
    /// another; pixels never removed get <see cref="int.MaxValue"/>. <paramref name="protect"/> (0..1 per pixel, or null)
    /// keeps protected pixels out of seams as long as possible.
    /// </summary>
    public static int[] RemovalOrder(float[] rgba, int width, int height, int seams, float[]? protect = null, CancellationToken cancel = default) =>
        RemovalOrder(rgba, width, height, seams, protect, batch: 1, cancel);

    /// <summary>
    /// <see cref="RemovalOrder(float[], int, int, int, float[], CancellationToken)"/> taking up to <paramref name="batch"/>
    /// seams from each cost map: the cheapest ends on the last row are traced up in turn, each avoiding the pixels the
    /// ones before it took (a seam that runs into them is dropped until the next round). Large images use this to keep
    /// Content-Aware Scale to seconds; seams from one batch are nearly the ones a seam at a time would take, since
    /// removing a cheap seam barely changes the costs elsewhere.
    /// </summary>
    public static int[] RemovalOrder(float[] rgba, int width, int height, int seams, float[]? protect, int batch, CancellationToken cancel = default)
    {
        seams = Math.Clamp(seams, 0, width - 1);
        var order = new int[width * height];
        Array.Fill(order, int.MaxValue);
        if (seams == 0) return order;

        // Per row, the original column of each remaining pixel, their luminance-with-alpha and energy.
        int w = width;
        var index = new int[width * height];
        var value = new float[width * height];
        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x, s = i * 4;
                index[i] = x;
                // Premultiplied luminance, plus alpha so edges of transparency count as structure.
                value[i] = 0.299f * rgba[s] + 0.587f * rgba[s + 1] + 0.114f * rgba[s + 2] + 0.5f * rgba[s + 3];
            }
        });
        var energy = new float[width * height];
        void AllEnergy(int ww) => Parallel.For(0, height, y =>
        {
            for (int x = 0; x < ww; x++) energy[y * width + x] = Energy(value, protect, index, width, height, ww, x, y);
        });
        AllEnergy(w);

        var cost = new float[width * height];
        var seam = new int[height];
        var taken = new bool[width * height];
        var ends = new int[width];
        int step = 0;
        while (step < seams)
        {
            cancel.ThrowIfCancellationRequested();
            Accumulate(energy, cost, width, height, w);
            int want = Math.Min(Math.Max(1, batch), seams - step);
            int last = (height - 1) * width;
            if (want == 1)
            {
                // The cheapest end on the last row, then back up through the cheapest of the three above.
                int bx = 0;
                float best = float.MaxValue;
                for (int x = 0; x < w; x++)
                    if (cost[last + x] < best)
                    {
                        best = cost[last + x];
                        bx = x;
                    }
                seam[height - 1] = bx;
                for (int y = height - 2; y >= 0; y--)
                {
                    int row = y * width, x = seam[y + 1], pick = x;
                    float c = cost[row + x];
                    if (x > 0 && cost[row + x - 1] < c) (c, pick) = (cost[row + x - 1], x - 1);
                    if (x < w - 1 && cost[row + x + 1] < c) pick = x + 1;
                    seam[y] = pick;
                }
                // Remove it: label, shift the rest of each row left, and refresh the energy beside the cut.
                int label = step;
                Parallel.For(0, height, y =>
                {
                    int row = y * width, x = seam[y];
                    order[row + index[row + x]] = label;
                    int n = w - x - 1;
                    if (n > 0)
                    {
                        Array.Copy(index, row + x + 1, index, row + x, n);
                        Array.Copy(value, row + x + 1, value, row + x, n);
                        Array.Copy(energy, row + x + 1, energy, row + x, n);
                    }
                });
                w--;
                step++;
                // Energies use the pixels above and below too: refresh around the cut on each row and its neighbors.
                int ww = w;
                Parallel.For(0, height, y =>
                {
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int yy = y + dy;
                        if (yy < 0 || yy >= height) continue;
                        int x = seam[yy];
                        for (int xx = Math.Max(0, x - 2); xx <= Math.Min(ww - 1, x + 1); xx++)
                            energy[y * width + xx] = Energy(value, protect, index, width, height, ww, xx, y);
                    }
                });
                continue;
            }

            // A batch: trace the cheapest ends in turn through pixels no earlier seam of the batch took.
            for (int x = 0; x < w; x++) ends[x] = x;
            Array.Sort(ends, 0, w, Comparer<int>.Create((a, b) => cost[last + a].CompareTo(cost[last + b])));
            int found = 0;
            var path = new int[height];
            for (int e = 0; e < w && found < want; e++)
            {
                int x = ends[e];
                if (taken[last + x]) continue;
                path[height - 1] = x;
                bool ok = true;
                for (int y = height - 2; y >= 0 && ok; y--)
                {
                    int row = y * width, px = path[y + 1], pick = -1;
                    float c = float.MaxValue;
                    for (int d = -1; d <= 1; d++)
                    {
                        int q = px + d;
                        if (q < 0 || q >= w || taken[row + q]) continue;
                        if (cost[row + q] < c) (c, pick) = (cost[row + q], q);
                    }
                    if (pick < 0) ok = false;
                    else path[y] = pick;
                }
                if (!ok) continue;
                int label = step + found;
                for (int y = 0; y < height; y++)
                {
                    int row = y * width;
                    taken[row + path[y]] = true;
                    order[row + index[row + path[y]]] = label;
                }
                found++;
            }
            if (found == 0) break;
            // Compact every row past the taken pixels, then work the energy out again.
            Parallel.For(0, height, y =>
            {
                int row = y * width, o = 0;
                for (int x = 0; x < w; x++)
                {
                    if (taken[row + x])
                    {
                        taken[row + x] = false;
                        continue;
                    }
                    index[row + o] = index[row + x];
                    value[row + o] = value[row + x];
                    o++;
                }
            });
            w -= found;
            step += found;
            AllEnergy(w);
        }
        return order;
    }

    /// <summary>The gradient energy of remaining pixel <paramref name="x"/> on row <paramref name="y"/>.</summary>
    private static float Energy(float[] value, float[]? protect, int[] index, int width, int height, int w, int x, int y)
    {
        int row = y * width;
        float l = value[row + Math.Max(0, x - 1)], r = value[row + Math.Min(w - 1, x + 1)];
        // Above and below: the same remaining position on those rows (their own compacted rows line up closely).
        float u = value[Math.Max(0, y - 1) * width + Math.Min(x, w - 1)], d = value[Math.Min(height - 1, y + 1) * width + Math.Min(x, w - 1)];
        float e = Math.Abs(r - l) + Math.Abs(d - u);
        if (protect is not null) e += ProtectEnergy * protect[row + index[row + x]];
        return e;
    }

    /// <summary>Cumulative minimum cost down the rows: each pixel's energy plus the cheapest of the three above (vectorized across the row).</summary>
    private static void Accumulate(float[] energy, float[] cost, int width, int height, int w)
    {
        Array.Copy(energy, 0, cost, 0, w);
        int lanes = Vector<float>.Count;
        for (int y = 1; y < height; y++)
        {
            int row = y * width, up = row - width;
            // Edges by hand; the middle a vector at a time.
            cost[row] = energy[row] + Math.Min(cost[up], w > 1 ? cost[up + 1] : float.MaxValue);
            int x = 1;
            if (Vector.IsHardwareAccelerated)
                for (; x + lanes < w; x += lanes)
                {
                    var a = new Vector<float>(cost, up + x - 1);
                    var b = new Vector<float>(cost, up + x);
                    var c = new Vector<float>(cost, up + x + 1);
                    (new Vector<float>(energy, row + x) + Vector.Min(a, Vector.Min(b, c))).CopyTo(cost, row + x);
                }
            for (; x < w; x++)
            {
                float m = Math.Min(cost[up + x - 1], cost[up + x]);
                if (x < w - 1) m = Math.Min(m, cost[up + x + 1]);
                cost[row + x] = energy[row + x] + m;
            }
        }
    }

    /// <summary>
    /// The image at <paramref name="newWidth"/> columns using a removal order for its width: narrower keeps the pixels
    /// removed last, wider duplicates the first seams (each copy averaged with the pixel to its right). Every row keeps
    /// the same count because every seam takes exactly one pixel per row.
    /// </summary>
    public static float[] ApplyWidth(float[] rgba, int width, int height, int[] order, int newWidth)
    {
        var o = new float[newWidth * height * 4];
        int remove = width - newWidth;
        Parallel.For(0, height, y =>
        {
            int row = y * width, ox = 0;
            for (int x = 0; x < width && ox < newWidth; x++)
            {
                int k = order[row + x];
                if (remove > 0 && k < remove) continue;
                int s = (row + x) * 4, d = (y * newWidth + ox) * 4;
                for (int c = 0; c < 4; c++) o[d + c] = rgba[s + c];
                ox++;
                if (remove < 0 && k < -remove && ox < newWidth)
                {
                    // An inserted copy between this pixel and the next.
                    int n = (row + Math.Min(width - 1, x + 1)) * 4;
                    d = (y * newWidth + ox) * 4;
                    for (int c = 0; c < 4; c++) o[d + c] = (rgba[s + c] + rgba[n + c]) / 2;
                    ox++;
                }
            }
            // A row short of pixels (only when widening past the seams computed) repeats its last pixel.
            for (; ox < newWidth; ox++)
            {
                int s = (row + width - 1) * 4, d = (y * newWidth + ox) * 4;
                for (int c = 0; c < 4; c++) o[d + c] = rgba[s + c];
            }
        });
        return o;
    }

    /// <summary>A premultiplied RGBA image turned a quarter (rows become columns), for doing heights as widths.</summary>
    public static float[] Transpose(float[] rgba, int width, int height)
    {
        var o = new float[rgba.Length];
        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                int s = (y * width + x) * 4, d = (x * height + y) * 4;
                o[d] = rgba[s]; o[d + 1] = rgba[s + 1]; o[d + 2] = rgba[s + 2]; o[d + 3] = rgba[s + 3];
            }
        });
        return o;
    }

    /// <summary>A single plane transposed (for protection masks).</summary>
    public static float[] TransposePlane(float[] plane, int width, int height)
    {
        var o = new float[plane.Length];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++) o[x * height + y] = plane[y * width + x];
        return o;
    }

    /// <summary>
    /// Retargets an image to <paramref name="newWidth"/> × <paramref name="newHeight"/>: width first, then height on the
    /// result (protection follows the width change). Widening or narrowing by more than half is done in rounds.
    /// </summary>
    public static float[] Retarget(float[] rgba, int width, int height, int newWidth, int newHeight, float[]? protect = null, CancellationToken cancel = default)
    {
        var image = rgba;
        int w = width, h = height;
        var guard = protect;
        while (w != newWidth)
        {
            int step = Math.Clamp(newWidth - w, -(w - 1), w / 2 + 1);
            if (step == 0) break;
            var order = RemovalOrder(image, w, h, Math.Abs(step), guard, BatchFor(w, h, Math.Abs(step)), cancel);
            image = ApplyWidth(image, w, h, order, w + step);
            if (guard is not null) guard = ApplyWidthPlane(guard, w, h, order, w + step);
            w += step;
        }
        if (h != newHeight)
        {
            var t = Transpose(image, w, h);
            var tg = guard is null ? null : TransposePlane(guard, w, h);
            int tw = h, th = w;
            while (tw != newHeight)
            {
                int step = Math.Clamp(newHeight - tw, -(tw - 1), tw / 2 + 1);
                if (step == 0) break;
                var order = RemovalOrder(t, tw, th, Math.Abs(step), tg, BatchFor(tw, th, Math.Abs(step)), cancel);
                t = ApplyWidth(t, tw, th, order, tw + step);
                if (tg is not null) tg = ApplyWidthPlane(tg, tw, th, order, tw + step);
                tw += step;
            }
            image = Transpose(t, tw, th);
        }
        return image;
    }

    /// <summary>Seams per cost map: one at a time for screen-sized images, batches for large ones (about 50 maps in all).</summary>
    private static int BatchFor(int width, int height, int seams) =>
        (long)width * height <= 1_500_000 ? 1 : Math.Clamp(seams / 50, 1, 32);

    /// <summary><see cref="ApplyWidth"/> for a single plane.</summary>
    public static float[] ApplyWidthPlane(float[] plane, int width, int height, int[] order, int newWidth)
    {
        var o = new float[newWidth * height];
        int remove = width - newWidth;
        for (int y = 0; y < height; y++)
        {
            int row = y * width, ox = 0;
            for (int x = 0; x < width && ox < newWidth; x++)
            {
                int k = order[row + x];
                if (remove > 0 && k < remove) continue;
                o[y * newWidth + ox++] = plane[row + x];
                if (remove < 0 && k < -remove && ox < newWidth) o[y * newWidth + ox++] = plane[row + x];
            }
            for (; ox < newWidth; ox++) o[y * newWidth + ox] = plane[row + width - 1];
        }
        return o;
    }

    // ---- Rasters -----------------------------------------------------------------------------------------

    /// <summary>A raster as premultiplied RGBA floats (gray repeated; no alpha is opaque).</summary>
    public static float[] ToRgba(Raster raster)
    {
        int n = raster.Width * raster.Height;
        var o = new float[n * 4];
        bool gray = raster.ColorPlanes.Count == 1;
        Parallel.For(0, raster.Height, y =>
        {
            for (int x = 0, i = y * raster.Width; x < raster.Width; x++, i++)
            {
                float a = raster.Alpha?.GetNormalized(i) ?? 1f;
                float r = raster.ColorPlanes[0].GetNormalized(i);
                o[i * 4] = r * a;
                o[i * 4 + 1] = (gray ? r : raster.ColorPlanes[1].GetNormalized(i)) * a;
                o[i * 4 + 2] = (gray ? r : raster.ColorPlanes[2].GetNormalized(i)) * a;
                o[i * 4 + 3] = a;
            }
        });
        return o;
    }

    /// <summary>Premultiplied RGBA floats back to a raster in <paramref name="mode"/> and <paramref name="bitDepth"/> (with alpha).</summary>
    public static Raster FromRgba(float[] rgba, int width, int height, ColorMode mode, int bitDepth)
    {
        int colors = mode == ColorMode.Grayscale ? 1 : 3;
        var planes = Enumerable.Range(0, colors + 1).Select(_ => Plane.Create(width, height, bitDepth)).ToArray();
        Parallel.For(0, height, y =>
        {
            for (int x = 0, i = y * width; x < width; x++, i++)
            {
                float a = rgba[i * 4 + 3];
                float inv = a > 1e-9f ? 1f / a : 0f;
                for (int c = 0; c < colors; c++) Resampler.Store(planes[c], i, rgba[i * 4 + c] * inv);
                Resampler.Store(planes[colors], i, a);
            }
        });
        return new Raster(mode, planes[..colors], planes[colors]);
    }
}
