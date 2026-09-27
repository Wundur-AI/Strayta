namespace Strayta.Rendering.Transforms;

/// <summary>
/// A 2D affine map in document pixel space: (x, y) → (M11·x + M12·y + Dx, M21·x + M22·y + Dy).
/// Coordinates are continuous, so pixel (i, j) covers [i, i+1) × [j, j+1) and its center is (i + 0.5, j + 0.5).
/// </summary>
public readonly record struct Affine(double M11, double M12, double M21, double M22, double Dx, double Dy)
{
    public static Affine Identity => new(1, 0, 0, 1, 0, 0);

    public static Affine Translation(double dx, double dy) => new(1, 0, 0, 1, dx, dy);

    public static Affine Scale(double sx, double sy) => new(sx, 0, 0, sy, 0, 0);

    /// <summary>Rotation by <paramref name="radians"/>, clockwise on screen (y points down).</summary>
    public static Affine Rotation(double radians)
    {
        var (sin, cos) = Math.SinCos(radians);
        return new(cos, -sin, sin, cos, 0, 0);
    }

    public (double X, double Y) Apply(double x, double y) => (M11 * x + M12 * y + Dx, M21 * x + M22 * y + Dy);

    /// <summary>This map followed by <paramref name="next"/>.</summary>
    public Affine Then(Affine next) => new(
        next.M11 * M11 + next.M12 * M21, next.M11 * M12 + next.M12 * M22,
        next.M21 * M11 + next.M22 * M21, next.M21 * M12 + next.M22 * M22,
        next.M11 * Dx + next.M12 * Dy + next.Dx, next.M21 * Dx + next.M22 * Dy + next.Dy);

    public double Determinant => M11 * M22 - M12 * M21;

    public Affine Invert()
    {
        double det = Determinant;
        if (Math.Abs(det) < 1e-12) throw new InvalidOperationException("The transform is not invertible (zero width or height).");
        double i11 = M22 / det, i12 = -M12 / det, i21 = -M21 / det, i22 = M11 / det;
        return new(i11, i12, i21, i22, -(i11 * Dx + i12 * Dy), -(i21 * Dx + i22 * Dy));
    }

    /// <summary>True when the map only shifts by whole pixels, so pixels can be moved without resampling.</summary>
    public bool IsIntegerTranslation(out int dx, out int dy)
    {
        const double eps = 1e-6;
        dx = (int)Math.Round(Dx);
        dy = (int)Math.Round(Dy);
        return Math.Abs(M11 - 1) < eps && Math.Abs(M22 - 1) < eps && Math.Abs(M12) < eps && Math.Abs(M21) < eps
               && Math.Abs(Dx - dx) < eps && Math.Abs(Dy - dy) < eps;
    }

    /// <summary>The same map expressed in a space scaled down by <paramref name="factor"/> (a preview's pixels).</summary>
    public Affine Rescaled(int factor) => factor == 1 ? this : this with { Dx = Dx / factor, Dy = Dy / factor };
}
