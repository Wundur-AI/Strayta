namespace Strayta.Core.Painting;

/// <summary>What drives a Shape Dynamics setting beyond its jitter (the Control menus of the Brush Settings).</summary>
public enum DynamicsControl
{
    Off,
    PenPressure,
    /// <summary>Angle only: the tip follows the stroke's direction.</summary>
    Direction,
}

/// <summary>
/// Shape Dynamics and Scattering: per-dab variation of a brush's size, angle, roundness and position. Jitter amounts
/// are 0..1 (angle jitter 1 = a full turn either way); Scatter is in brush diameters. Dabs use a random sequence seeded
/// by <see cref="Seed"/>, so a stroke is the same wherever its dabs are computed.
/// </summary>
public sealed record BrushDynamics
{
    public float SizeJitter { get; init; }
    public DynamicsControl SizeControl { get; init; }

    /// <summary>The smallest a jittered or pressure-controlled dab gets, as a fraction of the size.</summary>
    public float MinimumDiameter { get; init; }

    public float AngleJitter { get; init; }
    public DynamicsControl AngleControl { get; init; }

    public float RoundnessJitter { get; init; }

    /// <summary>The flattest a jittered dab gets, as a fraction of the tip's roundness.</summary>
    public float MinimumRoundness { get; init; } = 0.25f;

    /// <summary>How far dabs spread from the path, in diameters (0..10).</summary>
    public float Scatter { get; init; }

    /// <summary>Scatter along the path as well as across it.</summary>
    public bool BothAxes { get; init; }

    /// <summary>Dabs placed at each spacing interval (1..16).</summary>
    public int Count { get; init; } = 1;

    /// <summary>How much the count varies from dab to dab, 0..1.</summary>
    public float CountJitter { get; init; }

    public int Seed { get; init; } = 1;

    public bool IsEmpty => SizeJitter <= 0 && SizeControl == DynamicsControl.Off && AngleJitter <= 0 && AngleControl == DynamicsControl.Off
                           && RoundnessJitter <= 0 && Scatter <= 0 && Count <= 1;

    /// <summary>
    /// One dab's size factor, extra angle (radians, counter-clockwise) and roundness factor, for pen
    /// <paramref name="pressure"/> and stroke <paramref name="direction"/> (radians, y down).
    /// </summary>
    public (float Size, float Turn, float Round) Shape(Random rng, float pressure, float direction)
    {
        float min = Math.Clamp(MinimumDiameter, 0f, 1f);
        float size = 1f;
        if (SizeControl == DynamicsControl.PenPressure) size *= min + (1f - min) * pressure;
        if (SizeJitter > 0f) size *= 1f - Math.Clamp(SizeJitter, 0f, 1f) * rng.NextSingle() * (1f - min);

        float turn = 0f;
        if (AngleControl == DynamicsControl.Direction) turn -= direction; // y points down: the heading turns clockwise on screen
        if (AngleJitter > 0f) turn += (rng.NextSingle() * 2f - 1f) * Math.Clamp(AngleJitter, 0f, 1f) * MathF.PI;

        float round = 1f;
        if (RoundnessJitter > 0f)
            round = 1f - Math.Clamp(RoundnessJitter, 0f, 1f) * rng.NextSingle() * (1f - Math.Clamp(MinimumRoundness, 0.01f, 1f));
        return (MathF.Max(size, 0.01f), turn, round);
    }
}
