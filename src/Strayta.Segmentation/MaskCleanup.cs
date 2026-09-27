namespace Strayta.Segmentation;

/// <summary>
/// Removes speckle from low-resolution mask logits before they are upscaled: small holes inside the object
/// (SAM tends to punch them where a label has text or a strong gradient) are filled, small islands apart from
/// the main object are dropped, and a light blur removes the one-cell checkerboard SAM's upsampling layers leave
/// where it is unsure. At 256 cells across a large photo each stray cell would otherwise become a visible blob
/// with its own marching ants.
/// </summary>
internal static class MaskCleanup
{
    /// <summary>Logit given to cleaned cells: clearly decided, but not so large that edges nearby get steep.</summary>
    private const float Decided = 4f;

    /// <param name="minIslandFraction">Selected components smaller than this fraction of the largest one are removed.</param>
    /// <param name="maxHoleFraction">Enclosed unselected components smaller than this fraction of the selected area are filled.</param>
    public static void Clean(float[] logits, int w, int h, float minIslandFraction, float maxHoleFraction)
    {
        var label = new int[w * h];
        var stack = new Stack<int>();
        var sizes = new List<int> { 0 };
        var touchesBorder = new List<bool> { false };

        // Label 4-connected components of the selected (> 0) and unselected cells separately.
        for (int start = 0; start < label.Length; start++)
        {
            if (label[start] != 0) continue;
            bool inside = logits[start] > 0;
            int id = sizes.Count;
            int size = 0;
            bool border = false;
            label[start] = id;
            stack.Push(start);
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                size++;
                int x = i % w, y = i / w;
                if (x == 0 || y == 0 || x == w - 1 || y == h - 1) border = true;
                if (x > 0) Visit(i - 1);
                if (x < w - 1) Visit(i + 1);
                if (y > 0) Visit(i - w);
                if (y < h - 1) Visit(i + w);
            }
            sizes.Add(size);
            touchesBorder.Add(border);

            void Visit(int j)
            {
                if (label[j] != 0 || logits[j] > 0 != inside) return;
                label[j] = id;
                stack.Push(j);
            }
        }

        int largest = 0, selected = 0;
        var isInside = new bool[sizes.Count];
        for (int i = 0; i < label.Length; i++) isInside[label[i]] = logits[i] > 0;
        for (int id = 1; id < sizes.Count; id++)
            if (isInside[id])
            {
                largest = Math.Max(largest, sizes[id]);
                selected += sizes[id];
            }
        if (largest == 0) return;

        for (int i = 0; i < label.Length; i++)
        {
            int id = label[i];
            if (isInside[id] && sizes[id] < largest * minIslandFraction) logits[i] = -Decided;
            else if (!isInside[id] && !touchesBorder[id] && sizes[id] <= selected * maxHoleFraction) logits[i] = Decided;
        }
        Smooth(logits, w, h);
    }

    /// <summary>A separable [1 2 1] / 4 blur in place (edges clamped).</summary>
    internal static void Smooth(float[] values, int w, int h)
    {
        var tmp = new float[values.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                tmp[i] = (values[i - (x > 0 ? 1 : 0)] + 2 * values[i] + values[i + (x < w - 1 ? 1 : 0)]) * 0.25f;
            }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                values[i] = (tmp[i - (y > 0 ? w : 0)] + 2 * tmp[i] + tmp[i + (y < h - 1 ? w : 0)]) * 0.25f;
            }
    }
}
