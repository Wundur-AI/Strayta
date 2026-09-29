using System.Diagnostics;
using Avalonia.Media;
using Strayta.Core.Painting;
using Strayta.Core.Selection;

namespace Strayta.Editor.ViewModels;

// Eyedropper (I, or Option with the Brush, Paint Bucket or Gradient): samples the selected layer or the image into the
// foreground or background color. It reads the same cached full-resolution sample images as the Magic Wand, so once
// they exist every sample during a drag is a lookup of at most a few thousand pixels, done on the UI thread.
public sealed partial class DocumentViewModel
{
    /// <summary>One click or drag: the image it samples (possibly still being prepared) and where it samples into.</summary>
    private sealed class EyedropperDrag(Task<SampleImage?> image, bool background)
    {
        public Task<SampleImage?> Image { get; } = image;
        public bool Background { get; } = background;
        /// <summary>The latest position while the image is not ready yet.</summary>
        public (int X, int Y)? Pending { get; set; }
    }

    private EyedropperDrag? _eyedropper;

    /// <summary>Press: starts sampling at image pixel (<paramref name="x"/>, <paramref name="y"/>), into the background color when asked.</summary>
    public bool BeginEyedropper(int x, int y, bool background)
    {
        if (IsTransforming) return false;
        _eyedropper = new EyedropperDrag(LayerSampleImageAsync(Editor.EyedropperSample), background); // DocumentViewModel.Sampling.cs
        SampleEyedropper(x, y);
        return true;
    }

    /// <summary>
    /// Drag: samples again. While the sample image is still being prepared (the first sample after an edit), only the
    /// latest position is kept and sampled as soon as it is ready.
    /// </summary>
    public void SampleEyedropper(int x, int y)
    {
        if (_eyedropper is not { } drag) return;
        if (drag.Image.IsCompletedSuccessfully)
        {
            PickColor(drag.Image.Result, x, y, drag.Background);
            return;
        }
        bool waiting = drag.Pending is not null;
        drag.Pending = (x, y);
        if (!waiting) _ = PickWhenReadyAsync(drag);
    }

    /// <summary>Release. A sample still waiting for its image is taken when the image is ready.</summary>
    public void EndEyedropper() => _eyedropper = null;

    private async Task PickWhenReadyAsync(EyedropperDrag drag)
    {
        var image = await drag.Image;
        if (drag.Pending is not { } p) return;
        drag.Pending = null;
        PickColor(image, p.X, p.Y, drag.Background);
    }

    /// <summary>How long the last sample took (averaging and setting the color), for the self-test and benchmark.</summary>
    public double LastEyedropperMs { get; private set; }

    private void PickColor(SampleImage? image, int x, int y, bool background)
    {
        if (image is null)
        {
            Notice = "Nothing to sample yet. Wait for the document to finish rendering.";
            return;
        }
        var clock = Stopwatch.StartNew();
        // Outside the canvas, or on pixels that are fully transparent, the color stays as it was (as in Photoshop).
        if (ColorSampler.Average(image, x, y, Editor.EyedropperSampleSize) is not { } c) return;
        var color = Color.FromRgb(c.R, c.G, c.B);
        if (background) Editor.BackgroundColor = color;
        else Editor.ForegroundColor = color;
        LastEyedropperMs = clock.Elapsed.TotalMilliseconds;
    }
}
