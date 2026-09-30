using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Strayta.Rendering.Transforms;

/// <summary>
/// One output pixel's filtered sample of a <see cref="ResampleSource"/> where the map does not shrink the image (the
/// common case for distorts, warps and liquify): a fixed 4×4 Catmull-Rom (or 2×2 tent) footprint with weights from the
/// polynomial directly, accumulated one vector per RGBA pixel. Shrinking pixels take the general path with the kernel
/// widened (<see cref="Resampler.Kernel"/>).
/// </summary>
internal static class PointSampler
{
    /// <summary>
    /// Adds the weighted source pixels around (<paramref name="sx"/>, <paramref name="sy"/>) (source pixel coordinates;
    /// pixel centers at +0.5, clamped at the edges) into <paramref name="acc"/> (cleared first); returns the total weight.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Unscaled(ResampleSource src, double sx, double sy, bool cubic, Span<float> acc)
    {
        acc.Clear();
        int sw = src.Width, sh = src.Height, ch = src.Channels;
        float[] data = src.Data;
        double gx = sx - 0.5, gy = sy - 0.5;
        int ix = (int)Math.Floor(gx), iy = (int)Math.Floor(gy);
        float tx = (float)(gx - ix), ty = (float)(gy - iy);
        Span<float> wx = stackalloc float[4], wy = stackalloc float[4];
        Span<int> cols = stackalloc int[4];
        int n;
        if (cubic)
        {
            Weights(tx, wx);
            Weights(ty, wy);
            n = 4;
            ix -= 1;
            iy -= 1;
        }
        else
        {
            wx[0] = 1 - tx; wx[1] = tx;
            wy[0] = 1 - ty; wy[1] = ty;
            n = 2;
        }
        for (int k = 0; k < n; k++) cols[k] = Math.Clamp(ix + k, 0, sw - 1);
        if (ch == 4 && Vector128.IsHardwareAccelerated)
        {
            ref float d = ref MemoryMarshal.GetArrayDataReference(data);
            var sum = Vector128<float>.Zero;
            for (int j = 0; j < n; j++)
            {
                float wj = wy[j];
                if (wj == 0) continue;
                long row = (long)Math.Clamp(iy + j, 0, sh - 1) * sw;
                var line = Vector128<float>.Zero;
                for (int i = 0; i < n; i++)
                    line += Vector128.LoadUnsafe(ref d, (nuint)((row + cols[i]) * 4)) * wx[i];
                sum += line * wj;
            }
            sum.CopyTo(acc);
            return 1;
        }
        for (int j = 0; j < n; j++)
        {
            float wj = wy[j];
            if (wj == 0) continue;
            long row = (long)Math.Clamp(iy + j, 0, sh - 1) * sw;
            for (int i = 0; i < n; i++)
            {
                float w = wx[i] * wj;
                long s = (row + cols[i]) * ch;
                for (int c = 0; c < ch; c++) acc[c] += data[s + c] * w;
            }
        }
        return 1;
    }

    /// <summary>Catmull-Rom weights of the four taps around a sample <paramref name="t"/> (0..1) past the second one; they sum to 1.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Weights(float t, Span<float> w)
    {
        float t2 = t * t, t3 = t2 * t;
        w[0] = -0.5f * t3 + t2 - 0.5f * t;
        w[1] = 1.5f * t3 - 2.5f * t2 + 1;
        w[2] = -1.5f * t3 + 2f * t2 + 0.5f * t;
        w[3] = 0.5f * t3 - 0.5f * t2;
    }
}
