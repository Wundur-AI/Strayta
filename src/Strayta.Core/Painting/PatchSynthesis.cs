namespace Strayta.Core.Painting;

/// <summary>What <see cref="PatchSynthesis"/> builds: plausible content continuing the surroundings, or a texture.</summary>
public enum SynthesisKind
{
    /// <summary>Spot Healing's Content-Aware and Edit › Content-Aware Fill: structure and texture from the surroundings, coarse to fine.</summary>
    ContentAware,

    /// <summary>Spot Healing's Create Texture: a texture made of patches of the area around the stroke, with no attempt to continue structure.</summary>
    CreateTexture,
}

/// <summary>Settings for <see cref="PatchSynthesis.Synthesize"/>.</summary>
public sealed record SynthesisOptions
{
    public SynthesisKind Kind { get; init; } = SynthesisKind.ContentAware;

    /// <summary>Patch width in pixels (odd).</summary>
    public int PatchSize { get; init; } = 7;

    /// <summary>
    /// How far around the hole to look for patches, in pixels; null samples the whole canvas (everything outside the
    /// hole, as Edit › Content-Aware Fill does).
    /// </summary>
    public int? SampleMargin { get; init; }

    /// <summary>Seed for the random search, so results are repeatable.</summary>
    public int Seed { get; init; } = 12345;

    /// <summary>Content-Aware only: EM iterations at the finest level (more is slower and slightly sharper).</summary>
    public int FinestIterations { get; init; } = 2;
}

/// <summary>
/// Fills a hole with patches of the rest of the image: Spot Healing's Content-Aware and Create Texture types and
/// Edit › Content-Aware Fill.
/// </summary>
/// <remarks>
/// <para>
/// Method, implemented from the published papers: the hole is completed by minimizing a patch-coherence energy
/// (Wexler, Shechtman and Irani, "Space-Time Completion of Video", IEEE PAMI 2007): every patch overlapping the hole
/// should look like some patch of the known image. It alternates two steps. (1) Search: for each such patch find its
/// nearest known patch with PatchMatch (Barnes, Shechtman, Finkelstein and Goldman, "PatchMatch: A Randomized
/// Correspondence Algorithm for Structural Image Editing", SIGGRAPH 2009): random initialization, then scans that
/// propagate good offsets from the left/upper (or right/lower) neighbours and a random search at exponentially
/// shrinking radii around the current match. (2) Vote: each hole pixel becomes the weighted average of the pixels the
/// overlapping patches' matches put there, weighted by exp(−d / 2σ²) with σ² the 75th percentile of the distances, as
/// Wexler et al. suggest.
/// </para>
/// <para>
/// Coarse to fine: a pyramid is built until the hole is about eight pixels across; the coarsest hole starts as a
/// smooth membrane fill from its edge, and each finer level starts from the upsampled image and matches (offsets
/// doubled), so large structures are settled cheaply at low resolution and only detail is refined at full resolution.
/// Only patches that overlap the hole are matched, and a source patch must lie entirely in known pixels.
/// </para>
/// <para>
/// The result is returned as a "texture" image: the vote over the hole and a thin ring around it. The callers then
/// heal it into the image with <see cref="Healing.Heal"/> (Poisson), whose boundary condition removes any remaining
/// color or brightness seam, as the Healing Brush does with its source.
/// </para>
/// <para>
/// Create Texture skips the pyramid: every hole patch starts matched to a random patch of the ring around the stroke
/// (a band as wide as the brush) and a few search/vote iterations make neighbouring patches agree, producing a
/// texture with the local grain and no attempt to continue structure.
/// </para>
/// </remarks>
public static class PatchSynthesis
{
    /// <summary>
    /// Synthesizes the pixels where <paramref name="coverage"/> (over <paramref name="area"/>) is above zero from the
    /// rest of <paramref name="image"/> within <paramref name="canvas"/>. Returns straight colors over the sampled
    /// window, with the hole and a ring of <see cref="RingWidth"/> pixels around it replaced by the synthesized texture,
    /// or null when there is nothing to sample.
    /// </summary>
    public static PixelSource? Synthesize(PixelSource image, float[] coverage, PixelRect area, PixelRect canvas, SynthesisOptions? options = null)
    {
        var o = options ?? new SynthesisOptions();
        int r = Math.Max(1, o.PatchSize / 2);
        var holeBox = area.Intersect(canvas);
        if (holeBox.IsEmpty) return null;
        int extent = Math.Max(holeBox.Width, holeBox.Height);
        int margin = o.Kind == SynthesisKind.CreateTexture
            ? Math.Max(2 * o.PatchSize + 2, Math.Min(holeBox.Width, holeBox.Height) / 2 + 2 * r)
            : o.SampleMargin ?? int.MaxValue / 4;
        var window = new PixelRect(holeBox.Left - margin, holeBox.Top - margin, holeBox.Right + margin, holeBox.Bottom + margin).Intersect(canvas);
        int colors = image.ColorChannels;

        var level0 = Level.Load(image, window, coverage, area, colors, o.Kind == SynthesisKind.CreateTexture ? margin : -1);
        if (!level0.AnyHole) return null;
        level0.Prepare(r);
        if (level0.ValidCount == 0) return null;

        var rng = new Random(o.Seed);
        int[] nnf;
        float[] dist;
        if (o.Kind == SynthesisKind.CreateTexture)
        {
            (nnf, dist) = level0.RandomField(rng);
            level0.Vote(nnf, dist, r, uniform: true); // a random mosaic of ring patches
            for (int it = 0; it < 3; it++)
            {
                level0.PatchMatch(nnf, dist, r, passes: 2, o.Seed + it);
                level0.Vote(nnf, dist, r, uniform: false);
            }
        }
        else
        {
            // Pyramid: halve until the hole is about eight pixels across (and patches still fit).
            var levels = new List<Level> { level0 };
            while ((extent >> levels.Count) > 8 && levels[^1].Width / 2 > 4 * o.PatchSize && levels[^1].Height / 2 > 4 * o.PatchSize)
            {
                var next = levels[^1].Half();
                next.Prepare(r);
                if (next.ValidCount < 16) break;
                levels.Add(next);
            }

            var top = levels[^1];
            top.MembraneFill();
            (nnf, dist) = top.RandomField(rng);
            for (int l = levels.Count - 1; l >= 0; l--)
            {
                var level = levels[l];
                if (l < levels.Count - 1)
                {
                    level.UpsampleHole(levels[l + 1]);
                    nnf = level.UpsampleField(levels[l + 1], nnf, rng);
                    dist = new float[nnf.Length];
                }
                int iterations = l == levels.Count - 1 ? 6 : l == 0 ? Math.Max(1, o.FinestIterations) : 4;
                for (int it = 0; it < iterations; it++)
                {
                    level.PatchMatch(nnf, dist, r, passes: it == 0 ? 3 : 2, o.Seed + 31 * l + it);
                    level.Vote(nnf, dist, r, uniform: false);
                }
            }
        }

        return level0.Texture(nnf, dist, r, window, sharp: o.Kind == SynthesisKind.CreateTexture);
    }

    /// <summary>Width of the ring outside the hole included in <see cref="Synthesize"/>'s texture (the heal's boundary lies in it).</summary>
    public const int RingWidth = 2;

    /// <summary>
    /// Content-Aware (or Create Texture) healing of a finished stroke: synthesis over the painted area, then the Poisson
    /// heal of the result into the image. Null when the image offers nothing to sample.
    /// </summary>
    public static PixelSource? HealStroke(PixelSource image, PaintStroke stroke, PixelRect canvas, SynthesisKind kind, HealOptions? heal = null)
    {
        var coverage = Healing.Coverage(stroke);
        int extent = Math.Max(stroke.Bounds.Width, stroke.Bounds.Height);
        var options = new SynthesisOptions
        {
            Kind = kind,
            SampleMargin = Math.Clamp(3 * extent, 48, 600),
            PatchSize = stroke.Brush.Size < 12 ? 5 : 7,
        };
        if (Synthesize(image, coverage, stroke.Bounds, canvas, options) is not { } texture) return null;
        return Healing.Heal(image, texture, 0, 0, coverage, stroke.Bounds, canvas, heal ?? new HealOptions { BrushSize = stroke.Brush.Size });
    }

    /// <summary>One pyramid level of the window: premultiplied colors then alpha, and which pixels are hole or known.</summary>
    private sealed class Level
    {
        private Level(int width, int height, int channels)
        {
            Width = width;
            Height = height;
            C = channels;
            Px = new float[width * height * channels];
            Hole = new bool[width * height];
            Known = new bool[width * height];
        }

        public int Width { get; }
        public int Height { get; }
        public int C { get; }
        public float[] Px { get; }
        public bool[] Hole { get; }
        public bool[] Known { get; }

        /// <summary>Source patch centers: the patch lies entirely in known pixels.</summary>
        public bool[] Valid { get; private set; } = [];
        public int[] ValidList { get; private set; } = [];
        public int ValidCount => ValidList.Length;

        /// <summary>Target patch centers: the patch overlaps the hole.</summary>
        public bool[] Target { get; private set; } = [];
        private PixelRect _targetBox;

        public bool AnyHole { get; private set; }

        /// <summary>
        /// Level 0 from the image. With <paramref name="ringOnly"/> ≥ 0 only known pixels within that distance of the
        /// hole may be sampled (Create Texture).
        /// </summary>
        public static Level Load(PixelSource image, PixelRect window, float[] coverage, PixelRect area, int colors, int ringOnly)
        {
            int c = colors + 1;
            var level = new Level(window.Width, window.Height, c);
            bool any = false;
            Parallel.For(0, window.Height, () => false, (row, _, found) =>
            {
                int y = window.Top + row;
                Span<float> color = stackalloc float[3];
                for (int col = 0; col < window.Width; col++)
                {
                    int x = window.Left + col, i = row * window.Width + col, o = i * c;
                    float a = image.Read(x, y, color);
                    for (int k = 0; k < colors; k++) level.Px[o + k] = color[k] * a;
                    level.Px[o + colors] = a;
                    bool hole = x >= area.Left && x < area.Right && y >= area.Top && y < area.Bottom
                                && coverage[(y - area.Top) * area.Width + (x - area.Left)] > 0f;
                    level.Hole[i] = hole;
                    level.Known[i] = !hole;
                    found |= hole;
                }
                return found;
            }, found => { if (found) any = true; });
            level.AnyHole = any;
            if (ringOnly >= 0)
            {
                var near = Dilate(level.Hole, level.Width, level.Height, ringOnly);
                for (int i = 0; i < near.Length; i++) level.Known[i] &= near[i];
            }
            return level;
        }

        /// <summary>The next coarser level: known pixels averaged over 2×2; a cell is hole if any child is, known if all children are.</summary>
        public Level Half()
        {
            int w = (Width + 1) / 2, h = (Height + 1) / 2;
            var o = new Level(w, h, C) { AnyHole = AnyHole };
            Parallel.For(0, h, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    int oi = y * w + x, n = 0;
                    bool hole = false, known = true;
                    for (int sy = 2 * y; sy < Math.Min(Height, 2 * y + 2); sy++)
                        for (int sx = 2 * x; sx < Math.Min(Width, 2 * x + 2); sx++)
                        {
                            int i = sy * Width + sx;
                            hole |= Hole[i];
                            known &= Known[i];
                            if (Hole[i]) continue;
                            n++;
                            for (int k = 0; k < C; k++) o.Px[oi * C + k] += Px[i * C + k];
                        }
                    if (n > 0) for (int k = 0; k < C; k++) o.Px[oi * C + k] /= n;
                    o.Hole[oi] = hole;
                    o.Known[oi] = known && !hole;
                }
            });
            return o;
        }

        /// <summary>Works out source and target patch centers for patches of radius <paramref name="r"/>.</summary>
        public void Prepare(int r)
        {
            int w = Width, h = Height;
            // Valid: a (2r+1)² window of known pixels, via separable erosion.
            var rowOk = new bool[w * h];
            Parallel.For(0, h, y =>
            {
                int run = 0;
                var tmp = new int[w];
                for (int x = 0; x < w; x++)
                {
                    run = Known[y * w + x] ? run + 1 : 0;
                    tmp[x] = run;
                }
                for (int x = r; x < w - r; x++) rowOk[y * w + x] = tmp[x + r] >= 2 * r + 1;
            });
            var valid = new bool[w * h];
            Parallel.For(r, Math.Max(r, w - r), x =>
            {
                int run = 0;
                for (int y = 0; y < h; y++)
                {
                    run = rowOk[y * w + x] ? run + 1 : 0;
                    if (y - r >= r && run >= 2 * r + 1) valid[(y - r) * w + x] = true;
                }
            });
            Valid = valid;
            var list = new List<int>();
            for (int i = 0; i < valid.Length; i++)
                if (valid[i]) list.Add(i);
            ValidList = [.. list];

            Target = Dilate(Hole, w, h, r);
            int l = w, t = h, rr = 0, b = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (Target[y * w + x]) (l, t, rr, b) = (Math.Min(l, x), Math.Min(t, y), Math.Max(rr, x + 1), Math.Max(b, y + 1));
            _targetBox = rr > l ? new PixelRect(l, t, rr, b) : PixelRect.Empty;
        }

        /// <summary>Chebyshev dilation of a mask by <paramref name="r"/>.</summary>
        private static bool[] Dilate(bool[] mask, int w, int h, int r)
        {
            var horizontal = new bool[w * h];
            Parallel.For(0, h, y =>
            {
                int last = int.MinValue / 2;
                for (int x = 0; x < w; x++)
                {
                    if (mask[y * w + x]) last = x;
                    if (x - last <= r) horizontal[y * w + x] = true;
                }
                last = int.MaxValue / 2;
                for (int x = w - 1; x >= 0; x--)
                {
                    if (mask[y * w + x]) last = x;
                    if (last - x <= r) horizontal[y * w + x] = true;
                }
            });
            var result = new bool[w * h];
            Parallel.For(0, w, x =>
            {
                int last = int.MinValue / 2;
                for (int y = 0; y < h; y++)
                {
                    if (horizontal[y * w + x]) last = y;
                    if (y - last <= r) result[y * w + x] = true;
                }
                last = int.MaxValue / 2;
                for (int y = h - 1; y >= 0; y--)
                {
                    if (horizontal[y * w + x]) last = y;
                    if (last - y <= r) result[y * w + x] = true;
                }
            });
            return result;
        }

        /// <summary>The coarsest start: the hole filled by a smooth membrane from its edge.</summary>
        public void MembraneFill()
        {
            var state = new byte[Width * Height];
            for (int i = 0; i < state.Length; i++) state[i] = Hole[i] ? (byte)1 : (byte)0;
            Healing.SolveMembrane(Px, state, Width, Height, C);
        }

        /// <summary>A random match for every target patch.</summary>
        public (int[] Nnf, float[] Dist) RandomField(Random rng)
        {
            var nnf = new int[Width * Height];
            var dist = new float[Width * Height];
            Array.Fill(nnf, -1);
            for (int i = 0; i < nnf.Length; i++)
                if (Target[i]) nnf[i] = ValidList[rng.Next(ValidList.Length)];
            return (nnf, dist);
        }

        public void ComputeDistances(int[] nnf, float[] dist, int r)
        {
            var box = _targetBox;
            Parallel.For(box.Top, box.Bottom, y =>
            {
                for (int x = box.Left; x < box.Right; x++)
                {
                    int p = y * Width + x;
                    if (Target[p] && nnf[p] >= 0) dist[p] = Distance(p, nnf[p], r, float.MaxValue);
                }
            });
        }

        /// <summary>Sum of squared differences between the patch at <paramref name="p"/> and the source patch at <paramref name="s"/>; stops early above <paramref name="best"/>.</summary>
        private float Distance(int p, int s, int r, float best)
        {
            int w = Width, c = C;
            int px = p % w, py = p / w, sx = s % w, sy = s / w;
            int x0 = Math.Max(-r, -px), x1 = Math.Min(r, w - 1 - px);
            var pixels = Px;
            float sum = 0f;
            for (int j = -r; j <= r; j++)
            {
                int y = py + j;
                if (y < 0 || y >= Height) continue;
                int a = (y * w + px + x0) * c, b = ((sy + j) * w + sx + x0) * c, n = (x1 - x0 + 1) * c;
                for (int k = 0; k < n; k++)
                {
                    float d = pixels[a + k] - pixels[b + k];
                    sum += d * d;
                }
                if (sum >= best) return sum;
            }
            return sum;
        }

        /// <summary>
        /// PatchMatch: <paramref name="passes"/> scans (alternating direction) of propagation and random search. Rows
        /// are split into bands scanned in parallel; within a band the scan is sequential, as in the paper.
        /// </summary>
        public void PatchMatch(int[] nnf, float[] dist, int r, int passes, int seed)
        {
            var box = _targetBox;
            if (box.IsEmpty) return;
            ComputeDistances(nnf, dist, r); // the hole's pixels changed since the matches were scored
            int w = Width, maxRadius = Math.Max(Width, Height);
            const int Band = 8;
            int bands = (box.Height + Band - 1) / Band;
            for (int pass = 0; pass < passes; pass++)
            {
                bool forward = pass % 2 == 0;
                int step = forward ? 1 : -1;
                // Even bands, then odd ones: a band reads the edge row of its neighbour, which is then not being
                // written, so the result does not depend on thread timing.
                for (int parity = 0; parity < 2; parity++)
                    Parallel.For(0, (bands + 1 - parity) / 2, half => ScanBand(2 * half + parity, pass, forward, step));
            }

            void ScanBand(int band, int pass, bool forward, int step)
            {
                {
                    var rng = new Random(seed * 7919 + pass * 104729 + band);
                    int yStart = box.Top + band * Band, yEnd = Math.Min(box.Bottom, yStart + Band);
                    for (int yy = 0; yy < yEnd - yStart; yy++)
                    {
                        int y = forward ? yStart + yy : yEnd - 1 - yy;
                        for (int xx = 0; xx < box.Width; xx++)
                        {
                            int x = forward ? box.Left + xx : box.Right - 1 - xx;
                            int p = y * w + x;
                            if (!Target[p]) continue;
                            int best = nnf[p];
                            float bestD = best >= 0 ? dist[p] : float.MaxValue;

                            // Propagation from the previous pixel in the row and the previous row.
                            int qx = x - step, qy = y - step;
                            if (qx >= 0 && qx < w) Try(p, nnf[y * w + qx], step, 0, ref best, ref bestD);
                            if (qy >= 0 && qy < Height) Try(p, nnf[qy * w + x], 0, step, ref best, ref bestD);

                            // Random search around the current best at halving radii.
                            if (best >= 0)
                            {
                                int bx = best % w, by = best / w;
                                for (int radius = maxRadius; radius >= 1; radius /= 2)
                                {
                                    int cx = bx + rng.Next(-radius, radius + 1), cy = by + rng.Next(-radius, radius + 1);
                                    if (cx < 0 || cy < 0 || cx >= w || cy >= Height) continue;
                                    int s = cy * w + cx;
                                    if (!Valid[s] || s == best) continue;
                                    float d = Distance(p, s, r, bestD);
                                    if (d < bestD) (best, bestD) = (s, d);
                                }
                            }
                            else
                            {
                                best = ValidList[rng.Next(ValidList.Length)];
                                bestD = Distance(p, best, r, float.MaxValue);
                            }
                            nnf[p] = best;
                            dist[p] = bestD;
                        }
                    }

                    void Try(int p, int neighbour, int dx, int dy, ref int best, ref float bestD)
                    {
                        if (neighbour < 0) return;
                        int sx = neighbour % w + dx, sy = neighbour / w + dy;
                        if (sx < 0 || sy < 0 || sx >= w || sy >= Height) return;
                        int s = sy * w + sx;
                        if (!Valid[s] || s == best) return;
                        float d = Distance(p, s, r, bestD);
                        if (d < bestD) (best, bestD) = (s, d);
                    }
                }
            }
        }

        /// <summary>Weights exp(−d / 2σ²), σ² the 75th percentile of the patch distances (Wexler et al.).</summary>
        private float[] Weights(int[] nnf, float[] dist, int r, bool uniform)
        {
            var weights = new float[nnf.Length];
            var box = _targetBox;
            float norm = 1f / ((2 * r + 1) * (2 * r + 1) * C);
            float sigma2 = 1f;
            if (!uniform)
            {
                var sample = new List<float>();
                for (int y = box.Top; y < box.Bottom; y++)
                    for (int x = box.Left; x < box.Right; x++)
                    {
                        int p = y * Width + x;
                        if (Target[p] && nnf[p] >= 0) sample.Add(dist[p] * norm);
                    }
                if (sample.Count > 0)
                {
                    sample.Sort();
                    sigma2 = MathF.Max(1e-5f, sample[(int)(sample.Count * 0.75)]);
                }
            }
            Parallel.For(box.Top, box.Bottom, y =>
            {
                for (int x = box.Left; x < box.Right; x++)
                {
                    int p = y * Width + x;
                    if (!Target[p] || nnf[p] < 0) continue;
                    weights[p] = uniform ? 1f : MathF.Max(1e-6f, MathF.Exp(-dist[p] * norm / (2 * sigma2)));
                }
            });
            return weights;
        }

        /// <summary>Replaces every hole pixel with the weighted vote of the matches of the patches that cover it.</summary>
        public void Vote(int[] nnf, float[] dist, int r, bool uniform)
        {
            var weights = Weights(nnf, dist, r, uniform);
            var box = _targetBox;
            int w = Width, c = C;
            var result = new float[Px.Length];
            Parallel.For(box.Top, box.Bottom, y =>
            {
                Span<float> acc = stackalloc float[c];
                for (int x = box.Left; x < box.Right; x++)
                {
                    int q = y * w + x;
                    if (!Hole[q]) continue;
                    if (VoteAt(x, y, nnf, weights, r, acc))
                        for (int k = 0; k < c; k++) result[q * c + k] = acc[k];
                    else
                        for (int k = 0; k < c; k++) result[q * c + k] = Px[q * c + k];
                }
            });
            Parallel.For(box.Top, box.Bottom, y =>
            {
                for (int x = box.Left; x < box.Right; x++)
                {
                    int q = y * w + x;
                    if (Hole[q]) Array.Copy(result, q * c, Px, q * c, c);
                }
            });
        }

        /// <summary>The weighted average at (x, y) of what the covering patches' matches put there; false if none covers it.</summary>
        private bool VoteAt(int x, int y, int[] nnf, float[] weights, int r, Span<float> acc)
        {
            int w = Width, c = C;
            acc.Clear();
            float total = 0f;
            for (int j = -r; j <= r; j++)
            {
                int py = y + j;
                if (py < 0 || py >= Height) continue;
                for (int i = -r; i <= r; i++)
                {
                    int px = x + i;
                    if (px < 0 || px >= w) continue;
                    int p = py * w + px;
                    float wt = weights[p];
                    if (wt <= 0f) continue;
                    int s = nnf[p];
                    int src = ((s / w - j) * w + (s % w - i)) * c; // the source pixel at the same place in the matched patch
                    for (int k = 0; k < c; k++) acc[k] += Px[src + k] * wt;
                    total += wt;
                }
            }
            if (total <= 0f) return false;
            for (int k = 0; k < c; k++) acc[k] /= total;
            return true;
        }

        /// <summary>A finer level's hole pixels from the coarser level's (bilinear).</summary>
        public void UpsampleHole(Level coarse)
        {
            int c = C;
            Parallel.For(0, Height, y =>
            {
                float v = Math.Clamp((y + 0.5f) / 2f - 0.5f, 0f, coarse.Height - 1);
                int y0 = (int)v, y1 = Math.Min(coarse.Height - 1, y0 + 1);
                float fy = v - y0;
                for (int x = 0; x < Width; x++)
                {
                    int i = y * Width + x;
                    if (!Hole[i]) continue;
                    float u = Math.Clamp((x + 0.5f) / 2f - 0.5f, 0f, coarse.Width - 1);
                    int x0 = (int)u, x1 = Math.Min(coarse.Width - 1, x0 + 1);
                    float fx = u - x0;
                    for (int k = 0; k < c; k++)
                    {
                        float top = coarse.Px[(y0 * coarse.Width + x0) * c + k] * (1 - fx) + coarse.Px[(y0 * coarse.Width + x1) * c + k] * fx;
                        float bottom = coarse.Px[(y1 * coarse.Width + x0) * c + k] * (1 - fx) + coarse.Px[(y1 * coarse.Width + x1) * c + k] * fx;
                        Px[i * c + k] = top * (1 - fy) + bottom * fy;
                    }
                }
            });
        }

        /// <summary>The coarser level's matches with doubled offsets; invalid ones start random.</summary>
        public int[] UpsampleField(Level coarse, int[] coarseNnf, Random rng)
        {
            var nnf = new int[Width * Height];
            Array.Fill(nnf, -1);
            int seed = rng.Next();
            var box = _targetBox;
            Parallel.For(box.Top, box.Bottom, y =>
            {
                var local = new Random(seed + y);
                for (int x = box.Left; x < box.Right; x++)
                {
                    int p = y * Width + x;
                    if (!Target[p]) continue;
                    int cp = Math.Min(coarse.Height - 1, y / 2) * coarse.Width + Math.Min(coarse.Width - 1, x / 2);
                    int s = -1;
                    if (coarseNnf[cp] is var cs and >= 0)
                    {
                        int sx = (cs % coarse.Width) * 2 + (x & 1), sy = (cs / coarse.Width) * 2 + (y & 1);
                        if (sx < Width && sy < Height && Valid[sy * Width + sx]) s = sy * Width + sx;
                    }
                    nnf[p] = s >= 0 ? s : ValidList[local.Next(ValidList.Length)];
                }
            });
            return nnf;
        }

        /// <summary>
        /// The result as straight colors over the window: the vote over the hole and the ring around it (the heal's
        /// boundary), the image elsewhere.
        /// </summary>
        public PixelSource Texture(int[] nnf, float[] dist, int r, PixelRect window, bool sharp)
        {
            var weights = Weights(nnf, dist, r, uniform: false);
            var ring = Dilate(Hole, Width, Height, RingWidth);
            int w = Width, c = C, colors = c - 1;
            var output = new float[Px.Length];
            Parallel.For(0, Height, y =>
            {
                Span<float> acc = stackalloc float[c];
                for (int x = 0; x < w; x++)
                {
                    int q = y * w + x, o = q * c;
                    if (!ring[q]) Px.AsSpan(o, c).CopyTo(acc);
                    else if (sharp && nnf[q] >= 0) Px.AsSpan(nnf[q] * c, c).CopyTo(acc); // the matched patch's own center: no averaging blur
                    else if (!VoteAt(x, y, nnf, weights, r, acc)) Px.AsSpan(o, c).CopyTo(acc);
                    float a = acc[colors];
                    for (int k = 0; k < colors; k++) output[o + k] = a > 1e-6f ? Math.Clamp(acc[k] / a, 0f, 1f) : 0f;
                    output[o + colors] = Math.Clamp(a, 0f, 1f);
                }
            });
            return PixelSource.FromFloats(output, colors, window);
        }
    }
}
