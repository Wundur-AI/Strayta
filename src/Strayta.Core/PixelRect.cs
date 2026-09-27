namespace Strayta.Core;

/// <summary>An integer rectangle in document pixel space. Right and Bottom are exclusive.</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public static PixelRect Empty => default;

    public int Width => Math.Max(0, Right - Left);
    public int Height => Math.Max(0, Bottom - Top);
    public bool IsEmpty => Width == 0 || Height == 0;

    public static PixelRect FromSize(int width, int height) => new(0, 0, width, height);

    public PixelRect Intersect(PixelRect other)
    {
        var r = new PixelRect(
            Math.Max(Left, other.Left), Math.Max(Top, other.Top),
            Math.Min(Right, other.Right), Math.Min(Bottom, other.Bottom));
        return r.IsEmpty ? Empty : r;
    }

    public override string ToString() => $"({Left},{Top})-({Right},{Bottom}) {Width}x{Height}";
}
