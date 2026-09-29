using Strayta.Core.Painting;

namespace Strayta.Editor.Controls;

/// <summary>The painting modes for Mode menus (Gradient, Paint Bucket, Fill, Stroke), in Photoshop's order and names.</summary>
public static class PaintModeNames
{
    public static IReadOnlyList<PaintMode> Modes { get; } = PaintMode.Menu.OfType<PaintMode>().ToList();

    public static IReadOnlyList<string> Names { get; } = Modes.Select(Of).ToList();

    public static string Of(PaintMode mode) => mode.Kind switch
    {
        PaintModeKind.Behind => "Behind",
        PaintModeKind.Clear => "Clear",
        _ => BlendModeNames.Of(mode.Blend),
    };

    public static int IndexOf(PaintMode mode) => Math.Max(0, Modes.ToList().IndexOf(mode));

    public static PaintMode? At(int index) => index >= 0 && index < Modes.Count ? Modes[index] : null;
}
