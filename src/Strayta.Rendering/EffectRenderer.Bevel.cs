using Strayta.Core;

namespace Strayta.Rendering;

// Bevel & Emboss.
internal static partial class EffectRenderer
{
    /// <summary>How steep a texture at 100% depth is: pixels of height per unit of pattern brightness.</summary>
    private const float TextureRelief = 2f;

    /// <summary>
    /// Bevel &amp; Emboss as lighting on a height field. The height rises across the bevel's width from the layer's
    /// edge: inside it (Inner Bevel), outside it (Outer Bevel), across it (Emboss), down into it from both sides
    /// (Pillow Emboss) or on the stroke (Stroke Emboss). Smooth builds the slope from the blurred shape, so corners
    /// round off; Chisel Hard from the exact distance to the edge (straight slopes, crisp mitres); Chisel Soft from
    /// that distance softened. The profile can be reshaped by the Contour sub-page and roughened by a texture. The
    /// surface normal of that height, lit from the light's angle and altitude, is compared with a flat surface's
    /// lighting: brighter becomes highlight, darker becomes shadow, each passed through the gloss contour and blurred
    /// by Soften. Depth scales the slope (100% is a 45° slope for a chisel bevel); Down turns the surface inside out.
    /// </summary>
    /// <returns>Highlight and shadow alpha (null when there is nothing to light, e.g. Stroke Emboss without a stroke).</returns>
    private static (float[] Highlight, float[] Shadow)? Bevel(EffectField f, BevelEffect b, float[]? strokes)
    {
        int w = f.W, h = f.H, n = w * h;
        float size = MathF.Max(b.Size, 0f);
        var texture = b.UseTexture && b.Texture is { Pattern.Pixels: { } px } t ? (Fill: t, Tile: PatternTile.Of(px)) : default;
        if (size <= 0f && texture.Tile is null) return null;

        float[] shape, region;
        switch (b.Style)
        {
            case BevelStyle.StrokeEmboss:
                if (strokes is null) return null;
                shape = new float[n];
                for (int i = 0; i < n; i++) shape[i] = MathF.Max(f.Shape[i], strokes[i]);
                region = strokes;
                break;
            case BevelStyle.OuterBevel:
                shape = f.Shape;
                region = new float[n];
                for (int i = 0; i < n; i++) region[i] = 1f - f.Shape[i];
                break;
            case BevelStyle.Emboss or BevelStyle.PillowEmboss:
                shape = f.Shape;
                region = new float[n];
                Array.Fill(region, 1f);
                break;
            default:
                shape = region = f.Shape;
                break;
        }

        // Height in pixels.
        var height = new float[n];
        if (size > 0f)
        {
            var profile = Profile(f, b, shape, size);
            if (b.UseContour && RangeMap.For(b.Contour, b.ContourAntiAliased, b.ContourRange) is { } map)
                for (int i = 0; i < n; i++) profile[i] = map.Apply(profile[i]);
            // Emboss spans twice the size (both sides of the edge), so its slope matches the other styles'.
            float rise = size * b.Depth * (b.Style == BevelStyle.Emboss ? 2f : 1f) * (b.Up ? 1f : -1f);
            for (int i = 0; i < n; i++) height[i] = profile[i] * rise;
        }
        if (texture.Tile is { } tile)
        {
            var fill = texture.Fill;
            float scale = MathF.Max(fill.Scale, 0.001f);
            float ox = (fill.LinkWithLayer ? f.ContentBounds.Left : 0) + fill.PhaseX;
            float oy = (fill.LinkWithLayer ? f.ContentBounds.Top : 0) + fill.PhaseY;
            float depth = b.TextureDepth * TextureRelief * (b.TextureInvert ? -1f : 1f);
            Parallel.For(0, h, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (f.Shape[i] <= 0f) continue;
                    float value = tile.Height((f.Area.Left + x + 0.5f - ox) / scale - 0.5f, (f.Area.Top + y + 0.5f - oy) / scale - 0.5f);
                    height[i] += (value - 0.5f) * depth * f.Shape[i];
                }
            });
        }

        // Light: counterclockwise from the right with y up, so y flips for image rows.
        double angle = b.Angle * Math.PI / 180.0, altitude = Math.Clamp(b.Altitude, 0f, 90f) * Math.PI / 180.0;
        float lx = (float)(Math.Cos(altitude) * Math.Cos(angle)), ly = (float)(-Math.Cos(altitude) * Math.Sin(angle)), lz = (float)Math.Sin(altitude);
        float flat = lz;
        var gloss = b.GlossContour.IsIdentity ? null : ContourLut.Of(b.GlossContour, b.GlossAntiAliased);
        float gloss0 = gloss?.Map(0f) ?? 0f;

        var highlight = new float[n];
        var shadow = new float[n];
        Parallel.For(0, h, y =>
        {
            int up = Math.Max(y - 1, 0), down = Math.Min(y + 1, h - 1);
            for (int x = 0; x < w; x++)
            {
                int left = Math.Max(x - 1, 0), right = Math.Min(x + 1, w - 1);
                float gx = (height[y * w + right] - height[y * w + left]) / Math.Max(right - left, 1);
                float gy = (height[down * w + x] - height[up * w + x]) / Math.Max(down - up, 1);
                if (gx == 0f && gy == 0f) continue;
                float inv = 1f / MathF.Sqrt(gx * gx + gy * gy + 1f);
                float s = (-gx * lx - gy * ly + lz) * inv;
                float hl = s > flat ? (flat < 0.999f ? (s - flat) / (1f - flat) : 0f) : 0f;
                float sh = s < flat ? Math.Clamp((flat - s) / MathF.Max(flat, 0.05f), 0f, 1f) : 0f;
                if (gloss is not null)
                {
                    hl = Math.Clamp(gloss.Map(hl) - gloss0, 0f, 1f);
                    sh = Math.Clamp(gloss.Map(sh) - gloss0, 0f, 1f);
                }
                int i = y * w + x;
                highlight[i] = hl;
                shadow[i] = sh;
            }
        });

        if (b.Soften > 0f)
        {
            highlight = FieldOps.Blur(highlight, w, h, b.Soften);
            shadow = FieldOps.Blur(shadow, w, h, b.Soften);
        }
        for (int i = 0; i < n; i++)
        {
            highlight[i] *= region[i];
            shadow[i] *= region[i];
        }
        return (highlight, shadow);
    }

    /// <summary>The bevel's profile: 0 where it starts, 1 where it has risen fully, for the style and technique.</summary>
    private static float[] Profile(EffectField f, BevelEffect b, float[] shape, float size)
    {
        int n = shape.Length;
        var p = new float[n];
        if (b.Technique == BevelTechnique.Smooth)
        {
            // A blurred step: 0.5 at the edge, 1 a size inside, 0 a size outside.
            var blurred = FieldOps.Blur(shape, f.W, f.H, size);
            for (int i = 0; i < n; i++)
            {
                float v = blurred[i];
                p[i] = b.Style switch
                {
                    BevelStyle.OuterBevel => Math.Clamp(2f * v, 0f, 1f),
                    BevelStyle.Emboss => v,
                    BevelStyle.PillowEmboss => MathF.Abs(2f * v - 1f),
                    _ => Math.Clamp(2f * v - 1f, 0f, 1f),
                };
            }
            return p;
        }

        var sd = ReferenceEquals(shape, f.Shape) ? f.SignedDistance : SignedDistance(shape, f.W, f.H);
        for (int i = 0; i < n; i++)
        {
            float d = sd[i] / size;
            p[i] = b.Style switch
            {
                BevelStyle.OuterBevel => Math.Clamp(1f + d, 0f, 1f),
                BevelStyle.Emboss => Math.Clamp(0.5f + d / 2f, 0f, 1f),
                BevelStyle.PillowEmboss => Math.Clamp(MathF.Abs(d), 0f, 1f),
                _ => Math.Clamp(d, 0f, 1f),
            };
        }
        if (b.Technique != BevelTechnique.ChiselSoft) return p;
        // Pillow Emboss is fully risen far from the edge on both sides; blurring its depth instead of its height keeps
        // the blur's empty border from reading as an edge.
        float soft = MathF.Max(3f, size / 3f);
        if (b.Style != BevelStyle.PillowEmboss) return FieldOps.Blur(p, f.W, f.H, soft);
        for (int i = 0; i < n; i++) p[i] = 1f - p[i];
        var blurredDepth = FieldOps.Blur(p, f.W, f.H, soft);
        for (int i = 0; i < n; i++) blurredDepth[i] = 1f - blurredDepth[i];
        return blurredDepth;
    }

    /// <summary>
    /// Signed distance to the shape's edge in pixels, positive inside. Whole pixels use the exact distance between
    /// pixel centers less the half pixel to the edge; partly covered edge pixels use their coverage, which keeps
    /// anti-aliased edges smooth.
    /// </summary>
    internal static float[] SignedDistance(float[] shape, int w, int h) =>
        SignedDistance(shape, FieldOps.DistanceTo(shape, w, h, v => v >= 0.5f), FieldOps.DistanceTo(shape, w, h, v => v < 0.5f));

    internal static float[] SignedDistance(float[] shape, float[] toShape, float[] toOutside)
    {
        var sd = new float[shape.Length];
        for (int i = 0; i < sd.Length; i++)
        {
            float s = shape[i];
            float d = s >= 0.5f ? toOutside[i] - 0.5f : -(toShape[i] - 0.5f);
            if (s is > 0f and < 1f && MathF.Abs(d) <= 1f) d = s - 0.5f;
            sd[i] = MathF.Min(d, 1e6f);
        }
        return sd;
    }
}
