namespace Strayta.Core.Painting;

/// <summary>
/// Photoshop's gradient Method: the color space colors are blended in between stops.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Perceptual</b> blends in Oklab (Björn Ottosson's perceptual space, 2020, public domain), whose lightness
/// and hue axes follow human vision: a red-to-green gradient passes through an even, lighter yellow-brown instead
/// of Classic's dark muddy middle, and lightness changes evenly along a black-to-white one.</item>
/// <item><b>Linear</b> blends in linear light (the stops' sRGB values decoded with the sRGB transfer curve): what
/// mixing real light does, so a black-to-white gradient looks light-heavy but blends like a physical blur.</item>
/// <item><b>Classic</b> blends the document's own encoded values, as Photoshop did before 2021 (and as layer styles
/// still do).</item>
/// </list>
/// The document's colors are taken to be sRGB-encoded for the first two; for other RGB profiles (e.g. Adobe RGB,
/// whose curve is a 2.2 power) the result is close, not exact.
/// </remarks>
public enum GradientMethod
{
    Perceptual,
    Linear,
    Classic,
}

/// <summary>
/// A gradient evaluated once into a table of <see cref="Size"/> + 1 straight-alpha RGBA samples (document encoding,
/// 0..1), so drawing costs a lookup and a linear blend per pixel whatever the stops, method or smoothness.
/// </summary>
/// <remarks>
/// <para>Between two stops a color moves from one stop's value to the next as <c>v</c> goes 0..1, where <c>v</c> is the
/// position within the segment remapped piecewise-linearly so the segment's midpoint diamond gives 0.5 (Photoshop's
/// midpoint: where the blend is half way).</para>
/// <para>Smoothness blends that linear ramp toward a monotone cubic (Fritsch–Carlson tangents: the harmonic mean of
/// the neighboring slopes, zero where the slope changes sign, one-sided at the end stops). A two-stop gradient is
/// therefore unchanged by smoothness, and a multi-stop one loses the sharp corners ("Mach bands") at its interior
/// stops without overshooting any stop's color. Opacity stops are blended the same way.</para>
/// </remarks>
public sealed class GradientLut
{
    /// <summary>Samples in the table (plus one for t = 1): fine enough that blending neighbors is exact to 16 bits for linear ramps.</summary>
    public const int Size = 2048;

    private readonly float[] _rgba = new float[(Size + 1) * 4];

    private GradientLut()
    {
    }

    /// <summary>
    /// Evaluates <paramref name="gradient"/> (stops already resolved to colors, see <see cref="GradientModel.Resolve"/>).
    /// With <paramref name="transparency"/> off the opacity stops are ignored, as Photoshop's Transparency option does.
    /// </summary>
    public static GradientLut Build(Gradient gradient, GradientMethod method, bool transparency = true)
    {
        var lut = new GradientLut();
        if (gradient.Noise is { } noise) lut.FillNoise(noise, transparency);
        else lut.FillStops(gradient, method, transparency);
        return lut;
    }

    /// <summary>Straight-alpha color at <paramref name="t"/> (clamped to 0..1).</summary>
    public (float R, float G, float B, float A) At(float t)
    {
        float f = Math.Clamp(t, 0f, 1f) * Size;
        int i = (int)f;
        if (i >= Size) i = Size - 1;
        float u = f - i;
        int a = i * 4, b = a + 4;
        var d = _rgba;
        return (d[a] + (d[b] - d[a]) * u, d[a + 1] + (d[b + 1] - d[a + 1]) * u, d[a + 2] + (d[b + 2] - d[a + 2]) * u, d[a + 3] + (d[b + 3] - d[a + 3]) * u);
    }

    // ---- Stops ------------------------------------------------------------------------------------

    private void FillStops(Gradient g, GradientMethod method, bool transparency)
    {
        var colors = g.Colors.OrderBy(c => c.Location).ToList();
        var opacities = g.Opacities.OrderBy(o => o.Location).ToList();
        if (colors.Count == 0) colors = [new GradientColorStop(0f, 0.5f, RgbColor.Black)];

        var locations = colors.Select(c => c.Location).ToArray();
        var mids = colors.Select(c => c.Midpoint).ToArray();
        var space = colors.Select(c => ToSpace(c.Color, method)).ToArray();
        var c1 = new StopCurve(locations, mids, space.Select(v => v.X).ToArray(), g.Smoothness);
        var c2 = new StopCurve(locations, mids, space.Select(v => v.Y).ToArray(), g.Smoothness);
        var c3 = new StopCurve(locations, mids, space.Select(v => v.Z).ToArray(), g.Smoothness);
        var alpha = opacities.Count == 0 || !transparency ? null
            : new StopCurve(opacities.Select(o => o.Location).ToArray(), opacities.Select(o => o.Midpoint).ToArray(),
                opacities.Select(o => o.Opacity).ToArray(), g.Smoothness);

        for (int i = 0; i <= Size; i++)
        {
            float t = i / (float)Size;
            var (r, gr, b) = FromSpace(new(c1.At(t), c2.At(t), c3.At(t)), method);
            int k = i * 4;
            _rgba[k] = Math.Clamp(r, 0f, 1f);
            _rgba[k + 1] = Math.Clamp(gr, 0f, 1f);
            _rgba[k + 2] = Math.Clamp(b, 0f, 1f);
            _rgba[k + 3] = alpha is null ? 1f : Math.Clamp(alpha.At(t), 0f, 1f);
        }
    }

    /// <summary>One channel through a list of stops: segment lookup, midpoint remap, linear blended with a monotone cubic.</summary>
    private sealed class StopCurve
    {
        private readonly float[] _x, _mid, _p, _m;
        private readonly float _smooth;

        public StopCurve(float[] x, float[] mid, float[] p, float smoothness)
        {
            _x = x;
            _mid = mid;
            _p = p;
            _smooth = Math.Clamp(smoothness, 0f, 1f);
            int n = x.Length;
            _m = new float[n];
            if (n < 2) return;
            var d = new float[n - 1];
            for (int k = 0; k < n - 1; k++)
            {
                float dx = x[k + 1] - x[k];
                d[k] = dx > 1e-6f ? (p[k + 1] - p[k]) / dx : 0f;
            }
            _m[0] = d[0];
            _m[n - 1] = d[n - 2];
            for (int k = 1; k < n - 1; k++)
                _m[k] = d[k - 1] * d[k] <= 0f ? 0f : 2f / (1f / d[k - 1] + 1f / d[k]);
        }

        public float At(float t)
        {
            int n = _x.Length;
            if (n == 1 || t <= _x[0]) return _p[0];
            if (t >= _x[n - 1]) return _p[n - 1];
            int k = 0;
            while (k < n - 2 && t > _x[k + 1]) k++;
            float x0 = _x[k], x1 = _x[k + 1], h = x1 - x0;
            if (h <= 1e-6f) return _p[k + 1];
            float u = (t - x0) / h;
            float v = Midpoint(u, _mid[k]);
            float p0 = _p[k], p1 = _p[k + 1];
            float linear = p0 + (p1 - p0) * v;
            if (_smooth <= 0f) return linear;
            float v2 = v * v, v3 = v2 * v;
            float cubic = (2 * v3 - 3 * v2 + 1) * p0 + (v3 - 2 * v2 + v) * h * _m[k] + (-2 * v3 + 3 * v2) * p1 + (v3 - v2) * h * _m[k + 1];
            return linear + (cubic - linear) * _smooth;
        }
    }

    /// <summary>Piecewise-linear remap of 0..1 so that <paramref name="mid"/> maps to 0.5.</summary>
    public static float Midpoint(float u, float mid)
    {
        float m = Math.Clamp(mid, 0.001f, 0.999f);
        return u < m ? 0.5f * u / m : 0.5f + 0.5f * (u - m) / (1f - m);
    }

    // ---- Color spaces ------------------------------------------------------------------------------

    private readonly record struct Vec3(float X, float Y, float Z);

    private static Vec3 ToSpace(RgbColor c, GradientMethod method) => method switch
    {
        GradientMethod.Linear => new(SrgbToLinear(c.R), SrgbToLinear(c.G), SrgbToLinear(c.B)),
        GradientMethod.Perceptual => ToOklab(SrgbToLinear(c.R), SrgbToLinear(c.G), SrgbToLinear(c.B)),
        _ => new(c.R, c.G, c.B),
    };

    private static (float R, float G, float B) FromSpace(Vec3 v, GradientMethod method)
    {
        switch (method)
        {
            case GradientMethod.Linear:
                return (LinearToSrgb(v.X), LinearToSrgb(v.Y), LinearToSrgb(v.Z));
            case GradientMethod.Perceptual:
                var (r, g, b) = FromOklab(v);
                return (LinearToSrgb(r), LinearToSrgb(g), LinearToSrgb(b));
            default:
                return (v.X, v.Y, v.Z);
        }
    }

    /// <summary>The sRGB transfer curve (IEC 61966-2-1), encoded 0..1 to linear light.</summary>
    public static float SrgbToLinear(float v) => v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);

    public static float LinearToSrgb(float v) =>
        v <= 0f ? 0f : v <= 0.0031308f ? v * 12.92f : 1.055f * MathF.Pow(v, 1f / 2.4f) - 0.055f;

    // Oklab from linear sRGB, with Ottosson's published matrices.
    private static Vec3 ToOklab(float r, float g, float b)
    {
        float l = MathF.Cbrt(0.4122214708f * r + 0.5363325363f * g + 0.0514459929f * b);
        float m = MathF.Cbrt(0.2119034982f * r + 0.6806995451f * g + 0.1073969566f * b);
        float s = MathF.Cbrt(0.0883024619f * r + 0.2817188376f * g + 0.6299787005f * b);
        return new(0.2104542553f * l + 0.7936177850f * m - 0.0040720468f * s,
            1.9779984951f * l - 2.4285922050f * m + 0.4505937099f * s,
            0.0259040371f * l + 0.7827717662f * m - 0.8086757660f * s);
    }

    private static (float R, float G, float B) FromOklab(Vec3 lab)
    {
        float l = lab.X + 0.3963377774f * lab.Y + 0.2158037573f * lab.Z;
        float m = lab.X - 0.1055613458f * lab.Y - 0.0638541728f * lab.Z;
        float s = lab.X - 0.0894841775f * lab.Y - 1.2914855480f * lab.Z;
        l = l * l * l;
        m = m * m * m;
        s = s * s * s;
        return (4.0767416621f * l - 3.3077115913f * m + 0.2309699292f * s,
            -1.2684380046f * l + 2.6097574011f * m - 0.3413193965f * s,
            -0.0041960863f * l - 0.7034186147f * m + 1.7076147010f * s);
    }

    // ---- Noise -------------------------------------------------------------------------------------

    /// <summary>
    /// A noise gradient: each channel wanders between random values inside its range. Roughness sets how many values
    /// there are (3 at 0%, about 100 at 100%) and how sharply the channel moves between them (a smooth ease at low
    /// roughness, straight lines at high).
    /// </summary>
    private void FillNoise(GradientNoise noise, bool transparency)
    {
        uint state = (uint)noise.Seed * 2654435761u + 0x9E3779B9u;
        float Next()
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return (state >> 8) * (1f / (1 << 24));
        }
        float rough = Math.Clamp(noise.Roughness, 0f, 1f);
        int knots = 3 + (int)MathF.Round(rough * rough * 100f);
        float[] Channel(ChannelRange range)
        {
            var values = new float[knots];
            for (int k = 0; k < knots; k++) values[k] = range.Min + (range.Max - range.Min) * Next();
            return values;
        }
        var ranges = new[] { noise.C1, noise.C2, noise.C3, new ChannelRange(0f, 1f) };
        var channels = ranges.Select(Channel).ToArray();
        float ease = 1f - rough;

        for (int i = 0; i <= Size; i++)
        {
            float f = i / (float)Size * (knots - 1);
            int k = Math.Min((int)f, knots - 2);
            float u = f - k;
            u += (u * u * (3f - 2f * u) - u) * ease;
            float Ch(int c) => channels[c][k] + (channels[c][k + 1] - channels[c][k]) * u;
            float a = Ch(0), b = Ch(1), c3 = Ch(2);
            var (r, g, bl) = noise.Model == NoiseColorModel.Hsb ? HsbToRgb(a, b, c3) : (a, b, c3);
            if (noise.RestrictColors) (r, g, bl) = Restrict(r, g, bl);
            int o = i * 4;
            _rgba[o] = Math.Clamp(r, 0f, 1f);
            _rgba[o + 1] = Math.Clamp(g, 0f, 1f);
            _rgba[o + 2] = Math.Clamp(bl, 0f, 1f);
            _rgba[o + 3] = noise.AddTransparency && transparency ? Math.Clamp(Ch(3), 0f, 1f) : 1f;
        }
    }

    /// <summary>Pulls a color toward its gray so no channel reaches full saturation (at most 80% of the way out).</summary>
    private static (float, float, float) Restrict(float r, float g, float b)
    {
        float gray = (r + g + b) / 3f;
        return (gray + (r - gray) * 0.8f, gray + (g - gray) * 0.8f, gray + (b - gray) * 0.8f);
    }

    private static (float, float, float) HsbToRgb(float h, float s, float v)
    {
        h = (h - MathF.Floor(h)) * 6f;
        int i = (int)h % 6;
        float f = h - MathF.Floor(h), p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
        return i switch
        {
            0 => (v, t, p), 1 => (q, v, p), 2 => (p, v, t),
            3 => (p, q, v), 4 => (t, p, v), _ => (v, p, q),
        };
    }
}
