namespace Strayta.Segmentation;

/// <summary>
/// Turns an <see cref="RgbaImage"/> into a model input: resized to a square, normalized, planar (NCHW) floats.
/// </summary>
/// <remarks>
/// Both models were trained on images squashed to a square without keeping the aspect ratio (SAM 2 resizes to
/// 1024×1024, BiRefNet to its input size), so this does the same rather than letterboxing. Downscaling uses a
/// tent filter as wide as the scale factor, i.e. every source pixel contributes; plain bilinear sampling of a
/// 4000-pixel photo at 1024 would alias fine texture into patterns the encoder has never seen.
/// </remarks>
public static class ImagePreprocessor
{
    /// <summary>ImageNet channel means, used by SAM 2 and BiRefNet alike.</summary>
    public static readonly float[] ImageNetMean = [0.485f, 0.456f, 0.406f];

    /// <summary>ImageNet channel standard deviations.</summary>
    public static readonly float[] ImageNetStd = [0.229f, 0.224f, 0.225f];

    /// <summary>
    /// Transparent pixels are composited over mid gray: a neutral backdrop that reads as neither the object nor a
    /// white studio background.
    /// </summary>
    public const byte Matte = 128;

    /// <summary>Returns a 3×<paramref name="size"/>×<paramref name="size"/> tensor, channel planes in R, G, B order.</summary>
    public static float[] ToTensor(RgbaImage image, int size, ReadOnlySpan<float> mean, ReadOnlySpan<float> std)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        int w = image.Width, h = image.Height;
        var px = image.Pixels;
        var xw = ResampleWeights.Create(w, size);
        var yw = ResampleWeights.Create(h, size);

        // Horizontal pass: h rows × size columns × 3 channels, values 0..255 with alpha composited over the matte.
        var rows = new float[(long)h * size * 3];
        Parallel.For(0, h, y =>
        {
            long src = (long)y * w * 4;
            long dst = (long)y * size * 3;
            for (int o = 0; o < size; o++)
            {
                float r = 0, g = 0, b = 0;
                int start = xw.Start[o], count = xw.Count[o], wi = o * xw.Stride;
                for (int k = 0; k < count; k++)
                {
                    long i = src + (long)(start + k) * 4;
                    float wt = xw.Weight[wi + k];
                    float a = px[i + 3] * (1f / 255f), m = Matte * (1f - a);
                    r += wt * (px[i] * a + m);
                    g += wt * (px[i + 1] * a + m);
                    b += wt * (px[i + 2] * a + m);
                }
                long d = dst + o * 3;
                rows[d] = r;
                rows[d + 1] = g;
                rows[d + 2] = b;
            }
        });

        // Vertical pass straight into normalized planes.
        var tensor = new float[3L * size * size];
        float s0 = 1f / (255f * std[0]), s1 = 1f / (255f * std[1]), s2 = 1f / (255f * std[2]);
        float m0 = mean[0] / std[0], m1 = mean[1] / std[1], m2 = mean[2] / std[2];
        int plane = size * size;
        Parallel.For(0, size, oy =>
        {
            int start = yw.Start[oy], count = yw.Count[oy], wi = oy * yw.Stride;
            for (int ox = 0; ox < size; ox++)
            {
                float r = 0, g = 0, b = 0;
                for (int k = 0; k < count; k++)
                {
                    long i = ((long)(start + k) * size + ox) * 3;
                    float wt = yw.Weight[wi + k];
                    r += wt * rows[i];
                    g += wt * rows[i + 1];
                    b += wt * rows[i + 2];
                }
                int t = oy * size + ox;
                tensor[t] = r * s0 - m0;
                tensor[plane + t] = g * s1 - m1;
                tensor[2 * plane + t] = b * s2 - m2;
            }
        });
        return tensor;
    }
}

/// <summary>Precomputed 1-D tent filter taps for resampling <c>source</c> samples to <c>target</c> samples.</summary>
internal sealed class ResampleWeights
{
    public required int[] Start { get; init; }
    public required int[] Count { get; init; }
    public required float[] Weight { get; init; }
    public required int Stride { get; init; }

    public static ResampleWeights Create(int source, int target)
    {
        double scale = (double)source / target;
        // A tent with radius 1 is bilinear interpolation (upscaling); wider tents average (downscaling).
        double radius = Math.Max(1.0, scale);
        int stride = (int)Math.Ceiling(radius) * 2 + 1;
        var start = new int[target];
        var count = new int[target];
        var weight = new float[target * stride];
        for (int o = 0; o < target; o++)
        {
            double center = (o + 0.5) * scale - 0.5;
            int lo = Math.Max(0, (int)Math.Floor(center - radius) + 1);
            int hi = Math.Min(source - 1, (int)Math.Ceiling(center + radius) - 1);
            double sum = 0;
            int n = 0;
            for (int i = lo; i <= hi && n < stride; i++, n++)
            {
                double wt = Math.Max(0, 1 - Math.Abs(i - center) / radius);
                weight[o * stride + n] = (float)wt;
                sum += wt;
            }
            if (sum <= 0)
            {
                // Only happens at the very edge when upscaling; take the nearest sample.
                lo = Math.Clamp((int)Math.Round(center), 0, source - 1);
                n = 1;
                weight[o * stride] = 1;
                sum = 1;
            }
            for (int k = 0; k < n; k++) weight[o * stride + k] = (float)(weight[o * stride + k] / sum);
            start[o] = lo;
            count[o] = n;
        }
        return new ResampleWeights { Start = start, Count = count, Weight = weight, Stride = stride };
    }
}
