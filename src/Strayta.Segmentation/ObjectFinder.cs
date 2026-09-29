using System.Numerics;
using Strayta.Core;
using Strayta.Core.Selection;

namespace Strayta.Segmentation;

/// <summary>
/// One object the Object Finder found: SAM's mask for it (<see cref="Logits"/>, which Object Selection turns into
/// the selection when the object is clicked), a thresholded copy at the mask's resolution for hit tests, a coarse bit
/// grid for comparing objects, and its outline for the hover highlight.
/// </summary>
public sealed class FoundObject
{
    internal FoundObject(MaskLogits logits, byte[] binary, ulong[] bits, int area, float score, float stability)
    {
        Logits = logits;
        Binary = binary;
        Bits = bits;
        Area = area;
        Score = score;
        Stability = stability;
    }

    public MaskLogits Logits { get; }

    /// <summary>1 inside the object, per mask cell (<see cref="MaskLogits.Width"/> × <see cref="MaskLogits.Height"/>).</summary>
    internal byte[] Binary { get; }

    /// <summary>The object on a <see cref="ObjectFinder.Grid"/>² grid, one bit per cell, for fast overlap tests.</summary>
    internal ulong[] Bits { get; }

    /// <summary>Mask cells inside the object.</summary>
    public int Area { get; }

    /// <summary>SAM's own quality estimate.</summary>
    public float Score { get; }

    /// <summary>How little the mask changes when its threshold moves (1 = a crisp, confident mask).</summary>
    public float Stability { get; }

    private IReadOnlyList<Vector2[]>? _outline;

    /// <summary>The object's outline in document coordinates (traced at mask resolution, then scaled).</summary>
    public IReadOnlyList<Vector2[]> Outline => _outline ??= ObjectFinder.TraceOutline(this);

    /// <summary>True when the document point lies inside the object.</summary>
    public bool Contains(double docX, double docY)
    {
        var p = Logits.Placement;
        if (docX < p.Left || docY < p.Top || docX >= p.Right || docY >= p.Bottom) return false;
        int u = Math.Clamp((int)((docX - p.Left) * Logits.Width / p.Width), 0, Logits.Width - 1);
        int v = Math.Clamp((int)((docY - p.Top) * Logits.Height / p.Height), 0, Logits.Height - 1);
        return Binary[v * Logits.Width + u] != 0;
    }
}

/// <summary>
/// Photoshop's Object Finder: finds the objects in an image ahead of time, so hovering with Object Selection highlights
/// the object under the pointer and a click selects it. SAM is prompted with a grid of points over the image (its
/// "automatic mask generation"); every candidate mask SAM is confident in and that is stable under its threshold is
/// kept, and near-duplicates (the same object found from several points) are merged, keeping the better one.
/// </summary>
public static class ObjectFinder
{
    /// <summary>Side of the coarse bit grid used to compare objects.</summary>
    public const int Grid = 64;

    /// <summary>Candidates SAM rates below this are dropped (SAM's own automatic generator uses 0.88).</summary>
    public const float MinScore = 0.8f;

    /// <summary>Candidates whose area changes more than this between thresholds −1 and +1 are dropped.</summary>
    public const float MinStability = 0.85f;

    /// <summary>Two masks overlapping more than this (intersection over union) are the same object.</summary>
    public const float DuplicateIoU = 0.7f;

    /// <summary>Most objects kept per image.</summary>
    public const int MaxObjects = 96;

    /// <summary>
    /// Prompt points at the centers of a <paramref name="perSide"/> × <paramref name="perSide"/> grid over
    /// <paramref name="placement"/> (document coordinates).
    /// </summary>
    public static IReadOnlyList<PromptPoint> GridPoints(PixelRect placement, int perSide)
    {
        var points = new List<PromptPoint>(perSide * perSide);
        for (int j = 0; j < perSide; j++)
            for (int i = 0; i < perSide; i++)
                points.Add(new PromptPoint(placement.Left + (i + 0.5f) * placement.Width / perSide, placement.Top + (j + 0.5f) * placement.Height / perSide));
        return points;
    }

    /// <summary>
    /// The object for one candidate mask, or null when it is not a good object: SAM is not confident, the mask is
    /// unstable, tiny, or covers nearly the whole image (the background).
    /// </summary>
    public static FoundObject? Candidate(MaskLogits logits)
    {
        if (!(logits.Score >= MinScore)) return null;
        int w = logits.Width, h = logits.Height, n = w * h;
        var values = logits.Values;
        var binary = new byte[n];
        int area = 0, loose = 0, tight = 0;
        for (int i = 0; i < n; i++)
        {
            float v = values[i];
            if (v > 0f)
            {
                binary[i] = 1;
                area++;
            }
            if (v > -1f) loose++;
            if (v > 1f) tight++;
        }
        if (area < n / 1000 + 4 || area > n * 0.9) return null;
        float stability = loose == 0 ? 0f : (float)tight / loose;
        if (stability < MinStability) return null;
        return new FoundObject(logits, binary, ToBits(binary, w, h), area, logits.Score, stability);
    }

    /// <summary>
    /// Adds <paramref name="candidate"/> to <paramref name="kept"/> unless a kept object is the same one; when it is,
    /// the one with the higher score (then stability) stays. Returns true when the list changed.
    /// </summary>
    public static bool Merge(List<FoundObject> kept, FoundObject candidate)
    {
        for (int k = 0; k < kept.Count; k++)
        {
            if (IoU(kept[k].Bits, candidate.Bits) <= DuplicateIoU) continue;
            var old = kept[k];
            if (candidate.Score + 0.1f * candidate.Stability <= old.Score + 0.1f * old.Stability) return false;
            kept[k] = candidate;
            return true;
        }
        if (kept.Count >= MaxObjects)
        {
            // Full: replace the weakest if this one is better.
            int weakest = 0;
            for (int k = 1; k < kept.Count; k++)
                if (kept[k].Score < kept[weakest].Score) weakest = k;
            if (kept[weakest].Score >= candidate.Score) return false;
            kept[weakest] = candidate;
            return true;
        }
        kept.Add(candidate);
        return true;
    }

    /// <summary>The smallest object containing the document point (the innermost thing under the pointer), or null.</summary>
    public static FoundObject? ObjectAt(IReadOnlyList<FoundObject> objects, double docX, double docY)
    {
        FoundObject? best = null;
        foreach (var o in objects)
            if (o.Contains(docX, docY) && (best is null || o.Area < best.Area)) best = o;
        return best;
    }

    /// <summary>Intersection over union of two bit grids.</summary>
    public static float IoU(ulong[] a, ulong[] b)
    {
        int inter = 0, union = 0;
        for (int i = 0; i < a.Length; i++)
        {
            inter += BitOperations.PopCount(a[i] & b[i]);
            union += BitOperations.PopCount(a[i] | b[i]);
        }
        return union == 0 ? 0f : (float)inter / union;
    }

    /// <summary>A mask on the coarse grid: a cell is set when any mask cell it covers is set.</summary>
    internal static ulong[] ToBits(byte[] binary, int w, int h)
    {
        var bits = new ulong[Grid * Grid / 64];
        for (int y = 0; y < h; y++)
        {
            int gy = y * Grid / h;
            for (int x = 0; x < w; x++)
            {
                if (binary[y * w + x] == 0) continue;
                int cell = gy * Grid + x * Grid / w;
                bits[cell >> 6] |= 1UL << (cell & 63);
            }
        }
        return bits;
    }

    internal static IReadOnlyList<Vector2[]> TraceOutline(FoundObject o)
    {
        var m = o.Logits;
        var coverage = new byte[o.Binary.Length];
        for (int i = 0; i < coverage.Length; i++) coverage[i] = o.Binary[i] != 0 ? (byte)255 : (byte)0;
        var loops = SelectionOutline.Trace(SelectionMask.FromCoverage(PixelRect.FromSize(m.Width, m.Height), coverage));
        var p = m.Placement;
        float sx = (float)p.Width / m.Width, sy = (float)p.Height / m.Height;
        return loops.Select(loop => loop.Select(v => new Vector2(p.Left + v.X * sx, p.Top + v.Y * sy)).ToArray()).ToList();
    }
}
