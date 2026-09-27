using Strayta.Core;

namespace Strayta.Segmentation.Tests;

public class PreprocessingTests
{
    private static readonly float[] NoMean = [0f, 0f, 0f];
    private static readonly float[] NoStd = [1f, 1f, 1f];

    [Theory]
    [InlineData(4000, 1024)]
    [InlineData(1024, 1024)]
    [InlineData(300, 1024)]
    [InlineData(7, 3)]
    public void Resample_weights_are_normalized_and_in_range(int source, int target)
    {
        var w = ResampleWeights.Create(source, target);
        for (int o = 0; o < target; o++)
        {
            float sum = 0;
            for (int k = 0; k < w.Count[o]; k++) sum += w.Weight[o * w.Stride + k];
            Assert.Equal(1f, sum, 1e-4f);
            Assert.InRange(w.Start[o], 0, source - 1);
            Assert.InRange(w.Start[o] + w.Count[o] - 1, 0, source - 1);
        }
    }

    [Fact]
    public void Same_size_resampling_is_the_identity()
    {
        var w = ResampleWeights.Create(10, 10);
        for (int o = 0; o < 10; o++)
        {
            Assert.Equal(1, w.Count[o]);
            Assert.Equal(o, w.Start[o]);
        }
    }

    [Fact]
    public void Constant_image_normalizes_to_the_imagenet_values()
    {
        var image = Solid(37, 23, 200, 100, 50);
        var t = ImagePreprocessor.ToTensor(image, 16, ImagePreprocessor.ImageNetMean, ImagePreprocessor.ImageNetStd);
        Assert.Equal(3 * 16 * 16, t.Length);
        float[] expected = [(200 / 255f - 0.485f) / 0.229f, (100 / 255f - 0.456f) / 0.224f, (50 / 255f - 0.406f) / 0.225f];
        for (int c = 0; c < 3; c++)
            for (int i = 0; i < 256; i++)
                Assert.Equal(expected[c], t[c * 256 + i], 1e-4f);
    }

    [Fact]
    public void Downscaling_averages_every_source_pixel()
    {
        // A one-pixel checkerboard would alias to solid black or white under point sampling; the tent filter gives gray.
        var px = new byte[64 * 64 * 4];
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
            {
                int i = (y * 64 + x) * 4;
                byte v = (byte)((x + y) % 2 == 0 ? 255 : 0);
                (px[i], px[i + 1], px[i + 2], px[i + 3]) = (v, v, v, 255);
            }
        var t = ImagePreprocessor.ToTensor(new RgbaImage(px, 64, 64, PixelRect.FromSize(64, 64)), 8, NoMean, NoStd);
        for (int y = 1; y < 7; y++) // away from the edges, where the filter is clamped
            for (int x = 1; x < 7; x++)
                Assert.Equal(0.5f, t[y * 8 + x], 0.02f);
    }

    [Fact]
    public void Non_square_images_are_squashed_not_letterboxed()
    {
        // Left half red, right half blue, 200×50: the model image must be red on the left and blue on the right
        // all the way from top to bottom (no padding bands).
        var px = new byte[200 * 50 * 4];
        for (int y = 0; y < 50; y++)
            for (int x = 0; x < 200; x++)
            {
                int i = (y * 200 + x) * 4;
                (px[i], px[i + 1], px[i + 2], px[i + 3]) = x < 100 ? ((byte)255, (byte)0, (byte)0, (byte)255) : ((byte)0, (byte)0, (byte)255, (byte)255);
            }
        var t = ImagePreprocessor.ToTensor(new RgbaImage(px, 200, 50, new PixelRect(10, 10, 210, 60)), 32, NoMean, NoStd);
        int plane = 32 * 32;
        foreach (int y in new[] { 0, 16, 31 })
        {
            Assert.Equal(1f, t[y * 32 + 2], 1e-3f);                // red channel, left
            Assert.Equal(0f, t[2 * plane + y * 32 + 2], 1e-3f);    // blue channel, left
            Assert.Equal(1f, t[2 * plane + y * 32 + 29], 1e-3f);   // blue channel, right
        }
    }

    [Fact]
    public void Transparent_pixels_become_the_matte()
    {
        var image = Solid(4, 4, 255, 255, 255, alpha: 0);
        var t = ImagePreprocessor.ToTensor(image, 4, NoMean, NoStd);
        foreach (var v in t) Assert.Equal(ImagePreprocessor.Matte / 255f, v, 1e-4f);
    }

    [Fact]
    public void Prompt_coordinates_map_into_the_squashed_model_image()
    {
        // A 2000×1000 layer at (100, 50): its center must land on the center of the 1024 model image (minus SAM's
        // half-pixel shift), and its far corner on the last model pixel's far edge.
        var placement = new PixelRect(100, 50, 2100, 1050);
        var prompt = new SamPrompt([new PromptPoint(1100, 550), new PromptPoint(100, 50, Positive: false)], (100, 50, 2100, 1050));
        var (coords, labels) = prompt.ToModel(placement, 1024);
        Assert.Equal([1f, 0f, 2f, 3f], labels);
        Assert.Equal(511.5f, coords[0], 1e-3f);
        Assert.Equal(511.5f, coords[1], 1e-3f);
        Assert.Equal(-0.5f, coords[2], 1e-3f);
        Assert.Equal(-0.5f, coords[3], 1e-3f);
        Assert.Equal(-0.5f, coords[4], 1e-3f); // box top-left
        Assert.Equal(1023.5f, coords[6], 1e-3f); // box bottom-right
        Assert.Equal(1023.5f, coords[7], 1e-3f);
    }

    [Fact]
    public void Rectangle_prompt_is_box_only()
    {
        var prompt = SamPrompt.Rectangle(new PixelRect(10, 20, 30, 40));
        Assert.Empty(prompt.Points);
        Assert.Equal((10f, 20f, 30f, 40f), prompt.Box);
        Assert.False(prompt.IsEmpty);
        Assert.True(new SamPrompt([]).IsEmpty);
    }

    private static RgbaImage Solid(int w, int h, byte r, byte g, byte b, byte alpha = 255)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4) (px[i], px[i + 1], px[i + 2], px[i + 3]) = (r, g, b, alpha);
        return new RgbaImage(px, w, h, PixelRect.FromSize(w, h));
    }
}
