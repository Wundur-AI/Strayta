namespace Strayta.Core;

/// <summary>
/// Converts rasters to 8-bit sRGB RGBA for display and export. Conversions are the simple textbook ones
/// (no ICC color management), which is good enough for previews and structural comparison.
/// </summary>
public static class RgbaConverter
{
    /// <param name="palette">256 RGB triples, required for <see cref="ColorMode.Indexed"/>.</param>
    public static byte[] ToRgba8(Raster raster, byte[]? palette = null)
    {
        int n = raster.Width * raster.Height;
        var rgba = new byte[(long)n * 4];
        var planes = raster.ColorPlanes;
        bool linear = raster.BitDepth == 32;

        int width = raster.Width;
        Parallel.For(0, raster.Height, y =>
        {
        for (int i = y * width; i < (y + 1) * width; i++)
        {
            float r, g, b;
            switch (raster.ColorMode)
            {
                case ColorMode.Rgb when planes.Count >= 3:
                    r = planes[0].GetNormalized(i);
                    g = planes[1].GetNormalized(i);
                    b = planes[2].GetNormalized(i);
                    if (linear) (r, g, b) = (LinearToSrgb(r), LinearToSrgb(g), LinearToSrgb(b));
                    break;

                case ColorMode.Cmyk when planes.Count >= 4:
                    // Photoshop stores CMYK inverted: 1.0 means no ink.
                    float k = planes[3].GetNormalized(i);
                    r = planes[0].GetNormalized(i) * k;
                    g = planes[1].GetNormalized(i) * k;
                    b = planes[2].GetNormalized(i) * k;
                    break;

                case ColorMode.Lab when planes.Count >= 3:
                    (r, g, b) = LabToSrgb(
                        planes[0].GetNormalized(i) * 100f,
                        planes[1].GetNormalized(i) * 255f - 128f,
                        planes[2].GetNormalized(i) * 255f - 128f);
                    break;

                case ColorMode.Indexed when palette is not null && planes.Count >= 1:
                    int idx = planes[0].Data[i] * 3;
                    r = palette[idx] / 255f;
                    g = palette[idx + 1] / 255f;
                    b = palette[idx + 2] / 255f;
                    break;

                default:
                    float v = planes.Count > 0 ? planes[0].GetNormalized(i) : 0f;
                    if (linear) v = LinearToSrgb(v);
                    r = g = b = v;
                    break;
            }

            long o = (long)i * 4;
            rgba[o] = ToByte(r);
            rgba[o + 1] = ToByte(g);
            rgba[o + 2] = ToByte(b);
            rgba[o + 3] = raster.Alpha is null ? (byte)255 : ToByte(raster.Alpha.GetNormalized(i));
        }
        });

        return rgba;
    }

    public static byte ToByte(float v) => (byte)Math.Clamp(MathF.Round(v * 255f), 0f, 255f);

    public static float LinearToSrgb(float v) =>
        v <= 0.0031308f ? 12.92f * v : 1.055f * MathF.Pow(v, 1f / 2.4f) - 0.055f;

    private static (float R, float G, float B) LabToSrgb(float l, float a, float b)
    {
        // Lab (D50) -> XYZ (D50)
        float fy = (l + 16f) / 116f, fx = fy + a / 500f, fz = fy - b / 200f;
        static float F(float t) => t > 6f / 29f ? t * t * t : 3f * (6f / 29f) * (6f / 29f) * (t - 4f / 29f);
        float x = 0.96422f * F(fx), y = F(fy), z = 0.82521f * F(fz);

        // XYZ (D50) -> linear sRGB (D65), Bradford-adapted matrix.
        float lr = 3.1338561f * x - 1.6168667f * y - 0.4906146f * z;
        float lg = -0.9787684f * x + 1.9161415f * y + 0.0334540f * z;
        float lb = 0.0719453f * x - 0.2289914f * y + 1.4052427f * z;
        return (LinearToSrgb(Math.Clamp(lr, 0, 1)), LinearToSrgb(Math.Clamp(lg, 0, 1)), LinearToSrgb(Math.Clamp(lb, 0, 1)));
    }
}
