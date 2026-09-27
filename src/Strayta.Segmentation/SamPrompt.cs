using Strayta.Core;

namespace Strayta.Segmentation;

/// <summary>A click in document coordinates: on the object (positive) or on something to leave out (negative).</summary>
public readonly record struct PromptPoint(float X, float Y, bool Positive = true);

/// <summary>
/// What the user pointed at, in document coordinates (continuous; pixel centers at +0.5): any number of points
/// and/or one box.
/// </summary>
public sealed record SamPrompt(IReadOnlyList<PromptPoint> Points, (float Left, float Top, float Right, float Bottom)? Box = null)
{
    public static SamPrompt Click(float x, float y) => new([new PromptPoint(x, y)]);

    public static SamPrompt Rectangle(PixelRect box) => new([], (box.Left, box.Top, box.Right, box.Bottom));

    public bool IsEmpty => Points.Count == 0 && Box is null;

    /// <summary>
    /// The prompt as the SAM 2 decoder takes it: coordinates in the squashed <paramref name="size"/>-pixel model
    /// image, and labels 1 (positive), 0 (negative), 2/3 (box top-left/bottom-right).
    /// </summary>
    /// <remarks>
    /// SAM 2 normalizes pixel indices by the image size and its prompt encoder adds half a pixel in model space,
    /// so a continuous document coordinate c maps to c·size/extent − 0.5.
    /// </remarks>
    public (float[] Coords, float[] Labels) ToModel(PixelRect placement, int size)
    {
        int n = Points.Count + (Box is null ? 0 : 2);
        var coords = new float[n * 2];
        var labels = new float[n];
        float sx = (float)size / placement.Width, sy = (float)size / placement.Height;
        int i = 0;
        void Add(float x, float y, float label)
        {
            coords[i * 2] = (x - placement.Left) * sx - 0.5f;
            coords[i * 2 + 1] = (y - placement.Top) * sy - 0.5f;
            labels[i++] = label;
        }
        foreach (var p in Points) Add(p.X, p.Y, p.Positive ? 1 : 0);
        if (Box is { } b)
        {
            Add(b.Left, b.Top, 2);
            Add(b.Right, b.Bottom, 3);
        }
        return (coords, labels);
    }
}
