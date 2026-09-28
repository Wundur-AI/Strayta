namespace Strayta.Rendering.Filters;

/// <summary>
/// What a filter works on: float planes (0..1) over a rectangle of the document, color premultiplied by
/// <see cref="Alpha"/> when there is one. Premultiplied color makes every linear filter (the blurs) treat transparency
/// correctly: transparent pixels contribute nothing, so edges never darken or pick up the color hidden under them.
/// </summary>
public sealed class FilterImage
{
    public FilterImage(int width, int height, int colorChannels, bool hasAlpha)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        Width = width;
        Height = height;
        int n = width * height;
        Color = Enumerable.Range(0, colorChannels).Select(_ => new float[n]).ToArray();
        Alpha = hasAlpha ? new float[n] : null;
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>Color planes, row-major, premultiplied when <see cref="Alpha"/> is set.</summary>
    public float[][] Color { get; }

    /// <summary>Coverage, or null when every pixel is opaque (a Background layer, a layer mask).</summary>
    public float[]? Alpha { get; }

    /// <summary>Position of the top-left pixel in the document, at the working scale.</summary>
    public int Left { get; init; }

    /// <inheritdoc cref="Left"/>
    public int Top { get; init; }

    /// <summary>
    /// How many full-resolution pixels one pixel here stands for (1 at full resolution, 4 for a quarter-size
    /// preview). Filters that depend on position (noise) use it to find the same document pixel at every scale.
    /// </summary>
    public int Scale { get; init; } = 1;

    /// <summary>Every plane, color first, then alpha.</summary>
    public IEnumerable<float[]> Planes => Alpha is null ? Color : Color.Append(Alpha);

    /// <summary>After a filter that can overshoot (sharpening): alpha within 0..1 and color within 0..alpha.</summary>
    public void ClampPremultiplied()
    {
        int w = Width;
        var alpha = Alpha;
        Parallel.For(0, Height, y =>
        {
            for (int i = y * w, end = i + w; i < end; i++)
            {
                float a = 1f;
                if (alpha is not null) a = alpha[i] = Math.Clamp(alpha[i], 0f, 1f);
                foreach (var c in Color) c[i] = Math.Clamp(c[i], 0f, a);
            }
        });
    }
}
