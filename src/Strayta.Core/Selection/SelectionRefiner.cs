using System.Numerics;

namespace Strayta.Core.Selection;

/// <summary>Select and Mask settings, in Photoshop's units.</summary>
/// <param name="Radius">Edge detection: pixels on either side of the selection edge whose coverage is re-decided from the image's colors (Photoshop offers 0–250; up to 500 is accepted).</param>
/// <param name="Smooth">Rounds off jagged outlines (0–100).</param>
/// <param name="Feather">Softens the edge: Gaussian σ in pixels (Photoshop offers 0–250; up to 1000 is accepted).</param>
/// <param name="Contrast">Hardens soft edges, in percent (0–100).</param>
/// <param name="ShiftEdge">Moves the edge out (positive) or in (negative), in percent (−100–100; ±100% is ±<see cref="SelectionRefiner.MaxShift"/> pixels).</param>
/// <param name="SmartRadius">Adapts the radius along the edge: narrow where the image's edge is hard, the full radius where it is soft (hair, fur, blur).</param>
public readonly record struct RefineSettings(float Radius = 0, float Smooth = 0, float Feather = 0, float Contrast = 0, float ShiftEdge = 0, bool SmartRadius = false)
{
    /// <summary>The same settings for an image scaled by <paramref name="scale"/> (e.g. ¼ for a quarter-size preview).</summary>
    public RefineSettings Scaled(float scale) => this with { Radius = Radius * scale, Smooth = Smooth * scale, Feather = Feather * scale, ShiftEdge = ShiftEdge * scale };
}

/// <summary>
/// Where the Refine Edge brush has painted: pixels whose coverage Select and Mask re-decides from the image's colors
/// (<see cref="Refine"/>), or where Option-painting erased the edge detection (<see cref="Erased"/>). Painted on
/// the UI thread; <see cref="Snapshot"/> hands a copy to background work.
/// </summary>
public sealed class RefineBrushMask(int width, int height)
{
    public const byte Refine = 255;
    public const byte Erased = 1;

    private byte[]? _mask;

    public int Width { get; } = width;
    public int Height { get; } = height;

    /// <summary>Incremented by every change, so results computed from an older snapshot can be recognized.</summary>
    public int Version { get; private set; }

    /// <summary>The largest brush radius used; edge detection looks this far for known colors.</summary>
    public float MaxRadius { get; private set; }

    public bool IsEmpty => _mask is null;

    /// <summary>
    /// Paints a stroke segment (a capsule of <paramref name="diameter"/>) from <paramref name="from"/> to
    /// <paramref name="to"/> in image coordinates. Returns the pixels it touched.
    /// </summary>
    public PixelRect Paint(Vector2 from, Vector2 to, float diameter, bool erase)
    {
        float r = Math.Max(0.5f, diameter / 2);
        var box = new PixelRect((int)MathF.Floor(MathF.Min(from.X, to.X) - r), (int)MathF.Floor(MathF.Min(from.Y, to.Y) - r),
                (int)MathF.Ceiling(MathF.Max(from.X, to.X) + r) + 1, (int)MathF.Ceiling(MathF.Max(from.Y, to.Y) + r) + 1)
            .Intersect(PixelRect.FromSize(Width, Height));
        if (box.IsEmpty) return box;
        _mask ??= new byte[(long)Width * Height];
        if (!erase) MaxRadius = Math.Max(MaxRadius, r);
        var d = to - from;
        float len2 = d.LengthSquared();
        byte value = erase ? Erased : Refine;
        for (int y = box.Top; y < box.Bottom; y++)
        {
            int row = y * Width;
            for (int x = box.Left; x < box.Right; x++)
            {
                var p = new Vector2(x + 0.5f, y + 0.5f) - from;
                float t = len2 > 0 ? Math.Clamp(Vector2.Dot(p, d) / len2, 0f, 1f) : 0f;
                if ((p - d * t).LengthSquared() <= r * r) _mask[row + x] = value;
            }
        }
        Version++;
        return box;
    }

    public void Clear()
    {
        _mask = null;
        MaxRadius = 0;
        Version++;
    }

    private RefineBrushSnapshot? _snapshot;

    /// <summary>A copy for background work (the same one until the next change), or null when nothing has been painted.</summary>
    public RefineBrushSnapshot? Snapshot()
    {
        if (_mask is null) return null;
        if (_snapshot?.Version != Version) _snapshot = new RefineBrushSnapshot((byte[])_mask.Clone(), Version, MaxRadius);
        return _snapshot;
    }
}

/// <summary>An immutable copy of <see cref="RefineBrushMask"/>'s pixels.</summary>
public sealed record RefineBrushSnapshot(byte[] Mask, int Version, float MaxRadius);

/// <summary>
/// Select and Mask: refines a selection over an image with edge detection (and the Refine Edge brush) followed by the
/// global refinements Smooth, Feather, Contrast and Shift Edge. Build one per session; <see cref="Refine"/> may be
/// called from any thread, one call at a time, and caches the edge detection so moving the other sliders only
/// reruns the cheap global steps.
/// </summary>
/// <remarks>
/// <para><b>Edge detection.</b> Pixels within <see cref="RefineSettings.Radius"/> of the selection's 50% outline, plus
/// those painted with the Refine Edge brush, form a band whose coverage is estimated from color. Outside the band a
/// pixel is known foreground (selected) or background. For each band pixel, the mean colors of the known foreground
/// and background pixels in a window around it (reaching past the band) give two local colors F and B; the pixel's
/// color C is placed on the segment between them, α = (C − B)·(F − B) / |F − B|², clamped to 0..1. That is the
/// alpha a mix of the two colors would need, which captures hair, fur and blurred edges against a locally uniform
/// background. Where F and B are too similar to tell apart, or one side has no known pixels nearby, the original
/// coverage stays; in between the two are blended by how distinct the colors are.</para>
/// <para>The window means come from block sums (blocks a quarter of the window wide) box-filtered over the window
/// and interpolated bilinearly, so the cost is independent of the radius. A side with no known pixels in the window
/// is looked for in two and then four times the window, so brush strokes along hair that stands far out from the
/// subject still find its color.</para>
/// <para><b>Global refinements</b>, in Photoshop's order: Smooth (a majority vote like Select › Modify › Smooth with a
/// radius of Smooth/10 pixels), Feather (Gaussian), Contrast (steepens the ramp around 50%), Shift Edge (grayscale
/// dilation or erosion, blended between whole pixels so the slider moves the edge continuously).</para>
/// </remarks>
public sealed class SelectionRefiner
{
    /// <summary>Pixels Shift Edge moves the edge at ±100%.</summary>
    public const float MaxShift = 10f;

    // Colors closer than this (0..255 per channel, Euclidean over premultiplied RGBA) are not trusted to separate
    // foreground from background; the estimate takes over fully at MinContrast + ContrastRamp.
    private const float MinContrast = 8f;
    private const float ContrastRamp = 24f;

    private readonly SampleImage _image;
    private readonly byte[] _base;
    private readonly byte[] _binary;
    private readonly object _cacheLock = new();
    private (int Radius, bool Smart, int BrushVersion, byte[] Matte)? _edgeCache;

    /// <param name="image">The pixels edge detection looks at, covering the canvas.</param>
    /// <param name="selection">The selection to refine (null refines an empty selection, which the brush can still add to).</param>
    public SelectionRefiner(SampleImage image, SelectionMask? selection)
        : this(image, MaskFilters.Extract(selection, image.Bounds))
    {
    }

    /// <param name="image">The pixels edge detection looks at, covering the canvas.</param>
    /// <param name="coverage">The selection to refine as coverage over the whole canvas (row-major, 255 = selected); kept, not copied.</param>
    public SelectionRefiner(SampleImage image, byte[] coverage)
    {
        if (coverage.LongLength != (long)image.Width * image.Height) throw new ArgumentException("Expected one byte per pixel.", nameof(coverage));
        _image = image;
        _base = coverage;
        _binary = new byte[_base.Length];
        Parallel.For(0, image.Height, y =>
        {
            for (int i = y * image.Width, end = i + image.Width; i < end; i++) _binary[i] = _base[i] >= 128 ? (byte)255 : (byte)0;
        });
    }

    public int Width => _image.Width;
    public int Height => _image.Height;

    /// <summary>The refined coverage over the whole canvas (row-major, 255 = selected).</summary>
    public byte[] Refine(RefineSettings settings, RefineBrushSnapshot? brush, CancellationToken cancel = default)
    {
        var matte = EdgeMatte((int)MathF.Round(Math.Clamp(settings.Radius, 0, SelectionModify.MaxRadius)), settings.SmartRadius, brush, cancel);
        return Global(matte, settings, cancel);
    }

    /// <summary>The coverage being refined (the selection as given), row-major over the canvas.</summary>
    public byte[] BaseCoverage => _base;

    /// <summary>The refined selection, trimmed (null when nothing is selected).</summary>
    public SelectionMask? RefineSelection(RefineSettings settings, RefineBrushSnapshot? brush, CancellationToken cancel = default) =>
        SelectionMask.FromCoverage(_image.Bounds, Refine(settings, brush, cancel));

    // ---- Edge detection -----------------------------------------------------------------------------

    private byte[] EdgeMatte(int radius, bool smart, RefineBrushSnapshot? brush, CancellationToken cancel)
    {
        if (radius == 0 && brush is null) return _base;
        int version = brush?.Version ?? -1;
        lock (_cacheLock)
            if (_edgeCache is { } c && c.Radius == radius && c.Smart == smart && c.BrushVersion == version) return c.Matte;

        int w = Width, h = Height;
        var band = new byte[_base.Length]; // 1 = coverage is re-decided here
        if (radius > 0 && smart)
        {
            SmartBand(band, radius, cancel);
        }
        else if (radius > 0)
        {
            var outer = MaskFilters.Dilate(_binary, w, h, radius, GridEdges.All(0));
            cancel.ThrowIfCancellationRequested();
            var inner = MaskFilters.Erode(_binary, w, h, radius, GridEdges.All(255));
            cancel.ThrowIfCancellationRequested();
            Parallel.For(0, h, y =>
            {
                for (int i = y * w, end = i + w; i < end; i++) band[i] = outer[i] != inner[i] ? (byte)1 : (byte)0;
            });
        }
        if (brush is not null)
        {
            var painted = brush.Mask;
            Parallel.For(0, h, y =>
            {
                for (int i = y * w, end = i + w; i < end; i++)
                    if (painted[i] == RefineBrushMask.Refine) band[i] = 1;
                    else if (painted[i] == RefineBrushMask.Erased) band[i] = 0;
            });
        }

        // Known pixels are found within the window: past the radius band on both sides, and past the middle of the
        // widest brush stroke.
        int window = Math.Max(2 * radius + 2, (int)MathF.Ceiling(brush?.MaxRadius ?? 0) + 4);
        var matte = Estimate(band, window, cancel);
        lock (_cacheLock) _edgeCache = (radius, smart, version, matte);
        return matte;
    }

    /// <summary>
    /// Smart Radius: the band's half-width follows the image's edge. At each pixel of the selection's outline the
    /// edge's hardness is measured as the luminance step between neighbors against the contrast across a window
    /// around it: a crisp edge changes almost all of its contrast in one pixel (hardness 1), hair or a blurred edge
    /// spreads it over many (hardness near 0). The half-width there is the radius scaled from a quarter (hard) to the
    /// whole (soft), and every pixel takes the half-width of its nearest outline pixel.
    /// </summary>
    private void SmartBand(byte[] band, int radius, CancellationToken cancel)
    {
        int w = Width, h = Height;
        var outline = new byte[_base.Length];
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                byte v = _binary[i];
                if ((x > 0 && _binary[i - 1] != v) || (x < w - 1 && _binary[i + 1] != v) ||
                    (y > 0 && _binary[i - w] != v) || (y < h - 1 && _binary[i + w] != v))
                    outline[i] = 1;
            }
        });
        cancel.ThrowIfCancellationRequested();

        var (luma, step) = EdgeStrength();
        cancel.ThrowIfCancellationRequested();

        // Half-width per outline pixel. The drawn outline may be off the image's edge (a rough lasso), so the edge is
        // looked for in a window reaching half the radius: its hardness is the largest one-pixel step there over the
        // contrast across the window.
        var halfWidth = new float[_base.Length];
        int reach = Math.Clamp(radius / 2, 2, 12);
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                if (outline[y * w + x] == 0) continue;
                int lo = 255, hi = 0, steepest = 0;
                for (int yy = Math.Max(0, y - reach), y1 = Math.Min(h - 1, y + reach); yy <= y1; yy++)
                    for (int i = yy * w + Math.Max(0, x - reach), end = yy * w + Math.Min(w - 1, x + reach); i <= end; i++)
                    {
                        int l = luma[i];
                        if (l < lo) lo = l;
                        if (l > hi) hi = l;
                        if (step[i] > steepest) steepest = step[i];
                    }
                // A hard step's central difference is half the contrast; a ramp over n pixels gives 1/n of it.
                float hardness = Math.Clamp(2f * steepest / Math.Max(hi - lo, 16), 0f, 1f);
                halfWidth[y * w + x] = MathF.Max(1f, radius * (1f - 0.75f * hardness));
            }
        });
        cancel.ThrowIfCancellationRequested();

        var (nearest, dist2) = DistanceTransform.Compute(outline, w, h);
        cancel.ThrowIfCancellationRequested();
        Parallel.For(0, h, y =>
        {
            for (int i = y * w, end = i + w; i < end; i++)
            {
                int site = nearest[i];
                if (site < 0) continue;
                float r = halfWidth[site];
                if (dist2[i] <= r * r) band[i] = 1;
            }
        });
    }

    private (byte[] Luma, byte[] Step)? _edgeStrength;

    /// <summary>The image's luminance and the size of its one-pixel steps (half the central difference), computed once.</summary>
    private (byte[] Luma, byte[] Step) EdgeStrength()
    {
        lock (_cacheLock)
            if (_edgeStrength is { } cached) return cached;
        int w = Width, h = Height;
        var rgba = _image.Rgba;
        var luma = new byte[_base.Length];
        Parallel.For(0, h, y =>
        {
            for (int i = y * w, end = i + w; i < end; i++)
            {
                int p = i * 4;
                luma[i] = (byte)((77 * rgba[p] + 150 * rgba[p + 1] + 29 * rgba[p + 2] + 128) >> 8);
            }
        });
        var step = new byte[_base.Length];
        Parallel.For(0, h, y =>
        {
            int up = Math.Max(y - 1, 0) * w, down = Math.Min(y + 1, h - 1) * w, row = y * w;
            for (int x = 0; x < w; x++)
            {
                int gx = luma[row + Math.Min(x + 1, w - 1)] - luma[row + Math.Max(x - 1, 0)];
                int gy = luma[down + x] - luma[up + x];
                step[row + x] = (byte)Math.Min(255, (int)(MathF.Sqrt(gx * gx + gy * gy) / 2f + 0.5f));
            }
        });
        lock (_cacheLock) _edgeStrength = (luma, step);
        return (luma, step);
    }

    // Per block: foreground R, G, B, A, count, then background R, G, B, A, count.
    private const int Channels = 10;

    private byte[] Estimate(byte[] band, int window, CancellationToken cancel)
    {
        int w = Width, h = Height;
        int q = Math.Max(4, window / 4);
        int bw = (w + q - 1) / q, bh = (h + q - 1) / q;
        var rgba = _image.Rgba;

        // 1. Sums of the known pixels' colors per block.
        var blocks = new float[bw * bh * Channels];
        Parallel.For(0, bh, by =>
        {
            int y0 = by * q, y1 = Math.Min(y0 + q, h);
            for (int y = y0; y < y1; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (band[i] != 0) continue;
                    int o = (by * bw + x / q) * Channels + (_binary[i] != 0 ? 0 : 5);
                    int p = i * 4;
                    blocks[o] += rgba[p];
                    blocks[o + 1] += rgba[p + 1];
                    blocks[o + 2] += rgba[p + 2];
                    blocks[o + 3] += rgba[p + 3];
                    blocks[o + 4] += 1;
                }
        });
        cancel.ThrowIfCancellationRequested();

        // 2. Box sums over the window (in blocks), and over two and four times the window for pixels with no known
        //    foreground or background close by (e.g. brush strokes along hair far from the subject's body).
        int rb = Math.Max(1, (window + q - 1) / q);
        var scales = new[] { BoxSum(blocks, bw, bh, rb), BoxSum(blocks, bw, bh, rb * 2), BoxSum(blocks, bw, bh, rb * 4) };
        cancel.ThrowIfCancellationRequested();

        // 3. Each band pixel between its local foreground and background colors.
        var matte = (byte[])_base.Clone();
        Parallel.For(0, h, y =>
        {
            Span<float> local = stackalloc float[Channels];
            Span<float> f = stackalloc float[4], b = stackalloc float[4];
            float fy = (y + 0.5f) / q - 0.5f;
            int by0 = Math.Clamp((int)MathF.Floor(fy), 0, bh - 1), by1 = Math.Min(by0 + 1, bh - 1);
            float ty = Math.Clamp(fy - by0, 0f, 1f);
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (band[i] == 0) continue;
                float fx = (x + 0.5f) / q - 0.5f;
                int bx0 = Math.Clamp((int)MathF.Floor(fx), 0, bw - 1), bx1 = Math.Min(bx0 + 1, bw - 1);
                float tx = Math.Clamp(fx - bx0, 0f, 1f);
                bool haveF = false, haveB = false;
                foreach (var sums in scales)
                {
                    local.Clear();
                    Add(local, sums.AsSpan((by0 * bw + bx0) * Channels, Channels), (1 - tx) * (1 - ty));
                    Add(local, sums.AsSpan((by0 * bw + bx1) * Channels, Channels), tx * (1 - ty));
                    Add(local, sums.AsSpan((by1 * bw + bx0) * Channels, Channels), (1 - tx) * ty);
                    Add(local, sums.AsSpan((by1 * bw + bx1) * Channels, Channels), tx * ty);
                    if (!haveF && local[4] >= 0.5f)
                    {
                        for (int c = 0; c < 4; c++) f[c] = local[c] / local[4];
                        haveF = true;
                    }
                    if (!haveB && local[9] >= 0.5f)
                    {
                        for (int c = 0; c < 4; c++) b[c] = local[5 + c] / local[9];
                        haveB = true;
                    }
                    if (haveF && haveB) break;
                }
                if (!haveF || !haveB) continue; // one side unknown here: keep the coverage

                int p = i * 4;
                float dot = 0, dd = 0;
                for (int c = 0; c < 4; c++)
                {
                    float d = f[c] - b[c];
                    dot += (rgba[p + c] - b[c]) * d;
                    dd += d * d;
                }
                float distinct = (MathF.Sqrt(dd) - MinContrast) / ContrastRamp;
                if (distinct <= 0) continue;
                float alpha = Math.Clamp(dot / dd, 0f, 1f) * 255f;
                float trust = Math.Min(1f, distinct);
                matte[i] = MaskFilters.ClampByte(_base[i] + (alpha - _base[i]) * trust);
            }
        });
        return matte;
    }

    /// <summary>Box sums of the block grid over (2r+1)² blocks: rows, then columns, as running sums.</summary>
    private static float[] BoxSum(float[] blocks, int bw, int bh, int rb)
    {
        var rows = new float[blocks.Length];
        Parallel.For(0, bh, by =>
        {
            Span<float> sum = stackalloc float[Channels];
            int row = by * bw;
            for (int bx = -rb; bx <= rb; bx++)
                if (bx >= 0 && bx < bw) Add(sum, blocks.AsSpan((row + bx) * Channels, Channels), 1);
            for (int bx = 0; bx < bw; bx++)
            {
                sum.CopyTo(rows.AsSpan((row + bx) * Channels, Channels));
                if (bx + rb + 1 < bw) Add(sum, blocks.AsSpan((row + bx + rb + 1) * Channels, Channels), 1);
                if (bx - rb >= 0) Add(sum, blocks.AsSpan((row + bx - rb) * Channels, Channels), -1);
            }
        });
        var sums = new float[blocks.Length];
        Parallel.For(0, bw, bx =>
        {
            Span<float> sum = stackalloc float[Channels];
            for (int by = -rb; by <= rb; by++)
                if (by >= 0 && by < bh) Add(sum, rows.AsSpan((by * bw + bx) * Channels, Channels), 1);
            for (int by = 0; by < bh; by++)
            {
                sum.CopyTo(sums.AsSpan((by * bw + bx) * Channels, Channels));
                if (by + rb + 1 < bh) Add(sum, rows.AsSpan(((by + rb + 1) * bw + bx) * Channels, Channels), 1);
                if (by - rb >= 0) Add(sum, rows.AsSpan(((by - rb) * bw + bx) * Channels, Channels), -1);
            }
        });
        return sums;
    }

    private static void Add(Span<float> sum, ReadOnlySpan<float> values, float weight)
    {
        for (int c = 0; c < Channels; c++) sum[c] += values[c] * weight;
    }

    // ---- Global refinements -------------------------------------------------------------------------

    private byte[] Global(byte[] matte, RefineSettings s, CancellationToken cancel)
    {
        int w = Width, h = Height;
        var repeat = GridEdges.All(0) with { Repeat = true };
        byte[] result = matte;

        if (s.Smooth > 0)
        {
            float sigma = SelectionModify.BoxSigma(Math.Min(s.Smooth, 100) / 10f);
            result = SelectionModify.Threshold(MaskFilters.Blur(MaskFilters.ToFloat(result), w, h, sigma, repeat), sigma);
            cancel.ThrowIfCancellationRequested();
        }

        float contrast = Math.Clamp(s.Contrast, 0, 100) / 100f;
        if (s.Feather >= 0.1f || contrast > 0)
        {
            var values = MaskFilters.ToFloat(result);
            if (s.Feather >= 0.1f) values = MaskFilters.Blur(values, w, h, Math.Min(s.Feather, SelectionModify.MaxFeather), repeat);
            cancel.ThrowIfCancellationRequested();
            if (contrast > 0)
            {
                // At 100% the ramp around 50% is a hundred times steeper: practically a hard edge.
                float k = 1f / (1f - 0.99f * contrast);
                var v = values;
                Parallel.For(0, h, y =>
                {
                    for (int i = y * w, end = i + w; i < end; i++) v[i] = (v[i] - 127.5f) * k + 127.5f;
                });
            }
            result = MaskFilters.ToBytes(values);
        }

        float shift = Math.Clamp(s.ShiftEdge, -100, 100) / 100f * MaxShift;
        if (MathF.Abs(shift) >= 0.05f)
        {
            int whole = (int)MathF.Floor(MathF.Abs(shift));
            float fraction = MathF.Abs(shift) - whole;
            byte[] Move(int by) => by == 0 ? result : shift > 0
                ? MaskFilters.Dilate(result, w, h, by, GridEdges.All(0))
                : MaskFilters.Erode(result, w, h, by, GridEdges.All(255));
            var near = Move(whole);
            cancel.ThrowIfCancellationRequested();
            if (fraction >= 0.05f)
            {
                var far = Move(whole + 1);
                var blended = new byte[near.Length];
                Parallel.For(0, h, y =>
                {
                    for (int i = y * w, end = i + w; i < end; i++) blended[i] = MaskFilters.ClampByte(near[i] + (far[i] - near[i]) * fraction);
                });
                near = blended;
            }
            result = near;
        }
        return ReferenceEquals(result, _base) ? (byte[])result.Clone() : result;
    }
}
