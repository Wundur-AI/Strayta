using System.Collections.Concurrent;

namespace Strayta.Core.Painting;

/// <summary>The focus tools (Blur, Sharpen, Smudge; a tool-strip slot without a shortcut in Photoshop).</summary>
public enum LocalTool
{
    Blur,
    Sharpen,
    Smudge,
}

/// <summary>Settings of a Blur, Sharpen or Smudge stroke.</summary>
public sealed record LocalToolSettings(LocalTool Tool)
{
    /// <summary>0..1: how much each dab changes the pixels (Smudge: how long the picked-up paint lasts).</summary>
    public float Strength { get; init; } = 0.5f;

    /// <summary>
    /// How the tool's result combines with the pixel: Normal, Darken, Lighten, Hue, Saturation, Color, Luminosity
    /// (Darken lets a blur only darken, and so on). Other modes behave as their blend does.
    /// </summary>
    public PaintMode Mode { get; init; }

    /// <summary>Sharpen: a gentler sharpen that never overshoots its neighbours (no halos, no amplified noise).</summary>
    public bool ProtectDetail { get; init; } = true;

    /// <summary>Smudge: start each stroke with <see cref="FingerColor"/> instead of the color under the brush.</summary>
    public bool FingerPainting { get; init; }

    public RgbColor FingerColor { get; init; }
}

/// <summary>
/// The working image of a Blur, Sharpen or Smudge stroke: a copy of the target's pixels (or mask), changed dab by dab.
/// Each dab reads what the stroke has made so far, so going over an area again blurs it more and smudged paint travels
/// with the brush. Renderers show the working pixels wherever the stroke has been, and the baker commits exactly them.
/// </summary>
/// <remarks>
/// <para>Pixels are kept premultiplied (color × alpha, then alpha) in 64×64 tiles, created from the target the first
/// time a dab reaches them. A dab with weight w (tip × selection × pen pressure) at a pixel moves it toward the tool's
/// result F by k: <c>P′ = P + (F − P)·k</c>, in premultiplied values, so transparent areas blur without dark fringes.</para>
/// <para><b>Blur</b>: F is a 5×5 binomial blur B5 ([1 4 6 4 1]/16 each way); k = w × Strength. <b>Sharpen</b>: F adds
/// a band-pass detail, <c>C + a·(B3 − B5)</c> with B3 the 3×3 binomial blur ([1 2 1]/4) and a = 3. Unlike a plain unsharp
/// mask (C − B) the band-pass has no response at the pixel frequency, so repeated dabs steepen edges instead of
/// turning single-pixel noise into a growing checkerboard. Protect Detail uses a = 2 and limits F to the 3×3
/// neighbourhood's range, so a dab never overshoots its neighbours (no halos); k = w × Strength. <b>Smudge</b> carries a patch of paint the size of the brush: the first dab picks up the pixels under it
/// (or the foreground color with Finger Painting); each dab lays the patch down with k = w × min(1, 0.25 + Strength)
/// and then picks up what is under it again, keeping Strength of the old patch (<c>patch′ = patch·s + P′·(1 − s)</c>),
/// so at 100% the first color is dragged along indefinitely and at low strength it soon fades.</para>
/// <para>Sample All Layers: the tools read a flattened copy of the image (taken when the stroke starts) instead of the
/// layer and lay their result over the layer as paint (<c>L′ = L + (F − L)·k</c> with F's alpha, so an empty layer
/// receives the blurred image). The flattened copy is updated the same way, so later dabs see the stroke's own work;
/// this is exact for a normal, fully opaque layer at the top and a close model otherwise.</para>
/// <para>Near the document's edge the kernels repeat the edge pixels rather than reading transparency, so a blur never
/// fades the image edge.</para>
/// </remarks>
public sealed class LocalStroke
{
    private readonly WorkImage _layer;
    private WorkImage? _sample;
    private readonly bool _separate;
    private readonly PixelRect _limit;
    private readonly List<(PixelRect Rect, float[] Weights, float Cx, float Cy)> _pending = [];

    // Smudge: the patch of paint the brush carries, premultiplied, centered on the last dab.
    private float[]? _patch;
    private readonly int _patchSize;

    /// <param name="target">The layer's pixels (or its mask) as they are at the stroke's start.</param>
    /// <param name="limit">The document's bounds: dabs never reach outside, and kernels repeat the pixels at its edge.</param>
    /// <param name="maxDiameter">The largest dab the stroke can make, in pixels (Smudge's patch size).</param>
    /// <param name="sampleSeparately">
    /// Sample All Layers: dabs read a flattened image (given later with <see cref="SetSample"/>; dabs wait until then).
    /// </param>
    public LocalStroke(LocalToolSettings settings, PixelSource target, PixelRect limit, float maxDiameter, bool sampleSeparately = false)
    {
        Settings = settings;
        Channels = target.ColorChannels;
        _layer = new WorkImage(target, Channels);
        _limit = limit;
        _separate = sampleSeparately;
        _patchSize = (int)MathF.Ceiling(MathF.Max(maxDiameter, 1f) * 1.5f) + 6;
    }

    public LocalToolSettings Settings { get; }

    /// <summary>Color channels: 3 for RGB, 1 for grayscale images and masks.</summary>
    public int Channels { get; }

    /// <summary>False while a Sample All Layers stroke waits for its flattened image; dabs are kept until it arrives.</summary>
    public bool IsReady => !_separate || _sample is not null;

    /// <summary>Raised after waiting dabs were applied (<see cref="SetSample"/>), so views redraw.</summary>
    public event Action? Replayed;

    /// <summary>Hands a Sample All Layers stroke its flattened image and applies the dabs made while it was prepared.</summary>
    public void SetSample(PixelSource sample)
    {
        if (!_separate || _sample is not null) return;
        _sample = new WorkImage(sample, Channels);
        foreach (var (rect, weights, cx, cy) in _pending) Dab(rect, weights, cx, cy);
        _pending.Clear();
        Replayed?.Invoke();
    }

    /// <summary>The working pixel's straight color (<see cref="Channels"/> entries) at a document pixel; returns its alpha.</summary>
    public float Read(int x, int y, Span<float> color)
    {
        Span<float> p = stackalloc float[4];
        _layer.Read(x, y, p);
        return Unpremultiply(p, color, Channels);
    }

    /// <summary>
    /// The average of the <paramref name="factor"/> × <paramref name="factor"/> working pixels from
    /// (<paramref name="x0"/>, <paramref name="y0"/>), straight; returns the alpha. For downscaled previews.
    /// </summary>
    public float ReadArea(int x0, int y0, int factor, Span<float> color)
    {
        if (factor <= 1) return Read(x0, y0, color);
        Span<float> sum = stackalloc float[4];
        Span<float> p = stackalloc float[4];
        for (int y = y0; y < y0 + factor; y++)
            for (int x = x0; x < x0 + factor; x++)
            {
                _layer.Read(x, y, p);
                for (int k = 0; k <= Channels; k++) sum[k] += p[k];
            }
        float inv = 1f / (factor * factor);
        for (int k = 0; k <= Channels; k++) sum[k] *= inv;
        return Unpremultiply(sum, color, Channels);
    }

    private static float Unpremultiply(ReadOnlySpan<float> p, Span<float> color, int colors)
    {
        float a = p[colors];
        float inv = a > 1e-6f ? 1f / a : 0f;
        for (int k = 0; k < colors; k++) color[k] = Math.Clamp(p[k] * inv, 0f, 1f);
        return Math.Clamp(a, 0f, 1f);
    }

    /// <summary>One dab: <paramref name="weights"/> (row-major over <paramref name="rect"/>) at center (<paramref name="cx"/>, <paramref name="cy"/>).</summary>
    internal void Dab(PixelRect rect, float[] weights, float cx, float cy)
    {
        if (!IsReady)
        {
            _pending.Add((rect, weights[..(rect.Width * rect.Height)], cx, cy));
            return;
        }
        var s = Settings;
        int stride = Channels + 1, w = rect.Width, h = rect.Height;
        var source = _separate ? _sample! : _layer;

        // What each pixel of the dab becomes at full weight (premultiplied), from the image as it is now. Buffers are
        // reused from dab to dab: a large brush would otherwise allocate large arrays per dab and stall on collections.
        int n = w * h * stride;
        var current = Scratch(ref _current, n);
        source.CopyOut(rect, current);
        var result = s.Tool switch
        {
            LocalTool.Blur => Blur(source, rect, Scratch(ref _result, n)),
            LocalTool.Sharpen => Sharpen(source, rect, current),
            _ => Smudge(source, rect, current, cx, cy),
        };
        if (s.Mode != PaintMode.Normal) ApplyMode(s.Mode, current, result, Channels, w * h);

        float gain = s.Tool == LocalTool.Smudge ? MathF.Min(1f, 0.25f + s.Strength) : Math.Clamp(s.Strength, 0f, 1f);
        var layer = _separate ? Scratch(ref _layerBuffer, n) : current;
        if (_separate) _layer.CopyOut(rect, layer);
        for (int i = 0; i < w * h; i++)
        {
            float k = weights[i] * gain;
            if (k <= 0f) continue;
            int o = i * stride;
            for (int c = 0; c < stride; c++)
            {
                layer[o + c] += (result[o + c] - layer[o + c]) * k;
                if (_separate) current[o + c] += (result[o + c] - current[o + c]) * k;
            }
        }
        _layer.CopyIn(rect, layer, weights);
        if (_separate) _sample!.CopyIn(rect, current, weights);
        if (s.Tool == LocalTool.Smudge) PickUp(rect, weights, _separate ? current : layer, cx, cy);
    }

    // ---- Blur and Sharpen --------------------------------------------------------------------------------

    /// <summary>A 5×5 binomial blur of the dab's area into <paramref name="into"/> (edge pixels of the document repeat).</summary>
    private float[] Blur(WorkImage source, PixelRect rect, float[] into) =>
        Convolve(source, rect, [1f / 16, 4f / 16, 6f / 16, 4f / 16, 1f / 16], 2, into);

    private float[] _current = [], _result = [], _layerBuffer = [], _src = [], _rows = [], _narrow = [], _around = [];

    /// <summary>A reusable buffer of at least <paramref name="n"/> floats (contents undefined).</summary>
    private static float[] Scratch(ref float[] buffer, int n)
    {
        if (buffer.Length < n) buffer = new float[Math.Max(n, buffer.Length + buffer.Length / 2)];
        return buffer;
    }

    private const float SharpenAmount = 3f, SharpenProtected = 2f;

    private float[] Sharpen(WorkImage source, PixelRect rect, float[] current)
    {
        // A band-pass (difference of a 3-tap and a 5-tap binomial blur) finds edges but has no response at the pixel
        // frequency, so going over an area again sharpens edges without turning single-pixel noise into a checkerboard.
        int n = rect.Width * rect.Height * (Channels + 1);
        var narrow = Convolve(source, rect, [0.25f, 0.5f, 0.25f], 1, Scratch(ref _narrow, n));
        var wide = Blur(source, rect, Scratch(ref _result, n));
        bool protect = Settings.ProtectDetail;
        float amount = protect ? SharpenProtected : SharpenAmount;
        int stride = Channels + 1, w = rect.Width, h = rect.Height;
        var outer = Inflate(rect, 1);
        var around = protect ? Scratch(ref _around, outer.Width * outer.Height * stride) : null;
        if (around is not null) source.CopyOut(outer, around);
        var result = wide; // each pixel's result is written after its own wide value is read
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * stride;
                float a = current[o + Channels];
                for (int c = 0; c < Channels; c++)
                {
                    float v = current[o + c] + amount * (narrow[o + c] - wide[o + c]);
                    if (around is not null)
                    {
                        float lo = float.MaxValue, hi = float.MinValue;
                        for (int dy = 0; dy < 3; dy++)
                            for (int dx = 0; dx < 3; dx++)
                            {
                                float nb = around[((y + dy) * outer.Width + x + dx) * stride + c];
                                lo = MathF.Min(lo, nb);
                                hi = MathF.Max(hi, nb);
                            }
                        v = Math.Clamp(v, lo, hi);
                    }
                    result[o + c] = Math.Clamp(v, 0f, a);
                }
                result[o + Channels] = a;
            }
        return result;
    }

    /// <summary>A separable convolution of the dab's area with <paramref name="kernel"/> (radius <paramref name="r"/>) both ways.</summary>
    private float[] Convolve(WorkImage source, PixelRect rect, float[] kernel, int r, float[] result)
    {
        int stride = Channels + 1, w = rect.Width, h = rect.Height;
        // Read the area around the dab within the document; positions beyond the document repeat its edge.
        var outer = Inflate(rect, r).Intersect(_limit);
        int ow = outer.Width, oh = outer.Height;
        var src = Scratch(ref _src, ow * oh * stride);
        source.CopyOut(outer, src);
        var rows = Scratch(ref _rows, ow * h * stride); // vertical pass first: rect's rows × outer's columns
        // Large dabs are split across cores by rows; small ones are not worth the hand-off.
        ForRows(h, w * h > 16384, y =>
        {
            for (int x = 0; x < ow; x++)
            {
                int o = (y * ow + x) * stride;
                for (int c = 0; c < stride; c++) rows[o + c] = 0f;
                for (int t = -r; t <= r; t++)
                {
                    int sy = Math.Clamp(rect.Top + y + t, outer.Top, outer.Bottom - 1) - outer.Top;
                    int si = (sy * ow + x) * stride;
                    float k = kernel[t + r];
                    for (int c = 0; c < stride; c++) rows[o + c] += src[si + c] * k;
                }
            }
        });
        ForRows(h, w * h > 16384, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * stride;
                for (int c = 0; c < stride; c++) result[o + c] = 0f;
                for (int t = -r; t <= r; t++)
                {
                    int sx = Math.Clamp(rect.Left + x + t, outer.Left, outer.Right - 1) - outer.Left;
                    int si = (y * ow + sx) * stride;
                    float k = kernel[t + r];
                    for (int c = 0; c < stride; c++) result[o + c] += rows[si + c] * k;
                }
            }
        });
        return result;
    }

    private static void ForRows(int h, bool parallel, Action<int> row)
    {
        if (parallel) Parallel.For(0, h, row);
        else for (int y = 0; y < h; y++) row(y);
    }

    // ---- Smudge ------------------------------------------------------------------------------------------

    private (int X, int Y) PatchOrigin(float cx, float cy) =>
        ((int)MathF.Round(cx) - _patchSize / 2, (int)MathF.Round(cy) - _patchSize / 2);

    /// <summary>The patch laid over the dab's area; the first dab picks it up first.</summary>
    private float[] Smudge(WorkImage source, PixelRect rect, float[] current, float cx, float cy)
    {
        int stride = Channels + 1, w = rect.Width, h = rect.Height;
        var (ox, oy) = PatchOrigin(cx, cy);
        if (_patch is null)
        {
            _patch = new float[_patchSize * _patchSize * stride];
            if (Settings.FingerPainting)
            {
                var c = Settings.FingerColor;
                ReadOnlySpan<float> color = Channels == 1 ? [Toning.Luma(c.R, c.G, c.B), 1f] : [c.R, c.G, c.B, 1f];
                for (int i = 0; i < _patchSize * _patchSize; i++) color.CopyTo(_patch.AsSpan(i * stride, stride));
            }
            else
            {
                var area = new PixelRect(ox, oy, ox + _patchSize, oy + _patchSize).Intersect(_limit);
                if (!area.IsEmpty) Fill(area, source, ox, oy);
            }
        }
        var result = Scratch(ref _result, w * h * stride);
        Array.Copy(current, result, w * h * stride);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int px = rect.Left + x - ox, py = rect.Top + y - oy;
                if (px < 0 || py < 0 || px >= _patchSize || py >= _patchSize) continue;
                Array.Copy(_patch, (py * _patchSize + px) * stride, result, (y * w + x) * stride, stride);
            }
        return result;
    }

    /// <summary>Copies the image under the patch into it (first pickup).</summary>
    private void Fill(PixelRect area, WorkImage source, int ox, int oy)
    {
        int stride = Channels + 1;
        var tmp = new float[area.Width * area.Height * stride];
        source.CopyOut(area, tmp);
        for (int y = 0; y < area.Height; y++)
            Array.Copy(tmp, y * area.Width * stride, _patch!, ((area.Top + y - oy) * _patchSize + area.Left - ox) * stride, area.Width * stride);
    }

    /// <summary>After a smudge dab: the patch keeps Strength of itself and picks up the rest from the pixels under it.</summary>
    private void PickUp(PixelRect rect, float[] weights, float[] under, float cx, float cy)
    {
        int stride = Channels + 1, w = rect.Width;
        float keep = Math.Clamp(Settings.Strength, 0f, 1f);
        var (ox, oy) = PatchOrigin(cx, cy);
        for (int y = 0; y < rect.Height; y++)
            for (int x = 0; x < w; x++)
            {
                int px = rect.Left + x - ox, py = rect.Top + y - oy;
                if (px < 0 || py < 0 || px >= _patchSize || py >= _patchSize) continue;
                int p = (py * _patchSize + px) * stride, o = (y * w + x) * stride;
                float k = weights[y * w + x] > 0f ? keep : 0f;
                for (int c = 0; c < stride; c++) _patch![p + c] = _patch[p + c] * k + under[o + c] * (1f - k);
            }
    }

    // ---- Modes -------------------------------------------------------------------------------------------

    /// <summary>Combines the result with the current pixels in <paramref name="mode"/> (Darken: only darker, and so on).</summary>
    private static void ApplyMode(PaintMode mode, float[] current, float[] result, int colors, int pixels)
    {
        int stride = colors + 1;
        Span<float> b = stackalloc float[3];
        Span<float> s = stackalloc float[3];
        for (int o = 0; o < pixels * stride; o += stride)
        {
            float ba = current[o + colors], sa = result[o + colors];
            if (ba <= 1e-6f || sa <= 1e-6f) continue;
            for (int c = 0; c < colors; c++)
            {
                b[c] = current[o + c] / ba;
                s[c] = result[o + c] / sa;
            }
            if (colors == 3 && !PaintBlender.IsSeparable(mode))
            {
                var (r, g, bl) = PaintBlender.NonSeparable(mode, b[0], b[1], b[2], s[0], s[1], s[2]);
                (s[0], s[1], s[2]) = (r, g, bl);
            }
            else if (PaintBlender.IsSeparable(mode))
                for (int c = 0; c < colors; c++) s[c] = PaintBlender.Separable(mode, b[c], s[c]);
            for (int c = 0; c < colors; c++) result[o + c] = Math.Clamp(s[c], 0f, 1f) * sa;
        }
    }

    private static PixelRect Inflate(PixelRect r, int by) => new(r.Left - by, r.Top - by, r.Right + by, r.Bottom + by);

    /// <summary>A tiled, premultiplied copy of an image, created from <c>source</c> tile by tile as it is first touched.</summary>
    private sealed class WorkImage(PixelSource source, int colors)
    {
        private const int T = 64;
        private readonly ConcurrentDictionary<(int, int), float[]> _tiles = new();
        private readonly int _stride = colors + 1;

        /// <summary>A premultiplied pixel (renders read this while dabs are added; values are replaced whole, never half-written tiles).</summary>
        public void Read(int x, int y, Span<float> p)
        {
            int tx = FloorDiv(x, T), ty = FloorDiv(y, T);
            if (_tiles.TryGetValue((tx, ty), out var tile))
            {
                int o = ((y - ty * T) * T + (x - tx * T)) * _stride;
                for (int c = 0; c < _stride; c++) p[c] = tile[o + c];
                return;
            }
            ReadSource(x, y, p);
        }

        private void ReadSource(int x, int y, Span<float> p)
        {
            Span<float> color = stackalloc float[3];
            float a = source.Read(x, y, color);
            int n = Math.Min(colors, source.ColorChannels);
            for (int c = 0; c < colors; c++) p[c] = (c < n ? color[c] : color[0]) * a;
            p[colors] = a;
        }

        private float[] Tile(int tx, int ty)
        {
            if (_tiles.TryGetValue((tx, ty), out var tile)) return tile;
            tile = new float[T * T * _stride];
            Span<float> p = stackalloc float[4];
            for (int y = 0; y < T; y++)
                for (int x = 0; x < T; x++)
                {
                    ReadSource(tx * T + x, ty * T + y, p);
                    p[.._stride].CopyTo(tile.AsSpan((y * T + x) * _stride, _stride));
                }
            return _tiles.GetOrAdd((tx, ty), tile);
        }

        /// <summary>Copies <paramref name="r"/> out, row-major and premultiplied, into <paramref name="dst"/>.</summary>
        public void CopyOut(PixelRect r, float[] dst)
        {
            for (int ty = FloorDiv(r.Top, T); ty <= FloorDiv(r.Bottom - 1, T); ty++)
            {
                int y0 = Math.Max(r.Top, ty * T), y1 = Math.Min(r.Bottom, (ty + 1) * T);
                for (int tx = FloorDiv(r.Left, T); tx <= FloorDiv(r.Right - 1, T); tx++)
                {
                    int x0 = Math.Max(r.Left, tx * T), x1 = Math.Min(r.Right, (tx + 1) * T);
                    var tile = Tile(tx, ty);
                    for (int y = y0; y < y1; y++)
                        Array.Copy(tile, ((y - ty * T) * T + x0 - tx * T) * _stride, dst, ((y - r.Top) * r.Width + x0 - r.Left) * _stride, (x1 - x0) * _stride);
                }
            }
        }

        /// <summary>Writes the pixels of <paramref name="r"/> that have a weight back from <paramref name="src"/>.</summary>
        public void CopyIn(PixelRect r, float[] src, float[] weights)
        {
            for (int ty = FloorDiv(r.Top, T); ty <= FloorDiv(r.Bottom - 1, T); ty++)
            {
                int y0 = Math.Max(r.Top, ty * T), y1 = Math.Min(r.Bottom, (ty + 1) * T);
                for (int tx = FloorDiv(r.Left, T); tx <= FloorDiv(r.Right - 1, T); tx++)
                {
                    int x0 = Math.Max(r.Left, tx * T), x1 = Math.Min(r.Right, (tx + 1) * T);
                    var tile = Tile(tx, ty);
                    for (int y = y0; y < y1; y++)
                        for (int x = x0; x < x1; x++)
                        {
                            int i = (y - r.Top) * r.Width + x - r.Left;
                            if (weights[i] <= 0f) continue;
                            Array.Copy(src, i * _stride, tile, ((y - ty * T) * T + x - tx * T) * _stride, _stride);
                        }
                }
            }
        }

        private static int FloorDiv(int a, int b) => a >= 0 ? a / b : (a - b + 1) / b;
    }
}
