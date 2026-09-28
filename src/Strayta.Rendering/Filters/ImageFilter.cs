namespace Strayta.Rendering.Filters;

/// <summary>
/// A pixel filter (Photoshop's Filter menu) with its settings. Filters are immutable values: the same filter applied
/// to the same pixels always gives the same result, which is what lets an editor preview one and then apply exactly
/// what was previewed. Apply them with <see cref="FilterEngine"/>, which handles layers, masks, selections,
/// transparency and bit depth; a filter itself only sees premultiplied float planes (<see cref="FilterImage"/>).
/// </summary>
public abstract record ImageFilter
{
    /// <summary>Photoshop's name for the filter, e.g. "Gaussian Blur" (also the undo step's name).</summary>
    public abstract string Name { get; }

    /// <summary>How far, in pixels, an output pixel reads from; the engine reads this much around the area it changes.</summary>
    public abstract int Reach { get; }

    /// <summary>
    /// True when the filter changes areas of one flat color (noise, high pass). Blurs and sharpening leave them alone,
    /// which lets the engine skip the empty parts of layers and masks.
    /// </summary>
    public virtual bool ChangesFlatAreas => false;

    /// <summary>True when the filter never changes alpha (noise, high pass), so it cannot spread a layer beyond its pixels.</summary>
    public virtual bool KeepsTransparency => false;

    /// <summary>
    /// The same filter for an image scaled by <paramref name="scale"/> (0.25 for a quarter-resolution preview): radii and
    /// distances shrink with the image, so a preview looks like a reduced copy of the result.
    /// </summary>
    public abstract ImageFilter Scaled(double scale);

    /// <summary>Filters <paramref name="image"/> in place.</summary>
    public abstract void Apply(FilterImage image, CancellationToken cancel = default);
}

/// <summary>
/// Filter › Blur › Gaussian Blur. <see cref="Radius"/> is Photoshop's Radius (0.1–1000 pixels), used as the Gaussian's
/// standard deviation, the relationship Photoshop's results are widely observed to follow.
/// </summary>
public sealed record GaussianBlurFilter(double Radius) : ImageFilter
{
    public const double MinRadius = 0.1, MaxRadius = 1000;

    public override string Name => "Gaussian Blur";
    public double Sigma => Radius;
    public override int Reach => Blur.GaussianReach(Sigma);
    public override ImageFilter Scaled(double scale) => this with { Radius = Radius * scale };

    public override void Apply(FilterImage image, CancellationToken cancel = default)
    {
        foreach (var plane in image.Planes) Blur.Gaussian(plane, image.Width, image.Height, Sigma, cancel);
    }
}

/// <summary>
/// Filter › Blur › Box Blur: the average of a square of (2 × <see cref="Radius"/> + 1) pixels around each pixel
/// (Radius 1–2000). Fractional radii (previews) weight the square's outer ring partially.
/// </summary>
public sealed record BoxBlurFilter(double Radius) : ImageFilter
{
    public const double MinRadius = 1, MaxRadius = 2000;

    public override string Name => "Box Blur";

    /// <summary>Half the side of the averaged square, in pixels.</summary>
    public double HalfWidth => Math.Max(0.5, Radius + 0.5);

    public override int Reach => (int)Math.Ceiling(HalfWidth) + 1;

    // Scale the square's side (2r + 1), not r, so a preview averages the same area of the image.
    public override ImageFilter Scaled(double scale) => this with { Radius = Math.Max(0, (Radius + 0.5) * scale - 0.5) };

    public override void Apply(FilterImage image, CancellationToken cancel = default)
    {
        foreach (var plane in image.Planes) Blur.Box(plane, image.Width, image.Height, HalfWidth, cancel);
    }
}

/// <summary>
/// Filter › Blur › Motion Blur: averages along a line <see cref="Distance"/> pixels long (1–2000) centered on each pixel,
/// at <see cref="Angle"/> degrees counterclockwise from horizontal, as a camera moving in both directions would.
/// </summary>
public sealed record MotionBlurFilter(double Angle, double Distance) : ImageFilter
{
    public const double MinDistance = 1, MaxDistance = 2000;

    public override string Name => "Motion Blur";
    public override int Reach => (int)Math.Ceiling(Distance / 2) + 3;
    public override ImageFilter Scaled(double scale) => this with { Distance = Distance * scale };

    public override void Apply(FilterImage image, CancellationToken cancel = default)
    {
        foreach (var plane in image.Planes) Blur.Motion(plane, image.Width, image.Height, Angle, Distance, cancel);
    }
}

/// <summary>
/// Filter › Sharpen › Unsharp Mask: adds <see cref="Amount"/> percent (1–500) of the difference between each pixel and a
/// Gaussian blur of <see cref="Radius"/> pixels (0.1–1000), where that difference is at least <see cref="Threshold"/>
/// levels (0–255, on the 8-bit scale at every bit depth).
/// </summary>
public sealed record UnsharpMaskFilter(double Amount, double Radius, int Threshold) : ImageFilter
{
    public const double MinAmount = 1, MaxAmount = 500;

    public override string Name => "Unsharp Mask";
    public override int Reach => Blur.GaussianReach(Radius);
    public override ImageFilter Scaled(double scale) => this with { Radius = Radius * scale };

    public override void Apply(FilterImage image, CancellationToken cancel = default)
    {
        float amount = (float)(Amount / 100), threshold = Threshold / 255f - 1e-6f;
        int n = image.Width * image.Height;
        foreach (var plane in image.Planes)
        {
            var blurred = (float[])plane.Clone();
            Blur.Gaussian(blurred, image.Width, image.Height, Radius, cancel);
            Parallel.For(0, image.Height, new ParallelOptions { CancellationToken = cancel }, y =>
            {
                for (int i = y * image.Width, end = i + image.Width; i < end; i++)
                {
                    float d = plane[i] - blurred[i];
                    if (MathF.Abs(d) >= threshold) plane[i] += amount * d;
                }
            });
        }
        image.ClampPremultiplied();
    }
}

/// <summary>
/// Filter › Other › High Pass: keeps detail finer than a Gaussian of <see cref="Radius"/> pixels (0.1–1000) around middle
/// gray (the image minus its blur, plus 50%). Transparency is kept as it was.
/// </summary>
public sealed record HighPassFilter(double Radius) : ImageFilter
{
    public override string Name => "High Pass";
    public override int Reach => Blur.GaussianReach(Radius);
    public override bool ChangesFlatAreas => true;
    public override bool KeepsTransparency => true;
    public override ImageFilter Scaled(double scale) => this with { Radius = Radius * scale };

    public override void Apply(FilterImage image, CancellationToken cancel = default)
    {
        int w = image.Width, h = image.Height;
        var alpha = image.Alpha;
        float[]? blurredAlpha = null;
        if (alpha is not null)
        {
            blurredAlpha = (float[])alpha.Clone();
            Blur.Gaussian(blurredAlpha, w, h, Radius, cancel);
        }
        foreach (var plane in image.Color)
        {
            var blurred = (float[])plane.Clone();
            Blur.Gaussian(blurred, w, h, Radius, cancel);
            Parallel.For(0, h, new ParallelOptions { CancellationToken = cancel }, y =>
            {
                for (int i = y * w, end = i + w; i < end; i++)
                {
                    if (alpha is null)
                    {
                        plane[i] = Math.Clamp(plane[i] - blurred[i] + 0.5f, 0f, 1f);
                        continue;
                    }
                    // Compare straight colors: the blur's color is the average of the visible pixels around.
                    float a = alpha[i], ba = blurredAlpha![i];
                    float c = a > 0f ? plane[i] / a : 0f, bc = ba > 1e-7f ? blurred[i] / ba : c;
                    plane[i] = Math.Clamp(c - bc + 0.5f, 0f, 1f) * a;
                }
            });
        }
    }
}

/// <summary>How Add Noise distributes its random values.</summary>
public enum NoiseDistribution
{
    Uniform,
    Gaussian,
}

/// <summary>
/// Filter › Noise › Add Noise: <see cref="Amount"/> percent (0.1–400) of random variation added to each color channel,
/// or the same variation to all of them when <see cref="Monochromatic"/>. Uniform noise spreads evenly over ±Amount/2
/// of the full range; Gaussian noise has a standard deviation of Amount/4, so it is as strong but has rare large
/// values that read as speckles. The noise at a pixel depends only on <see cref="Seed"/> and the pixel's position in
/// the document, so a preview of any area matches the result exactly. Transparency is kept.
/// </summary>
public sealed record AddNoiseFilter(double Amount, NoiseDistribution Distribution, bool Monochromatic, int Seed) : ImageFilter
{
    public const double MinAmount = 0.1, MaxAmount = 400;

    public override string Name => "Add Noise";
    public override int Reach => 0;
    public override bool ChangesFlatAreas => true;
    public override bool KeepsTransparency => true;

    // Noise is per pixel: a reduced preview shows the full-resolution noise sampled at each preview pixel.
    public override ImageFilter Scaled(double scale) => this;

    public override void Apply(FilterImage image, CancellationToken cancel = default)
    {
        int w = image.Width, h = image.Height, colors = image.Color.Length;
        float spread = (float)(Amount / 100) * (Distribution == NoiseDistribution.Uniform ? 0.5f : 0.25f);
        bool mono = Monochromatic || colors == 1;
        var alpha = image.Alpha;
        uint seed = (uint)Seed;
        Parallel.For(0, h, new ParallelOptions { CancellationToken = cancel }, y =>
        {
            int dy = (image.Top + y) * image.Scale;
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x, dx = (image.Left + x) * image.Scale;
                float a = alpha?[i] ?? 1f;
                if (a <= 0f) continue;
                float shared = mono ? Sample(seed, dx, dy, 0) * spread : 0f;
                for (int c = 0; c < colors; c++)
                {
                    float n = mono ? shared : Sample(seed, dx, dy, c) * spread;
                    float straight = image.Color[c][i] / a;
                    image.Color[c][i] = Math.Clamp(straight + n, 0f, 1f) * a;
                }
            }
        });
    }

    /// <summary>A reproducible random value for a document pixel and channel: uniform in [-1, 1], or standard normal.</summary>
    private float Sample(uint seed, int x, int y, int channel)
    {
        uint h1 = Hash(seed, x, y, channel * 2);
        float u1 = (h1 >> 8) * (1f / 16777216f);
        if (Distribution == NoiseDistribution.Uniform) return u1 * 2f - 1f;
        uint h2 = Hash(seed, x, y, channel * 2 + 1);
        float u2 = (h2 >> 8) * (1f / 16777216f);
        // Box–Muller; the half-step offset keeps log away from zero.
        return MathF.Sqrt(-2f * MathF.Log(u1 + 0.5f / 16777216f)) * MathF.Cos(2f * MathF.PI * u2);
    }

    /// <summary>Murmur3's finalizer over the inputs: every bit of the result depends on every input bit.</summary>
    internal static uint Hash(uint seed, int x, int y, int c)
    {
        uint h = seed ^ 0x9E3779B9u;
        h = Mix(h ^ (uint)x * 0x85EBCA6Bu);
        h = Mix(h ^ (uint)y * 0xC2B2AE35u);
        h = Mix(h ^ (uint)c * 0x27D4EB2Fu);
        return h;

        static uint Mix(uint v)
        {
            v ^= v >> 16;
            v *= 0x85EBCA6Bu;
            v ^= v >> 13;
            v *= 0xC2B2AE35u;
            v ^= v >> 16;
            return v;
        }
    }
}
