using System.Numerics;

namespace Strayta.Core.Selection;

/// <summary>
/// An outside guess of where the region a Quick Selection stroke grows should lie, such as an object found by a
/// segmentation model: a signed distance to the guessed outline for every working cell, positive inside. Pass it
/// to <see cref="QuickSelectionStroke.SetPrior"/>; it steers the growth rather than replacing it (see the remarks
/// there). Core knows nothing about where the guess comes from.
/// </summary>
public sealed class QuickSelectionPrior
{
    private QuickSelectionPrior(int width, int height, float[] distance, float uncertainty, float trust)
    {
        Width = width;
        Height = height;
        Distance = distance;
        Uncertainty = uncertainty;
        Trust = Math.Clamp(trust, 0f, 1f);
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>
    /// How far off, in image pixels, the guessed outline may be: within this distance of it the image's own edges
    /// decide where the region ends, and this far outside it the region cannot go.
    /// </summary>
    public float Uncertainty { get; }

    /// <summary>
    /// How much the inside of the outline is trusted to be one thing, 0..1. At 1 growth crosses shading and details
    /// inside it freely (an object); at 0 inside grows as without a prior and only the outside is held back (a large
    /// region such as a wall or the sky, where filling all of it at once would surprise).
    /// </summary>
    public float Trust { get; }

    /// <summary>Signed distance in image pixels from each working cell's center to the guessed outline, positive inside.</summary>
    internal float[] Distance { get; }

    /// <summary>
    /// Samples <paramref name="signedDistance"/> (image coordinates of a cell's center → signed distance in image
    /// pixels, positive inside; ±infinity is fine) at every cell of <paramref name="image"/>. The function is called
    /// from several threads at once.
    /// </summary>
    public static QuickSelectionPrior Sample(QuickSelectionImage image, Func<Vector2, float> signedDistance, float uncertainty, float trust = 1f)
    {
        int w = image.Width, h = image.Height, f = image.Factor;
        int sw = image.Source.Width, sh = image.Source.Height;
        var distance = new float[w * h];
        Parallel.For(0, h, cy =>
        {
            // A cell's center, clamped to the image (the last row and column of cells may be partial).
            float y = (cy * f + Math.Min(cy * f + f, sh)) / 2f;
            for (int cx = 0; cx < w; cx++)
            {
                float x = (cx * f + Math.Min(cx * f + f, sw)) / 2f;
                float d = signedDistance(new Vector2(x, y));
                distance[cy * w + cx] = float.IsNaN(d) ? float.NegativeInfinity : d;
            }
        });
        return new QuickSelectionPrior(w, h, distance, Math.Max(uncertainty, f), trust);
    }

    /// <summary>A prior from signed distances already laid out on <paramref name="image"/>'s cells (row-major).</summary>
    public static QuickSelectionPrior FromCells(QuickSelectionImage image, float[] signedDistance, float uncertainty, float trust = 1f)
    {
        if (signedDistance.Length != image.Width * image.Height)
            throw new ArgumentException($"Expected {image.Width}×{image.Height} cells.", nameof(signedDistance));
        return new QuickSelectionPrior(image.Width, image.Height, signedDistance, Math.Max(uncertainty, image.Factor), trust);
    }
}
