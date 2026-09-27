using Strayta.Core;
using Strayta.Rendering.Transforms;

namespace Strayta.Rendering.Tests;

public class ResamplerTests
{
    /// <summary>A smooth, non-symmetric RGBA test image so rotations and flips are detectable.</summary>
    private static Raster Pattern(int w, int h, int bitDepth = 8, bool alpha = true)
    {
        var planes = Enumerable.Range(0, 4).Select(_ => Plane.Create(w, h, bitDepth)).ToArray();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float r = x / (float)(w - 1), g = y / (float)(h - 1);
                float b = 0.5f + 0.4f * MathF.Sin(x * 0.2f) * MathF.Cos(y * 0.15f);
                float a = alpha ? 0.3f + 0.7f * (x + y) / (w + h) : 1f;
                Set(planes, x + y * w, bitDepth, r, g, b, a);
            }
        return new Raster(ColorMode.Rgb, planes[..3], alpha ? planes[3] : null);
    }

    private static void Set(Plane[] planes, int i, int bitDepth, params float[] v)
    {
        for (int c = 0; c < v.Length && c < planes.Length; c++)
            switch (bitDepth)
            {
                case 8: planes[c].Data[i] = RgbaConverter.ToByte(v[c]); break;
                case 16: planes[c].AsUInt16()[i] = (ushort)MathF.Round(v[c] * 65535f); break;
                default: planes[c].AsSingle()[i] = v[c]; break;
            }
    }

    [Fact]
    public void IdentityAndWholePixelMovesKeepPixelsExactly()
    {
        var r = Pattern(40, 30);
        var bounds = new PixelRect(10, 20, 50, 50);
        var (same, sameBounds) = Resampler.TransformRaster(r, bounds, Affine.Identity, ResampleFilter.Bicubic);
        Assert.Same(r, same);
        Assert.Equal(bounds, sameBounds);

        var (moved, movedBounds) = Resampler.TransformRaster(r, bounds, Affine.Translation(-7, 3), ResampleFilter.Bicubic);
        Assert.Same(r, moved);
        Assert.Equal(new PixelRect(3, 23, 43, 53), movedBounds);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    public void QuarterTurnsAreExactAndFourMakeTheOriginal(int bitDepth)
    {
        var r = Pattern(37, 23, bitDepth);
        var bounds = new PixelRect(5, 8, 42, 31);
        // A quarter turn about a pixel corner maps pixel centers onto pixel centers.
        var turn = Affine.Translation(-5, -8).Then(new Affine(0, -1, 1, 0, 0, 0)).Then(Affine.Translation(100, 50));
        var (q, qb) = Resampler.TransformRaster(r, bounds, turn, ResampleFilter.Bicubic);
        Assert.Equal(23, qb.Width);
        Assert.Equal(37, qb.Height);
        // Source (x, y) lands at (-y, x) relative to the pivot: rows become columns.
        for (int y = 0; y < 23; y++)
            for (int x = 0; x < 37; x++)
            {
                int src = y * 37 + x;
                int dst = x * 23 + (22 - y);
                for (int c = 0; c < 3; c++)
                    Assert.Equal(r.ColorPlanes[c].GetNormalized(src), q!.ColorPlanes[c].GetNormalized(dst), 1e-4f);
                Assert.Equal(r.Alpha!.GetNormalized(src), q!.Alpha!.GetNormalized(dst), 1e-4f);
            }

        Raster cur = r;
        var curBounds = bounds;
        for (int i = 0; i < 4; i++)
        {
            var (next, nextBounds) = Resampler.TransformRaster(cur, curBounds, new Affine(0, -1, 1, 0, 0, 0), ResampleFilter.Bicubic);
            (cur, curBounds) = (next!, nextBounds);
        }
        Assert.Equal(bounds, curBounds);
        for (int c = 0; c < 3; c++)
            for (int i = 0; i < 37 * 23; i++)
                Assert.Equal(r.ColorPlanes[c].GetNormalized(i), cur.ColorPlanes[c].GetNormalized(i), 1e-4f);
    }

    [Theory]
    [InlineData(ResampleFilter.Bilinear)]
    [InlineData(ResampleFilter.Bicubic)]
    public void DoublingThenHalvingIsCloseToTheOriginal(ResampleFilter filter)
    {
        var r = Pattern(64, 48);
        var bounds = new PixelRect(0, 0, 64, 48);
        var (up, upBounds) = Resampler.TransformRaster(r, bounds, Affine.Scale(2, 2), filter);
        Assert.Equal(new PixelRect(0, 0, 128, 96), upBounds);
        var (down, downBounds) = Resampler.TransformRaster(up!, upBounds, Affine.Scale(0.5, 0.5), filter);
        Assert.Equal(bounds, downBounds);

        double err = 0;
        for (int c = 0; c < 3; c++)
            for (int i = 0; i < 64 * 48; i++)
                err += Math.Abs(r.ColorPlanes[c].Data[i] - down!.ColorPlanes[c].Data[i]);
        double mean = err / (3 * 64 * 48);
        Assert.True(mean < 2.0, $"mean error {mean:F2} levels");
        // Opaque content stays opaque right up to the edges (no half-transparent rim from the resampling).
        var opaque = Pattern(64, 48, alpha: false);
        var (big, _) = Resampler.TransformRaster(opaque, bounds, Affine.Scale(2, 2), filter);
        Assert.All(big!.Alpha!.Data, a => Assert.Equal(255, a));
    }

    [Fact]
    public void RotatedAndScaledEdgesHaveNoDarkFringe()
    {
        // An opaque red square on transparent black (the usual content of a layer's empty pixels).
        int n = 40;
        var planes = Enumerable.Range(0, 4).Select(_ => Plane.Create(n, n, 8)).ToArray();
        for (int y = 10; y < 30; y++)
            for (int x = 10; x < 30; x++)
            {
                planes[0].Data[y * n + x] = 255;
                planes[3].Data[y * n + x] = 255;
            }
        var r = new Raster(ColorMode.Rgb, planes[..3], planes[3]);
        var m = Affine.Translation(-20, -20).Then(Affine.Rotation(0.5)).Then(Affine.Scale(1.7, 1.3)).Then(Affine.Translation(40, 40));
        foreach (var filter in new[] { ResampleFilter.Bilinear, ResampleFilter.Bicubic })
        {
            var (t, _) = Resampler.TransformRaster(r, PixelRect.FromSize(n, n), m, filter);
            int partial = 0;
            for (int i = 0; i < t!.Width * t.Height; i++)
            {
                if (t.Alpha!.Data[i] == 0) continue;
                if (t.Alpha.Data[i] < 255) partial++;
                Assert.True(t.ColorPlanes[0].Data[i] >= 250, $"red {t.ColorPlanes[0].Data[i]} at alpha {t.Alpha.Data[i]}");
                Assert.True(t.ColorPlanes[1].Data[i] <= 3 && t.ColorPlanes[2].Data[i] <= 3);
            }
            Assert.True(partial > 20, "rotated edges are anti-aliased");
        }
    }

    [Fact]
    public void StrongReductionAveragesInsteadOfAliasing()
    {
        // One-pixel black and white stripes shrunk 8x must become flat mid-gray, not stripes or a solid color.
        int w = 256, h = 64;
        var p = Plane.Create(w, h, 8);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) p.Data[y * w + x] = (byte)(x % 2 == 0 ? 255 : 0);
        var r = new Raster(ColorMode.Grayscale, [p], null);
        var (small, b) = Resampler.TransformRaster(r, PixelRect.FromSize(w, h), Affine.Scale(1 / 8.0, 1 / 8.0).Then(Affine.Translation(0.3, 0)), ResampleFilter.Bicubic);
        Assert.Single(small!.ColorPlanes);
        for (int y = 1; y < b.Height - 1; y++)
            for (int x = 2; x < b.Width - 2; x++)
                Assert.InRange(small.ColorPlanes[0].Data[y * b.Width + x], 118, 137);
    }

    [Fact]
    public void MasksFollowTheLayerAndKeepTheirDefaultOutside()
    {
        var plane = Plane.Create(10, 10, 8);
        Array.Fill(plane.Data, (byte)255);
        var mask = new LayerMask { Bounds = new PixelRect(0, 0, 10, 10), Pixels = plane, DefaultColor = 0 };
        var m = Affine.Scale(2, 2).Then(Affine.Translation(5.5, 0));
        var t = Resampler.TransformMask(mask, m, ResampleFilter.Bicubic)!;
        Assert.Equal(new PixelRect(5, 0, 26, 20), t.Bounds);
        Assert.Equal(255, t.Pixels!.Data[10 * 21 + 10]);
        Assert.InRange(t.Pixels.Data[10 * 21 + 0], 120, 135); // half-covered column blends toward the default (black)

        var unlinked = new LayerMask { Bounds = mask.Bounds, Pixels = plane, PositionRelativeToLayer = true };
        Assert.Same(unlinked, Resampler.TransformMask(unlinked, m, ResampleFilter.Bicubic));
    }

    [Fact]
    public void ClipLimitsTheOutput()
    {
        var r = Pattern(40, 30);
        var (t, b) = Resampler.TransformRaster(r, PixelRect.FromSize(40, 30), Affine.Scale(3, 3), ResampleFilter.Bilinear, clip: new PixelRect(0, 0, 50, 50));
        Assert.Equal(new PixelRect(0, 0, 50, 50), b);
        Assert.Equal(50, t!.Width);
    }

    [Fact]
    public void AffineInverseAndComposition()
    {
        var m = Affine.Rotation(0.3).Then(Affine.Scale(2, 0.5)).Then(Affine.Translation(3, -4));
        var (x, y) = m.Then(m.Invert()).Apply(12.5, -7.25);
        Assert.Equal(12.5, x, 9);
        Assert.Equal(-7.25, y, 9);
    }
}
