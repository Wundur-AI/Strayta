using Strayta.Core;
using Strayta.Core.Selection;
using Strayta.Rendering.Filters;

namespace Strayta.Rendering.Tests;

public class FilterTests
{
    // ---- Helpers ----------------------------------------------------------------------------------------

    /// <summary>A 32-bit grayscale image (no quantization) with one value per pixel from <paramref name="f"/>.</summary>
    private static Raster Gray32(int w, int h, Func<int, int, float> f)
    {
        var p = Plane.Create(w, h, 32);
        var s = p.AsSingle();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) s[y * w + x] = f(x, y);
        return new Raster(ColorMode.Grayscale, [p], null);
    }

    private static float At(Raster r, int x, int y, int plane = 0) => (plane < r.ColorPlanes.Count ? r.ColorPlanes[plane] : r.Alpha!).GetNormalized(y * r.Width + x);

    private static Raster Rgba8(int w, int h, Func<int, int, (byte R, byte G, byte B, byte A)> f, bool alpha = true)
    {
        var planes = Enumerable.Range(0, 4).Select(_ => Plane.Create(w, h, 8)).ToArray();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var (r, g, b, a) = f(x, y);
                int i = y * w + x;
                planes[0].Data[i] = r;
                planes[1].Data[i] = g;
                planes[2].Data[i] = b;
                planes[3].Data[i] = a;
            }
        return new Raster(ColorMode.Rgb, planes[..3], alpha ? planes[3] : null);
    }

    /// <summary>Sum, center of mass and variance of a 32-bit gray image along x and y.</summary>
    private static (double Sum, double Mx, double My, double Vx, double Vy) Moments(Raster r)
    {
        double sum = 0, mx = 0, my = 0;
        for (int y = 0; y < r.Height; y++)
            for (int x = 0; x < r.Width; x++)
            {
                double v = At(r, x, y);
                sum += v;
                mx += v * x;
                my += v * y;
            }
        mx /= sum;
        my /= sum;
        double vx = 0, vy = 0;
        for (int y = 0; y < r.Height; y++)
            for (int x = 0; x < r.Width; x++)
            {
                double v = At(r, x, y);
                vx += v * (x - mx) * (x - mx);
                vy += v * (y - my) * (y - my);
            }
        return (sum, mx, my, vx / sum, vy / sum);
    }

    // ---- Gaussian Blur ----------------------------------------------------------------------------------

    [Theory]
    [InlineData(0.3)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    [InlineData(1.9)]
    [InlineData(2.0)]
    [InlineData(3.7)]
    [InlineData(10)]
    [InlineData(25)]
    public void GaussianImpulseHasVarianceRadiusSquaredAndKeepsEnergy(double radius)
    {
        int n = (int)(radius * 12) + 41, c = n / 2;
        var impulse = Gray32(n, n, (x, y) => x == c && y == c ? 1f : 0f);
        var blurred = FilterEngine.Apply(impulse, new GaussianBlurFilter(radius));
        var m = Moments(blurred);
        Assert.Equal(1.0, m.Sum, 3);
        Assert.Equal(c, m.Mx, 3);
        Assert.Equal(c, m.My, 3);
        // Photoshop's Radius is the standard deviation.
        Assert.InRange(m.Vx, radius * radius * 0.99 - 1e-3, radius * radius * 1.01 + 1e-3);
        Assert.InRange(m.Vy, radius * radius * 0.99 - 1e-3, radius * radius * 1.01 + 1e-3);
    }

    [Theory]
    [InlineData(1.2, 0.005)]
    [InlineData(2.5, 0.005)]
    [InlineData(6, 0.005)]
    [InlineData(30, 0.005)]
    [InlineData(300, 0.005)]
    [InlineData(1000, 0.005)]
    public void GaussianProfileIsCloseToATrueGaussian(double sigma, double tolerance)
    {
        // A line blurs to the one-dimensional profile. Small radii convolve with the sampled Gaussian itself; larger ones
        // use a recursive filter (Deriche's approximation), within a fraction of a percent.
        int n = (int)(sigma * 12) + 41, c = n / 2;
        var blurred = FilterEngine.Apply(Gray32(n, 9, (x, _) => x == c ? 1f : 0f), new GaussianBlurFilter(sigma));
        double peak = 1 / (Math.Sqrt(2 * Math.PI) * sigma);
        double worst = 0;
        for (int d = 0; d <= 3 * sigma; d++)
        {
            double expected = peak * Math.Exp(-d * d / (2 * sigma * sigma));
            worst = Math.Max(worst, Math.Abs(At(blurred, c + d, 4) - expected) / peak);
        }
        Assert.True(worst < tolerance, $"profile differs from a Gaussian by {worst:P2} of the peak");
    }

    [Fact]
    public void BackgroundEdgesStayOpaqueAndUnchangedWhenFlat()
    {
        var flat = Rgba8(60, 40, (_, _) => (200, 100, 50, 255), alpha: false);
        var canvas = PixelRect.FromSize(60, 40);
        var (pixels, bounds) = FilterEngine.ApplyToLayer(flat, canvas, new GaussianBlurFilter(8), new FilterScope(canvas));
        Assert.Equal(canvas, bounds);
        Assert.Null(pixels!.Alpha);
        Assert.All(Enumerable.Range(0, 3), k => Assert.True(pixels.ColorPlanes[k].Data.SequenceEqual(flat.ColorPlanes[k].Data)));
    }

    [Fact]
    public void EdgesRepeatInsteadOfFadingToBlack()
    {
        // A vertical step: blurring must not darken the top and bottom rows, which touch the canvas edge.
        var step = Gray32(80, 50, (x, _) => x < 40 ? 0.2f : 0.8f);
        var blurred = FilterEngine.Apply(step, new GaussianBlurFilter(12));
        for (int x = 0; x < 80; x += 7)
        {
            Assert.Equal(At(blurred, x, 25), At(blurred, x, 0), 4);
            Assert.Equal(At(blurred, x, 25), At(blurred, x, 49), 4);
        }
        Assert.Equal(0.2f, At(blurred, 0, 25), 2);
        Assert.Equal(0.8f, At(blurred, 79, 25), 2);
    }

    [Fact]
    public void TransparentLayersBlurPremultipliedWithoutDarkFringes()
    {
        // A red square whose transparent surroundings hide black: blurred edges must stay pure red, only fading out.
        var layer = Rgba8(20, 20, (x, y) => x is >= 5 and < 15 && y is >= 5 and < 15 ? ((byte)255, (byte)0, (byte)0, (byte)255) : ((byte)0, (byte)0, (byte)0, (byte)0));
        var canvas = PixelRect.FromSize(200, 200);
        var bounds = new PixelRect(90, 90, 110, 110);
        var (pixels, newBounds) = FilterEngine.ApplyToLayer(layer, bounds, new GaussianBlurFilter(4), new FilterScope(canvas));
        Assert.True(newBounds.Left < 90 && newBounds.Right > 110, $"the blur spreads beyond the layer ({newBounds})");
        int w = newBounds.Width, faint = 0;
        for (int i = 0; i < w * newBounds.Height; i++)
        {
            byte a = pixels!.Alpha!.Data[i];
            if (a < 8) continue; // 8-bit color is meaningless where almost nothing is visible
            faint += a < 128 ? 1 : 0;
            Assert.True(pixels.ColorPlanes[0].Data[i] >= 250 && pixels.ColorPlanes[1].Data[i] <= 3, $"pixel {i} is {pixels.ColorPlanes[0].Data[i]},{pixels.ColorPlanes[1].Data[i]} at alpha {a}");
        }
        Assert.True(faint > 50, "the edge fades out gradually");
        // Energy (alpha) is conserved: nothing leaks off the canvas here.
        double before = layer.Alpha!.Data.Sum(b => b / 255.0), after = pixels!.Alpha!.Data.Sum(b => b / 255.0);
        Assert.Equal(before, after, 0);
    }

    [Fact]
    public void LockedTransparencyKeepsAlpha()
    {
        var layer = Rgba8(30, 30, (x, _) => x < 15 ? ((byte)255, (byte)0, (byte)0, (byte)255) : ((byte)0, (byte)0, (byte)255, (byte)128));
        var canvas = PixelRect.FromSize(30, 30);
        var (pixels, bounds) = FilterEngine.ApplyToLayer(layer, canvas, new GaussianBlurFilter(3), new FilterScope(canvas) { PreserveTransparency = true });
        Assert.Equal(canvas, bounds);
        Assert.True(pixels!.Alpha!.Data.SequenceEqual(layer.Alpha!.Data));
        Assert.NotEqual(layer.ColorPlanes[0].Data[15], pixels.ColorPlanes[0].Data[15]);
    }

    [Fact]
    public void SelectionLimitsTheFilterAndSoftEdgesBlend()
    {
        var stripes = Rgba8(100, 100, (x, _) => x % 2 == 0 ? ((byte)255, (byte)255, (byte)255, (byte)255) : ((byte)0, (byte)0, (byte)0, (byte)255), alpha: false);
        var canvas = PixelRect.FromSize(100, 100);
        var rect = SelectionMask.Rectangle(new PixelRect(20, 20, 60, 60), canvas)!;
        var (pixels, _) = FilterEngine.ApplyToLayer(stripes, canvas, new BoxBlurFilter(2), new FilterScope(canvas) { Selection = rect });
        for (int y = 0; y < 100; y += 3)
            for (int x = 0; x < 100; x++)
            {
                int i = y * 100 + x;
                bool inside = x is >= 20 and < 60 && y is >= 20 and < 60;
                if (inside) Assert.InRange(pixels!.ColorPlanes[0].Data[i], 90, 165); // stripes average out
                else Assert.Equal(stripes.ColorPlanes[0].Data[i], pixels!.ColorPlanes[0].Data[i]);
            }

        // Half-selected pixels land halfway between original and filtered.
        var half = SelectionMask.FromCoverage(canvas, Enumerable.Repeat((byte)128, 100 * 100).ToArray(), canvas)!;
        var (mixed, _) = FilterEngine.ApplyToLayer(stripes, canvas, new BoxBlurFilter(2), new FilterScope(canvas) { Selection = half });
        var (full, _) = FilterEngine.ApplyToLayer(stripes, canvas, new BoxBlurFilter(2), new FilterScope(canvas));
        for (int x = 10; x < 90; x++)
        {
            int i = 50 * 100 + x;
            double expected = stripes.ColorPlanes[0].Data[i] + (full!.ColorPlanes[0].Data[i] - stripes.ColorPlanes[0].Data[i]) * (128 / 255.0);
            Assert.InRange(mixed!.ColorPlanes[0].Data[i], expected - 1.01, expected + 1.01);
        }
    }

    [Fact]
    public void SixteenBitKeepsPrecision()
    {
        var planes = Enumerable.Range(0, 3).Select(_ => Plane.Create(256, 8, 16)).ToArray();
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 256; x++)
                foreach (var p in planes) p.AsUInt16()[y * 256 + x] = (ushort)(x * 40); // a gentle ramp
        var raster = new Raster(ColorMode.Rgb, planes, null);
        var result = FilterEngine.Apply(raster, new GaussianBlurFilter(5));
        Assert.Equal(16, result.BitDepth);
        var row = result.ColorPlanes[0].AsUInt16();
        // In the middle a linear ramp is unchanged by a symmetric blur, to within 16-bit rounding, not 8-bit steps.
        for (int x = 40; x < 216; x++) Assert.InRange(row[4 * 256 + x], x * 40 - 2, x * 40 + 2);
    }

    // ---- Box and Motion Blur ----------------------------------------------------------------------------

    [Fact]
    public void BoxBlurAveragesASquare()
    {
        var impulse = Gray32(41, 41, (x, y) => x == 20 && y == 20 ? 1f : 0f);
        var blurred = FilterEngine.Apply(impulse, new BoxBlurFilter(3));
        for (int y = 0; y < 41; y++)
            for (int x = 0; x < 41; x++)
            {
                bool inside = Math.Abs(x - 20) <= 3 && Math.Abs(y - 20) <= 3;
                Assert.Equal(inside ? 1 / 49f : 0f, At(blurred, x, y), 5);
            }
    }

    [Theory]
    [InlineData(0, 21)]
    [InlineData(90, 21)]
    [InlineData(30, 40)]
    [InlineData(-45, 30)]
    [InlineData(120, 25)]
    public void MotionBlurSpreadsAlongTheAngle(double angle, double distance)
    {
        var impulse = Gray32(121, 121, (x, y) => x == 60 && y == 60 ? 1f : 0f);
        var blurred = FilterEngine.Apply(impulse, new MotionBlurFilter(angle, distance));
        var m = Moments(blurred);
        Assert.Equal(1.0, m.Sum, 3);
        Assert.Equal(60, m.Mx, 1);
        Assert.Equal(60, m.My, 1);
        // Variance along the direction is that of a line of this length (L²/12); across it, only interpolation softness.
        double rad = angle * Math.PI / 180, ux = Math.Cos(rad), uy = -Math.Sin(rad);
        double along = 0, across = 0, sum = 0;
        for (int y = 0; y < 121; y++)
            for (int x = 0; x < 121; x++)
            {
                double v = At(blurred, x, y), dx = x - 60, dy = y - 60;
                double a = dx * ux + dy * uy, c = -dx * uy + dy * ux;
                along += v * a * a;
                across += v * c * c;
                sum += v;
            }
        Assert.InRange(along / sum, distance * distance / 12 * 0.9, distance * distance / 12 * 1.1 + 1);
        Assert.True(across / sum < 0.6, $"spread across the motion {across / sum:F2}");
    }

    // ---- Unsharp Mask, High Pass ------------------------------------------------------------------------

    [Fact]
    public void UnsharpMaskOvershootsEdgesAndRespectsThreshold()
    {
        var step = Gray32(60, 10, (x, _) => x < 30 ? 0.4f : 0.6f);
        var sharp = FilterEngine.Apply(step, new UnsharpMaskFilter(100, 2, 0));
        Assert.True(At(sharp, 29, 5) < 0.39f && At(sharp, 30, 5) > 0.61f, "the edge gets darker and lighter halos");
        Assert.Equal(0.4f, At(sharp, 5, 5), 4); // flat areas stay
        Assert.Equal(0.6f, At(sharp, 55, 5), 4);

        // The step is 51 levels; a threshold above the largest difference leaves it alone.
        var untouched = FilterEngine.Apply(step, new UnsharpMaskFilter(100, 2, 60));
        Assert.Equal(At(step, 29, 5), At(untouched, 29, 5), 5);
    }

    [Fact]
    public void HighPassTurnsFlatAreasGrayAndKeepsTransparency()
    {
        var layer = Rgba8(40, 40, (x, _) => x < 20 ? ((byte)30, (byte)30, (byte)30, (byte)255) : ((byte)220, (byte)220, (byte)220, (byte)100));
        var canvas = PixelRect.FromSize(40, 40);
        var (pixels, bounds) = FilterEngine.ApplyToLayer(layer, canvas, new HighPassFilter(3), new FilterScope(canvas));
        Assert.Equal(canvas, bounds);
        Assert.True(pixels!.Alpha!.Data.SequenceEqual(layer.Alpha!.Data));
        Assert.InRange(pixels.ColorPlanes[0].Data[20 * 40 + 3], 126, 129);
        Assert.InRange(pixels.ColorPlanes[0].Data[20 * 40 + 36], 126, 129);
        Assert.True(pixels.ColorPlanes[0].Data[20 * 40 + 19] < 110 && pixels.ColorPlanes[0].Data[20 * 40 + 20] > 145);
    }

    // ---- Add Noise ----------------------------------------------------------------------------------

    [Fact]
    public void NoiseIsDeterministicAndPreviewsMatchTheResult()
    {
        var gray = Rgba8(64, 64, (_, _) => (128, 128, 128, 255), alpha: false);
        var canvas = PixelRect.FromSize(64, 64);
        var noise = new AddNoiseFilter(25, NoiseDistribution.Gaussian, Monochromatic: false, Seed: 1234);
        var a = FilterEngine.Apply(gray, noise);
        var b = FilterEngine.Apply(gray, noise);
        Assert.True(a.ColorPlanes[1].Data.SequenceEqual(b.ColorPlanes[1].Data));
        var other = FilterEngine.Apply(gray, noise with { Seed = 99 });
        Assert.False(a.ColorPlanes[1].Data.SequenceEqual(other.ColorPlanes[1].Data));

        // A preview of part of the image shows exactly the pixels the full application produces there.
        var area = new PixelRect(10, 20, 40, 50);
        var preview = FilterEngine.PreviewLayer(gray, canvas, noise, new FilterScope(canvas), area, ColorMode.Rgb, 8);
        for (int y = area.Top; y < area.Bottom; y++)
            for (int x = area.Left; x < area.Right; x++)
                Assert.Equal(a.ColorPlanes[0].Data[y * 64 + x], preview.ColorPlanes[0].Data[(y - area.Top) * area.Width + (x - area.Left)]);
    }

    [Theory]
    [InlineData(NoiseDistribution.Uniform, false)]
    [InlineData(NoiseDistribution.Gaussian, false)]
    [InlineData(NoiseDistribution.Gaussian, true)]
    public void NoiseHasTheExpectedStrength(NoiseDistribution distribution, bool mono)
    {
        var gray = Gray32(256, 256, (_, _) => 0.5f);
        var planes = new[] { gray.ColorPlanes[0], gray.ColorPlanes[0], gray.ColorPlanes[0] };
        var rgb = new Raster(ColorMode.Rgb, planes, null);
        var result = FilterEngine.Apply(rgb, new AddNoiseFilter(20, distribution, mono, Seed: 7));
        var r = result.ColorPlanes[0].AsSingle().ToArray();
        var g = result.ColorPlanes[1].AsSingle().ToArray();
        double mean = r.Average(v => v - 0.5), std = Math.Sqrt(r.Average(v => (v - 0.5) * (v - 0.5)));
        double expected = distribution == NoiseDistribution.Uniform ? 0.1 / Math.Sqrt(3) : 0.05;
        Assert.InRange(mean, -0.003, 0.003);
        Assert.InRange(std, expected * 0.95, expected * 1.05);
        Assert.Equal(mono, r.SequenceEqual(g));
    }

    [Fact]
    public void NoiseLeavesTransparentPixelsAlone()
    {
        var layer = Rgba8(32, 32, (x, _) => x < 16 ? ((byte)100, (byte)100, (byte)100, (byte)255) : ((byte)0, (byte)0, (byte)0, (byte)0));
        var canvas = PixelRect.FromSize(64, 64);
        var (pixels, bounds) = FilterEngine.ApplyToLayer(layer, new PixelRect(0, 0, 32, 32), new AddNoiseFilter(50, NoiseDistribution.Uniform, false, 3), new FilterScope(canvas));
        Assert.Equal(new PixelRect(0, 0, 32, 32), bounds);
        Assert.True(pixels!.Alpha!.Data.SequenceEqual(layer.Alpha!.Data));
    }

    // ---- Masks and previews -------------------------------------------------------------------------

    [Fact]
    public void MasksBlurAndSolidMasksStaySolid()
    {
        var canvas = PixelRect.FromSize(100, 100);
        var solid = LayerMasks.Solid(reveal: true);
        Assert.Same(solid, FilterEngine.ApplyToMask(solid, new GaussianBlurFilter(5), new FilterScope(canvas), 8));

        var data = new byte[40 * 40];
        Array.Fill(data, (byte)255);
        var mask = new LayerMask { Bounds = new PixelRect(30, 30, 70, 70), Pixels = new Plane(40, 40, 8, data), DefaultColor = 0 };
        var blurred = FilterEngine.ApplyToMask(mask, new GaussianBlurFilter(4), new FilterScope(canvas), 8);
        Assert.True(blurred.Bounds.Left < 30 && blurred.Bounds.Right > 70);
        byte Value(LayerMask m, int x, int y) => m.Pixels!.Data[(y - m.Bounds.Top) * m.Bounds.Width + (x - m.Bounds.Left)];
        Assert.Equal(255, Value(blurred, 50, 50));
        Assert.InRange(Value(blurred, 30, 50), 110, 145); // the edge is now half way
        Assert.InRange(Value(blurred, 27, 50), 20, 100);

        // High Pass changes even a solid mask: everything becomes middle gray.
        var gray = FilterEngine.ApplyToMask(solid, new HighPassFilter(3), new FilterScope(canvas), 8);
        Assert.Equal(canvas, gray.Bounds);
        Assert.All(gray.Pixels!.Data, v => Assert.InRange(v, 127, 128));
    }

    [Fact]
    public void ScaledPreviewMatchesAReducedResult()
    {
        // A preview at half resolution with the radius halved looks like the full result reduced.
        var img = Gray32(200, 200, (x, y) => (x / 10 + y / 10) % 2 == 0 ? 1f : 0f);
        var full = FilterEngine.Apply(img, new GaussianBlurFilter(8));
        var half = Gray32(100, 100, (x, y) => (At(img, 2 * x, 2 * y) + At(img, 2 * x + 1, 2 * y) + At(img, 2 * x, 2 * y + 1) + At(img, 2 * x + 1, 2 * y + 1)) / 4);
        var preview = FilterEngine.Apply(half, new GaussianBlurFilter(8).Scaled(0.5));
        for (int y = 5; y < 95; y += 6)
            for (int x = 5; x < 95; x += 6)
            {
                float reduced = (At(full, 2 * x, 2 * y) + At(full, 2 * x + 1, 2 * y) + At(full, 2 * x, 2 * y + 1) + At(full, 2 * x + 1, 2 * y + 1)) / 4;
                Assert.InRange(At(preview, x, y), reduced - 0.02f, reduced + 0.02f);
            }
    }

    [Fact]
    public void HugeRadiusFinishesAndFlattens()
    {
        var img = Gray32(300, 200, (x, _) => x < 150 ? 0f : 1f);
        var blurred = FilterEngine.Apply(img, new GaussianBlurFilter(1000));
        // With edges repeating, a radius far larger than the image averages the whole row toward the middle.
        Assert.InRange(At(blurred, 0, 100), 0.4f, 0.6f);
        Assert.InRange(At(blurred, 299, 100), 0.4f, 0.6f);
        Assert.All(Enumerable.Range(0, 300), x => Assert.False(float.IsNaN(At(blurred, x, 50))));
    }

    [Fact]
    public void CancellationStopsWork()
    {
        var img = Gray32(500, 500, (x, y) => (x ^ y) & 1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            FilterEngine.ApplyToLayer(img, PixelRect.FromSize(500, 500), new GaussianBlurFilter(20), new FilterScope(PixelRect.FromSize(500, 500)), cts.Token));
    }
}
